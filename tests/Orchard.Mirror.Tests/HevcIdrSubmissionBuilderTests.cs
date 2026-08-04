using System.Buffers.Binary;
using Orchard.Mirror.Media;
using Orchard.Mirror.Video.Windows;

namespace Orchard.Mirror.Tests;

internal static class HevcIdrSubmissionBuilderTests
{
    private static readonly byte[] SequenceParameterSet = Convert.FromHexString(
        "4201010408000003009FA800000300005DA00280802D165BA924CAF016808000000300800000030084");
    private static readonly byte[] PictureParameterSet = Convert.FromHexString("4401C172B06240");
    private static readonly byte[] IdrSlice = Convert.FromHexString(
        "2801AF71868D95D65545134D34A0EBAEFBFFFC5B655F2B05C50C5BD70901BC0D116C00000300000300000300000300002E60A66535202881A2330000030000030000030000030000030001839304109B000003000003000003000003000003000003010B8700000300000300000300000300000300000300003A600000030000030000030000030000030000030000346000000300000300000300000300000300000300074C000003000003000003000003000003000003003C6000000300000300000300000300000300000300FF00000300000300000300000300000300000302B200000300000300000300000300000300000303F6000003000003000003000003000003000007DC000003000003000003000003000003000027A0");
    private static readonly byte[] SustainedSequenceParameterSet = Convert.FromHexString(
        "420101016000000300900000030000030078A00280802D165BA4A4C2F0168080000003008000001E04");
    private static readonly byte[] SustainedPictureParameterSet = Convert.FromHexString("4401C073C189");
    private static readonly byte[] SustainedIdrSlice = Convert.FromHexString(
        "2801AC290B966F735CD6B18B5AD6A525294A5298114FFDDD6986B540454EA00000030000030008D80AFCA7738C91F00000030000030001871CF42D9178000003000003000013901EFB22F00000030000030000060C2162F000000300000300000300F5801FF8000003000003000003001450000003000003000003000003017100000300000300000300000E18000003000003000003000059400000030000030000030001FE000003000003000003000BE80000030000030000030023A0000003000003000003006BC0000003000003000003011F000003000003000003030E000003000003000005CC00000300000300000B38000003000003000013D000000300000300001F50000003000003000030200000030000030000474000000300000300005F400000030000030000030000030000030000030072C0");
    private static readonly byte[] SustainedPSlice = Convert.FromHexString(
        "0201D0097881485DB492449249249249309280FE80001C100033E0005CC000988000E080015501DB0292035E0444051C065C072C085809480A880B380C080C880D480DC80644");

    internal static void SerializesAnIdrIntoTheWindowsDxvaContract()
    {
        HevcIdrSubmissionBuilder builder = new();
        HevcAccessUnit accessUnit = Fixture();

        Assert(builder.TryBuild(accessUnit, 1, out HevcDecodeSubmission? result), "The Main-profile IDR did not become a submission.");
        HevcDecodeSubmission submission = result
            ?? throw new InvalidOperationException("The builder returned true without a submission.");

        Assert(submission.PictureParameters.Length == 232, $"Picture parameters were {submission.PictureParameters.Length} bytes.");
        Assert(submission.PictureParameters.Span[6] == 1, "The selected output surface was not written to CurrPic.");
        Assert(submission.PictureParameters.Span.Slice(124, 15).ToArray().All(value => value == 0xFF), "IDR reference entries were not invalidated.");
        Assert(submission.InverseQuantizationMatrix.IsEmpty, "A stream without scaling lists emitted a matrix buffer.");
        Assert(submission.SliceControl.Length == 12, "The single IDR slice did not emit one short-slice entry.");
        Assert((submission.Bitstream.Length & 127) == 0, "The IDR bitstream was not padded to the driver boundary.");
    }

