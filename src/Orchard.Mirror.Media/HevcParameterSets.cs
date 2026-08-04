namespace Orchard.Mirror.Media;

/// <summary>One delta picture-order-count entry in an HEVC short-term reference set.</summary>
public sealed record HevcShortTermReference(int DeltaPictureOrderCount, bool UsedByCurrentPicture);

/// <summary>A parsed HEVC short-term reference-picture set.</summary>
public sealed record HevcShortTermReferencePictureSet(
    IReadOnlyList<HevcShortTermReference> References,
    int ReferenceDeltaPictureOrderCountCount = 0);

/// <summary>The SPS fields required by the Windows HEVC picture-parameter contract.</summary>
public sealed record HevcSequenceParameterSet(
    int Id,
    int Width,
    int Height,
    int ChromaFormat,
    bool SeparateColourPlane,
    int BitDepthLumaMinus8,
    int BitDepthChromaMinus8,
    int Log2MaxPictureOrderCountLsbMinus4,
    int MaxDecodedPictureBufferingMinus1,
    int Log2MinLumaCodingBlockSizeMinus3,
    int Log2DiffMaxMinLumaCodingBlockSize,
    int Log2MinTransformBlockSizeMinus2,
    int Log2DiffMaxMinTransformBlockSize,
    int MaxTransformHierarchyDepthInter,
    int MaxTransformHierarchyDepthIntra,
    int ShortTermReferencePictureSetCount,
    int LongTermReferencePictureCount,
    bool ScalingListEnabled,
    bool AsymmetricMotionPartitionsEnabled,
    bool SampleAdaptiveOffsetEnabled,
    bool PcmEnabled,
    bool LongTermReferencesPresent,
    bool TemporalMotionVectorPredictionEnabled,
    bool StrongIntraSmoothingEnabled,
    IReadOnlyList<HevcShortTermReferencePictureSet> ShortTermReferencePictureSets,
    bool VideoFullRange = false,
    int MatrixCoefficients = 2)
{
    /// <summary>
    /// The value H.265 infers for <c>matrix_coeffs</c> when the stream does not describe its
    /// colour. It means "unspecified", not "BT.601": nothing may read it as a licence to guess.
    /// </summary>
    public const int UnspecifiedMatrixCoefficients = 2;

    public static bool TryParse(ReadOnlySpan<byte> nalUnit, out HevcSequenceParameterSet? parameterSet)
    {
        parameterSet = null;
        if (HevcNalType.Of(nalUnit) != HevcNalType.SequenceParameterSet)
        {
            return false;
        }

        try
        {
            byte[] rbsp = HevcRbsp.Decode(nalUnit);
            HevcBitReader bits = new(rbsp);
            _ = bits.ReadBits(4);
            int maximumSubLayersMinus1 = checked((int)bits.ReadBits(3));
            _ = bits.ReadFlag();
            SkipProfileTierLevel(ref bits, maximumSubLayersMinus1);

            int id = ReadInt(ref bits);
            int chromaFormat = ReadInt(ref bits);
            if (chromaFormat is < 0 or > 3)
            {
                return false;
            }

            bool separateColourPlane = chromaFormat == 3 && bits.ReadFlag();
            int codedWidth = ReadInt(ref bits);
            int codedHeight = ReadInt(ref bits);
            int width = codedWidth;
            int height = codedHeight;
            if (bits.ReadFlag())
            {
                int left = ReadInt(ref bits);
                int right = ReadInt(ref bits);
                int top = ReadInt(ref bits);
                int bottom = ReadInt(ref bits);
                int subWidth = chromaFormat is 1 or 2 ? 2 : 1;
                int subHeight = chromaFormat == 1 ? 2 : 1;
                width -= checked((left + right) * subWidth);
                height -= checked((top + bottom) * subHeight);
            }

            int bitDepthLumaMinus8 = ReadInt(ref bits);
            int bitDepthChromaMinus8 = ReadInt(ref bits);
            int log2MaxPocLsbMinus4 = ReadInt(ref bits);
            bool orderingInfoPresent = bits.ReadFlag();
            int maxDecodedPictureBufferingMinus1 = 0;
            int firstLayer = orderingInfoPresent ? 0 : maximumSubLayersMinus1;
            for (int layer = firstLayer; layer <= maximumSubLayersMinus1; layer++)
            {
                maxDecodedPictureBufferingMinus1 = ReadInt(ref bits);
                _ = bits.ReadUnsignedExpGolomb();
                _ = bits.ReadUnsignedExpGolomb();
            }

            int log2MinLumaCodingBlockSizeMinus3 = ReadInt(ref bits);
            int log2DiffMaxMinLumaCodingBlockSize = ReadInt(ref bits);
            int log2MinTransformBlockSizeMinus2 = ReadInt(ref bits);
            int log2DiffMaxMinTransformBlockSize = ReadInt(ref bits);
            int maxTransformHierarchyDepthInter = ReadInt(ref bits);
            int maxTransformHierarchyDepthIntra = ReadInt(ref bits);

            bool scalingListEnabled = bits.ReadFlag();
            if (scalingListEnabled && bits.ReadFlag())
            {
                SkipScalingList(ref bits);
            }

            bool ampEnabled = bits.ReadFlag();
            bool saoEnabled = bits.ReadFlag();
            bool pcmEnabled = bits.ReadFlag();
            if (pcmEnabled)
            {
                _ = bits.ReadBits(4);
                _ = bits.ReadBits(4);
                _ = bits.ReadUnsignedExpGolomb();
                _ = bits.ReadUnsignedExpGolomb();
                _ = bits.ReadFlag();
            }

            int shortTermSetCount = ReadInt(ref bits);
            if (shortTermSetCount > 64)
            {
                return false;
            }

            List<HevcShortTermReferencePictureSet> shortTermSets = new(shortTermSetCount);
            for (int index = 0; index < shortTermSetCount; index++)
            {
                shortTermSets.Add(ParseShortTermReferencePictureSet(ref bits, index, shortTermSetCount, shortTermSets));
            }

            bool longTermReferencesPresent = bits.ReadFlag();
            int longTermReferenceCount = 0;
            if (longTermReferencesPresent)
            {
                longTermReferenceCount = ReadInt(ref bits);
                int pocBits = log2MaxPocLsbMinus4 + 4;
                for (int index = 0; index < longTermReferenceCount; index++)
                {
                    _ = bits.ReadBits(pocBits);
                    _ = bits.ReadFlag();
                }
            }

            bool temporalMvpEnabled = bits.ReadFlag();
            bool strongIntraSmoothingEnabled = bits.ReadFlag();
            if (id > 15 || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                return false;
            }

            // The VUI is optional and everything above is already decided, so a stream that ends
            // here is still usable: fall back to what H.265 infers rather than failing the parse.
            bool videoFullRange = false;
            int matrixCoefficients = UnspecifiedMatrixCoefficients;
            try
            {
                if (bits.ReadFlag())
                {
                    ReadVideoSignalType(ref bits, out videoFullRange, out matrixCoefficients);
                }
            }
            catch (Exception exception) when (exception is HevcBitstreamException or OverflowException)
            {
                videoFullRange = false;
                matrixCoefficients = UnspecifiedMatrixCoefficients;
            }

            parameterSet = new HevcSequenceParameterSet(
                id,
                width,
                height,
                chromaFormat,
                separateColourPlane,
                bitDepthLumaMinus8,
                bitDepthChromaMinus8,
                log2MaxPocLsbMinus4,
                maxDecodedPictureBufferingMinus1,
                log2MinLumaCodingBlockSizeMinus3,
                log2DiffMaxMinLumaCodingBlockSize,
                log2MinTransformBlockSizeMinus2,
                log2DiffMaxMinTransformBlockSize,
                maxTransformHierarchyDepthInter,
                maxTransformHierarchyDepthIntra,
                shortTermSetCount,
                longTermReferenceCount,
                scalingListEnabled,
                ampEnabled,
                saoEnabled,
                pcmEnabled,
                longTermReferencesPresent,
                temporalMvpEnabled,
                strongIntraSmoothingEnabled,
                shortTermSets,
                videoFullRange,
                matrixCoefficients);
            return true;
        }
        catch (Exception exception) when (exception is HevcBitstreamException or OverflowException)
        {
            return false;
        }
    }

    internal static HevcShortTermReferencePictureSet ParseShortTermReferencePictureSet(
        ref HevcBitReader bits,
        int index,
        int setsInSequenceParameterSet,
        IReadOnlyList<HevcShortTermReferencePictureSet> priorSets)
    {
        bool predicted = index != 0 && bits.ReadFlag();
        if (predicted)
        {
            int deltaIndexMinus1 = index == setsInSequenceParameterSet
                ? ReadInt(ref bits)
                : 0;
            bool deltaSign = bits.ReadFlag();
            int deltaMagnitude = checked(ReadInt(ref bits) + 1);
            int delta = deltaSign ? -deltaMagnitude : deltaMagnitude;
            int referenceSetIndex = checked(index - deltaIndexMinus1 - 1);
            if ((uint)referenceSetIndex >= (uint)priorSets.Count)
            {
                throw new HevcBitstreamException();
            }

            IReadOnlyList<HevcShortTermReference> referenceSet = priorSets[referenceSetIndex].References;
            bool[] usedByCurrent = new bool[referenceSet.Count + 1];
            bool[] useDelta = new bool[referenceSet.Count + 1];
            for (int item = 0; item <= referenceSet.Count; item++)
            {
                usedByCurrent[item] = bits.ReadFlag();
                useDelta[item] = usedByCurrent[item] || bits.ReadFlag();
            }

            List<HevcShortTermReference> derived = [];
            IEnumerable<(HevcShortTermReference Reference, int Index)> candidates =
                referenceSet.Select((reference, item) => (reference, item))
                    .Append((new HevcShortTermReference(0, false), referenceSet.Count));
            foreach ((HevcShortTermReference reference, int item) in candidates)
            {
                int derivedDelta = checked(reference.DeltaPictureOrderCount + delta);
                if (useDelta[item] && derivedDelta != 0)
                {
                    derived.Add(new HevcShortTermReference(derivedDelta, usedByCurrent[item]));
                }
            }

            return new HevcShortTermReferencePictureSet(
                [.. derived.OrderByDescending(reference => reference.DeltaPictureOrderCount < 0)
                    .ThenByDescending(reference => reference.DeltaPictureOrderCount < 0
                        ? reference.DeltaPictureOrderCount
                        : -reference.DeltaPictureOrderCount)],
                referenceSet.Count);
        }

        int negativeCount = ReadInt(ref bits);
        int positiveCount = ReadInt(ref bits);
        if (negativeCount + positiveCount > 32)
        {
            throw new HevcBitstreamException();
        }

        List<HevcShortTermReference> references = new(negativeCount + positiveCount);
        int previous = 0;
        for (int item = 0; item < negativeCount; item++)
        {
            previous = checked(previous - ReadInt(ref bits) - 1);
            references.Add(new HevcShortTermReference(previous, bits.ReadFlag()));
        }

        previous = 0;
        for (int item = 0; item < positiveCount; item++)
        {
            previous = checked(previous + ReadInt(ref bits) + 1);
            references.Add(new HevcShortTermReference(previous, bits.ReadFlag()));
        }

        return new HevcShortTermReferencePictureSet(references);
    }

    /// <summary>
    /// Reads the head of the VUI as far as <c>video_signal_type</c>, which is where a stream says
    /// whether its luma spans 16..235 or 0..255. Everything before it is skipped rather than kept:
    /// the aspect-ratio and overscan fields are variable width, so they have to be walked exactly
    /// even though nothing here wants them.
    /// </summary>
    private static void ReadVideoSignalType(
        ref HevcBitReader bits,
        out bool videoFullRange,
        out int matrixCoefficients)
    {
        videoFullRange = false;
        matrixCoefficients = UnspecifiedMatrixCoefficients;

        if (bits.ReadFlag())
        {
            const uint extendedSar = 255;
            if (bits.ReadBits(8) == extendedSar)
            {
                bits.SkipBits(32);
            }
        }

        if (bits.ReadFlag())
        {
            _ = bits.ReadFlag();
        }

        if (!bits.ReadFlag())
        {
            return;
        }

        bits.SkipBits(3);
        videoFullRange = bits.ReadFlag();
        if (bits.ReadFlag())
        {
            bits.SkipBits(16);
            matrixCoefficients = checked((int)bits.ReadBits(8));
        }
    }

    private static void SkipProfileTierLevel(ref HevcBitReader bits, int maximumSubLayersMinus1)
    {
        bits.SkipBits(2 + 1 + 5 + 32 + 4 + 32 + 12 + 8);
        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (int layer = 0; layer < maximumSubLayersMinus1; layer++)
        {
            profilePresent[layer] = bits.ReadFlag();
            levelPresent[layer] = bits.ReadFlag();
        }

        if (maximumSubLayersMinus1 > 0)
        {
            bits.SkipBits((8 - maximumSubLayersMinus1) * 2);
        }

        for (int layer = 0; layer < maximumSubLayersMinus1; layer++)
        {
            if (profilePresent[layer])
            {
                bits.SkipBits(2 + 1 + 5 + 32 + 4 + 32 + 12);
            }

            if (levelPresent[layer])
            {
                _ = bits.ReadBits(8);
            }
        }
    }

    internal static void SkipScalingList(ref HevcBitReader bits)
    {
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            int step = sizeId == 3 ? 3 : 1;
            for (int matrixId = 0; matrixId < 6; matrixId += step)
            {
                if (!bits.ReadFlag())
                {
                    _ = bits.ReadUnsignedExpGolomb();
                    continue;
                }

                int coefficientCount = Math.Min(64, 1 << (4 + (sizeId << 1)));
                if (sizeId > 1)
                {
                    _ = bits.ReadSignedExpGolomb();
                }

                for (int coefficient = 0; coefficient < coefficientCount; coefficient++)
                {
                    _ = bits.ReadSignedExpGolomb();
                }
            }
        }
    }

    private static int ReadInt(ref HevcBitReader bits) => checked((int)bits.ReadUnsignedExpGolomb());
}

