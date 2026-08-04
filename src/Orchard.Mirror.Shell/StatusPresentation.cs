namespace Orchard.Mirror.Shell;

/// <summary>Where a status is allowed to appear.</summary>
/// <remarks>
/// This distinction is the fix for the worst of the flow complaints. Everything used to arrive on
/// the same full-surface panel, so locking the phone — which the phone keeps streaming through —
/// slammed a sheet of glass over a picture that was still perfectly good.
/// </remarks>
public enum StatusSurface
{
    /// <summary>Not shown. Worth recording, not worth interrupting for.</summary>
    None,

    /// <summary>A small note over the live mirror, which dismisses itself.</summary>
    Toast,

    /// <summary>The whole surface. Only when there is nothing worth looking at behind it.</summary>
    Full,
}

/// <summary>A named colour, resolved to an actual value by whoever is painting.</summary>
/// <remarks>Roles rather than colours, so the palette lives in one place instead of two that disagree.</remarks>
public enum ColorRole
{
    /// <summary>Ordinary headline text.</summary>
    TextPrimary,

    /// <summary>Supporting text.</summary>
    TextSecondary,

    /// <summary>Something is under way.</summary>
    Progress,

    /// <summary>Working, but not as intended.</summary>
    Warning,

    /// <summary>Stopped.</summary>
    Failure,
}

/// <summary>The mark drawn beside a status.</summary>
public enum GlyphRole
{
    /// <summary>A screen. The opening page.</summary>
    Display,

    /// <summary>A cable.</summary>
    Link,

    /// <summary>A switch being thrown — Orchard is doing something.</summary>
    Working,

    /// <summary>Done.</summary>
    Ready,

    /// <summary>A darkened screen.</summary>
    Asleep,

    /// <summary>Stopped on something the owner has to resolve.</summary>
    Blocked,
}

/// <summary>What the button under a status does, if there is one.</summary>
public enum StatusActionKind
{
    /// <summary>No button.</summary>
    None,

    /// <summary>Start connecting again. Never the first-run walkthrough.</summary>
    Reconnect,

    /// <summary>Press the phone's power button.</summary>
    Wake,
}

/// <summary>Everything needed to draw one status, decided in one place.</summary>
/// <param name="Surface">Where it may appear.</param>
/// <param name="Kicker">The small word above the headline.</param>
/// <param name="Headline">The headline. Never empty for a surface that shows one.</param>
/// <param name="Detail">Supporting text, or empty.</param>
/// <param name="Accent">The colour the headline takes.</param>
/// <param name="Spinner">Whether something is visibly turning.</param>
/// <param name="Glyph">The mark beside it.</param>
/// <param name="Action">The button under it.</param>
/// <param name="ActionLabel">What the button says, or empty when there is none.</param>
/// <param name="AutoDismiss">How long a toast holds before it fades. Null for surfaces that stay.</param>
/// <param name="Rows">
/// Feature rows under the detail text. Only the opening page has any: a routed status is one thing
/// that has happened, and a list of three is not a way of saying one thing.
/// </param>
public readonly record struct StatusPresentation(
    StatusSurface Surface,
    string Kicker,
    string Headline,
    string Detail,
    ColorRole Accent,
    bool Spinner,
    GlyphRole Glyph,
    StatusActionKind Action,
    string ActionLabel,
    TimeSpan? AutoDismiss,
    IReadOnlyList<FeatureRow>? Rows = null)
{
    /// <summary>The rows, never null, so callers do not each have to remember that they can be.</summary>
    public IReadOnlyList<FeatureRow> FeatureRows => Rows ?? [];
}
