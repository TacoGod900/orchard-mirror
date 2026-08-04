namespace Orchard.Mirror.Shell;

/// <summary>What the router needs to know beyond the notice itself.</summary>
/// <param name="Notice">What happened.</param>
/// <param name="Picture">
/// What is on the mirror surface right now. This is the fact that decides whether a status may take
/// the whole surface: with anything worth looking at behind it, almost nothing has earned that.
/// It was a boolean until a held frame turned out to be a third case — see <see cref="PictureState"/>.
/// </param>
/// <param name="RetryInFlight">Whether Orchard is currently trying to get the connection back.</param>
public readonly record struct RouteInput(MirrorNotice Notice, PictureState Picture, bool RetryInFlight);

/// <summary>Decides how — and whether — one notice is shown.</summary>
/// <remarks>
/// <para>The only place that turns "what happened" into "what the owner sees". It exists because the
/// view used to decide this from a latched phase enum: once a frame had arrived the phase stuck at
/// <c>Ready</c> forever, and that phase hardcoded its own title, so every later message — a lost
/// connection, a decode failure, a sleeping phone — rendered under "READY / All set" with the real
/// text demoted to the body. Errors were invisible for as long as the app had ever worked once.</para>
/// <para>Two rules carry most of the weight. <b>The caller's words always win</b>: a headline that
/// was passed in is never replaced, only defaulted when absent. And <b>a live picture is protected</b>:
/// news that the phone keeps streaming through — a lock, a sound starting — is never allowed to
/// cover it. Everything unrecognised takes the surface, because a silent unknown state is a frozen
/// window with no explanation.</para>
/// </remarks>
public static class StatusRouter
{
    /// <summary>How long a self-dismissing note sits over the picture.</summary>
    private static readonly TimeSpan ToastHold = TimeSpan.FromSeconds(2.6);

    /// <summary>
    /// How long "all set" holds before handing the screen back. Long enough to be read, short
    /// enough not to be a wait — the picture is already live behind it, so every extra moment here
    /// is a moment of the phone being withheld.
    /// </summary>
    private static readonly TimeSpan ReadyHold = TimeSpan.FromSeconds(1.1);

    /// <summary>Decide how to present one notice.</summary>
    public static StatusPresentation Route(RouteInput input)
    {
        MirrorNotice notice = input.Notice;
        StatusSurface surface = SurfaceFor(notice, input.Picture);
        (StatusActionKind action, string actionLabel) = ActionFor(notice.Kind);

        return new StatusPresentation(
            Surface: surface,
            Kicker: KickerFor(notice.Kind),
            Headline: notice.Headline.Trim().Length > 0 ? notice.Headline : DefaultHeadlineFor(notice.Kind),
            Detail: notice.Detail,
            Accent: AccentFor(notice.Severity),
            Spinner: SpinnerFor(notice.Kind, input.RetryInFlight),
            Glyph: GlyphFor(notice.Kind),
            Action: action,
            ActionLabel: actionLabel,
            AutoDismiss: AutoDismissFor(notice.Kind, surface));
    }

    /// <summary>
    /// How long a presentation stays before it stops being shown. For a toast that means fading;
    /// for the full surface it means uncovering the mirror behind it.
    /// </summary>
    private static TimeSpan? AutoDismissFor(NoticeKind kind, StatusSurface surface)
    {
        if (surface == StatusSurface.Toast)
        {
            return ToastHold;
        }

        // "All set" is the one full-surface state that is finished the moment it has been read.
        return surface == StatusSurface.Full && kind == NoticeKind.FirstFrame ? ReadyHold : null;
    }

    /// <summary>
    /// Where a notice is allowed to appear. The picture is the thing being protected: a phone that
    /// locks, or starts making a sound, carries on streaming, and interrupting that to announce it
    /// is the single most irritating thing the old flow did.
    /// </summary>
    private static StatusSurface SurfaceFor(MirrorNotice notice, PictureState picture)
    {
        // A failure has earned the surface whatever is behind it -- the picture it is covering is
        // stale by definition once something has stopped working.
        if (notice.Severity == NoticeSeverity.Failure)
        {
            return StatusSurface.Full;
        }

        return notice.Kind switch
        {
            // Bookkeeping. Worth logging, never worth a pixel.
            NoticeKind.StreamHealth => StatusSurface.None,

            // The picture speaks for itself once it is back; announcing it would cover the very
            // thing the owner is waiting to see.
            NoticeKind.Awake => StatusSurface.None,

            // News the phone keeps streaming through. Only against a moving picture: said over a
            // frozen one, "iPhone audio playing" is a claim the screen is visibly contradicting.
            NoticeKind.AudioStarted or NoticeKind.LockChanged =>
                picture == PictureState.Live ? StatusSurface.Toast : StatusSurface.None,

            // A stream that has stopped while its last frame is still up.
            //
            // This is the ordinary blip -- a cable knocked, a Wi-Fi stall -- and the retry ladder
            // starts at 250 milliseconds, so it has usually recovered before a panel would have
            // finished sliding into place. Covering a picture that is still on screen, to announce
            // a fault that is already over, is what made an intermittent connection feel like a
            // broken application. A note is enough while the frame is still worth believing; once
            // it is not, the caller stops passing Held and this returns the surface.
            NoticeKind.Interrupted =>
                picture == PictureState.Held ? StatusSurface.Toast : StatusSurface.Full,

            // A confirmation the owner asked for. Worth a note over anything on screen, and worth
            // nothing over a blank window -- if there is no picture there was nothing to paste into,
            // so there is nothing to confirm.
            NoticeKind.Info =>
                picture == PictureState.None ? StatusSurface.None : StatusSurface.Toast,

            // Progress. Unobtrusive if there is anything to look at -- moving or not -- and front
            // and centre if the window is otherwise empty.
            NoticeKind.Connecting or NoticeKind.Connected =>
                picture == PictureState.None ? StatusSurface.Full : StatusSurface.Toast,

            // Everything else -- setup, sleep, giving up, and anything this build does not
            // recognise -- takes the surface. Suppressing an unknown state would leave a frozen
            // window and no reason given, which is the worse of the two mistakes.
            _ => StatusSurface.Full,
        };
    }

