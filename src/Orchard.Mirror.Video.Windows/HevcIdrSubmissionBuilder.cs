using System.Buffers.Binary;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Video.Windows;

/// <summary>
/// Builds driver-ready DXVA submissions for a sustained HEVC stream. Parameter sets are cached,
/// picture order counts are unwrapped, and decoder surfaces are tracked as a decoded
/// picture buffer. A missing reference fails closed until the next random-access picture.
/// </summary>
public sealed class HevcIdrSubmissionBuilder
{
    private const int PictureParameterByteCount = 232;
    private readonly Dictionary<int, HevcSequenceParameterSet> sequenceParameterSets = [];
    private readonly Dictionary<int, HevcPictureParameterSet> pictureParameterSets = [];
    private readonly Dictionary<int, int> surfaceByPictureOrderCount = [];
    private readonly int surfaceCount;
    private uint feedbackNumber;
    private int previousPictureOrderCountMsb;
    private int previousPictureOrderCountLsb;
    private bool hasPreviousPictureOrderCount;

    /// <summary>True until a random-access picture has established a valid decoded-picture buffer.</summary>
    public bool RequiresRandomAccessPicture { get; private set; } = true;

    /// <summary>The stable diagnostic reason for the most recent rejected access unit.</summary>
    public string? LastFailureReason { get; private set; }

    private string randomAccessReason = "initial";

