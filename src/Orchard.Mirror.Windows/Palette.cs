namespace Orchard.Mirror.Windows;

/// <summary>
/// Every colour the window's own surfaces use, in one table.
/// </summary>
/// <remarks>
/// <para>The setup surface and the control strip each carried a private copy of what were supposed
/// to be the same greys, and they had already drifted apart: two secondary inks, two panel darks,
/// two near-blacks. Naming them once is the only arrangement that stays true.</para>
/// <para><b>The surface is flat and neutral on purpose.</b> What was here before was a field of four
/// saturated lobes drifting behind everything — two of them violet, one of them exactly
/// <c>#A855F7</c> — rendered into a small bitmap and scaled up on every frame. Two objections, and
/// the second is the one that mattered. It cost a full-surface composite per frame to say nothing.
/// And a violet-to-teal wash is the single most recognisable mark of an interface that was
/// decorated rather than designed, which is a poor first impression for a tool whose whole claim is
/// that it does something difficult correctly.</para>
/// <para>Colour now appears in exactly three places: the primary action, and the two states that
/// have to be distinguishable from ordinary progress at a glance. Everywhere else is grey.</para>
/// </remarks>
internal static class Palette
{
    /// <summary>The surface everything is drawn on.</summary>
    internal static readonly Color Surface = Color.FromArgb(16, 18, 22);

    /// <summary>
    /// The top of the surface's falloff. Two and a half percent lighter than <see cref="Surface"/>,
    /// which is enough to keep a tall window from reading as a flat void and far too little to be
    /// noticed as a gradient.
    /// </summary>
    internal static readonly Color SurfaceLift = Color.FromArgb(26, 29, 35);

    /// <summary>The control strip, which sits over live video and so is opaque.</summary>
    internal static readonly Color Panel = Color.FromArgb(22, 25, 31);

    /// <summary>Headlines and anything that must be read first.</summary>
    internal static readonly Color TextPrimary = Color.FromArgb(242, 244, 248);

    /// <summary>Body copy. Around 8:1 against <see cref="Surface"/>.</summary>
    internal static readonly Color TextSecondary = Color.FromArgb(154, 163, 178);

    /// <summary>Labels and captions, the quietest ink that still passes for body text at this size.</summary>
    internal static readonly Color TextTertiary = Color.FromArgb(124, 133, 148);

    /// <summary>The primary action. Apple's system blue, because the thing on screen is an iPhone.</summary>
    internal static readonly Color Accent = Color.FromArgb(10, 132, 255);

    /// <summary>The primary action under the pointer.</summary>
    internal static readonly Color AccentHot = Color.FromArgb(48, 150, 255);

    /// <summary>The primary action while it is held.</summary>
    internal static readonly Color AccentDown = Color.FromArgb(7, 106, 208);

    /// <summary>Working, but not as intended.</summary>
    internal static readonly Color Warning = Color.FromArgb(240, 188, 96);

    /// <summary>Stopped.</summary>
    internal static readonly Color Failure = Color.FromArgb(255, 105, 97);

    /// <summary>A separating line, or the edge of a panel.</summary>
    internal static readonly Color Hairline = Color.FromArgb(24, 255, 255, 255);

    /// <summary>The same line where it has to be seen against video rather than against the surface.</summary>
    internal static readonly Color HairlineStrong = Color.FromArgb(36, 255, 255, 255);

    /// <summary>The wash under a hovered control that has no fill of its own.</summary>
    internal static readonly Color HoverWash = Color.FromArgb(20, 255, 255, 255);
}
