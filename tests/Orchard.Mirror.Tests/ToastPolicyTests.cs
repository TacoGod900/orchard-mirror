using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The toast is the surface <see cref="StatusRouter"/> has been routing to since it was written,
/// and which until now nothing drew — so everything routine was silent rather than small.
/// </summary>
/// <remarks>
/// <para>Every mistake available here happens in under three seconds, which is why it is tested
/// rather than looked at. A fade that pumps, a hold eaten by its own fade-in, or two notices
/// arriving a few frames apart and cross-fading through each other all read to an owner as "the
/// window glitched", and none of them is reproducible on demand in front of a phone.</para>
/// <para>Durations are asserted as relationships — the hold is at least the hold, the fade settles
/// and stops — rather than as the constants the policy is built from, following
/// <see cref="ControlStripPolicyTests"/>.</para>
/// </remarks>
internal static class ToastPolicyTests
{
    /// <summary>One frame at the policy's own tick rate.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);

    /// <summary>Far longer than any fade, for testing that a step cannot overshoot.</summary>
    private static readonly TimeSpan Enormous = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The hold is measured from full opacity, not from the moment the notice arrived.
    /// </summary>
    /// <remarks>
    /// Counting the hold from arrival spends the fade-in out of the reading time, so the faster the
    /// fade is tuned the longer the words stay — which is exactly backwards, and means tuning the
    /// animation silently changes how long the message is legible.
    /// </remarks>
    internal static void TheHoldIsMeasuredFromFullOpacity()
    {
        ToastPolicy policy = new();
        StatusPresentation note = Toast(NoticeKind.LockChanged, "iPhone locked");
        TimeSpan hold = note.AutoDismiss ?? ToastPolicy.DefaultHold;
        policy.Present(note);

        TimeSpan elapsed = TimeSpan.Zero;
        TimeSpan? reachedFull = null;
        TimeSpan? startedLeaving = null;

        for (int tick = 0; tick < 1000 && startedLeaving is null; tick++)
        {
            ToastFrame frame = policy.Advance(Tick);
            elapsed += Tick;

            if (reachedFull is null && frame.Phase == ToastPhase.Holding)
            {
                reachedFull = elapsed;
            }

            if (frame.Phase == ToastPhase.Leaving)
            {
                startedLeaving = elapsed;
            }
        }

        Assert(reachedFull is not null, "the note never reached full opacity");
        Assert(startedLeaving is not null, "the note never started to leave, so it would sit over the phone forever");

        TimeSpan legible = startedLeaving!.Value - reachedFull!.Value;
        Assert(
            legible >= hold - Tick,
            $"the words were fully legible for {legible.TotalMilliseconds:F0}ms of a {hold.TotalMilliseconds:F0}ms hold");
    }

    /// <summary>
    /// The same words arriving again extend the hold without restarting the fade.
    /// </summary>
    /// <remarks>
    /// <para>The bug this guards: a status that repeats on a timer — a lock that flaps, a
    /// stream-health line — restarting the fade each time would pump the opacity up and down for as
    /// long as it kept arriving. It is the same fault <see cref="StripFade"/> documents at the other
    /// end of the same problem, and it is less visible here only because the surface is smaller.</para>
    /// <para>Held separately from <see cref="AReplacementSwapsInPlaceRatherThanCrossFading"/> even
    /// though one line of the policy satisfies both. A repeat and a replacement are different events
    /// to an owner, and the day they stop being served by the same rule this is the test that says
    /// which of them broke.</para>
    /// </remarks>
    internal static void ARepeatExtendsTheHoldWithoutRestartingTheFade()
    {
        ToastPolicy policy = new();
        StatusPresentation note = Toast(NoticeKind.LockChanged, "iPhone locked");
        policy.Present(note);
        RunUntilHolding(policy);

        // Most of the way through the hold, then the same news again.
        Advance(policy, TimeSpan.FromSeconds(2));
        policy.Present(note);

        Assert(
            policy.Opacity == ToastPolicy.Shown,
            $"a repeat dropped the opacity to {policy.Opacity}, which is a visible flicker");
        Assert(
            policy.Phase == ToastPhase.Holding,
            $"a repeat moved the note to {policy.Phase} instead of leaving it where it was");

        // And it is a fresh hold, not the remains of the old one.
        Advance(policy, TimeSpan.FromSeconds(2));
        Assert(
            policy.Phase == ToastPhase.Holding,
            "a repeated note expired on the original hold rather than the extended one");
    }

