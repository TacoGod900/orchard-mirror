namespace Orchard.Mirror.Shell;

/// <summary>What happened, as a kind rather than as a sentence.</summary>
/// <remarks>
/// The session used to report its state as free text plus a handful of parallel booleans, which
/// meant the only way to decide how loudly to say something was to inspect the words. A kind can be
/// routed; a sentence cannot.
/// </remarks>
public enum NoticeKind
{
    /// <summary>Looking for the phone, opening the tunnel, mounting the developer image.</summary>
    Connecting,

    /// <summary>A step of the first-run walkthrough is under way.</summary>
    SetupStep,

    /// <summary>The tunnel is up and the stream has been asked for.</summary>
    Connected,

    /// <summary>A frame reached the screen. The mirror is live from here.</summary>
    FirstFrame,

    /// <summary>Routine stream bookkeeping — frame counters and the like.</summary>
    StreamHealth,

    /// <summary>Sound is coming through.</summary>
    AudioStarted,

    /// <summary>The phone was locked or unlocked. It keeps streaming either way.</summary>
    LockChanged,

    /// <summary>The screen went off, so there is nothing to show until it comes back.</summary>
    Asleep,

    /// <summary>The screen came back.</summary>
    Awake,

    /// <summary>The stream broke and Orchard is dealing with it.</summary>
    Interrupted,

    /// <summary>An attempt to connect failed and Orchard has stopped trying for now.</summary>
    AttemptFailed,

    /// <summary>Orchard has exhausted its retries and will not try again unasked.</summary>
    GaveUp,

    /// <summary>The owner asked to stop mirroring.</summary>
    UserDisconnected,

    /// <summary>Setup stopped on something only the owner can resolve.</summary>
    SetupBlocked,

    /// <summary>
    /// A one-off confirmation the owner asked for — a paste landing, a setting taking effect.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than borrowing one of the phone-state notices, because those carry the
    /// wrong glyph and the wrong meaning: a paste is not the phone locking. It routes as a toast when
    /// there is a picture to sit over and is simply dropped when there is not, since a confirmation
    /// of something that only happens against a live mirror has nothing to confirm on a blank window.
    /// </remarks>
    Info,
}

/// <summary>How much of the owner's attention a notice has earned.</summary>
public enum NoticeSeverity
{
    /// <summary>Something is under way.</summary>
    Progress,

    /// <summary>A fact, neither good nor bad.</summary>
    Neutral,

    /// <summary>Working, but not as intended.</summary>
    Warning,

    /// <summary>Stopped, and it will not fix itself.</summary>
    Failure,
}

/// <summary>One thing that happened, ready to be routed to a surface.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Headline">
/// What to say. Empty means "use the default for this kind" — a caller that has something specific
/// to say always wins, which is the whole point: the old view discarded the caller's headline in
/// three of its five phases and showed "All set" over the top of live errors.
/// </param>
/// <param name="Detail">Supporting text, or empty.</param>
/// <param name="Severity">How loudly to say it.</param>
/// <param name="Code">A stable machine-readable cause, when one is known.</param>
public readonly record struct MirrorNotice(
    NoticeKind Kind,
    string Headline = "",
    string Detail = "",
    NoticeSeverity Severity = NoticeSeverity.Progress,
    string? Code = null);
