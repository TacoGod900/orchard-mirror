using Orchard.Mirror.Video.Windows;

namespace Orchard.Mirror.Tests;

internal static class D3D11HevcDecoderTests
{
    internal static void CreatesDriverDecoderAndSixteenNv12Surfaces()
    {
        using D3D11HevcDecoder? decoder = D3D11HevcDecoder.TryCreate(1170, 2532, surfaceCount: 16);
        if (decoder is null)
        {
            throw new Program.SkipException(
                "No hardware D3D11 HEVC Main decoder with NV12 output is available in this process.");
        }

        Assert(decoder.OutputTexture != 0, "Decoder returned no NV12 output texture.");
        Assert(decoder.GetOutputView(0) != 0, "Decoder returned no first output view.");
        Assert(decoder.GetOutputView(15) != 0, "Decoder returned no sixteenth output view.");
        Assert(decoder.SurfaceCount == 16, $"Decoder created {decoder.SurfaceCount} surfaces instead of sixteen.");
        Assert(decoder.Width == 1170 && decoder.Height == 2532, "Decoder dimensions changed during creation.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