    /// <summary>
    /// A different notice replaces the words in place rather than cross-fading.
    /// </summary>
    /// <remarks>
    /// A phone locking and its audio starting can arrive within a few frames of each other. Two
    /// overlapping fades over live video is a flicker, and the better the connection the more often
    /// it happens — the same shape of fault as the rushed page slide on the full surface.
    /// </remarks>
    internal static void AReplacementSwapsInPlaceRatherThanCrossFading()
    {
        ToastPolicy policy = new();
        policy.Present(Toast(NoticeKind.LockChanged, "iPhone locked"));
        RunUntilHolding(policy);

        policy.Present(Toast(NoticeKind.AudioStarted, "iPhone audio playing"));

        Assert(
            policy.Opacity == ToastPolicy.Shown,
            $"a replacement dipped the opacity to {policy.Opacity} instead of swapping the words underneath it");
        Assert(
            policy.Content.Headline == "iPhone audio playing",
            $"the replacement showed \"{policy.Content.Headline}\" rather than the new headline");
    }

    /// <summary>
    /// A full-surface status dismisses whatever note is up.
    /// </summary>
    /// <remarks>
    /// The full surface is about to cover the whole window. A note fading out on top of it is a
    /// second explanation of the same event, arriving late and over the first one.
    /// </remarks>
    internal static void AFullSurfaceStatusDismissesTheNote()
    {
        ToastPolicy policy = new();
        policy.Present(Toast(NoticeKind.LockChanged, "iPhone locked"));
        RunUntilHolding(policy);

        bool taken = policy.Present(Route(NoticeKind.GaveUp, "Cannot reach the iPhone", mirrorLive: false));

        Assert(!taken, "the toast surface accepted a presentation that was not routed to it");
        Assert(
            policy.Phase == ToastPhase.Leaving,
            $"a full-surface status left the note in {policy.Phase} instead of dismissing it");

        Advance(policy, Enormous);
        Assert(policy.Phase == ToastPhase.Hidden, "the dismissed note never went away");
    }

    /// <summary>
    /// Neither end of the fade can be passed, however large a step lands on it.
    /// </summary>
    /// <remarks>
    /// A step that overshoots and is corrected on the following tick is a limit cycle, which is what
    /// made the control strip strobe at ten hertz. A dropped frame or a debugger break makes the
    /// step arbitrarily large, so this is reachable in ordinary use rather than only in a test.
    /// </remarks>
    internal static void NeitherEndOfTheFadeCanBePassed()
    {
        ToastPolicy policy = new();
        policy.Present(Toast(NoticeKind.LockChanged, "iPhone locked"));

        ToastFrame arrived = policy.Advance(Enormous);
        Assert(
            arrived.Opacity == ToastPolicy.Shown,
            $"one enormous step took the opacity to {arrived.Opacity}, past the shown level");

        // Sitting at the top must be motionless, not an oscillation too fast to see.
        for (int tick = 0; tick < 100; tick++)
        {
            ToastFrame frame = policy.Advance(Tick);
            if (frame.Phase != ToastPhase.Holding)
            {
                break;
            }

            Assert(
                frame.Opacity == ToastPolicy.Shown,
                $"a held note drifted to {frame.Opacity} on tick {tick}");
        }

        policy.Dismiss();
        ToastFrame gone = policy.Advance(Enormous);
        Assert(gone.Opacity == 0, $"one enormous step took the opacity to {gone.Opacity}, past nothing");
        Assert(gone.Phase == ToastPhase.Hidden, $"a fully faded note was left in {gone.Phase}");
    }

