using System.Runtime.InteropServices;
using Orchard.Mirror.Media;
using Orchard.Mirror.Video.Windows;

namespace Orchard.Mirror.Tests;

internal static partial class DxgiFlipPresenterTests
{
    /// <summary>
    /// D3D11_VIDEO_PROCESSOR_COLOR_SPACE is a packed bitfield, so a wrong shift is silent: the
    /// driver just converts the picture differently. The bit positions asserted here come from the
    /// struct's documented field order -- Usage, RGB_Range, YCbCr_Matrix, YCbCr_xvYCC, then a
    /// two-bit Nominal_Range -- not from the code that builds them.
    /// </summary>
    internal static void PacksTheColourSpaceTheWayDirect3DReadsIt()
    {
        const int usage = 1 << 0;
        const int rgbRangeLimited = 1 << 1;
        const int matrixBt709 = 1 << 2;
        const int xvYcc = 1 << 3;
        const int nominalRangeStudio = 1 << 4;
        const int nominalRangeFull = 2 << 4;

        uint limited709 = DxgiFlipPresenter.StreamColourSpace(videoFullRange: false, matrixCoefficients: 1);
        Assert(
            limited709 == (matrixBt709 | nominalRangeStudio),
            $"Limited-range BT.709 packed as 0x{limited709:X}, expected 0x{matrixBt709 | nominalRangeStudio:X}.");

        uint full709 = DxgiFlipPresenter.StreamColourSpace(videoFullRange: true, matrixCoefficients: 1);
        Assert(
            full709 == (matrixBt709 | nominalRangeFull),
            $"Full-range BT.709 packed as 0x{full709:X}, expected 0x{matrixBt709 | nominalRangeFull:X}.");

        // matrix_coeffs 5 and 6 are the BT.601 family; the matrix bit must clear, and only it.
        uint limited601 = DxgiFlipPresenter.StreamColourSpace(videoFullRange: false, matrixCoefficients: 6);
        Assert(
            limited601 == nominalRangeStudio,
            $"Limited-range BT.601 packed as 0x{limited601:X}, expected 0x{nominalRangeStudio:X}.");

        Assert((limited709 & usage) == 0, "The playback usage bit was set.");
        Assert((limited709 & rgbRangeLimited) == 0, "A YUV input was described with a limited RGB range.");
        Assert((limited709 & xvYcc) == 0, "The xvYCC bit was set for a conventional stream.");

        // An unspecified matrix must not silently mean BT.601: everything this presenter will ever
        // see is HD or larger, where BT.709 is the only sane reading.
        Assert(
            DxgiFlipPresenter.StreamColourSpace(videoFullRange: false, matrixCoefficients: 2) == limited709,
            "An unspecified matrix was not treated as BT.709.");
    }

