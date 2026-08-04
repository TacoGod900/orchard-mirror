namespace Orchard.Mirror.Shell;

/// <summary>Why a session is being taken down, which decides how patient it is worth being.</summary>
public enum TeardownReason
{
    /// <summary>The owner closed the window.</summary>
    WindowClosing,

    /// <summary>The owner pressed disconnect but kept the window.</summary>
    UserDisconnect,

    /// <summary>The stream broke and Orchard is reconnecting.</summary>
    StreamInterrupted,

    /// <summary>An attempt failed, usually before there was a session at all.</summary>
    AttemptFailed,
}

/// <summary>How long each step of a teardown is given.</summary>
/// <param name="InputRelease">Releasing a held touch contact.</param>
/// <param name="StopVideo">Telling the phone to stop the paired media session.</param>
/// <param name="SessionStop">Closing sockets, decoder and presenter after that.</param>
/// <param name="AgentExit">Letting the agent process finish its own teardown and exit.</param>
/// <param name="TunnelPresumedAlive">
/// Whether the phone can still be reached. When it cannot, waiting on it is pure delay.
/// </param>
public readonly record struct TeardownPlan(
    TimeSpan InputRelease,
    TimeSpan StopVideo,
    TimeSpan SessionStop,
    TimeSpan AgentExit,
    bool TunnelPresumedAlive)
{
    /// <summary>The longest the whole teardown can take.</summary>
    public TimeSpan Total => InputRelease + StopVideo + SessionStop + AgentExit;
}

/// <summary>
/// How long to spend letting go of the phone, which is not the same question every time.
/// </summary>
/// <remarks>
/// <para>The budget used to be a single 700 ms on every path. That number was chosen for one case —
/// a cable pulled out, where the tunnel is already gone and waiting on it is what made the window
/// freeze for seconds — and then applied to the cases where the phone is still perfectly reachable
/// and the message being abandoned is the one that matters.</para>
/// <para>The message that matters is <c>stopAll</c>, which ends the paired AVConference session.
/// The agent's own note records what losing it costs: the phone's media daemon can be left unable
/// to accept another DisplayService channel until the device is restarted. Cycling connect and
/// disconnect is exactly how a person uses this app, so an abandon budget on that path turns a rare
/// wedge into a reliable one.</para>
/// </remarks>
public static class Teardown
{
    /// <summary>
    /// The agent's own timeout on the stopAll it sends to the phone. A graceful teardown has to
    /// leave room for at least this, or it abandons the one message it was waiting for.
    /// </summary>
    public static readonly TimeSpan AgentStopAllTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The budget for one teardown.</summary>
    public static TeardownPlan For(TeardownReason reason) => reason switch
    {
        // The phone is there and will be asked to do things again. Spend the time.
        TeardownReason.WindowClosing or TeardownReason.UserDisconnect => new TeardownPlan(
            InputRelease: TimeSpan.FromMilliseconds(300),
            StopVideo: TimeSpan.FromSeconds(2.5),
            SessionStop: TimeSpan.FromMilliseconds(700),
            AgentExit: TimeSpan.FromSeconds(2.5),
            TunnelPresumedAlive: true),

        // The tunnel is presumed gone. Recovering sooner beats a goodbye nothing will hear, and
        // this is the path the measured cable-unplug recovery depends on.
        // The agent is killed outright here rather than asked to stop, so AgentExit only has to
        // cover a process kill. Asking first is what broke this: the polite stop takes up to two
        // seconds, the wait was bounded at one, so it returned while the agent was still alive and
        // the retry started a second one — and the phone talks to only one agent. Waiting the full
        // two seconds instead would have fixed that at the cost of the quick recovery this case
        // exists for, so the phone that cannot hear the goodbye simply does not get one.
        _ => new TeardownPlan(
            InputRelease: TimeSpan.FromMilliseconds(300),
            StopVideo: TimeSpan.FromMilliseconds(700),
            SessionStop: TimeSpan.FromSeconds(2),
            AgentExit: TimeSpan.FromSeconds(1),
            TunnelPresumedAlive: false),
    };
}
