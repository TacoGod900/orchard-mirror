using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

internal static class HevcParameterSetTests
{
    // One 128x96 Main-profile intra picture produced by x265. The values asserted below were read
    // independently with ffprobe; keeping only the small VPS/SPS/PPS NALs avoids a codec fixture.
    private static readonly byte[] SequenceParameterSet = Convert.FromHexString(
        "4201010408000003009FA800000300001EA010206165BA924CAF0168080000030008000003000840");

    private static readonly byte[] PictureParameterSet = Convert.FromHexString("4401C172B02240");

    // Three x265 sequence parameter sets that differ only in what they say about colour. ffprobe
    // read each one back independently: tv/bt709, pc/bt709, and tv/gbr respectively. The first two
    // differ in exactly one bit -- video_full_range_flag -- which is the bit the presenter needs in
    // order to stop stretching studio-range luma as though it were full range.
    private static readonly byte[] LimitedRangeBt709 = Convert.FromHexString(
        "42010101600000030090000003000003001EA010206165959A4932BC05A80808082000000300200000030321");

    private static readonly byte[] FullRangeBt709 = Convert.FromHexString(
        "42010101600000030090000003000003001EA010206165959A4932BC05B80808082000000300200000030321");

    private static readonly byte[] LimitedRangeIdentityMatrix = Convert.FromHexString(
        "42010101600000030090000003000003001EA010206165959A4932BC05A8000003002000000300200000030321");

    internal static void ReadsWhatTheStreamSaysAboutColourRange()
    {
        Assert(
            HevcSequenceParameterSet.TryParse(LimitedRangeBt709, out HevcSequenceParameterSet? limitedResult),
            "The limited-range sequence parameter set did not parse.");
        HevcSequenceParameterSet limited = limitedResult
            ?? throw new InvalidOperationException("The parser returned true without an SPS.");
        Assert(!limited.VideoFullRange, "A stream ffprobe calls tv-range was read as full range.");
        Assert(limited.MatrixCoefficients == 1, $"Matrix coefficients were {limited.MatrixCoefficients}, expected BT.709 (1).");

        Assert(
            HevcSequenceParameterSet.TryParse(FullRangeBt709, out HevcSequenceParameterSet? fullResult),
            "The full-range sequence parameter set did not parse.");
        HevcSequenceParameterSet full = fullResult
            ?? throw new InvalidOperationException("The parser returned true without an SPS.");
        Assert(full.VideoFullRange, "A stream ffprobe calls pc-range was read as limited range.");
        Assert(full.MatrixCoefficients == 1, $"Matrix coefficients were {full.MatrixCoefficients}, expected BT.709 (1).");
    }

    /// <summary>
    /// A stream that says nothing about its colour is limited-range by inference, never full range:
    /// H.265 specifies the fallback, and reading it the other way is what washes a picture out.
    /// </summary>
    internal static void AssumesLimitedRangeWhenTheStreamSaysNothing()
    {
        Assert(
            HevcSequenceParameterSet.TryParse(SequenceParameterSet, out HevcSequenceParameterSet? result),
            "The x265 sequence parameter set did not parse.");
        HevcSequenceParameterSet sps = result
            ?? throw new InvalidOperationException("The parser returned true without an SPS.");

        Assert(!sps.VideoFullRange, "A stream that describes no range was read as full range.");
        Assert(
            sps.MatrixCoefficients == HevcSequenceParameterSet.UnspecifiedMatrixCoefficients,
            $"An undescribed matrix was reported as {sps.MatrixCoefficients} rather than unspecified.");
    }

    internal static void ReadsAMatrixThatIsNotBt709()
    {
        Assert(
            HevcSequenceParameterSet.TryParse(LimitedRangeIdentityMatrix, out HevcSequenceParameterSet? result),
            "The identity-matrix sequence parameter set did not parse.");
        HevcSequenceParameterSet sps = result
            ?? throw new InvalidOperationException("The parser returned true without an SPS.");

        Assert(!sps.VideoFullRange, "A stream ffprobe calls tv-range was read as full range.");
        Assert(sps.MatrixCoefficients == 0, $"Matrix coefficients were {sps.MatrixCoefficients}, expected GBR (0).");
    }

    internal static void ParsesSequenceDimensionsAndDxvaFields()
    {
        Assert(
            HevcSequenceParameterSet.TryParse(SequenceParameterSet, out HevcSequenceParameterSet? result),
            "A valid x265 sequence parameter set did not parse.");
        HevcSequenceParameterSet sps = result
            ?? throw new InvalidOperationException("The parser returned true without an SPS.");

        Assert(sps.Id == 0, $"SPS id was {sps.Id}, expected 0.");
        Assert(sps.Width == 128 && sps.Height == 96, $"SPS dimensions were {sps.Width}x{sps.Height}.");
        Assert(sps.ChromaFormat == 1, $"Chroma format was {sps.ChromaFormat}, expected 4:2:0 (1).");
        Assert(sps.BitDepthLumaMinus8 == 0 && sps.BitDepthChromaMinus8 == 0, "The 8-bit Main profile was not recognized.");
        Assert(sps.Log2MaxPictureOrderCountLsbMinus4 == 4, "The POC-LSB width was parsed incorrectly.");
        Assert(sps.SampleAdaptiveOffsetEnabled, "The SPS sample-adaptive-offset flag was lost.");
    }

    internal static void ParsesPictureCodingAndFilterFlags()
    {
        Assert(
            HevcPictureParameterSet.TryParse(PictureParameterSet, out HevcPictureParameterSet? result),
            "A valid x265 picture parameter set did not parse.");
        HevcPictureParameterSet pps = result
            ?? throw new InvalidOperationException("The parser returned true without a PPS.");

        Assert(pps.Id == 0 && pps.SequenceParameterSetId == 0, "The PPS/SPS identifiers were wrong.");
        Assert(pps.InitialQuantizationParameterMinus26 == 0, $"Initial QP delta was {pps.InitialQuantizationParameterMinus26}.");
        Assert(!pps.TilesEnabled, "The single-slice fixture unexpectedly enabled tiles.");
        Assert(!pps.DeblockingFilterDisabled, "The fixture's deblocking filter was marked disabled.");
    }

    internal static void RejectsTruncatedParameterSets()
    {
        Assert(!HevcSequenceParameterSet.TryParse(SequenceParameterSet.AsSpan(0, 8), out _), "A truncated SPS was accepted.");
        Assert(!HevcPictureParameterSet.TryParse(PictureParameterSet.AsSpan(0, 3), out _), "A truncated PPS was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
