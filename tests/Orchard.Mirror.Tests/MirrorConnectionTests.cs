using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The connection's state used to live in scattered fields that could not express what they were
/// asked to, and the two defects that produced are the ones written as stories below: a disconnect
/// pressed during a reconnect did not stick, and reconnecting sent the owner to a walkthrough that
/// waits for a USB cable they may not need.
/// </summary>
/// <remarks>
/// Written as sequences of things that happen to a person, never against the machine's own
/// branches. A test shaped like the implementation agrees with it and confirms nothing.
/// </remarks>
internal static class MirrorConnectionTests
{
    /// <summary>
    /// The disconnect race. The old retry loop checked only a "closing" flag, never the one the
    /// disconnect set, so pressing Shift-Escape while it said "Reconnecting" tore the session down
    /// and then connected again anyway, painting over the screen the owner had just asked for.
    /// </summary>
    internal static void DisconnectDuringARetryStaysDisconnected()
    {
        MirrorConnectionMachine machine = Started();
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.FirstFrame));
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.StreamInterrupted));
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.UserDisconnect));

        // The retry that was already pending now comes due.
        IReadOnlyList<ConnectionCommand> afterRetry =
            machine.Handle(new ConnectionEvent(ConnectionTrigger.RetryDue));

        Assert(
            machine.State == ConnectionState.UserDisconnected,
            $"a disconnect must stick; the machine went to {machine.State}");
        Assert(
            !Contains(afterRetry, CommandKind.StartAttempt),
            "a retry that was already in flight pulled the phone back after the owner let it go");
    }

    /// <summary>
    /// The Wi-Fi trap. Reconnect was wired to the first-run walkthrough, which polls a USB-only
    /// status forever — so an owner mirroring over Wi-Fi who disconnected was stuck on "Plug in
    /// your iPhone" with no way back.
    /// </summary>
    internal static void ReconnectAfterDisconnectNeverRunsTheWalkthrough()
    {
        // Deliberately the harder case: this owner has never completed onboarding, so anything
        // keyed off that marker rather than off the state would send them to the cable page.
        MirrorConnectionMachine machine = new();
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.Shown, OnboardingComplete: false));
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.SetupSkipped));
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.FirstFrame));
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.UserDisconnect));

        IReadOnlyList<ConnectionCommand> reconnect =
            machine.Handle(new ConnectionEvent(ConnectionTrigger.UserReconnect));

        Assert(
            Contains(reconnect, CommandKind.StartAttempt),
            "pressing Reconnect must actually try to connect");
        Assert(
            !Contains(reconnect, CommandKind.RunGuidedSetup),
            "Reconnect sent the owner to the first-run cable walkthrough, which Wi-Fi cannot satisfy");
    }

    /// <summary>The walkthrough belongs to a cold start and nowhere else.</summary>
    internal static void RunsTheWalkthroughOnlyFromAColdStart()
    {
        MirrorConnectionMachine machine = new();
        Assert(
            Contains(machine.Handle(new ConnectionEvent(ConnectionTrigger.Shown, OnboardingComplete: false)), CommandKind.RunGuidedSetup),
            "a first run must offer the walkthrough");

        // Nothing that can happen afterwards may start it again.
        foreach (ConnectionTrigger trigger in Enum.GetValues<ConnectionTrigger>())
        {
            if (trigger == ConnectionTrigger.WindowClosing)
            {
                continue;
            }

            Assert(
                !Contains(machine.Handle(new ConnectionEvent(trigger)), CommandKind.RunGuidedSetup),
                $"{trigger} restarted the first-run walkthrough");
        }
    }

    /// <summary>
    /// A phone left in a drawer used to make Orchard spawn an agent, open a tunnel and negotiate a
    /// DisplayService session every few seconds for as long as the window was open.
    /// </summary>
    internal static void GivesUpAfterABoundedNumberOfAttempts()
    {
        MirrorConnectionMachine machine = Started();
        for (int attempt = 0; attempt < RetryPolicy.MaxAttempts + 4; attempt++)
        {
            _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.AttemptFailed));
            _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.RetryDue));
        }

        Assert(
            machine.State == ConnectionState.GaveUp,
            $"Orchard kept trying forever; it ended in {machine.State}");
        Assert(
            !Contains(machine.Handle(new ConnectionEvent(ConnectionTrigger.RetryDue)), CommandKind.StartAttempt),
            "a machine that gave up still started another attempt when a stale retry came due");
        Assert(
            Contains(machine.Handle(new ConnectionEvent(ConnectionTrigger.UserReconnect)), CommandKind.StartAttempt),
            "having given up, asking again must still work");
    }

    /// <summary>Getting the mirror back must buy a full set of retries, not the tail of the last set.</summary>
    internal static void AFreshPictureEarnsAFreshRetryBudget()
    {
        MirrorConnectionMachine machine = Started();
        for (int attempt = 0; attempt < RetryPolicy.MaxAttempts - 1; attempt++)
        {
            _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.AttemptFailed));
            _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.RetryDue));
        }

        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.FirstFrame));
        Assert(machine.FailedAttempts == 0, "a picture on screen must clear the failure count");

        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.StreamInterrupted));
        Assert(
            machine.State == ConnectionState.WaitingToRetry,
            "a stream that broke after a healthy session must be retried, not given up on");
    }

    /// <summary>
    /// A sleeping phone is not a lost one. The session is intact; tearing it down and reconnecting
    /// would be both slow and, per the recorded DisplayService churn, actively harmful.
    /// </summary>
    internal static void ASleepingPhoneIsNotALostConnection()
    {
        MirrorConnectionMachine machine = Started();
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.FirstFrame));

        IReadOnlyList<ConnectionCommand> asleep =
            machine.Handle(new ConnectionEvent(ConnectionTrigger.PhoneAsleep));

        Assert(machine.State == ConnectionState.Asleep, "a dark screen must be its own state");
        Assert(!Contains(asleep, CommandKind.TearDown), "a sleeping phone must not be torn down");
        Assert(!Contains(asleep, CommandKind.ScheduleRetry), "a sleeping phone must not be retried");

        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.PhoneAwake));
        Assert(machine.State == ConnectionState.Live, "waking must return to the live mirror");
    }

    /// <summary>Whatever the window was doing, closing it releases the phone once.</summary>
    internal static void ClosingTearsDownExactlyOnce()
    {
        foreach (ConnectionTrigger reached in Enum.GetValues<ConnectionTrigger>())
        {
            if (reached == ConnectionTrigger.WindowClosing)
            {
                continue;
            }

            MirrorConnectionMachine machine = Started();
            _ = machine.Handle(new ConnectionEvent(reached));

            Assert(
                Count(machine.Handle(new ConnectionEvent(ConnectionTrigger.WindowClosing)), CommandKind.TearDown) == 1,
                $"closing after {reached} did not release the phone exactly once");
            Assert(
                machine.State == ConnectionState.ShuttingDown,
                $"closing after {reached} left the machine in {machine.State}");
            Assert(
                !Contains(machine.Handle(new ConnectionEvent(ConnectionTrigger.RetryDue)), CommandKind.StartAttempt),
                $"a closing window started a new attempt after {reached}");
        }
    }

    /// <summary>
    /// Closing is not idempotent by accident. WinForms can raise the closing path more than once,
    /// and a second teardown would race the first over the same agent and session.
    /// </summary>
    internal static void ClosingTwiceTearsDownOnlyOnce()
    {
        MirrorConnectionMachine machine = Started();
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.FirstFrame));

        int first = Count(machine.Handle(new ConnectionEvent(ConnectionTrigger.WindowClosing)), CommandKind.TearDown);
        int second = Count(machine.Handle(new ConnectionEvent(ConnectionTrigger.WindowClosing)), CommandKind.TearDown);

        Assert(first == 1, "the first close must release the phone");
        Assert(second == 0, "a second close asked for another teardown of an already-released session");
    }

    /// <summary>
    /// Stated as a budget rather than by restating the constants: what matters is that an
    /// unreachable phone costs a bounded amount of churn, and that waiting never gets shorter.
    /// </summary>
    internal static void BoundsHowLongItKeepsTrying()
    {
        TimeSpan previous = TimeSpan.Zero;
        for (int attempt = 1; attempt <= RetryPolicy.MaxAttempts; attempt++)
        {
            TimeSpan delay = RetryPolicy.Delay(attempt);
            Assert(delay >= previous, $"the wait before attempt {attempt} got shorter");
            Assert(delay <= TimeSpan.FromSeconds(5), $"attempt {attempt} waits an unreasonably long time");
            previous = delay;
        }

        Assert(
            RetryPolicy.TotalBudget < TimeSpan.FromMinutes(1),
            "a phone that is simply not there should be given up on inside a minute");
        Assert(
            RetryPolicy.TotalBudget > TimeSpan.FromSeconds(5),
            "a brief stall must not be mistaken for an absent phone");
    }

    /// <summary>A machine that has been shown and is connecting, which is where most stories start.</summary>
    private static MirrorConnectionMachine Started()
    {
        MirrorConnectionMachine machine = new();
        _ = machine.Handle(new ConnectionEvent(ConnectionTrigger.Shown, OnboardingComplete: true));
        return machine;
    }

    private static int Count(IReadOnlyList<ConnectionCommand> commands, CommandKind kind)
    {
        int found = 0;
        foreach (ConnectionCommand command in commands)
        {
            if (command.Kind == kind)
            {
                found++;
            }
        }

        return found;
    }

    private static bool Contains(IReadOnlyList<ConnectionCommand> commands, CommandKind kind)
    {
        foreach (ConnectionCommand command in commands)
        {
            if (command.Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