    /// <summary>
    /// The shown level stays below full opacity, for the reason recorded on <see cref="StripFade.Shown"/>:
    /// WinForms drops WS_EX_LAYERED from a form whose opacity is exactly 1, so landing there puts a
    /// window-style change at the end of every fade.
    /// </summary>
    internal static void AShownNoteKeepsASliverOfTranslucency()
    {
        Assert(
            ToastPolicy.Shown < 1,
            $"the shown level is {ToastPolicy.Shown}; at 1 the window stops being layered");
        Assert(
            ToastPolicy.Shown > 0.9,
            $"the shown level is {ToastPolicy.Shown}, too faint to read against a bright phone");
    }

    /// <summary>
    /// A toast built without an expiry still expires.
    /// </summary>
    /// <remarks>
    /// <see cref="StatusRouter"/> always sets one, so this only guards a presentation assembled by
    /// hand. It is worth guarding because the failure is a permanent smudge over the phone rather
    /// than a missing message, and nothing else in the window would ever clear it.
    /// </remarks>
    internal static void ANoteWithNoExpirySetStillExpires()
    {
        ToastPolicy policy = new();
        policy.Present(new StatusPresentation(
            StatusSurface.Toast,
            "IPHONE",
            "Something happened",
            string.Empty,
            ColorRole.TextPrimary,
            Spinner: false,
            GlyphRole.Ready,
            StatusActionKind.None,
            ActionLabel: string.Empty,
            AutoDismiss: null));

        Advance(policy, ToastPolicy.DefaultHold + TimeSpan.FromSeconds(1));
        Assert(
            policy.Phase is ToastPhase.Leaving or ToastPhase.Hidden,
            $"a note with no expiry was still in {policy.Phase} well past the default hold");
    }

    /// <summary>A note that has gone forgets what it said, so nothing can repaint a stale one.</summary>
    internal static void AVanishedNoteForgetsWhatItSaid()
    {
        ToastPolicy policy = new();
        policy.Present(Toast(NoticeKind.LockChanged, "iPhone locked"));
        RunUntilHolding(policy);

        Advance(policy, Enormous);
        Advance(policy, Enormous);

        Assert(policy.Phase == ToastPhase.Hidden, $"the note was left in {policy.Phase}");
        Assert(!policy.Visible, "a hidden note still reported itself as visible");
        Assert(
            policy.Content.Headline.Length == 0,
            $"a hidden note still held \"{policy.Content.Headline}\"");
    }

    /// <summary>Route a notice the way the window does, over a live picture.</summary>
    private static StatusPresentation Toast(NoticeKind kind, string headline) =>
        Route(kind, headline, mirrorLive: true);

    private static StatusPresentation Route(NoticeKind kind, string headline, bool mirrorLive) =>
        StatusRouter.Route(new RouteInput(
            new MirrorNotice(kind, headline, Severity: NoticeSeverity.Neutral),
            mirrorLive ? PictureState.Live : PictureState.None,
            RetryInFlight: false));

    /// <summary>Tick until the note is fully arrived, or give up loudly.</summary>
    private static void RunUntilHolding(ToastPolicy policy)
    {
        for (int tick = 0; tick < 1000; tick++)
        {
            if (policy.Advance(Tick).Phase == ToastPhase.Holding)
            {
                return;
            }
        }

        throw new InvalidOperationException("the note never finished arriving");
    }

    /// <summary>Advance by a span, in real ticks, so phase changes are not stepped over.</summary>
    private static void Advance(ToastPolicy policy, TimeSpan span)
    {
        for (TimeSpan spent = TimeSpan.Zero; spent < span; spent += Tick)
        {
            policy.Advance(Tick);
        }
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
