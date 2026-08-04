using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// Whether a quiet stream means sleep or death. Both mistakes are expensive: calling sleep a dead
/// session strands the user, because a sleeping iPhone stops advertising RemotePairing and cannot
/// be rediscovered; calling a dead session sleep leaves a window that never recovers.
/// </summary>
internal static class MirrorStreamMonitorTests
{
    private static readonly TimeSpan Flowing = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Silent = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(50);

    /// <summary>Dark for longer than the monitor waits before believing it.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(3);

    internal static void ReportsStreamingWhileFramesArrive()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true) == MirrorStreamAction.Streaming,
            "A healthy stream is streaming.");
        Assert(!monitor.IsAsleep, "A healthy stream is not asleep.");
    }

    /// <summary>
    /// A phone that went dark and then stopped sending is asleep, and must be held rather than torn
    /// down: a sleeping iPhone stops advertising RemotePairing, so a dropped session cannot be
    /// rediscovered until someone picks the phone up.
    /// </summary>
    internal static void CallsASilentStreamFromADarkPhoneAsleep()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Silent, Fresh, tunnelAlive: true, darkFor: Settled) == MirrorStreamAction.Asleep,
            "A dark phone that stopped sending is asleep, not a dead session.");
        Assert(monitor.IsAsleep, "The monitor must remember it is asleep.");
    }

    /// <summary>
    /// Sitting on the home screen without touching anything is not sleep.
    /// </summary>
    /// <remarks>
    /// <para>The screen encoder sends nothing while nothing changes, so a still picture is silent —
    /// indefinitely. Silence was read as sleep whenever the phone had not just said otherwise, and
    /// this phone can never say otherwise: its lockState reply is
    /// <c>{"supported":false,"reason":"feature-not-implemented"}</c>, measured on the device. So the
    /// guard that was supposed to prevent this could never fire, and a phone resting on its home
    /// screen was announced as asleep after a second and a half.</para>
    /// <para>What separates the two is the last picture, not the silence. A phone on its way to
    /// sleep blacks its display out first and those frames do arrive, so a bright last picture with
    /// a live tunnel means awake and idle.</para>
    /// </remarks>
    internal static void DoesNotCallAStillHomeScreenAsleep()
    {
        MirrorStreamMonitor monitor = new();
        MirrorStreamAction action = monitor.Evaluate(
            Silent, Silent, tunnelAlive: true, darkFor: TimeSpan.Zero, deviceAwake: null);

        Assert(
            action != MirrorStreamAction.Asleep,
            "A lit, unchanging screen was announced as asleep because it had nothing to send.");
        Assert(!monitor.IsAsleep, "The monitor recorded a sleep for a phone that was awake and idle.");
    }

    /// <summary>
    /// Nor may an idle phone be torn down. Frame age is meaningless when the phone is deliberately
    /// sending nothing, so letting it escalate would trade a false sleep for a false reconnect —
    /// and reconnecting is the more expensive mistake.
    /// </summary>
    internal static void LeavesALongIdleSessionAlone()
    {
        MirrorStreamMonitor monitor = new();
        foreach (TimeSpan idle in new[] { Silent, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5) })
        {
            MirrorStreamAction action = monitor.Evaluate(
                idle, idle, tunnelAlive: true, darkFor: TimeSpan.Zero, deviceAwake: null);

            // A keyframe request is fine and even useful — it costs one frame and proves the
            // session is alive. Sleeping or tearing down is what an idle phone must never provoke.
            Assert(
                action is not (MirrorStreamAction.Reconnect or MirrorStreamAction.Asleep),
                $"A phone idle for {idle.TotalSeconds:N0}s on a live tunnel was {action}.");
        }
    }

    /// <summary>
    /// A gap in the stream from a phone that has just said it is up is a stall, not a sleep.
    /// </summary>
    /// <remarks>
    /// The reported symptom was "the stream randomly says my phone is sleeping when it isn't". Any
    /// silence over a second and a half was being called sleep on the strength of nothing but the
    /// silence, and on Wi-Fi that is an ordinary hiccup. The session polls the phone's lock state on
    /// the same loop, so the answer was already in hand and simply not consulted.
    /// </remarks>
    internal static void DoesNotCallAnAwakeDeviceAsleepWhenTheStreamGapsMomentarily()
    {
        MirrorStreamMonitor monitor = new();
        MirrorStreamAction action = monitor.Evaluate(
            Silent, Silent, tunnelAlive: true, darkFor: TimeSpan.Zero, deviceAwake: true);

        Assert(
            action != MirrorStreamAction.Asleep,
            "A phone that has just reported itself unlocked was announced as asleep over a stream gap.");
        Assert(
            !monitor.IsAsleep,
            "The monitor recorded a sleep for a device it had been told was awake.");
        Assert(
            action == MirrorStreamAction.RequestKeyframe,
            $"A stalled but awake stream should ask for a keyframe, not {action}.");
    }

    /// <summary>
    /// A dark picture is still a sleep, whatever the phone last said about being unlocked.
    /// </summary>
    /// <remarks>
    /// This is the pair to the test above and the reason the fix is not simply "trust the device".
    /// Darkness is the signal an ordinary sleep actually produces, because the phone keeps streaming
    /// with its display off. If believing the lock state suppressed that too, the fix would have
    /// traded a false sleep for a phone that never reports sleeping at all.
    /// </remarks>
    internal static void StillCallsADarkPictureAsleepWhileTheDeviceReportsAwake()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Fresh, Fresh, tunnelAlive: true, darkFor: TimeSpan.FromSeconds(3), deviceAwake: true)
                == MirrorStreamAction.Asleep,
            "A display that has been black for three seconds is asleep however recently it was unlocked.");
        Assert(monitor.IsAsleep, "The monitor must remember it is asleep.");
    }

    internal static void CallsASilentStreamWithADeadTunnelLost()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Silent, Fresh, tunnelAlive: false) == MirrorStreamAction.Reconnect,
            "Silence with no tunnel is a lost session.");
        Assert(!monitor.IsAsleep, "A lost session is not a sleeping one.");
    }

    /// <summary>
    /// The regression this type exists for. A phone that slept for a minute has a minute-old
    /// picture, and reading that as a hung decoder reconnects on the very first tick after it woke
    /// — which then cannot succeed, because it is the reconnect that strands the session.
    /// </summary>
    internal static void DoesNotMistakeASleptThroughGapForAHungDecoder()
    {
        MirrorStreamMonitor monitor = new();
        _ = monitor.Evaluate(Silent, Fresh, tunnelAlive: true, darkFor: Settled);
        Assert(monitor.IsAsleep, "Precondition: the phone is asleep.");

        TimeSpan sleptThrough = TimeSpan.FromMinutes(1);
        Assert(
            monitor.Evaluate(Flowing, sleptThrough, tunnelAlive: true) == MirrorStreamAction.Woke,
            "The first tick after packets resume must report waking, not a stalled picture.");
        Assert(!monitor.IsAsleep, "Waking clears the asleep state.");
    }

    /// <summary>Waking is announced once, not on every tick that follows it.</summary>
    internal static void ReportsWakingOnlyOnce()
    {
        MirrorStreamMonitor monitor = new();
        _ = monitor.Evaluate(Silent, Fresh, tunnelAlive: true, darkFor: Settled);
        Assert(monitor.IsAsleep, "Precondition: the phone is asleep.");

        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true) == MirrorStreamAction.Woke,
            "The first tick after the picture returns must report waking.");
        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true) == MirrorStreamAction.Streaming,
            "The tick after waking is an ordinary streaming tick.");
    }

    /// <summary>
    /// Sleep must not swallow the stall recovery that existed before it: packets flowing with no
    /// picture is still a decoder that needs a fresh random-access frame.
    /// </summary>
    internal static void StillAsksForAKeyframeWhenThePictureStallsWhileAwake()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Flowing, TimeSpan.FromSeconds(2), tunnelAlive: true) == MirrorStreamAction.RequestKeyframe,
            "Packets without pictures must still request a keyframe.");
        Assert(
            monitor.Evaluate(Flowing, TimeSpan.FromSeconds(6), tunnelAlive: true) == MirrorStreamAction.Reconnect,
            "A picture that never recovers must still end the session.");
    }

    /// <summary>The tunnel is only worth probing when the answer changes the decision.</summary>
    internal static void OnlyProbesTheTunnelWhenTheStreamIsSilent()
    {
        Assert(!MirrorStreamMonitor.NeedsTunnelProbe(Flowing), "A flowing stream needs no probe.");
        Assert(MirrorStreamMonitor.NeedsTunnelProbe(Silent), "A silent stream needs a probe.");
    }

    /// <summary>
    /// The sleep that actually happens. The phone keeps streaming with its display off, so the
    /// packets never stop and only the picture going black gives it away.
    /// </summary>
    internal static void CallsAStreamingButDarkPictureAsleep()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true, darkFor: TimeSpan.FromSeconds(4))
                == MirrorStreamAction.Asleep,
            "A dark picture arriving on time is a phone with its screen off.");
        Assert(monitor.IsAsleep, "The monitor must remember it is asleep.");
    }

    /// <summary>
    /// The mistake that would be worse than the bug. A fade to black, a video cutting to a dark
    /// shot, or a screen transition must not press Home under someone who is watching.
    /// </summary>
    internal static void DoesNotCallABriefDarkMomentSleep()
    {
        MirrorStreamMonitor monitor = new();
        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true, darkFor: TimeSpan.FromSeconds(1))
                == MirrorStreamAction.Streaming,
            "A second of darkness is content, not sleep.");
        Assert(!monitor.IsAsleep, "A brief dark moment must not latch the asleep state.");
    }

    /// <summary>A picture that lights up again ends the sleep, exactly once.</summary>
    internal static void WakesWhenThePictureLightsUpAgain()
    {
        MirrorStreamMonitor monitor = new();
        _ = monitor.Evaluate(Flowing, Fresh, tunnelAlive: true, darkFor: TimeSpan.FromSeconds(4));
        Assert(monitor.IsAsleep, "Precondition: asleep.");

        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true, darkFor: TimeSpan.Zero) == MirrorStreamAction.Woke,
            "A lit picture ends the sleep.");
        Assert(
            monitor.Evaluate(Flowing, Fresh, tunnelAlive: true, darkFor: TimeSpan.Zero) == MirrorStreamAction.Streaming,
            "And says so only once.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
