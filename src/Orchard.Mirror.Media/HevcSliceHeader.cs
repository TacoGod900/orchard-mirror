namespace Orchard.Mirror.Media;

/// <summary>The portion of an HEVC slice header required to maintain the DXVA decoded-picture buffer.</summary>
public sealed record HevcSliceHeader(
    int PictureParameterSetId,
    int SliceType,
    int PictureOrderCountLsb,
    bool IsIdr,
    bool IsRandomAccessPoint,
    IReadOnlyList<HevcShortTermReference> ShortTermReferences,
    int ShortTermReferencePictureSetBitCount = 0,
    int ReferenceDeltaPictureOrderCountCount = 0)
{
    /// <summary>Parse the first independent slice of a picture through its short-term RPS.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> nalUnit,
        HevcPictureParameterSet pps,
        HevcSequenceParameterSet sps,
        out HevcSliceHeader? header)
    {
        ArgumentNullException.ThrowIfNull(pps);
        ArgumentNullException.ThrowIfNull(sps);
        header = null;
        int nalType = HevcNalType.Of(nalUnit);
        if (!HevcNalType.IsCodedSlice(nalType))
        {
            return false;
        }

        try
        {
            HevcBitReader bits = new(HevcRbsp.Decode(nalUnit));
            bool firstSliceSegment = bits.ReadFlag();
            if (HevcNalType.IsRandomAccessPoint(nalType))
            {
                _ = bits.ReadFlag();
            }

            int ppsId = ReadInt(ref bits);
            if (!firstSliceSegment || ppsId != pps.Id)
            {
                return false;
            }

            bits.SkipBits(pps.ExtraSliceHeaderBits);
            int sliceType = ReadInt(ref bits);
            if (sliceType is < 0 or > 2)
            {
                return false;
            }

            if (pps.OutputFlagPresent)
            {
                _ = bits.ReadFlag();
            }

            if (sps.SeparateColourPlane)
            {
                bits.SkipBits(2);
            }

            if (HevcNalType.IsIdr(nalType))
            {
                header = new HevcSliceHeader(ppsId, sliceType, 0, true, true, []);
                return true;
            }

            int pocLsb = checked((int)bits.ReadBits(sps.Log2MaxPictureOrderCountLsbMinus4 + 4));
            HevcShortTermReferencePictureSet referenceSet;
            int referencePictureSetBitCount = 0;
            if (!bits.ReadFlag())
            {
                int referencePictureSetStart = bits.Position;
                referenceSet = HevcSequenceParameterSet.ParseShortTermReferencePictureSet(
                    ref bits,
                    sps.ShortTermReferencePictureSets.Count,
                    sps.ShortTermReferencePictureSets.Count,
                    sps.ShortTermReferencePictureSets);
                referencePictureSetBitCount = bits.Position - referencePictureSetStart;
            }
            else if (sps.ShortTermReferencePictureSets.Count == 0)
            {
                referenceSet = new HevcShortTermReferencePictureSet([]);
            }
            else
            {
                int index = sps.ShortTermReferencePictureSets.Count == 1
                    ? 0
                    : checked((int)bits.ReadBits(CeilLog2(sps.ShortTermReferencePictureSets.Count)));
                if ((uint)index >= (uint)sps.ShortTermReferencePictureSets.Count)
                {
                    return false;
                }

                referenceSet = sps.ShortTermReferencePictureSets[index];
            }

            // Long-term references need a larger DPB than Orchard's low-latency two-surface
            // contract. Reject them explicitly instead of producing an incomplete DXVA RPS.
            if (sps.LongTermReferencesPresent)
            {
                return false;
            }

            header = new HevcSliceHeader(
                ppsId,
                sliceType,
                pocLsb,
                false,
                HevcNalType.IsRandomAccessPoint(nalType),
                referenceSet.References,
                referencePictureSetBitCount,
                referenceSet.ReferenceDeltaPictureOrderCountCount);
            return true;
        }
        catch (Exception exception) when (exception is HevcBitstreamException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Read the PPS id without requiring cached parameter sets.</summary>
    public static bool TryReadPictureParameterSetId(ReadOnlySpan<byte> nalUnit, out int pictureParameterSetId)
    {
        pictureParameterSetId = 0;
        int nalType = HevcNalType.Of(nalUnit);
        if (!HevcNalType.IsCodedSlice(nalType))
        {
            return false;
        }

        try
        {
            HevcBitReader bits = new(HevcRbsp.Decode(nalUnit));
            _ = bits.ReadFlag();
            if (HevcNalType.IsRandomAccessPoint(nalType))
            {
                _ = bits.ReadFlag();
            }

            pictureParameterSetId = ReadInt(ref bits);
            return pictureParameterSetId <= 63;
        }
        catch (Exception exception) when (exception is HevcBitstreamException or OverflowException)
        {
            return false;
        }
    }

    private static int CeilLog2(int value)
    {
        int bits = 0;
        for (int remaining = value - 1; remaining > 0; remaining >>= 1)
        {
            bits++;
        }

        return bits;
    }

    private static int ReadInt(ref HevcBitReader bits) => checked((int)bits.ReadUnsignedExpGolomb());
}
