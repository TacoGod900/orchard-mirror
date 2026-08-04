using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The router decides what the owner sees when something happens to the phone. Both mistakes it can
/// make are ones this app actually shipped: saying the wrong thing (every error rendered under a
/// hardcoded "READY / All set" once the mirror had worked once), and saying it too loudly (locking
/// the phone dropped a full sheet of glass over a picture that was still streaming).
/// </summary>
/// <remarks>
/// These are written as claims about what a person sees, never against the router's own branches.
/// A test shaped like the implementation passes while the behaviour is wrong — this repository has
/// paid for that four times — so each one below names an outcome the old code got wrong.
/// </remarks>
internal static class StatusRouterTests
{
    /// <summary>
    /// The headline bug, stated directly. Once a frame had arrived the view latched to its Ready
    /// phase and never left, and that phase hardcoded its own title, so "The stream was interrupted"
    /// appeared on screen as "All set".
    /// </summary>
    internal static void SaysWhatWentWrongAfterTheMirrorHasWorked()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.Interrupted, "Reconnecting iPhone", Severity: NoticeSeverity.Warning),
            mirrorLive: true);

        Assert(
            shown.Headline.Contains("Reconnecting", StringComparison.Ordinal),
            "a lost connection must say so; the caller's headline was discarded");
        Assert(
            !shown.Headline.Contains("All set", StringComparison.OrdinalIgnoreCase),
            "a lost connection must not be reported as success");
    }

    /// <summary>
    /// The general form of the same defect: three of the view's five phases threw away the headline
    /// they were handed. Anything a caller took the trouble to say must survive to the screen.
    /// </summary>
    internal static void NeverDiscardsWhatTheCallerSaid()
    {
        // Every picture state, not just the empty one. The defect being guarded against was
        // specific to the live case -- the phase only latched after a frame had arrived -- so a
        // loop over the dead half alone would have watched the bug go past.
        foreach (NoticeKind kind in Enum.GetValues<NoticeKind>())
        {
            foreach (PictureState picture in Enum.GetValues<PictureState>())
            {
                const string Spoken = "Preparing developer services";
                StatusPresentation shown = Route(new MirrorNotice(kind, Spoken), picture);
                if (shown.Surface == StatusSurface.None)
                {
                    continue;
                }

                Assert(
                    shown.Headline == Spoken,
                    $"{kind} rewrote the caller's headline as \"{shown.Headline}\" (picture: {picture})");
            }
        }
    }

    /// <summary>
    /// A phone that locks keeps streaming — Orchard decodes the live lock screen. Covering the
    /// picture to announce it is the single most irritating thing the old flow did.
    /// </summary>
    internal static void KeepsRoutineChatterOffTheLivePicture()
    {
        foreach (NoticeKind kind in (NoticeKind[])[NoticeKind.LockChanged, NoticeKind.AudioStarted, NoticeKind.StreamHealth])
        {
            StatusPresentation shown = Route(new MirrorNotice(kind, "iPhone unlocked"), mirrorLive: true);
            Assert(
                shown.Surface != StatusSurface.Full,
                $"{kind} covered a live mirror; routine news must never take the whole surface");
        }
    }

    /// <summary>
    /// The other half of that rule. With no picture to protect the surface is free, so progress
    /// belongs on it — suppressing news here would leave a blank window explaining nothing.
    /// </summary>
    internal static void SpeaksUpWhenThereIsNoPictureToProtect()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.Connecting, "Looking for iPhone"),
            mirrorLive: false);

        Assert(
            shown.Surface == StatusSurface.Full,
            "with no mirror up, connecting progress must take the surface rather than hide");
    }

    /// <summary>A stop the owner has to act on must be legible as one, and offer the action.</summary>
    internal static void StopsWithSomethingToPress()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.GaveUp, "Cannot reach the iPhone", Severity: NoticeSeverity.Failure),
            mirrorLive: false);

        Assert(shown.Surface == StatusSurface.Full, "a give-up must be shown, not toasted away");
        Assert(shown.Accent == ColorRole.Failure, "a give-up must not be coloured like progress");
        Assert(!shown.Spinner, "a give-up must not keep spinning as though it were still trying");
        Assert(
            shown.Action != StatusActionKind.None && shown.ActionLabel.Length > 0,
            "a give-up must offer a way back; a dead end with no button is how this felt frozen");
    }

    /// <summary>
    /// While Orchard is genuinely retrying, something must move. A static panel reading
    /// "Reconnecting" is indistinguishable from a hang, which is exactly how an unplugged cable read.
    /// </summary>
    internal static void KeepsSomethingTurningWhileItRetries()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.Interrupted, "Reconnecting iPhone", Severity: NoticeSeverity.Warning),
            mirrorLive: false,
            retryInFlight: true);

        Assert(shown.Spinner, "a retry in flight must show motion, or it reads as a freeze");
    }

    /// <summary>
    /// The amber that never appeared. The view carried a Warning colour and a Tone field for exactly
    /// this, and the field was assigned fifteen times and read none, so every state was white.
    /// </summary>
    internal static void ColoursAWarningDifferentlyFromProgressAndFailure()
    {
        ColorRole progress = Route(new MirrorNotice(NoticeKind.Connecting, "Connecting"), mirrorLive: false).Accent;
        ColorRole warning = Route(
            new MirrorNotice(NoticeKind.Interrupted, "Reconnecting", Severity: NoticeSeverity.Warning),
            mirrorLive: false).Accent;
        ColorRole failure = Route(
            new MirrorNotice(NoticeKind.GaveUp, "Stopped", Severity: NoticeSeverity.Failure),
            mirrorLive: false).Accent;

        Assert(warning == ColorRole.Warning, "a warning must reach the amber the palette carries for it");
        Assert(
            progress != warning && warning != failure && progress != failure,
            "progress, warning and failure must be told apart at a glance");
    }

    /// <summary>Anything shown at all must have something to read.</summary>
    internal static void NeverShowsABlankHeadline()
    {
        foreach (NoticeKind kind in Enum.GetValues<NoticeKind>())
        {
            foreach (bool live in (bool[])[false, true])
            {
                StatusPresentation shown = Route(new MirrorNotice(kind), live);
                if (shown.Surface == StatusSurface.None)
                {
                    continue;
                }

                Assert(
                    shown.Headline.Trim().Length > 0,
                    $"{kind} produced a visible status with no headline (live: {live})");
            }
        }
    }

    /// <summary>
    /// A toast must dismiss itself. The status it replaces had no timer at all, so "Waking iPhone"
    /// stayed on screen until something else happened to overwrite it.
    /// </summary>
    internal static void GivesEveryToastAnExpiry()
    {
        foreach (NoticeKind kind in Enum.GetValues<NoticeKind>())
        {
            StatusPresentation shown = Route(new MirrorNotice(kind, "Something happened"), mirrorLive: true);
            if (shown.Surface != StatusSurface.Toast)
            {
                continue;
            }

            Assert(
                shown.AutoDismiss is { } hold && hold > TimeSpan.Zero,
                $"{kind} toasts without an expiry, so it would sit on the picture forever");
        }
    }

    /// <summary>
    /// "All set" must hand the screen back on its own. It is the only full-surface state that is
    /// finished the moment it has been read, and the picture is already live behind it.
    /// </summary>
    internal static void HandsTheScreenBackAfterSayingReady()
    {
        StatusPresentation shown = Route(new MirrorNotice(NoticeKind.FirstFrame), mirrorLive: false);

        Assert(shown.Surface == StatusSurface.Full, "the handover moment is worth showing");
        Assert(
            shown.AutoDismiss is { } hold && hold > TimeSpan.Zero && hold < TimeSpan.FromSeconds(3),
            "\"all set\" must uncover the mirror by itself, and promptly");
        Assert(!shown.Spinner, "nothing is still working once the picture is up");
    }

    /// <summary>
    /// A state that stays until something changes must not carry an expiry, or it would uncover a
    /// mirror that is not there and leave the owner looking at a dead window.
    /// </summary>
    internal static void LetsAStoppedStateStay()
    {
        foreach (NoticeKind kind in (NoticeKind[])[NoticeKind.GaveUp, NoticeKind.UserDisconnected, NoticeKind.SetupBlocked])
        {
            StatusPresentation shown = Route(
                new MirrorNotice(kind, "Stopped", Severity: NoticeSeverity.Failure),
                mirrorLive: false);

            Assert(
                shown.AutoDismiss is null,
                $"{kind} expires by itself, which would uncover a mirror that is not running");
        }
    }

    /// <summary>
    /// The one place the router must fail loudly rather than quietly. Suppressing an unknown state
    /// leaves a frozen picture and no explanation, so anything unrecognised takes the surface.
    /// </summary>
    internal static void ShowsAnUnrecognisedStateRatherThanSwallowingIt()
    {
        StatusPresentation shown = Route(new MirrorNotice((NoticeKind)9999, "Something new"), mirrorLive: true);

        Assert(
            shown.Surface == StatusSurface.Full,
            "an unrecognised notice must be shown; silence here is a frozen window with no reason given");
    }

    /// <summary>
    /// A stall that recovers must never cover the phone.
    /// </summary>
    /// <remarks>
    /// The complaint this answers was "random disconnects". The connection was usually back within
    /// a second — the retry ladder starts at 250 milliseconds — but a broken stream leaves its last
    /// frame on the swap chain, so the phone was still on screen and Orchard dropped a full-surface
    /// panel over it anyway. What the owner saw was not a brief stall but the application visibly
    /// breaking and repairing itself, several times an hour.
    /// </remarks>
    internal static void AStallOverAHeldFrameNeverCoversThePhone()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.Interrupted, "Reconnecting iPhone", Severity: NoticeSeverity.Warning),
            PictureState.Held,
            retryInFlight: true);

        Assert(
            shown.Surface == StatusSurface.Toast,
            $"a stall over a held frame took the {shown.Surface} surface; the picture is still on screen");
        Assert(shown.Spinner, "a reconnection in flight must show something turning");
    }

    /// <summary>
    /// The other half. A frame held past its grace is a photograph, and the caller stops passing
    /// Held — at which point the same notice is owed the whole surface.
    /// </summary>
    internal static void AStallWithNothingHeldTakesTheSurface()
    {
        StatusPresentation shown = Route(
            new MirrorNotice(NoticeKind.Interrupted, "Reconnecting iPhone", Severity: NoticeSeverity.Warning),
            PictureState.None,
            retryInFlight: true);

        Assert(
            shown.Surface == StatusSurface.Full,
            $"a stall with nothing on screen took the {shown.Surface} surface, leaving a blank window");
    }

    /// <summary>
    /// News the phone streams through is only true of a moving picture.
    /// </summary>
    /// <remarks>
    /// Said over a held frame, "iPhone audio playing" is a claim the screen is contradicting: the
    /// stream carrying that audio is the one that has stopped. Silence is the honest answer.
    /// </remarks>
    internal static void RoutineNewsIsSuppressedOverAFrozenPicture()
    {
        foreach (NoticeKind kind in (NoticeKind[])[NoticeKind.LockChanged, NoticeKind.AudioStarted])
        {
            StatusPresentation shown = Route(new MirrorNotice(kind, "iPhone audio playing"), PictureState.Held);
            Assert(
                shown.Surface == StatusSurface.None,
                $"{kind} was shown over a frozen picture as {shown.Surface}; the stream it describes has stopped");
        }
    }

    /// <summary>
    /// Progress belongs on a note whenever there is anything to look at, frozen or not.
    /// </summary>
    /// <remarks>
    /// Connecting is routed on every attempt of the retry ladder. If those took the surface while a
    /// frame was held, the panel the interruption itself avoided would arrive a quarter of a second
    /// later anyway, and the hold would buy nothing.
    /// </remarks>
    internal static void ReconnectionProgressStaysOffAHeldPicture()
    {
        foreach (NoticeKind kind in (NoticeKind[])[NoticeKind.Connecting, NoticeKind.Connected])
        {
            StatusPresentation shown = Route(new MirrorNotice(kind, "Looking for iPhone"), PictureState.Held);
            Assert(
                shown.Surface == StatusSurface.Toast,
                $"{kind} took the {shown.Surface} surface over a held frame, undoing the hold");
        }
    }

    /// <summary>
    /// A failure outranks a held frame, whatever kind it arrives as.
    /// </summary>
    /// <remarks>
    /// <para>Whatever is on screen at that point is a photograph of a phone Orchard has stopped
    /// being able to reach. Protecting it would be protecting the lie.</para>
    /// <para>The kinds here are chosen to actually exercise the severity check. An earlier version
    /// of this test used <see cref="NoticeKind.GaveUp"/>, which routes to the full surface through
    /// the fallthrough regardless of severity — so deleting the severity check entirely left the
    /// test green. These two are the kinds the check alone saves: without it a fatal interruption
    /// would be a note over a frozen picture, and a fatal one arriving as routine news would be
    /// silent.</para>
    /// </remarks>
    internal static void AFailureTakesTheSurfaceEvenOverAHeldFrame()
    {
        foreach (NoticeKind kind in (NoticeKind[])[NoticeKind.Interrupted, NoticeKind.LockChanged])
        {
            StatusPresentation shown = Route(
                new MirrorNotice(kind, "The iPhone stopped responding", Severity: NoticeSeverity.Failure),
                PictureState.Held);

            Assert(
                shown.Surface == StatusSurface.Full,
                $"a fatal {kind} was left as {shown.Surface} over a frozen picture");
            Assert(
                shown.Accent == ColorRole.Failure,
                $"a fatal {kind} was not coloured as a failure");
        }
    }

    /// <summary>
    /// The grace has to agree with the retry ladder, and this is the only place both are visible.
    /// </summary>
    /// <remarks>
    /// Too short and every recoverable stall still flashes a panel, which is the entire fault being
    /// fixed. Too long and the panel never arrives before Orchard gives up, so the owner watches a
    /// still image for the whole budget and is then told the phone was never coming back.
    /// </remarks>
    internal static void TheHeldFrameGraceAgreesWithTheRetryLadder()
    {
        TimeSpan firstThreeAttempts = RetryPolicy.Delay(1) + RetryPolicy.Delay(2) + RetryPolicy.Delay(3);

        Assert(
            HeldFrame.Believable > firstThreeAttempts,
            $"the grace is {HeldFrame.Believable.TotalSeconds:F1}s but the first three retries take "
                + $"{firstThreeAttempts.TotalSeconds:F1}s, so an ordinary stall would still flash a panel");
        Assert(
            HeldFrame.Believable < RetryPolicy.TotalBudget,
            $"the grace is {HeldFrame.Believable.TotalSeconds:F1}s and the whole retry budget is "
                + $"{RetryPolicy.TotalBudget.TotalSeconds:F1}s, so the surface would never be taken before giving up");
    }

    /// <summary>A frame frozen long ago is not believable, and one frozen just now is.</summary>
    internal static void BelievabilityRunsOutRatherThanLatching()
    {
        Assert(HeldFrame.StillBelievable(TimeSpan.Zero), "a frame that has only just stopped was called stale");
        Assert(
            HeldFrame.StillBelievable(HeldFrame.Believable - TimeSpan.FromMilliseconds(1)),
            "a frame inside the grace was called stale");
        Assert(
            !HeldFrame.StillBelievable(HeldFrame.Believable),
            "the grace did not end at its own duration");
        Assert(
            !HeldFrame.StillBelievable(TimeSpan.FromMinutes(5)),
            "a five-minute-old photograph of a phone was called a picture of one");
        Assert(
            !HeldFrame.StillBelievable(TimeSpan.FromSeconds(-1)),
            "a negative age was treated as believable; a clock that moves backwards must not hold the frame");
    }

    /// <summary>
    /// A confirmation the owner asked for sits over the picture and never takes the whole surface.
    /// </summary>
    /// <remarks>
    /// A paste landing is good news, and good news the owner already knows the cause of should be a
    /// note, not a panel over the very screen they just pasted into. Over a held frame it is still a
    /// note; only with nothing on screen at all is it dropped, because a paste cannot have happened
    /// against a blank window.
    /// </remarks>
    internal static void AConfirmationIsANoteOverAPictureAndNothingOverNone()
    {
        foreach (PictureState picture in (PictureState[])[PictureState.Live, PictureState.Held])
        {
            StatusPresentation shown = Route(new MirrorNotice(NoticeKind.Info, "Pasted from PC"), picture);
            Assert(
                shown.Surface == StatusSurface.Toast,
                $"a confirmation took the {shown.Surface} surface over a {picture} picture");
            Assert(shown.Glyph == GlyphRole.Ready, "a confirmation should read as done, not as a problem");
            Assert(!shown.Spinner, "a confirmation is finished, so nothing should be turning");
            Assert(shown.Action == StatusActionKind.None, "a confirmation needs no button");
            Assert(shown.AutoDismiss is not null, "a confirmation toast must expire on its own");
        }

        StatusPresentation blank = Route(new MirrorNotice(NoticeKind.Info, "Pasted from PC"), PictureState.None);
        Assert(
            blank.Surface == StatusSurface.None,
            $"a confirmation with nothing on screen was shown as {blank.Surface}");
    }

    private static StatusPresentation Route(MirrorNotice notice, bool mirrorLive, bool retryInFlight = false) =>
        Route(notice, mirrorLive ? PictureState.Live : PictureState.None, retryInFlight);

    private static StatusPresentation Route(MirrorNotice notice, PictureState picture, bool retryInFlight = false) =>
        StatusRouter.Route(new RouteInput(notice, picture, retryInFlight));

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
