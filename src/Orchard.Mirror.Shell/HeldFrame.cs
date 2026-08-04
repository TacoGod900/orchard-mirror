namespace Orchard.Mirror.Shell;

/// <summary>What is actually on the mirror surface at the moment a status is shown.</summary>
/// <remarks>
/// <para>This replaces a single <c>MirrorLive</c> boolean, which conflated two facts that come
/// apart precisely when it matters: whether a picture is on screen, and whether the stream feeding
/// it is still running. A broken stream does not take its last frame with it — the swap chain still
/// holds it and DWM carries on compositing it — so for a second or two after a cable is knocked,
/// the phone is still there on screen, unchanged and perfectly legible.</para>
/// <para>Treating that as "no picture" is what made an ordinary blip look like a fault: the last
/// frame stayed up, and Orchard dropped a full-surface panel over it to announce a reconnection
/// that had usually already succeeded by the time the panel finished sliding in.</para>
/// </remarks>
public enum PictureState
{
    /// <summary>Nothing worth protecting. The surface is free.</summary>
    None,

    /// <summary>A current frame, still updating.</summary>
    Live,

    /// <summary>
    /// The last frame of a stream that has stopped. Still on screen, still legible, and no longer
    /// true — but not yet a lie worth covering.
    /// </summary>
    Held,
}

/// <summary>How long the last frame of a broken stream stays worth keeping.</summary>
/// <remarks>
/// <para>A frozen picture is honest for a moment and dishonest after that. The window between the
/// two is what this names: long enough that a blip which recovers is never announced, short enough
/// that a phone which is genuinely gone stops being represented by a photograph of itself.</para>
/// <para>The duration is asserted against <see cref="RetryPolicy"/> rather than chosen in isolation,
/// because the two have to agree. Shorter than the first few retries and every recoverable stall
/// still flashes a panel, which is the whole fault being fixed. Longer than the retry budget and the
/// panel never appears at all before Orchard gives up, so the owner watches a still image for twenty
/// seconds and is then told it was never coming back.</para>
/// </remarks>
public static class HeldFrame
{
    /// <summary>How long a stopped stream's last frame stays on screen unremarked.</summary>
    public static readonly TimeSpan Believable = TimeSpan.FromSeconds(5);

    /// <summary>Whether a frame frozen this long ago is still worth showing without comment.</summary>
    public static bool StillBelievable(TimeSpan sinceInterruption) =>
        sinceInterruption >= TimeSpan.Zero && sinceInterruption < Believable;
}
