namespace Orchard.Mirror.Shell;

/// <summary>Where a toast has got to in its short life.</summary>
public enum ToastPhase
{
    /// <summary>Not on screen, and holding nothing.</summary>
    Hidden,

    /// <summary>Fading in.</summary>
    Arriving,

    /// <summary>Fully visible and being read.</summary>
    Holding,

    /// <summary>Fading out.</summary>
    Leaving,
}

/// <summary>What the window should draw this tick.</summary>
/// <param name="Phase">Where the toast has got to.</param>
/// <param name="Opacity">How opaque the window should be, from 0 to <see cref="ToastPolicy.Shown"/>.</param>
/// <param name="Content">What to draw. Meaningless when <paramref name="Phase"/> is hidden.</param>
public readonly record struct ToastFrame(ToastPhase Phase, double Opacity, StatusPresentation Content);

/// <summary>
/// Owns the timing of the small note that appears over a running mirror.
/// </summary>
/// <remarks>
/// <para>Separate from the window that draws it for the same reason
/// <see cref="ControlStripPolicy"/> is separate from the strip: this is where the mistakes live, and
/// none of them can be caught by looking at a screenshot. A toast is on screen for under three
/// seconds, so a fade that strobes, a hold that is eaten by its own fade, or a second notice that
/// restarts the first one's animation are all things that happen too quickly to see and too often
/// to tolerate.</para>
/// <para>Two rules carry the weight. <b>The hold is measured from full opacity</b>, so a message is
/// legible for its whole duration rather than for whatever is left after fading in. And <b>anything
/// arriving over a shown note swaps in place</b> rather than cross-fading, because a lock and a
/// sound starting can arrive within a few frames of each other, and two overlapping fades over live
/// video read as a flicker rather than as news. That second rule is what keeps a status repeating on
/// a timer from pumping the opacity, so there is no separate rule for a repeat.</para>
/// </remarks>
public sealed class ToastPolicy
{
    /// <summary>
    /// How opaque a toast gets.
    /// </summary>
    /// <remarks>
    /// Short of 1 for the reason recorded on <see cref="StripFade.Shown"/> — WinForms drops
    /// <c>WS_EX_LAYERED</c> from a form whose opacity is exactly 1, so landing there would add and
    /// remove a window style at the end of every fade — and short of it again because this sits over
    /// live video, which should stay faintly visible through it.
    /// </remarks>
    public const double Shown = 0.96;

    /// <summary>Below this a toast is treated as gone, so the window can be hidden outright.</summary>
    public const double Vanished = 0.01;

    /// <summary>Used when a toast arrives with no dismissal time of its own.</summary>
    /// <remarks>
    /// <see cref="StatusRouter"/> always sets one, so this is only reached by a caller building a
    /// presentation by hand. A toast that stays forever would be a permanent smudge over the phone,
    /// so the fallback is a duration rather than an exception.
    /// </remarks>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(2.6);

    /// <summary>Long enough not to snap, short enough not to delay the words.</summary>
    private static readonly TimeSpan ArriveDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>Slower than arriving, matching the control strip: leaving should not startle.</summary>
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>What a cleared toast holds.</summary>
    /// <remarks>
    /// Not <c>default</c>. <see cref="StatusPresentation"/> is a record struct, so its default
    /// leaves every string null, and both the width measurement and the paint read
    /// <see cref="StatusPresentation.Detail"/> before they read the phase. Clearing to real empty
    /// strings means a stale repaint draws nothing instead of throwing.
    /// </remarks>
    private static readonly StatusPresentation Nothing = new(
        StatusSurface.None,
        string.Empty,
        string.Empty,
        string.Empty,
        ColorRole.TextPrimary,
        Spinner: false,
        GlyphRole.Ready,
        StatusActionKind.None,
        ActionLabel: string.Empty,
        AutoDismiss: null);

    private StatusPresentation content = Nothing;
    private ToastPhase phase = ToastPhase.Hidden;
    private double opacity;
    private TimeSpan held;
    private TimeSpan hold = DefaultHold;

    /// <summary>Where the toast has got to.</summary>
    public ToastPhase Phase => phase;

    /// <summary>How opaque the window should be right now.</summary>
    public double Opacity => opacity;

    /// <summary>What is being shown.</summary>
    public StatusPresentation Content => content;