/// <summary>The PPS fields required by the Windows HEVC picture-parameter contract.</summary>
public sealed record HevcPictureParameterSet(
    int Id,
    int SequenceParameterSetId,
    bool DependentSliceSegmentsEnabled,
    bool OutputFlagPresent,
    int ExtraSliceHeaderBits,
    bool SignDataHidingEnabled,
    bool CabacInitializationPresent,
    int DefaultReferenceIndexL0Minus1,
    int DefaultReferenceIndexL1Minus1,
    int InitialQuantizationParameterMinus26,
    bool ConstrainedIntraPrediction,
    bool TransformSkipEnabled,
    bool CuQuantizationParameterDeltaEnabled,
    int DiffCuQuantizationParameterDeltaDepth,
    int CbQuantizationParameterOffset,
    int CrQuantizationParameterOffset,
    bool SliceChromaQuantizationParameterOffsetsPresent,
    bool WeightedPrediction,
    bool WeightedBiPrediction,
    bool TransquantBypassEnabled,
    bool TilesEnabled,
    bool EntropyCodingSyncEnabled,
    bool UniformTileSpacing,
    IReadOnlyList<int> TileColumnWidthsMinus1,
    IReadOnlyList<int> TileRowHeightsMinus1,
    bool LoopFilterAcrossTilesEnabled,
    bool LoopFilterAcrossSlicesEnabled,
    bool DeblockingFilterOverrideEnabled,
    bool DeblockingFilterDisabled,
    int BetaOffsetDiv2,
    int TcOffsetDiv2,
    bool ListsModificationPresent,
    int Log2ParallelMergeLevelMinus2,
    bool SliceSegmentHeaderExtensionPresent)
{
    public static bool TryParse(ReadOnlySpan<byte> nalUnit, out HevcPictureParameterSet? parameterSet)
    {
        parameterSet = null;
        if (HevcNalType.Of(nalUnit) != HevcNalType.PictureParameterSet)
        {
            return false;
        }

        try
        {
            byte[] rbsp = HevcRbsp.Decode(nalUnit);
            HevcBitReader bits = new(rbsp);
            int id = ReadInt(ref bits);
            int spsId = ReadInt(ref bits);
            bool dependentSlices = bits.ReadFlag();
            bool outputFlagPresent = bits.ReadFlag();
            int extraHeaderBits = checked((int)bits.ReadBits(3));
            bool signDataHiding = bits.ReadFlag();
            bool cabacInitializationPresent = bits.ReadFlag();
            int defaultL0 = ReadInt(ref bits);
            int defaultL1 = ReadInt(ref bits);
            int initialQpMinus26 = bits.ReadSignedExpGolomb();
            bool constrainedIntra = bits.ReadFlag();
            bool transformSkip = bits.ReadFlag();
            bool cuQpDeltaEnabled = bits.ReadFlag();
            int diffCuQpDeltaDepth = cuQpDeltaEnabled ? ReadInt(ref bits) : 0;
            int cbQpOffset = bits.ReadSignedExpGolomb();
            int crQpOffset = bits.ReadSignedExpGolomb();
            bool sliceChromaOffsets = bits.ReadFlag();
            bool weightedPrediction = bits.ReadFlag();
            bool weightedBiPrediction = bits.ReadFlag();
            bool transquantBypass = bits.ReadFlag();
            bool tilesEnabled = bits.ReadFlag();
            bool entropyCodingSync = bits.ReadFlag();
            bool uniformSpacing = true;
            List<int> columnWidths = [];
            List<int> rowHeights = [];
            bool loopFilterAcrossTiles = true;
            if (tilesEnabled)
            {
                int columnCountMinus1 = ReadInt(ref bits);
                int rowCountMinus1 = ReadInt(ref bits);
                if (columnCountMinus1 > 19 || rowCountMinus1 > 21)
                {
                    return false;
                }

                uniformSpacing = bits.ReadFlag();
                if (!uniformSpacing)
                {
                    for (int column = 0; column < columnCountMinus1; column++)
                    {
                        columnWidths.Add(ReadInt(ref bits));
                    }

                    for (int row = 0; row < rowCountMinus1; row++)
                    {
                        rowHeights.Add(ReadInt(ref bits));
                    }
                }

                loopFilterAcrossTiles = bits.ReadFlag();
            }

            bool loopFilterAcrossSlices = bits.ReadFlag();
            bool deblockingOverride = false;
            bool deblockingDisabled = false;
            int betaOffsetDiv2 = 0;
            int tcOffsetDiv2 = 0;
            if (bits.ReadFlag())
            {
                deblockingOverride = bits.ReadFlag();
                deblockingDisabled = bits.ReadFlag();
                if (!deblockingDisabled)
                {
                    betaOffsetDiv2 = bits.ReadSignedExpGolomb();
                    tcOffsetDiv2 = bits.ReadSignedExpGolomb();
                }
            }

            if (bits.ReadFlag())
            {
                HevcSequenceParameterSet.SkipScalingList(ref bits);
            }

            bool listsModification = bits.ReadFlag();
            int log2ParallelMergeLevelMinus2 = ReadInt(ref bits);
            bool headerExtension = bits.ReadFlag();
            if (id > 63 || spsId > 15)
            {
                return false;
            }

            parameterSet = new HevcPictureParameterSet(
                id,
                spsId,
                dependentSlices,
                outputFlagPresent,
                extraHeaderBits,
                signDataHiding,
                cabacInitializationPresent,
                defaultL0,
                defaultL1,
                initialQpMinus26,
                constrainedIntra,
                transformSkip,
                cuQpDeltaEnabled,
                diffCuQpDeltaDepth,
                cbQpOffset,
                crQpOffset,
                sliceChromaOffsets,
                weightedPrediction,
                weightedBiPrediction,
                transquantBypass,
                tilesEnabled,
                entropyCodingSync,
                uniformSpacing,
                columnWidths,
                rowHeights,
                loopFilterAcrossTiles,
                loopFilterAcrossSlices,
                deblockingOverride,
                deblockingDisabled,
                betaOffsetDiv2,
                tcOffsetDiv2,
                listsModification,
                log2ParallelMergeLevelMinus2,
                headerExtension);
            return true;
        }
        catch (Exception exception) when (exception is HevcBitstreamException or OverflowException)
        {
            return false;
        }
    }

    private static int ReadInt(ref HevcBitReader bits) => checked((int)bits.ReadUnsignedExpGolomb());
}
