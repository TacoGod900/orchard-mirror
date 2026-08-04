namespace Orchard.Mirror.Shell;

/// <summary>Where the pointer is, and whether the strip is being used.</summary>
/// <param name="PointerInWindow">Whether the pointer is over the mirror window at all.</param>
/// <param name="PointerYFraction">
/// How far down the window the pointer is, from 0 at the top to 1 at the bottom. A fraction rather
/// than a pixel count so the reveal band means the same thing in a window of any size.
/// </param>
/// <param name="HoldOpen">
/// Whether something is actively using the controls -- the pointer resting on one, a control
/// holding focus, a menu open beneath one. Any of those outranks where the pointer happens to be.
/// </param>
/// <param name="Now">The current time.</param>
public readonly record struct StripInput(
    bool PointerInWindow,
    double PointerYFraction,
    bool HoldOpen,
    DateTimeOffset Now);

/// <summary>
/// Eases the control strip's opacity towards where <see cref="ControlStripPolicy"/> wants it.
/// </summary>
/// <remarks>
/// <para>Separate from the fade timer that drives it because the arithmetic is where this went
/// wrong: choosing the direction with <c>target &gt; current</c> means a strip already at 1 takes
/// the *downward* branch, drops to 0.86, is pulled back to 1 on the next tick, and strobes at ten
/// hertz for as long as it is on screen.</para>
/// <para>So a step towards the target never passes it, and being there already is a step of
/// nothing. Compare <see cref="HoverEasing"/>, which solves the same problem for the setup
/// surface.</para>
/// </remarks>
public static class StripFade
{
    /// <summary>
    /// How opaque the controls get. Deliberately short of 1, because WinForms drops WS_EX_LAYERED
    /// from a form whose <c>Opacity</c> is exactly 1 — measured: at 1.0 the style bit is gone, at
    /// 0.98 it is set with an alpha of 249. Landing on 1 would therefore add and remove a window
    /// style at the end of every fade, and a style change forces a non-client repaint. Stopping a
    /// sliver short keeps the window layered for the whole of its life, so the fade is only ever
    /// an alpha.
    /// </summary>
    public const double Shown = 0.98;

    /// <summary>How much opacity a single tick adds while the controls are arriving.</summary>
    public const double ArrivingStep = 0.22;

    /// <summary>How much a single tick removes while they are leaving, slower than arriving.</summary>
    public const double LeavingStep = 0.14;

    /// <summary>Move one tick towards the target, stopping exactly on it.</summary>
    public static double Step(double current, double target) => current < target
        ? Math.Min(target, current + ArrivingStep)
        : Math.Max(target, current - LeavingStep);

    /// <summary>Whether the fade has arrived and the timer has nothing left to do.</summary>
    public static bool Settled(double current, double target) => current == target;
}

/// <summary>
/// Decides when the window controls are on screen.
/// </summary>
/// <remarks>
/// <para>The window is borderless and always-on-top with no title bar, so before this there was no
/// visible way to disconnect, unpin, minimise or close it — the only disconnect was a Shift-Escape
/// shortcut documented on a screen you could not reach without already knowing the shortcut.</para>
/// <para>Reaching for the top of a window is the gesture that asks for its controls, so that is what
/// summons them. They linger briefly after the pointer leaves, because a control that vanishes
/// while you are travelling towards it cannot be clicked, and they never leave while one of them
/// holds focus, because a keyboard user has no pointer to hold them with.</para>
/// </remarks>
public sealed class ControlStripPolicy
{
    /// <summary>
    /// How far down the window still counts as reaching for the controls.
    /// </summary>
    /// <remarks>
    /// The strip itself covers roughly the top fourteenth of the window, so this is about a strip
    /// and a half: enough that a hand travelling towards the controls has already summoned them
    /// before it arrives, and little enough that using the top of the phone's own screen does not.
    /// It was 0.18 — a fifth of the picture, which on a phone-shaped window is most of the way down
    /// the notification shade, and the bar kept appearing over things the owner was reaching for.
    /// </remarks>
    public const double RevealBand = 0.10;

    /// <summary>How long the controls linger once the pointer leaves the band.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(1.2);

    private DateTimeOffset? lastInBand;

    /// <summary>Whether the controls should be on screen.</summary>
    public bool Shown { get; private set; }

    /// <summary>Fold in where the pointer is now, and say whether the controls belong on screen.</summary>
    public bool Update(StripInput input)
    {
        bool inBand = input.PointerInWindow
            && input.PointerYFraction >= 0
            && input.PointerYFraction <= RevealBand;

        if (inBand)
        {
            lastInBand = input.Now;
        }

        bool lingering = lastInBand is { } seen && input.Now - seen < Dwell;

        // Using a control outranks where the pointer is. Resting on a button puts the pointer
        // below the reveal band in a short window, and fading out from under a press -- or from
        // under a keyboard user, who has no pointer in the band at all -- makes them unusable.
        Shown = inBand || lingering || input.HoldOpen;
        return Shown;
    }
}