    /// <summary>Whether the window has anything to draw at all.</summary>
    public bool Visible => phase != ToastPhase.Hidden && opacity > Vanished;

    /// <summary>
    /// Offer one routed presentation to the toast surface.
    /// </summary>
    /// <remarks>
    /// Anything that is not a toast dismisses whatever is up. A full-surface status is about to
    /// cover the whole window, and a note fading out on top of it would be the second of two things
    /// competing to explain the same event.
    /// </remarks>
    /// <returns>Whether the presentation was taken.</returns>
    public bool Present(StatusPresentation presentation)
    {
        if (presentation.Surface != StatusSurface.Toast)
        {
            Dismiss();
            return false;
        }

        TimeSpan wanted = presentation.AutoDismiss ?? DefaultHold;
        hold = wanted > TimeSpan.Zero ? wanted : DefaultHold;

        content = presentation;
        held = TimeSpan.Zero;

        // The whole of the arrival rule, and it covers a repeat as well as a replacement.
        //
        // Already at full opacity: keep it and swap the words underneath, which is what stops a
        // quick sequence -- a lock and its audio arriving a few frames apart -- reading as a
        // flicker. Part way through a fade, or not shown at all: carry on arriving from wherever
        // the opacity has got to, so a note that is asked for again mid-fade catches up instead of
        // snapping back to nothing.
        //
        // An earlier draft special-cased identical words to "extend the hold and touch nothing
        // else". That branch was removed because it was indistinguishable from this line: both
        // reset the hold above, and both leave a fully-shown note in Holding at Shown. Deleting it
        // changed no test, which is the definition of it not being there.
        phase = opacity >= Shown ? ToastPhase.Holding : ToastPhase.Arriving;
        return true;
    }

    /// <summary>Start fading out now, whatever is showing.</summary>
    public void Dismiss()
    {
        if (phase == ToastPhase.Hidden)
        {
            return;
        }

        phase = ToastPhase.Leaving;
    }

    /// <summary>Drop the toast immediately, without a fade.</summary>
    /// <remarks>For teardown, where there is no longer a window to fade over.</remarks>
    public void Clear()
    {
        phase = ToastPhase.Hidden;
        opacity = 0;
        held = TimeSpan.Zero;
        content = Nothing;
    }

    /// <summary>Move the toast on by one tick.</summary>
    /// <param name="elapsed">Real time since the last call.</param>
    public ToastFrame Advance(TimeSpan elapsed)
    {
        TimeSpan step = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;

        switch (phase)
        {
            case ToastPhase.Arriving:
                opacity = Approach(opacity, Shown, Shown * Ratio(step, ArriveDuration));
                if (opacity >= Shown)
                {
                    opacity = Shown;
                    phase = ToastPhase.Holding;
                }

                break;

            case ToastPhase.Holding:
                // Counted only from here, so the words are legible for the whole of their hold
                // rather than for the hold minus however long the fade took.
                held += step;
                if (held >= hold)
                {
                    phase = ToastPhase.Leaving;
                }

                break;

            case ToastPhase.Leaving:
                opacity = Approach(opacity, 0, Shown * Ratio(step, LeaveDuration));
                if (opacity <= Vanished)
                {
                    Clear();
                }

                break;

            case ToastPhase.Hidden:
            default:
                break;
        }

        return new ToastFrame(phase, opacity, content);
    }

    /// <summary>How much of a fade one tick is worth.</summary>
    private static double Ratio(TimeSpan step, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 1 : step.TotalMilliseconds / duration.TotalMilliseconds;

    /// <summary>
    /// Move <paramref name="current"/> towards <paramref name="target"/> without passing it.
    /// </summary>
    /// <remarks>
    /// Stepping by a delta and checking the direction afterwards is the arithmetic that made the
    /// control strip strobe at ten hertz, so this clamps. It is belt and braces rather than the
    /// enforcing bound: <see cref="Advance"/> settles the opacity onto <see cref="Shown"/> when a
    /// fade completes and onto zero when one ends, so an unclamped step here would be corrected
    /// within the same call and never reach a caller. Deliberately kept anyway — a helper whose
    /// summary is "without passing it" should be true on its own terms.
    /// </remarks>
    private static double Approach(double current, double target, double delta)
    {
        double size = Math.Abs(delta);
        return current < target
            ? Math.Min(target, current + size)
            : Math.Max(target, current - size);
    }
}