    /// <summary>
    /// The bug this guards: with no colour space set at all, the driver on this machine passed
    /// studio-range luma straight through, so Y=16 reached the screen as RGB 16 and Y=235 as 235 --
    /// lifted blacks and dulled whites. Describing the stream correctly must map 16 to 0 and 235 to
    /// 255, and that is a property of the picture, not of any particular interop call.
    /// </summary>
    internal static void StretchesStudioRangeLumaToTheFullDisplayRange()
    {
        using D3D11HevcDecoder? decoder = D3D11HevcDecoder.TryCreate(1280, 720, surfaceCount: 16);
        if (decoder is null)
        {
            throw new Program.SkipException(
                "No hardware D3D11 HEVC Main decoder with NV12 output is available in this process.");
        }

        nint window = CreateWindowExW(0, "STATIC", "Orchard Mirror colour test", 0, 0, 0, 1280, 720, 0, 0, 0, 0);
        if (window == 0)
        {
            throw new Program.SkipException("A test HWND could not be created in this process.");
        }

        try
        {
            HevcIdrSubmissionBuilder builder = new(surfaceCount: 16);
            Assert(
                builder.TryBuild(StudioRangeBandsFixture(), 0, out HevcDecodeSubmission? result),
                "The studio-range band fixture produced no decoder submission.");
            HevcDecodeSubmission submission = result
                ?? throw new InvalidOperationException("The builder returned true without a submission.");

            Assert(!submission.VideoFullRange, "The tv-range fixture reached the presenter as full range.");
            Assert(submission.MatrixCoefficients == 1, "The fixture's BT.709 matrix did not reach the presenter.");

            decoder.Decode(submission, submission.OutputSurfaceIndex);

            // Presented 1:1, so nothing but the colour conversion can move these values.
            using DxgiFlipPresenter? presenter = DxgiFlipPresenter.TryCreate(window, decoder, 1280, 720, submission);
            if (presenter is null)
            {
                throw new Program.SkipException("DXGI could not create a flip-model swap chain for this process.");
            }

            byte[] bgra = new byte[checked(1280 * 720 * 4)];
            Assert(
                presenter.TryPresent(submission.OutputSurfaceIndex, 1000, bgraOutput: bgra),
                "The band fixture timed out before presentation.");

            int black = Green(bgra, 1280, x: 640, y: 100);
            int white = Green(bgra, 1280, x: 640, y: 600);

            // The fixture's bands decode to exactly Y=16 and Y=235; ffprobe reads the stream back as
            // tv-range BT.709. Rendered honestly they are black and white, not two greys.
            Assert(black <= 4, $"Studio-range black (Y=16) reached the display as {black}, not black.");
            Assert(white >= 251, $"Studio-range white (Y=235) reached the display as {white}, not white.");
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    private static int Green(byte[] bgra, int width, int x, int y) => bgra[(((y * width) + x) * 4) + 1];

    // One 1280x720 x265 intra picture: the top half RGB black, the bottom half RGB white, encoded
    // as tv-range BT.709. ffprobe reads the stream back as tv/bt709 and ffmpeg decodes the bands to
    // exactly Y=16 and Y=235, so what the display should show is not a matter of opinion.
    private static HevcAccessUnit StudioRangeBandsFixture() => new(
        0,
        [
            Convert.FromHexString(
                "4201010408000003009FA800000300005DA00280802D165BA924CAF016A020202080000003008000000C84"),
            Convert.FromHexString("4401C1718312"),
            Convert.FromHexString(BandsSlice),
        ],
        true);

    private const string BandsSlice =
        "2801AF05718788EDC3566F58B260C18450E0F52FFB8C46CE93CBAA5A27A765046F8B1C80000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000004EC00AEDEDAA800000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300009180000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003001BB0000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003001C3000000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300005FC0012050C0E9DBFB2DCA8E47F94F20000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003001B5073800000030000030000030000030000030000030000030000030000030000030000030000030000030000030000030003EE00000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300CA80000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000004BC00000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300000BE800000300000300000300000300000300000300000300000300000300000300000300000300000300000300000300001750000003000003000003000003000003000003000003000003000003000003000003000003000003000003000003000074C0";

    internal static void DecodesAndPresentsThroughAWaitableFlipSwapChain()
    {
        using D3D11HevcDecoder? decoder = D3D11HevcDecoder.TryCreate(1280, 720, surfaceCount: 16);
        if (decoder is null)
        {
            throw new Program.SkipException(
                "No hardware D3D11 HEVC Main decoder with NV12 output is available in this process.");
        }

        nint window = CreateWindowExW(
            0,
            "STATIC",
            "Orchard Mirror DXGI smoke test",
            0,
            0,
            0,
            390,
            844,
            0,
            0,
            0,
            0);
        if (window == 0)
        {
            throw new Program.SkipException("A test HWND could not be created in this process.");
        }

        try
        {
            HevcIdrSubmissionBuilder builder = new(surfaceCount: 16);
            using DxgiFlipPresenter? presenter = DxgiFlipPresenter.TryCreate(window, decoder, 390, 844);
            if (presenter is null)
            {
                throw new Program.SkipException(
                    "DXGI could not create an NV12 flip-model swap chain for this process.");
            }

            Assert(presenter.FrameLatencyWaitableObject != 0, "Swap chain exposed no frame-latency waitable object.");
            byte[] bgra = new byte[checked(presenter.OutputWidth * presenter.OutputHeight * 4)];
            for (int frame = 0; frame < 32; frame++)
            {
                int surface = frame % decoder.SurfaceCount;
                Assert(
                    builder.TryBuild(
                        HevcIdrSubmissionBuilderTests.Fixture(),
                        surface,
                        out HevcDecodeSubmission? submission),
                    $"Presentation fixture {frame} produced no decoder submission.");
                decoder.Decode(
                    submission ?? throw new InvalidOperationException("The builder returned true without a submission."),
                    surface);
                Assert(
                    presenter.TryPresent(surface, 1000, bgraOutput: bgra),
                    $"NV12 frame {frame} on decoder surface {surface} timed out before presentation.");
            }

            Assert(bgra.Any(value => value != 0), "The scaled BGRA readback was empty.");
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
