namespace Orchard.Mirror.Shell;

/// <summary>Where the connection to the phone has got to.</summary>
public enum ConnectionState
{
    /// <summary>Nothing has been started yet.</summary>
    Idle,

    /// <summary>The first-run walkthrough is running.</summary>
    GuidedSetup,

    /// <summary>An attempt to reach the phone is in flight.</summary>
    Connecting,

    /// <summary>A picture is on screen.</summary>
    Live,

    /// <summary>The phone's screen is off. The session is intact and waiting to be woken.</summary>
    Asleep,

    /// <summary>An attempt failed and the next one is pending.</summary>
    WaitingToRetry,

    /// <summary>Orchard has stopped trying and will not start again unasked.</summary>
    GaveUp,

    /// <summary>The owner asked to stop. Nothing may pull the phone back.</summary>
    UserDisconnected,

    /// <summary>The window is closing.</summary>
    ShuttingDown,
}

/// <summary>Something that happened to the connection.</summary>
public enum ConnectionTrigger
{
    /// <summary>The window appeared.</summary>
    Shown,

    /// <summary>The walkthrough finished.</summary>
    SetupFinished,

    /// <summary>The owner said they had done this before.</summary>
    SetupSkipped,

    /// <summary>A frame reached the screen.</summary>
    FirstFrame,

    /// <summary>An attempt to connect failed.</summary>
    AttemptFailed,

    /// <summary>A running stream broke.</summary>
    StreamInterrupted,

    /// <summary>The phone's screen went off.</summary>
    PhoneAsleep,

    /// <summary>The phone's screen came back.</summary>
    PhoneAwake,

    /// <summary>The owner asked to stop mirroring.</summary>
    UserDisconnect,

    /// <summary>The owner asked to start again.</summary>
    UserReconnect,

    /// <summary>The wait before the next attempt has elapsed.</summary>
    RetryDue,

    /// <summary>The window is closing.</summary>
    WindowClosing,
}

/// <summary>One thing that happened.</summary>
/// <param name="Trigger">What happened.</param>
/// <param name="OnboardingComplete">
/// Only consulted on <see cref="ConnectionTrigger.Shown"/>. Whether the owner has been through the
/// walkthrough before.
/// </param>
public readonly record struct ConnectionEvent(ConnectionTrigger Trigger, bool OnboardingComplete = false);

/// <summary>Something the window must actually do.</summary>
public enum CommandKind
{
    /// <summary>Run the first-run walkthrough. Only ever from a cold start.</summary>
    RunGuidedSetup,

    /// <summary>Start one connection attempt.</summary>
    StartAttempt,

    /// <summary>Release the agent, session and input.</summary>
    TearDown,

    /// <summary>Come back after a delay.</summary>
    ScheduleRetry,

    /// <summary>Abandon any pending retry.</summary>
    CancelRetry,
}

/// <summary>One instruction to the window.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Delay">How long to wait, for <see cref="CommandKind.ScheduleRetry"/>.</param>
public readonly record struct ConnectionCommand(CommandKind Kind, TimeSpan Delay = default);

/// <summary>How long to wait between attempts, and when to stop.</summary>
/// <remarks>
/// A dropped cable or a brief Wi-Fi stall is usually recoverable on the very next attempt, so the
/// first wait is almost nothing. The cap matters more: without one, a phone left in a drawer makes
/// Orchard spawn an agent process, open a tunnel and negotiate a DisplayService session every few
/// seconds for as long as the window is open — and that churn is itself the thing recorded as
/// wedging the phone's media daemon.
/// </remarks>
public static class RetryPolicy
{
    /// <summary>How many consecutive failures before Orchard stops trying unasked.</summary>
    public const int MaxAttempts = 8;

    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(4);

    /// <summary>The wait before attempt number <paramref name="attempt"/>, counting from one.</summary>
    public static TimeSpan Delay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        double seconds = First.TotalSeconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(Ceiling.TotalSeconds, seconds));
    }

    /// <summary>The longest Orchard will keep trying before it stops and says so.</summary>
    public static TimeSpan TotalBudget
    {
        get
        {
            TimeSpan total = TimeSpan.Zero;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                total += Delay(attempt);
            }

            return total;
        }
    }
}

/// <summary>
/// The connection's whole state, in one place that can be tested.
/// </summary>
/// <remarks>
/// <para>This replaces a set of scattered fields that could not express what they were being asked
/// to. One int was overloaded as both "a retry loop is running" and "the owner asked to stop", so a
/// disconnect pressed during a reconnect set the flag the loop never read — the loop tore down,
/// reconnected anyway, and painted over the "Disconnected" screen the owner had just asked for.
/// Two more fields tracked shutting-down, two tracked sleep, and "connected" was inferred by
/// null-checking three separate references that were assigned outside the lock that guarded them.</para>
/// <para>The other defect it closes is a routing one. Reconnect used to be wired to the first-run
/// walkthrough, which waits for a USB cable — so an owner mirroring over Wi-Fi who disconnected
/// could never get back without plugging in. That the walkthrough runs only from a cold start is
/// now an invariant of this machine rather than a convention at a call site.</para>
/// <para>No timers, no tasks, no I/O: it answers what should happen and the window does it.</para>
/// </remarks>
public sealed class MirrorConnectionMachine
{
    /// <summary>Where the connection has got to.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    /// <summary>Consecutive failures since the last frame or explicit request.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Whether the walkthrough has ever been started.</summary>
    public bool WalkthroughStarted { get; private set; }