    /// <summary>
    /// Colour follows severity alone, so the same seriousness always looks the same. The field this
    /// replaces was written from fifteen call sites and read from none, so every state was white.
    /// </summary>
    private static ColorRole AccentFor(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Progress => ColorRole.Progress,
        NoticeSeverity.Warning => ColorRole.Warning,
        NoticeSeverity.Failure => ColorRole.Failure,
        _ => ColorRole.TextPrimary,
    };

    /// <summary>
    /// Something turns only while Orchard is genuinely working. A spinner on a state that has
    /// stopped is a lie the owner waits on; a static panel reading "Reconnecting" is a freeze.
    /// </summary>
    private static bool SpinnerFor(NoticeKind kind, bool retryInFlight) => kind switch
    {
        NoticeKind.Connecting or NoticeKind.Connected or NoticeKind.SetupStep => true,
        NoticeKind.Interrupted or NoticeKind.AttemptFailed => retryInFlight,
        _ => false,
    };

    private static GlyphRole GlyphFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Connecting or NoticeKind.Connected or NoticeKind.SetupStep => GlyphRole.Working,
        NoticeKind.FirstFrame or NoticeKind.AudioStarted or NoticeKind.LockChanged
            or NoticeKind.Awake or NoticeKind.StreamHealth or NoticeKind.Info => GlyphRole.Ready,
        NoticeKind.Asleep => GlyphRole.Asleep,
        _ => GlyphRole.Blocked,
    };

    private static (StatusActionKind Kind, string Label) ActionFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Asleep => (StatusActionKind.Wake, "Wake iPhone"),
        NoticeKind.SetupBlocked => (StatusActionKind.Reconnect, "Try again"),
        NoticeKind.AttemptFailed or NoticeKind.GaveUp or NoticeKind.UserDisconnected =>
            (StatusActionKind.Reconnect, "Reconnect"),

        // An unrecognised state is shown, and shown with a way out of it.
        NoticeKind.Connecting or NoticeKind.SetupStep or NoticeKind.Connected or NoticeKind.FirstFrame
            or NoticeKind.StreamHealth or NoticeKind.AudioStarted or NoticeKind.LockChanged
            or NoticeKind.Awake or NoticeKind.Interrupted or NoticeKind.Info =>
                (StatusActionKind.None, string.Empty),
        _ => (StatusActionKind.Reconnect, "Reconnect"),
    };

    private static string KickerFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Connecting or NoticeKind.Connected or NoticeKind.SetupStep => "SETTING UP",
        NoticeKind.FirstFrame => "READY",
        NoticeKind.StreamHealth or NoticeKind.AudioStarted or NoticeKind.LockChanged
            or NoticeKind.Awake or NoticeKind.Info => "IPHONE",
        NoticeKind.Asleep => "ASLEEP",
        NoticeKind.Interrupted => "RECONNECTING",
        NoticeKind.UserDisconnected => "DISCONNECTED",
        _ => "NEEDS YOU",
    };

    /// <summary>
    /// Only ever reached when a caller had nothing specific to say. Every one of these is a real
    /// sentence rather than a placeholder, because a status with no headline is a blank panel.
    /// </summary>
    private static string DefaultHeadlineFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Connecting => "Looking for iPhone",
        NoticeKind.SetupStep => "Setting up",
        NoticeKind.Connected => "iPhone connected",
        NoticeKind.FirstFrame => "All set",
        NoticeKind.StreamHealth => "Streaming",
        NoticeKind.AudioStarted => "iPhone audio playing",
        NoticeKind.LockChanged => "iPhone lock changed",
        NoticeKind.Asleep => "iPhone asleep",
        NoticeKind.Awake => "iPhone awake",
        NoticeKind.Interrupted => "Reconnecting iPhone",
        NoticeKind.AttemptFailed => "Could not reach the iPhone",
        NoticeKind.GaveUp => "Cannot reach the iPhone",
        NoticeKind.UserDisconnected => "Disconnected",
        NoticeKind.SetupBlocked => "Setup stopped",
        NoticeKind.Info => "Done",
        _ => "Something unexpected happened",
    };
}
