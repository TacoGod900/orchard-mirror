namespace Orchard.Mirror.Shell;

/// <summary>A rectangle in decoded-frame pixels.</summary>
/// <param name="X">Left edge, in frame pixels from the left.</param>
/// <param name="Y">Top edge, in frame pixels from the top.</param>
/// <param name="Width">Width in frame pixels.</param>
/// <param name="Height">Height in frame pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>The right edge, one past the last column.</summary>
    public int Right => X + Width;

    /// <summary>The bottom edge, one past the last row.</summary>
    public int Bottom => Y + Height;

    /// <summary>Area in pixels, which is zero for a click that never became a drag.</summary>
    public long Area => (long)Width * Height;
}

/// <summary>Where a text grab will read from, and whether it is worth reading at all.</summary>
/// <param name="Rect">The region of the decoded frame to hand to OCR.</param>
/// <param name="Valid">
/// Whether the selection is a real region rather than a stray click or a collapsed drag. A false
/// here is the signal to do nothing quietly, not to OCR an empty strip.
/// </param>
public readonly record struct FrameRegion(PixelRect Rect, bool Valid);

/// <summary>
/// Turns a rectangle the owner dragged over the mirror window into the region of the decoded frame
/// it covers.
/// </summary>
/// <remarks>
/// <para>This is the whole of the geometry for grabbing text off the phone, kept away from the
/// window and the GPU so it can be a table of cases rather than something only a running device can
/// exercise. The window shows the presented frame scaled to fill its client area, so a box in
/// window pixels is the same box in frame pixels once it is scaled by the ratio between the two —
/// the same shape of mapping the touch layer already does the other way, from a click to a
/// touchscreen coordinate.</para>
/// <para>Two things it refuses to hand on. A drag from bottom-right to top-left is the same
/// rectangle as top-left to bottom-right, so corners are ordered before anything else; without that
/// a backwards drag would produce a negative size and read nothing. And a selection smaller than a
/// few frame pixels each way is a click that twitched, not a region — those are marked invalid so
/// the caller stays silent rather than running OCR over three pixels and reporting "no text
/// found".</para>
/// </remarks>
public static class FrameSelection
{
    /// <summary>
    /// The smallest a selection may be, in frame pixels each way, and still count as deliberate.
    /// </summary>
    /// <remarks>
    /// Small enough that grabbing a single short word still works, large enough that the jitter of a
    /// press-and-release with no real drag falls under it. The unit is frame pixels rather than
    /// window pixels so the threshold means the same thing whether the window is tiny or maximised.
    /// </remarks>
    public const int MinimumDimension = 6;

    /// <summary>Map a dragged rectangle in window pixels to a region of the decoded frame.</summary>
    /// <param name="startX">One corner's X, in window client pixels.</param>
    /// <param name="startY">One corner's Y, in window client pixels.</param>
    /// <param name="endX">The opposite corner's X, in window client pixels.</param>
    /// <param name="endY">The opposite corner's Y, in window client pixels.</param>
    /// <param name="clientWidth">The width of the area the frame is drawn across, in window pixels.</param>
    /// <param name="clientHeight">The height of the area the frame is drawn across, in window pixels.</param>
    /// <param name="frameWidth">The decoded frame's width in pixels.</param>
    /// <param name="frameHeight">The decoded frame's height in pixels.</param>
    public static FrameRegion Map(
        int startX,
        int startY,
        int endX,
        int endY,
        int clientWidth,
        int clientHeight,
        int frameWidth,
        int frameHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new FrameRegion(default, Valid: false);
        }

        // Order the corners first, so a drag in any direction is the same rectangle. Everything
        // after this can assume left <= right and top <= bottom.
        int left = Math.Min(startX, endX);
        int right = Math.Max(startX, endX);
        int top = Math.Min(startY, endY);
        int bottom = Math.Max(startY, endY);

        // Clamp to the window before scaling: a drag that runs off the edge of the mirror selects up
        // to the edge, not past it, and a start point outside the window does not push the region
        // negative.
        left = Math.Clamp(left, 0, clientWidth);
        right = Math.Clamp(right, 0, clientWidth);
        top = Math.Clamp(top, 0, clientHeight);
        bottom = Math.Clamp(bottom, 0, clientHeight);

        int frameLeft = Scale(left, clientWidth, frameWidth);
        int frameRight = Scale(right, clientWidth, frameWidth);
        int frameTop = Scale(top, clientHeight, frameHeight);
        int frameBottom = Scale(bottom, clientHeight, frameHeight);

        PixelRect rect = new(
            frameLeft,
            frameTop,
            frameRight - frameLeft,
            frameBottom - frameTop);

        bool valid = rect.Width >= MinimumDimension && rect.Height >= MinimumDimension;
        return new FrameRegion(rect, valid);
    }

    /// <summary>
    /// Scale one window coordinate onto the frame, rounded, and never off the far edge.
    /// </summary>
    /// <remarks>
    /// Done in <see cref="long"/> because a 4K-wide frame times a window coordinate overflows an
    /// <see cref="int"/> before the divide. The clamp to the frame width matters at the bottom and
    /// right edges: rounding a coordinate that sits exactly on the window's edge can land one pixel
    /// past the last row or column, which a map for read would reject.
    /// </remarks>
    private static int Scale(int value, int fromExtent, int toExtent)
    {
        long scaled = ((long)value * toExtent) + (fromExtent / 2);
        int result = (int)(scaled / fromExtent);
        return Math.Clamp(result, 0, toExtent);
    }
}