    /// <summary>Advance the machine and say what the window should do.</summary>
    public IReadOnlyList<ConnectionCommand> Handle(ConnectionEvent connectionEvent)
    {
        // Nothing restarts a closing window, and closing twice releases the phone once. WinForms
        // can raise its closing path more than one time, and a second teardown would race the first
        // over the same agent and session.
        if (State == ConnectionState.ShuttingDown)
        {
            return [];
        }

        // The three the owner asks for outrank whatever the connection happened to be doing. That
        // is the whole point: a retry already in flight must not outvote someone standing up to
        // leave.
        switch (connectionEvent.Trigger)
        {
            case ConnectionTrigger.WindowClosing:
                State = ConnectionState.ShuttingDown;
                return [new ConnectionCommand(CommandKind.CancelRetry), new ConnectionCommand(CommandKind.TearDown)];

            case ConnectionTrigger.UserDisconnect:
                State = ConnectionState.UserDisconnected;
                FailedAttempts = 0;
                return [new ConnectionCommand(CommandKind.CancelRetry), new ConnectionCommand(CommandKind.TearDown)];

            case ConnectionTrigger.UserReconnect:
                // Never the walkthrough. It waits for a USB cable, and an owner mirroring over
                // Wi-Fi who pressed Reconnect used to be stranded on "Plug in your iPhone".
                State = ConnectionState.Connecting;
                FailedAttempts = 0;
                return [new ConnectionCommand(CommandKind.StartAttempt)];

            default:
                break;
        }

        return State switch
        {
            ConnectionState.Idle => FromIdle(connectionEvent),
            ConnectionState.GuidedSetup => FromGuidedSetup(connectionEvent),
            ConnectionState.Connecting => FromConnecting(connectionEvent),
            ConnectionState.Live or ConnectionState.Asleep => FromRunning(connectionEvent),
            ConnectionState.WaitingToRetry => FromWaitingToRetry(connectionEvent),

            // GaveUp and UserDisconnected are deliberately inert. Only the owner restarts them, and
            // that is handled above — a stale retry arriving here must do nothing at all.
            _ => [],
        };
    }

    private IReadOnlyList<ConnectionCommand> FromIdle(ConnectionEvent connectionEvent)
    {
        if (connectionEvent.Trigger != ConnectionTrigger.Shown)
        {
            return [];
        }

        if (connectionEvent.OnboardingComplete)
        {
            State = ConnectionState.Connecting;
            return [new ConnectionCommand(CommandKind.StartAttempt)];
        }

        // The only place the walkthrough is ever started. A first run is instructions, not a
        // connection attempt: the phone almost certainly is not in a state to be found yet.
        State = ConnectionState.GuidedSetup;
        WalkthroughStarted = true;
        return [new ConnectionCommand(CommandKind.RunGuidedSetup)];
    }

    private IReadOnlyList<ConnectionCommand> FromGuidedSetup(ConnectionEvent connectionEvent)
    {
        switch (connectionEvent.Trigger)
        {
            case ConnectionTrigger.SetupFinished:
            case ConnectionTrigger.SetupSkipped:
                State = ConnectionState.Connecting;
                return [new ConnectionCommand(CommandKind.StartAttempt)];

            case ConnectionTrigger.AttemptFailed:
                // Setup stopped on something only the owner can resolve; retrying it on a timer
                // would just repeat the same question.
                State = ConnectionState.GaveUp;
                return [new ConnectionCommand(CommandKind.TearDown)];

            default:
                return [];
        }
    }

    private IReadOnlyList<ConnectionCommand> FromConnecting(ConnectionEvent connectionEvent) =>
        connectionEvent.Trigger switch
        {
            ConnectionTrigger.FirstFrame => Live(),
            ConnectionTrigger.AttemptFailed or ConnectionTrigger.StreamInterrupted => Failed(),
            _ => [],
        };

    private IReadOnlyList<ConnectionCommand> FromRunning(ConnectionEvent connectionEvent)
    {
        switch (connectionEvent.Trigger)
        {
            case ConnectionTrigger.PhoneAsleep:
                // The session is intact. Tearing it down and reconnecting would be slow and, per
                // the recorded DisplayService churn, actively harmful.
                State = ConnectionState.Asleep;
                return [];

            case ConnectionTrigger.PhoneAwake:
            case ConnectionTrigger.FirstFrame:
                return Live();

            case ConnectionTrigger.StreamInterrupted:
                return Failed();

            default:
                return [];
        }
    }

    private IReadOnlyList<ConnectionCommand> FromWaitingToRetry(ConnectionEvent connectionEvent) =>
        connectionEvent.Trigger switch
        {
            ConnectionTrigger.RetryDue => StartAttempt(),
            ConnectionTrigger.FirstFrame => Live(),
            _ => [],
        };

    private IReadOnlyList<ConnectionCommand> StartAttempt()
    {
        State = ConnectionState.Connecting;
        return [new ConnectionCommand(CommandKind.StartAttempt)];
    }

    /// <summary>A picture on screen clears the failure count: getting back earns a full set of tries.</summary>
    private IReadOnlyList<ConnectionCommand> Live()
    {
        State = ConnectionState.Live;
        FailedAttempts = 0;
        return [];
    }

    private IReadOnlyList<ConnectionCommand> Failed()
    {
        FailedAttempts++;
        if (FailedAttempts >= RetryPolicy.MaxAttempts)
        {
            State = ConnectionState.GaveUp;
            return [new ConnectionCommand(CommandKind.TearDown)];
        }

        State = ConnectionState.WaitingToRetry;
        return
        [
            new ConnectionCommand(CommandKind.TearDown),
            new ConnectionCommand(CommandKind.ScheduleRetry, RetryPolicy.Delay(FailedAttempts)),
        ];
    }
}
