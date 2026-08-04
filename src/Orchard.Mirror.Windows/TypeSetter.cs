using System.Drawing.Text;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Windows;

/// <summary>
/// Turns the device-independent type scale into real fonts, and owns the one place display scale is
/// applied.
/// </summary>
/// <remarks>
/// <para>The single multiplication by <see cref="DpiScale"/> is deliberately the only one. Sizing
/// type in points while laying it out in device pixels applied the display scale to the text and
/// not to the boxes around it, which is what made everything wrap and clip on a scaled display.
/// Fonts here are built with <see cref="GraphicsUnit.Pixel"/> from an already-scaled size, so the
/// text and its box are in the same units by construction.</para>
/// <para>Fonts are cached because these are GDI handles and this paints repeatedly; the cache is
/// keyed by the rounded pixel size so a live resize drag reuses entries instead of destroying and
/// rebuilding every handle on each <c>WM_SIZE</c>.</para>
/// </remarks>
internal sealed class TypeSetter : IDisposable
{
    /// <summary>
    /// Preferred families, most specific first, ending in one that is always present.
    /// </summary>
    /// <remarks>
    /// A missing family does not throw: GDI+ silently substitutes Microsoft Sans Serif, so asking
    /// for something absent is invisible at the call site and obvious on screen. "Segoe UI Variable"
    /// only exists from Windows 11 22000, and the previous code also asked for
    /// "Segoe UI Variable Text Semib", which is not a family name at all and could therefore never
    /// have resolved.
    /// </remarks>
    private static readonly string[] DisplayLadder =
        ["Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", "Tahoma"];

    private static readonly string[] TextLadder =
        ["Segoe UI Variable Text", "Segoe UI Variable Small", "Segoe UI", "Tahoma"];

    private readonly Dictionary<(int Pixels, bool Bold, bool Display), Font> cache = [];

    /// <summary>Device pixels per device-independent pixel, from the window's current DPI.</summary>
    internal double DpiScale { get; private set; } = 1.0;

    /// <summary>Point the setter at a new display scale, dropping anything sized for the old one.</summary>
    internal void SetDpi(double dpiScale)
    {
        double next = dpiScale <= 0 ? 1.0 : dpiScale;
        if (Math.Abs(next - DpiScale) < 0.001)
        {
            return;
        }

        DpiScale = next;
        Drop();
    }

    /// <summary>Convert a length in device-independent pixels to device pixels.</summary>
    internal float Px(double dip) => (float)(dip * DpiScale);

    /// <summary>Convert a box in device-independent pixels to a device-pixel rectangle.</summary>
    internal RectangleF Rect(Box box) => new(Px(box.X), Px(box.Y), Px(box.Width), Px(box.Height));

    /// <summary>The font for one style, at this display scale.</summary>
    internal Font Font(TypeStyle style)
    {
        int pixels = Math.Max(1, (int)Math.Round(style.SizeDip * DpiScale));
        (int, bool, bool) key = (pixels, style.Bold, style.Display);
        if (cache.TryGetValue(key, out Font? cached))
        {
            return cached;
        }

        FontFamily family = Resolve(style.Display);
        Font font = new(family, pixels, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        cache[key] = font;
        return font;
    }

    /// <summary>Measure a string the way it will be drawn, in device-independent pixels.</summary>
    internal MeasureText Measure(Graphics graphics, TypeStyle style)
    {
        Font font = Font(style);
        return text => TextRenderer.MeasureText(
            graphics,
            text,
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width / DpiScale;
    }

    public void Dispose() => Drop();

    /// <summary>
    /// Pick the first installed family on the ladder for this optical size.
    /// </summary>
    /// <remarks>
    /// Checked against the installed set rather than assumed, so the fallback is a decision this
    /// code makes rather than one GDI+ makes silently on a machine nobody tested.
    /// </remarks>
    private static FontFamily Resolve(bool display)
    {
        foreach (string candidate in display ? DisplayLadder : TextLadder)
        {
            foreach (FontFamily installed in FontFamily.Families)
            {
                if (installed.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return installed;
                }
            }
        }

        return FontFamily.GenericSansSerif;
    }

    private void Drop()
    {
        foreach (Font font in cache.Values)
        {
            font.Dispose();
        }

        cache.Clear();
    }
}