    public HevcIdrSubmissionBuilder(int surfaceCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(surfaceCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(surfaceCount, 16);
        this.surfaceCount = surfaceCount;
    }

    public int SurfaceCount => surfaceCount;

    public bool TryBuild(
        HevcAccessUnit accessUnit,
        int outputSurfaceIndex,
        out HevcDecodeSubmission? submission)
    {
        ArgumentNullException.ThrowIfNull(accessUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)outputSurfaceIndex, (uint)surfaceCount);
        LastFailureReason = null;
        submission = null;

        byte[]? codedSlice = null;
        foreach (byte[] nalUnit in accessUnit.NalUnits)
        {
            switch (HevcNalType.Of(nalUnit))
            {
                case HevcNalType.SequenceParameterSet:
                    if (HevcSequenceParameterSet.TryParse(nalUnit, out HevcSequenceParameterSet? sps) && sps is not null)
                    {
                        sequenceParameterSets[sps.Id] = sps;
                    }

                    break;

                case HevcNalType.PictureParameterSet:
                    if (HevcPictureParameterSet.TryParse(nalUnit, out HevcPictureParameterSet? pps) && pps is not null)
                    {
                        pictureParameterSets[pps.Id] = pps;
                    }

                    break;

                default:
                    if (HevcNalType.IsCodedSlice(HevcNalType.Of(nalUnit)))
                    {
                        codedSlice ??= nalUnit;
                    }

                    break;
            }
        }

        if (codedSlice is null)
        {
            return Fail("no-coded-slice", out submission);
        }

        if (!HevcSliceHeader.TryReadPictureParameterSetId(codedSlice, out int ppsId))
        {
            return Fail("slice-pps-id", out submission);
        }

        if (!pictureParameterSets.TryGetValue(ppsId, out HevcPictureParameterSet? pictureParameterSet))
        {
            string cached = pictureParameterSets.Count == 0
                ? "none"
                : string.Join('-', pictureParameterSets.Keys.Order());
            return Fail($"missing-pps-{ppsId}-cached-{cached}", out submission);
        }

        if (!sequenceParameterSets.TryGetValue(pictureParameterSet.SequenceParameterSetId, out HevcSequenceParameterSet? sequenceParameterSet))
        {
            return Fail("missing-sps", out submission);
        }

        if (!HevcSliceHeader.TryParse(codedSlice, pictureParameterSet, sequenceParameterSet, out HevcSliceHeader? parsedHeader) ||
            parsedHeader is null)
        {
            return Fail("slice-header", out submission);
        }

        // The Main-profile CoreDevice stream is 8-bit 4:2:0. Custom scaling lists, PCM and tiles
        // require additional matrix/tile serialization and must not be silently approximated.
        if (sequenceParameterSet.ChromaFormat != 1 || sequenceParameterSet.SeparateColourPlane ||
            sequenceParameterSet.BitDepthLumaMinus8 != 0 || sequenceParameterSet.BitDepthChromaMinus8 != 0 ||
            sequenceParameterSet.ScalingListEnabled || sequenceParameterSet.PcmEnabled || pictureParameterSet.TilesEnabled)
        {
            return Fail("unsupported-format", out submission);
        }

        HevcSliceHeader sliceHeader = parsedHeader;
        if (RequiresRandomAccessPicture && !sliceHeader.IsRandomAccessPoint)
        {
            return Fail($"random-access-required-{randomAccessReason}", out submission);
        }

        int pictureOrderCount;
        if (sliceHeader.IsIdr)
        {
            pictureOrderCount = 0;
        }
        else
        {
            int maxPictureOrderCountLsb = 1 << (sequenceParameterSet.Log2MaxPictureOrderCountLsbMinus4 + 4);
            int pictureOrderCountMsb = previousPictureOrderCountMsb;
            if (hasPreviousPictureOrderCount)
            {
                if (sliceHeader.PictureOrderCountLsb < previousPictureOrderCountLsb
                    && previousPictureOrderCountLsb - sliceHeader.PictureOrderCountLsb >= maxPictureOrderCountLsb / 2)
                {
                    pictureOrderCountMsb = checked(previousPictureOrderCountMsb + maxPictureOrderCountLsb);
                }
                else if (sliceHeader.PictureOrderCountLsb > previousPictureOrderCountLsb
                    && sliceHeader.PictureOrderCountLsb - previousPictureOrderCountLsb > maxPictureOrderCountLsb / 2)
                {
                    pictureOrderCountMsb = checked(previousPictureOrderCountMsb - maxPictureOrderCountLsb);
                }
            }
            else
            {
                pictureOrderCountMsb = 0;
            }

            pictureOrderCount = checked(pictureOrderCountMsb + sliceHeader.PictureOrderCountLsb);
        }

        if (sliceHeader.IsRandomAccessPoint)
        {
            surfaceByPictureOrderCount.Clear();
        }

        List<DecodedReference> references = [];
        foreach (HevcShortTermReference reference in sliceHeader.ShortTermReferences)
        {
            int referencePictureOrderCount = checked(pictureOrderCount + reference.DeltaPictureOrderCount);
            if (!surfaceByPictureOrderCount.TryGetValue(referencePictureOrderCount, out int referenceSurface))
            {
                RequiresRandomAccessPicture = true;
                randomAccessReason = $"missing-reference-poc-{referencePictureOrderCount}";
                return Fail(randomAccessReason, out submission);
            }

            references.Add(new DecodedReference(
                referencePictureOrderCount,
                referenceSurface,
                reference.DeltaPictureOrderCount,
                reference.UsedByCurrentPicture));
        }

        if (references.Count > 15)
        {
            RequiresRandomAccessPicture = true;
            randomAccessReason = "too-many-references";
            return Fail("too-many-references", out submission);
        }

        HashSet<int> retainedPictureOrderCounts = references
            .Select(reference => reference.PictureOrderCount)
            .ToHashSet();
        foreach (int stalePictureOrderCount in surfaceByPictureOrderCount.Keys
            .Where(pictureOrderCount => !retainedPictureOrderCounts.Contains(pictureOrderCount))
            .ToArray())
        {
            surfaceByPictureOrderCount.Remove(stalePictureOrderCount);
        }

        int selectedOutputSurfaceIndex = outputSurfaceIndex;
        HashSet<int> referencedSurfaces = references.Select(reference => reference.SurfaceIndex).ToHashSet();
        if (referencedSurfaces.Contains(selectedOutputSurfaceIndex))
        {
            selectedOutputSurfaceIndex = -1;
            for (int candidate = 0; candidate < surfaceCount; candidate++)
            {
                if (!referencedSurfaces.Contains(candidate))
                {
                    selectedOutputSurfaceIndex = candidate;
                    break;
                }
            }

            if (selectedOutputSurfaceIndex < 0)
            {
                RequiresRandomAccessPicture = true;
                randomAccessReason = "no-free-output-surface";
                return Fail(randomAccessReason, out submission);
            }
        }

        if (!HevcDxvaPictureData.TryBuild(accessUnit, out HevcDxvaPictureData? pictureData) || pictureData is null)
        {
            return Fail("dxva-picture-data", out submission);
        }

        byte[] pictureParameters = SerializePictureParameters(
            sequenceParameterSet,
            pictureParameterSet,
            sliceHeader,
            pictureOrderCount,
            references,
            selectedOutputSurfaceIndex,
            unchecked(++feedbackNumber));
        submission = new HevcDecodeSubmission(
            pictureParameters,
            ReadOnlyMemory<byte>.Empty,
            pictureData.SliceControl,
            pictureData.Bitstream,
            sequenceParameterSet.Width,
            sequenceParameterSet.Height,
            selectedOutputSurfaceIndex,
            pictureOrderCount,
            sequenceParameterSet.VideoFullRange,
            sequenceParameterSet.MatrixCoefficients);

        foreach (int stalePictureOrderCount in surfaceByPictureOrderCount
            .Where(entry => entry.Value == selectedOutputSurfaceIndex)
            .Select(entry => entry.Key)
            .ToArray())
        {
            surfaceByPictureOrderCount.Remove(stalePictureOrderCount);
        }

        surfaceByPictureOrderCount[pictureOrderCount] = selectedOutputSurfaceIndex;
        previousPictureOrderCountLsb = sliceHeader.PictureOrderCountLsb;
        previousPictureOrderCountMsb = pictureOrderCount - sliceHeader.PictureOrderCountLsb;
        hasPreviousPictureOrderCount = true;
        RequiresRandomAccessPicture = false;
        randomAccessReason = "none";
        return true;
    }

