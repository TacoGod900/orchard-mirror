namespace Orchard.Mirror.Media;

/// <summary>What the session should do about the state of the stream.</summary>
public enum MirrorStreamAction
{
    /// <summary>Frames are arriving and being presented. Nothing to do.</summary>
    Streaming,

    /// <summary>The phone's screen went dark. Hold the session and wait to be told to wake it.</summary>
    Asleep,

    /// <summary>The phone just came back. Ordinary input applies again.</summary>
    Woke,

    /// <summary>Packets are flowing but no picture is reaching the screen; ask for a keyframe.</summary>
    RequestKeyframe,

    /// <summary>The session is unrecoverable in place and must be torn down.</summary>
    Reconnect,
}

/// <summary>
/// Decides whether a quiet stream means the phone is asleep, the decoder is stuck, or the session
/// is gone.
/// </summary>
/// <remarks>
/// <para>A dark phone and a dead session are indistinguishable from the stream alone: both simply
/// stop sending. The discriminator is the CoreDevice tunnel, so the caller probes it when
/// <see cref="NeedsTunnelProbe"/> says the answer matters, and passes the result in.</para>
/// <para>Getting this wrong is expensive rather than cosmetic. A sleeping iPhone stops advertising
/// RemotePairing entirely, so once the session is dropped there is nothing left to rediscover over
/// Wi-Fi — treating sleep as a lost session strands the user until they pick the phone up.</para>
/// </remarks>
public sealed class MirrorStreamMonitor
{
    /// <summary>
    /// How long the stream may be silent before it means something. Pulling the USB cable kills the
    /// tunnel instantly, so anything longer is dead time the user watches.
    /// </summary>
    public static readonly TimeSpan PacketStallLimit = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a picture may be stale before a fresh random-access frame is requested.</summary>
    private static readonly TimeSpan FrameStallLimit = TimeSpan.FromSeconds(1);

    /// <summary>How long keyframe requests may go unanswered before the session is abandoned.</summary>
    private static readonly TimeSpan FrameRecoveryLimit = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Brightest luma sample at or below which the phone's display is considered switched off.
    /// </summary>
    /// <remarks>
    /// Measured on the device: an off display peaks at about 5, a lit lock screen at 255. Peak
    /// rather than average is what keeps a dark-themed app -- black background, white text -- on
    /// the awake side of this line.
    /// </remarks>
    public const int DarkPeakLuma = 16;

    /// <summary>
    /// How long the picture must stay dark before the phone is called asleep.
    /// </summary>
    /// <remarks>
    /// Long enough to ride out a fade to black in a video or a screen transition, short enough that
    /// a user who locked the phone is not left watching a black window wondering.
    /// </remarks>
    private static readonly TimeSpan DarkSettleTime = TimeSpan.FromSeconds(2.5);

    /// <summary>Whether the phone is currently believed to be asleep.</summary>
    public bool IsAsleep { get; private set; }

    /// <summary>Does the tunnel have to be probed before <see cref="Evaluate"/> can decide?</summary>
    public static bool NeedsTunnelProbe(TimeSpan packetAge) => packetAge > PacketStallLimit;

    /// <summary>
    /// Classify the current state of the stream.
    /// </summary>
    /// <param name="packetAge">Time since the last RTP packet of any kind.</param>
    /// <param name="frameAge">Time since a decoded picture was last presented.</param>
    /// <param name="tunnelAlive">
    /// Whether the CoreDevice tunnel still answers. Only consulted when
    /// <see cref="NeedsTunnelProbe"/> is <see langword="true"/> for <paramref name="packetAge"/>.
    /// </param>
    /// <param name="darkFor">
    /// How long the decoded picture has been continuously dark, or <see cref="TimeSpan.Zero"/> when
    /// it is not. The phone keeps streaming while its display is off, so this — not silence — is
    /// what an ordinary sleep looks like.
    /// </param>
    /// <param name="deviceAwake">
    /// What the phone itself last said, or <see langword="null"/> when it has not been asked or
    /// could not answer. Only <see langword="true"/> changes anything: it means a gap in the stream
    /// is a stall rather than a sleep, because the device is known to be up.
    /// </param>
    /// <returns>
    /// <see cref="MirrorStreamAction.Woke"/> is returned exactly once per sleep, and the caller must
    /// restart its frame clock when it sees it: a phone that slept for a minute has a minute-old
    /// picture, and that is the sleep rather than a stall.
    /// </returns>
    public MirrorStreamAction Evaluate(
        TimeSpan packetAge,
        TimeSpan frameAge,
        bool tunnelAlive,
        TimeSpan darkFor = default,
        bool? deviceAwake = null)
    {
        if (NeedsTunnelProbe(packetAge))
        {
            if (!tunnelAlive)
            {
                IsAsleep = false;
                return MirrorStreamAction.Reconnect;
            }

            // Silence with a lit picture behind it is a phone nobody is touching.
            //
            // The screen encoder sends nothing while nothing changes, so resting on the home screen
            // is silent for as long as it lasts. Reading that as sleep is what announced a working
            // phone as asleep after a second and a half.
            //
            // The discriminator is the last picture rather than the silence, because a phone on its
            // way to sleep blacks its display out first and those frames do arrive. A bright last
            // picture on a live tunnel therefore means awake and idle -- and, unlike deviceAwake,
            // that is known for every phone. This one cannot answer at all: its lockState reply is
            // {"supported":false,"reason":"feature-not-implemented"}, so the guard that used to
            // stand here could never fire for it.
            //
            // A stale picture still earns a keyframe request: it costs one frame, and if the stream
            // has genuinely wedged rather than gone quiet it is the thing that unwedges it. What it
            // must never do is escalate. A phone sending nothing on purpose has an old picture by
            // definition, so letting frame age run on to Reconnect would trade a false sleep for a
            // false teardown -- the more expensive mistake, since a session dropped while the phone
            // is quiet may not be rediscoverable.
            if (darkFor == TimeSpan.Zero)
            {
                if (IsAsleep)
                {
                    IsAsleep = false;
                    return MirrorStreamAction.Woke;
                }

                return frameAge > FrameStallLimit
                    ? MirrorStreamAction.RequestKeyframe
                    : MirrorStreamAction.Streaming;
            }

            // Dark and silent, with nothing to say otherwise: an ordinary sleep. Held rather than
            // torn down, because a sleeping iPhone stops advertising RemotePairing and a dropped
            // session cannot be rediscovered until someone picks the phone up.
            if (deviceAwake != true)
            {
                IsAsleep = true;
                return MirrorStreamAction.Asleep;
            }
        }

        // The common sleep: the phone goes on streaming, and what it streams is black. Checked
        // before frame age, because a picture that is dark is still a picture arriving on time.
        if (darkFor >= DarkSettleTime)
        {
            IsAsleep = true;
            return MirrorStreamAction.Asleep;
        }

        if (IsAsleep)
        {
            IsAsleep = false;
            return MirrorStreamAction.Woke;
        }

        // Frame age is only meaningful once packets are known to be flowing, and only after any
        // sleep has been cleared above -- otherwise the first tick after waking reads as a hang.
        if (frameAge > FrameRecoveryLimit)
        {
            return MirrorStreamAction.Reconnect;
        }

        return frameAge > FrameStallLimit ? MirrorStreamAction.RequestKeyframe : MirrorStreamAction.Streaming;
    }
}
