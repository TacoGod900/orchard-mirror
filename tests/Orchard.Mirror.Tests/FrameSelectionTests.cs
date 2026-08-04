using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The geometry behind grabbing text off the phone: a box dragged over the mirror becomes a region
/// of the decoded frame to hand to OCR. It is pure arithmetic on purpose, so the corner ordering,
/// the edge clamping and the click-versus-drag threshold are a table of cases here rather than
/// something only a running device and a GPU could exercise.
/// </summary>
internal static class FrameSelectionTests
{
    /// <summary>A drag over a window that is the frame's exact size passes straight through.</summary>
    internal static void OneToOneWindowMapsUnchanged()
    {
        FrameRegion region = FrameSelection.Map(
            10, 20, 110, 220, clientWidth: 400, clientHeight: 800, frameWidth: 400, frameHeight: 800);

        Assert(region.Valid, "a real drag was rejected");
        Assert(
            region.Rect == new PixelRect(10, 20, 100, 200),
            $"a 1:1 window mapped to {region.Rect}");
    }

    /// <summary>
    /// A window smaller than the frame scales the selection up to frame pixels.
    /// </summary>
    /// <remarks>
    /// This is the ordinary case — the mirror window is almost always smaller than the phone's
    /// coded resolution — and getting it wrong is the difference between OCR reading the text the
    /// owner boxed and reading whatever sits a few hundred pixels away.
    /// </remarks>
    internal static void ASmallerWindowScalesUpToFramePixels()
    {
        // Window is half the frame each way, so every coordinate doubles.
        FrameRegion region = FrameSelection.Map(
            50, 100, 150, 200, clientWidth: 200, clientHeight: 400, frameWidth: 400, frameHeight: 800);

        Assert(region.Valid, "a scaled drag was rejected");
        Assert(
            region.Rect == new PixelRect(100, 200, 200, 200),
            $"a half-size window mapped to {region.Rect}");
    }

    /// <summary>A backwards drag is the same rectangle as a forwards one.</summary>
    /// <remarks>
    /// People box text from whatever corner is nearest. Without ordering the corners first, a drag
    /// that ends above-left of where it started would produce a negative width and read nothing —
    /// and it would do it only sometimes, which is the worst kind of bug to find on a device.
    /// </remarks>
    internal static void ABackwardsDragIsTheSameRectangle()
    {
        FrameRegion forward = FrameSelection.Map(
            10, 20, 110, 220, 400, 800, 400, 800);
        FrameRegion backward = FrameSelection.Map(
            110, 220, 10, 20, 400, 800, 400, 800);

        Assert(
            forward.Rect == backward.Rect,
            $"a backwards drag {backward.Rect} differed from the forwards one {forward.Rect}");
        Assert(backward.Valid, "a backwards drag was rejected");
    }

    /// <summary>A drag that runs off the window selects up to the edge, not past it.</summary>
    internal static void ADragOffTheEdgeClampsToTheFrame()
    {
        FrameRegion region = FrameSelection.Map(
            -50, -30, 500, 900, clientWidth: 400, clientHeight: 800, frameWidth: 400, frameHeight: 800);

        Assert(region.Valid, "a full-window drag was rejected");
        Assert(region.Rect.X == 0 && region.Rect.Y == 0, $"the origin was not clamped: {region.Rect}");
        Assert(
            region.Rect.Right <= 400 && region.Rect.Bottom <= 800,
            $"the region ran off the frame: right {region.Rect.Right}, bottom {region.Rect.Bottom}");
    }

    /// <summary>A right or bottom edge selection never lands one pixel past the frame.</summary>
    /// <remarks>
    /// The subtle one: rounding a coordinate that sits exactly on the window's edge can compute one
    /// pixel past the last row or column, and a GPU map for read of a region that extends past the
    /// texture is a hard failure, not a soft one. So the far edges are clamped after scaling.
    /// </remarks>
    internal static void TheFarEdgesStayInsideTheFrame()
    {
        FrameRegion region = FrameSelection.Map(
            0, 0, 400, 800, clientWidth: 400, clientHeight: 800, frameWidth: 1170, frameHeight: 2532);

        Assert(region.Rect.Right <= 1170, $"right edge {region.Rect.Right} exceeded the frame width");
        Assert(region.Rect.Bottom <= 2532, $"bottom edge {region.Rect.Bottom} exceeded the frame height");
    }

    /// <summary>A click that never became a drag is not a selection.</summary>
    /// <remarks>
    /// Entering grab mode and clicking once, or a press that twitches a pixel on release, must do
    /// nothing — not run OCR over a sliver and report that the phone had no text on it, which reads
    /// as the feature being broken rather than as nothing having been selected.
    /// </remarks>
    internal static void ABareClickIsNotASelection()
    {
        FrameRegion click = FrameSelection.Map(
            200, 400, 200, 400, 400, 800, 400, 800);
        Assert(!click.Valid, "a bare click was treated as a selection");

        FrameRegion twitch = FrameSelection.Map(
            200, 400, 202, 401, 400, 800, 400, 800);
        Assert(!twitch.Valid, "a two-pixel twitch was treated as a selection");
    }

    /// <summary>A short but deliberate word-sized box is a selection.</summary>
    /// <remarks>The other side of the threshold: the smallest thing worth grabbing still counts.</remarks>
    internal static void AWordSizedBoxIsASelection()
    {
        FrameRegion region = FrameSelection.Map(
            100, 100, 180, 130, 400, 800, 400, 800);
        Assert(region.Valid, "a word-sized selection was rejected as a click");
    }

    /// <summary>Degenerate window or frame sizes are refused rather than dividing by zero.</summary>
    internal static void DegenerateSizesAreRefused()
    {
        Assert(!FrameSelection.Map(0, 0, 10, 10, 0, 800, 400, 800).Valid, "a zero-width window was accepted");
        Assert(!FrameSelection.Map(0, 0, 10, 10, 400, 0, 400, 800).Valid, "a zero-height window was accepted");
        Assert(!FrameSelection.Map(0, 0, 10, 10, 400, 800, 0, 800).Valid, "a zero-width frame was accepted");
        Assert(!FrameSelection.Map(0, 0, 10, 10, 400, 800, 400, 0).Valid, "a zero-height frame was accepted");
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