    /// <summary>Forget parameter sets and all decoded references after a stream restart.</summary>
    public void Reset()
    {
        sequenceParameterSets.Clear();
        pictureParameterSets.Clear();
        surfaceByPictureOrderCount.Clear();
        previousPictureOrderCountMsb = 0;
        previousPictureOrderCountLsb = 0;
        hasPreviousPictureOrderCount = false;
        RequiresRandomAccessPicture = true;
        randomAccessReason = "initial";
        LastFailureReason = null;
    }

    private bool Fail(string reason, out HevcDecodeSubmission? submission)
    {
        LastFailureReason = reason;
        submission = null;
        return false;
    }

    private static byte[] SerializePictureParameters(
        HevcSequenceParameterSet sps,
        HevcPictureParameterSet pps,
        HevcSliceHeader sliceHeader,
        int pictureOrderCount,
        IReadOnlyList<DecodedReference> references,
        int outputSurfaceIndex,
        uint statusFeedbackNumber)
    {
        byte[] result = new byte[PictureParameterByteCount];
        int minimumCodingBlockSize = 1 << (sps.Log2MinLumaCodingBlockSizeMinus3 + 3);
        ushort widthInMinimumCodingBlocks = checked((ushort)((sps.Width + minimumCodingBlockSize - 1) / minimumCodingBlockSize));
        ushort heightInMinimumCodingBlocks = checked((ushort)((sps.Height + minimumCodingBlockSize - 1) / minimumCodingBlockSize));
        BinaryPrimitives.WriteUInt16LittleEndian(result, widthInMinimumCodingBlocks);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), heightInMinimumCodingBlocks);

        ushort formatFlags = checked((ushort)(
            sps.ChromaFormat |
            ((sps.SeparateColourPlane ? 1 : 0) << 2) |
            (sps.BitDepthLumaMinus8 << 3) |
            (sps.BitDepthChromaMinus8 << 6) |
            (sps.Log2MaxPictureOrderCountLsbMinus4 << 9)));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), formatFlags);
        result[6] = checked((byte)outputSurfaceIndex);
        result[7] = checked((byte)sps.MaxDecodedPictureBufferingMinus1);
        result[8] = checked((byte)sps.Log2MinLumaCodingBlockSizeMinus3);
        result[9] = checked((byte)sps.Log2DiffMaxMinLumaCodingBlockSize);
        result[10] = checked((byte)sps.Log2MinTransformBlockSizeMinus2);
        result[11] = checked((byte)sps.Log2DiffMaxMinTransformBlockSize);
        result[12] = checked((byte)sps.MaxTransformHierarchyDepthInter);
        result[13] = checked((byte)sps.MaxTransformHierarchyDepthIntra);
        result[14] = checked((byte)sps.ShortTermReferencePictureSetCount);
        result[15] = checked((byte)sps.LongTermReferencePictureCount);
        result[16] = checked((byte)pps.DefaultReferenceIndexL0Minus1);
        result[17] = checked((byte)pps.DefaultReferenceIndexL1Minus1);
        result[18] = checked((byte)(sbyte)pps.InitialQuantizationParameterMinus26);
        result[19] = checked((byte)sliceHeader.ReferenceDeltaPictureOrderCountCount);
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(20),
            checked((ushort)sliceHeader.ShortTermReferencePictureSetBitCount));

        uint codingFlags =
            (sps.AsymmetricMotionPartitionsEnabled ? 1u << 1 : 0) |
            (sps.SampleAdaptiveOffsetEnabled ? 1u << 2 : 0) |
            (sps.LongTermReferencesPresent ? 1u << 17 : 0) |
            (sps.TemporalMotionVectorPredictionEnabled ? 1u << 18 : 0) |
            (sps.StrongIntraSmoothingEnabled ? 1u << 19 : 0) |
            (pps.DependentSliceSegmentsEnabled ? 1u << 20 : 0) |
            (pps.OutputFlagPresent ? 1u << 21 : 0) |
            ((uint)pps.ExtraSliceHeaderBits << 22) |
            (pps.SignDataHidingEnabled ? 1u << 25 : 0) |
            (pps.CabacInitializationPresent ? 1u << 26 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), codingFlags);

        uint pictureFlags =
            (pps.ConstrainedIntraPrediction ? 1u : 0) |
            (pps.TransformSkipEnabled ? 1u << 1 : 0) |
            (pps.CuQuantizationParameterDeltaEnabled ? 1u << 2 : 0) |
            (pps.SliceChromaQuantizationParameterOffsetsPresent ? 1u << 3 : 0) |
            (pps.WeightedPrediction ? 1u << 4 : 0) |
            (pps.WeightedBiPrediction ? 1u << 5 : 0) |
            (pps.TransquantBypassEnabled ? 1u << 6 : 0) |
            (pps.EntropyCodingSyncEnabled ? 1u << 8 : 0) |
            (pps.LoopFilterAcrossSlicesEnabled ? 1u << 11 : 0) |
            (pps.DeblockingFilterOverrideEnabled ? 1u << 12 : 0) |
            (pps.DeblockingFilterDisabled ? 1u << 13 : 0) |
            (pps.ListsModificationPresent ? 1u << 14 : 0) |
            (pps.SliceSegmentHeaderExtensionPresent ? 1u << 15 : 0) |
            (sliceHeader.IsRandomAccessPoint ? 1u << 16 : 0) |
            (sliceHeader.IsIdr ? 1u << 17 : 0) |
            // DXVA defines IntraPicFlag for IRAP pictures, matching the Windows HEVC
            // reference implementation, rather than for every ordinary I slice.
            (sliceHeader.IsRandomAccessPoint ? 1u << 18 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), pictureFlags);
        result[32] = checked((byte)(sbyte)pps.CbQuantizationParameterOffset);
        result[33] = checked((byte)(sbyte)pps.CrQuantizationParameterOffset);
        result[116] = checked((byte)pps.DiffCuQuantizationParameterDeltaDepth);
        result[117] = checked((byte)(sbyte)pps.BetaOffsetDiv2);
        result[118] = checked((byte)(sbyte)pps.TcOffsetDiv2);
        result[119] = checked((byte)pps.Log2ParallelMergeLevelMinus2);

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(120), pictureOrderCount);
        result.AsSpan(124, 15).Fill(0xFF);
        for (int index = 0; index < 15; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(140 + (index * 4)), 0);
        }

        result.AsSpan(200, 24).Fill(0xFF);
        int beforeIndex = 0;
        int afterIndex = 0;
        for (int index = 0; index < references.Count; index++)
        {
            DecodedReference reference = references[index];
            result[124 + index] = checked((byte)reference.SurfaceIndex);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(140 + (index * 4)), reference.PictureOrderCount);
            if (!reference.UsedByCurrentPicture)
            {
                continue;
            }

            if (reference.DeltaPictureOrderCount < 0)
            {
                result[200 + beforeIndex++] = checked((byte)index);
            }
            else
            {
                result[208 + afterIndex++] = checked((byte)index);
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(228), statusFeedbackNumber);
        return result;
    }

    private sealed record DecodedReference(
        int PictureOrderCount,
        int SurfaceIndex,
        int DeltaPictureOrderCount,
        bool UsedByCurrentPicture);
}
