namespace Orchard.Mirror.Shell;

/// <summary>A rectangle in device-independent pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>Whether this box overlaps another at all.</summary>
    public bool Overlaps(Box other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

/// <summary>What a piece of text is for. Roles, never sizes, at the call site.</summary>
public enum TypeRole
{
    /// <summary>The small tracked-out word above a headline.</summary>
    Kicker,

    /// <summary>The headline.</summary>
    Title,

    /// <summary>Supporting paragraphs.</summary>
    Body,

    /// <summary>A button label.</summary>
    Button,

    /// <summary>The bold first line of a feature row.</summary>
    RowTitle,

    /// <summary>The quieter second line of a feature row.</summary>
    RowDetail,

    /// <summary>A short note over the live mirror.</summary>
    Toast,

    /// <summary>Secondary detail, smaller than body.</summary>
    Caption,
}

/// <summary>How one role is set.</summary>
/// <param name="SizeDip">Em size in device-independent pixels.</param>
/// <param name="LineHeightDip">Distance between baselines, in device-independent pixels.</param>
/// <param name="Bold">Whether the heavier weight is used.</param>
/// <param name="Display">
/// Whether to ask for the display optical size. Segoe UI Variable ships Display and Text as separate
/// optical sizes: Display is cut for headlines and reads too tight at body size.
/// </param>
/// <param name="TrackingDip">Extra advance between characters, for the tracked-out kicker.</param>
public readonly record struct TypeStyle(
    double SizeDip,
    double LineHeightDip,
    bool Bold,
    bool Display,
    double TrackingDip);

/// <summary>
/// One type scale, and the spacing that goes with it.
/// </summary>
/// <remarks>
/// <para>The sizes used to be five unrelated magic numbers — 9, 11.5, 12, 12.5 and 24 — three of
/// them within one point of each other and none derived from anything. They are now one modular
/// scale over a single base.</para>
/// <para><b>Spacing comes from this same object, and that is the point of it.</b> The old layout
/// sized type in points, which GDI+ scales by the surface's DPI, while every margin, medallion and
/// button height beside it was a raw device pixel. On a 150% display the text therefore came out
/// half again as large relative to the box it had to fit in — which is what made headlines wrap to
/// three lines, body text collapse to a single clipped line, and the whole page look wrong at a
/// scale nobody had tested. Type and space now scale together or not at all.</para>
/// <para>Everything here is in device-independent pixels. Converting to device pixels is the
/// renderer's single multiplication at the end, so DPI cannot be applied twice.</para>
/// </remarks>
public sealed class TypeScale
{
    /// <summary>The em size of body text at a scale factor of one.</summary>
    private const double BaseDip = 15.0;

    /// <summary>The ratio between adjacent steps.</summary>
    private const double Ratio = 1.25;

    /// <summary>The surface height the scale factor is measured against — a 390x844 phone window.</summary>
    private const double ReferenceHeightDip = 844.0;

    /// <summary>The width of that same reference window.</summary>
    private const double ReferenceWidthDip = 390.0;

    /// <summary>
    /// Build a scale for a surface of this height.
    /// </summary>
    /// <param name="surfaceHeightDip">
    /// The surface height in device-independent pixels. A phone-shaped window is resizable across a
    /// wide range, so type set at one fixed size is either enormous in a small window or lost in a
    /// large one. This is deliberately not the DPI: that is applied once, by the renderer.
    /// </param>
    public TypeScale(double surfaceHeightDip)
        : this(surfaceHeightDip, 0)
    {
    }

    /// <summary>
    /// Build a scale for a surface of this height and width.
    /// </summary>
    /// <remarks>
    /// <para>Height alone is not enough, and the window this draws into is resizable in both
    /// directions. A narrow window has a narrow column, and the headline is the one thing on the
    /// page sized to fill it; scaling type by height only meant that at the smallest size the window
    /// allows, "Opening secure iPhone tunnel" was set too large for a column that could not hold the
    /// word "Opening", and <see cref="TextFlow"/> did the only thing left to it and broke the word
    /// in half.</para>
    /// <para>The smaller of the two ratios wins, so whichever dimension is the binding constraint is
    /// the one that decides. The floor came down with it: 0.72 was chosen when only height mattered
    /// and is too high to rescue the narrowest window.</para>
    /// </remarks>
    /// <param name="surfaceHeightDip">The surface height in device-independent pixels.</param>
    /// <param name="surfaceWidthDip">The surface width, or 0 to size from height alone.</param>
    public TypeScale(double surfaceHeightDip, double surfaceWidthDip)
    {
        double byHeight = surfaceHeightDip / ReferenceHeightDip;
        double byWidth = surfaceWidthDip > 0 ? surfaceWidthDip / ReferenceWidthDip : byHeight;
        Factor = Math.Clamp(Math.Min(byHeight, byWidth), 0.62, 1.6);
    }

    /// <summary>How much larger or smaller than the reference this surface is.</summary>
    public double Factor { get; }

    /// <summary>The margin down each side of the content.</summary>
    public double MarginDip => Math.Round(26 * Factor);

    /// <summary>
    /// Where content starts, measured from the top of the surface. Clears the control strip, which
    /// sits across the top of the window and would otherwise cover the kicker.
    /// </summary>
    public double TopInsetDip => Math.Round(74 * Factor);

    /// <summary>The gap between the content and the window's bottom edge.</summary>
    public double EdgeGapDip => Math.Round(28 * Factor);

    /// <summary>
    /// The size of the mark above a page's title.
    /// </summary>
    /// <remarks>
    /// Was 72, when the mark sat inside a circle of glass that had to be big enough to hold it.
    /// Without the circle the mark is the only thing left, and at 72 it outweighed the headline
    /// underneath it. 34 puts it at roughly the same visual weight as one line of body copy, which
    /// is the weight a decoration should carry.
    /// </remarks>
    public double MedallionDip => Math.Round(34 * Factor);

    /// <summary>
    /// The diameter of the ring that turns while Orchard is working.
    /// </summary>
    /// <remarks>
    /// Its own size rather than a fraction of the mark beside it. It used to be three tenths of the
    /// medallion, which was invisible the moment the medallion stopped being 72 across.
    /// </remarks>
    public double SpinnerDip => Math.Round(20 * Factor);

    /// <summary>The height of a button.</summary>
    public double ButtonHeightDip => Math.Round(50 * Factor);

    /// <summary>The gap between stacked buttons.</summary>
    public double ButtonGapDip => Math.Round(8 * Factor);

    /// <summary>
    /// The square a feature row's mark is drawn in.
    /// </summary>
    /// <remarks>
    /// The numbers on this and the next two are the ones Apple's own welcome sheets use: a 37-point
    /// icon column, 17.5 points from it to the text, and 30 points between rows. They are unusually
    /// generous compared with anything else on this surface, and that generosity is most of what
    /// makes the pattern read the way it does.
    /// </remarks>
    public double RowIconDip => Math.Round(37 * Factor);

    /// <summary>The gap between a row's mark and its text.</summary>
    public double RowIconGapDip => Math.Round(17.5 * Factor);

    /// <summary>The gap between one feature row and the next.</summary>
    public double RowGapDip => Math.Round(30 * Factor);

    /// <summary>How one role is set on this surface.</summary>
    public TypeStyle For(TypeRole role)
    {
        (double steps, bool bold, double lineRatio, double tracking) = role switch
        {
            // Three and a half steps lands on 32.7 against a 15 base, which is where Apple's
            // largeTitle sits relative to its own 17-point body. Set tight: display type needs less
            // leading than body, not more.
            TypeRole.Title => (3.5, true, 1.16, 0.0),
            TypeRole.Body => (0.0, false, 1.45, 0.0),

            // Half a step up from body, the ratio Apple's headline keeps over its subheadline.
            TypeRole.Button => (0.5, true, 1.2, 0.0),
            TypeRole.RowTitle => (0.5, true, 1.3, 0.0),
            TypeRole.RowDetail => (0.0, false, 1.35, 0.0),
            TypeRole.Toast => (-1.0, false, 1.3, 0.0),
            TypeRole.Caption => (-1.0, false, 1.35, 0.0),

            // Two steps down and tracked out. 0.16em was a landing-page amount of tracking, wide
            // enough that the word stopped reading as a word; 0.09 is a section label.
            _ => (-2.0, true, 1.2, 0.09),
        };

        double size = BaseDip * Math.Pow(Ratio, steps) * Factor;
        return new TypeStyle(
            SizeDip: size,
            LineHeightDip: size * lineRatio,
            Bold: bold,
            // The two optical sizes cross over around here: anything headline-sized takes Display.
            Display: size >= 20 * Factor,
            TrackingDip: size * tracking);
    }

    /// <summary>The space between two stacked pieces of content.</summary>
    public double GapAfter(TypeRole role) => role switch
    {
        TypeRole.Kicker => Math.Round(6 * Factor),
        TypeRole.Title => Math.Round(14 * Factor),
        _ => Math.Round(20 * Factor),
    };
}