    internal static void SubmitsTheIdrToTheHardwareDecoder()
    {
        using D3D11HevcDecoder? decoder = D3D11HevcDecoder.TryCreate(1280, 720);
        if (decoder is null)
        {
            throw new Program.SkipException(
                "No hardware D3D11 HEVC Main decoder with NV12 output is available in this process.");
        }

        HevcIdrSubmissionBuilder builder = new();
        Assert(builder.TryBuild(Fixture(), 0, out HevcDecodeSubmission? result), "The hardware fixture produced no submission.");
        decoder.Decode(
            result ?? throw new InvalidOperationException("The builder returned true without a submission."),
            0);
    }

    internal static void SerializesAPictureReferenceIntoTheWindowsDxvaContract()
    {
        HevcIdrSubmissionBuilder builder = new();
        Assert(builder.TryBuild(SustainedIdrFixture(), 0, out _), "The synthetic IDR did not establish the DPB.");
        Assert(builder.TryBuild(SustainedPFixture(), 1, out HevcDecodeSubmission? result), "The following P picture did not become a submission.");
        HevcDecodeSubmission submission = result
            ?? throw new InvalidOperationException("The builder returned true without a P-picture submission.");

        ReadOnlySpan<byte> parameters = submission.PictureParameters.Span;
        Assert(submission.PictureOrderCount == 1, $"P-picture POC was {submission.PictureOrderCount}, expected 1.");
        Assert(parameters[124] == 0, "The P picture did not reference decoder surface 0.");
        Assert(BinaryPrimitives.ReadInt32LittleEndian(parameters[140..]) == 0, "The reference POC was not 0.");
        Assert(parameters[200] == 0, "The previous picture was not placed in RefPicSetStCurrBefore.");
        Assert((BinaryPrimitives.ReadUInt32LittleEndian(parameters[28..]) & (1u << 17)) == 0, "A P picture was marked as IDR.");
    }

    internal static void PreservesAReferencedSurfaceWhenSelectingOutput()
    {
        HevcIdrSubmissionBuilder builder = new(surfaceCount: 3);
        Assert(builder.TryBuild(SustainedIdrFixture(), 0, out _), "The synthetic IDR did not establish the DPB.");
        Assert(builder.TryBuild(SustainedPFixture(), 0, out HevcDecodeSubmission? result),
            $"The builder could not select around a referenced surface: {builder.LastFailureReason}.");
        HevcDecodeSubmission submission = result
            ?? throw new InvalidOperationException("The builder returned true without a P-picture submission.");

        Assert(submission.OutputSurfaceIndex != 0, "The P picture overwrote its referenced IDR surface.");
        Assert(submission.PictureParameters.Span[6] == submission.OutputSurfaceIndex,
            "CurrPic did not use the dynamically selected output surface.");
    }

    internal static void DecodesAnIdrAndFollowingPPicture()
    {
        using D3D11HevcDecoder? decoder = D3D11HevcDecoder.TryCreate(1280, 720);
        if (decoder is null)
        {
            throw new Program.SkipException(
                "No hardware D3D11 HEVC Main decoder with NV12 output is available in this process.");
        }

        HevcIdrSubmissionBuilder builder = new();
        Assert(builder.TryBuild(SustainedIdrFixture(), 0, out HevcDecodeSubmission? idr), "The sustained fixture produced no IDR submission.");
        decoder.Decode(idr ?? throw new InvalidOperationException("Missing IDR submission."), 0);
        Assert(builder.TryBuild(SustainedPFixture(), 1, out HevcDecodeSubmission? pPicture), "The sustained fixture produced no P submission.");
        decoder.Decode(pPicture ?? throw new InvalidOperationException("Missing P submission."), 1);
    }

    internal static HevcAccessUnit Fixture() => new(
        0,
        [SequenceParameterSet, PictureParameterSet, IdrSlice],
        true);

    private static HevcAccessUnit SustainedIdrFixture() => new(
        0,
        [SustainedSequenceParameterSet, SustainedPictureParameterSet, SustainedIdrSlice],
        true);

    private static HevcAccessUnit SustainedPFixture() => new(
        1,
        [SustainedPSlice],
        false);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
