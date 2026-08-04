using Orchard.Mirror.Agent.Windows;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// How long Orchard spends letting go of the phone. Both mistakes have been made here: one budget
/// for every case, chosen for the case where the phone is already unreachable, and then applied to
/// the cases where it is not.
/// </summary>
/// <remarks>
/// These are claims about consequences — that a graceful goodbye is actually waited for, and that a
/// pulled cable still recovers quickly — rather than restatements of the constants.
/// </remarks>
internal static class TeardownPlanTests
{
    /// <summary>
    /// On a path where the phone is still reachable, the stopAll has to be waited for. Losing it is
    /// what leaves the media daemon unable to accept another DisplayService channel.
    /// </summary>
    internal static void WaitsLongEnoughForTheStopThatMatters()
    {
        foreach (TeardownReason reason in (TeardownReason[])[TeardownReason.WindowClosing, TeardownReason.UserDisconnect])
        {
            TeardownPlan plan = Teardown.For(reason);

            Assert(plan.TunnelPresumedAlive, $"{reason} happens with the phone still reachable");
            Assert(
                plan.StopVideo + plan.AgentExit >= Teardown.AgentStopAllTimeout,
                $"{reason} abandons the phone before its own stopAll can finish");
        }
    }

    /// <summary>
    /// A pulled cable must still recover fast. Waiting on a tunnel that is already gone is what
    /// made the window freeze for seconds, and that regression is easy to reintroduce by making
    /// every path patient.
    /// </summary>
    internal static void GivesUpQuicklyOnAPhoneThatCannotHearIt()
    {
        TeardownPlan plan = Teardown.For(TeardownReason.StreamInterrupted);

        Assert(!plan.TunnelPresumedAlive, "an interrupted stream cannot assume the phone is reachable");
        Assert(
            plan.Total <= TimeSpan.FromSeconds(4.5),
            $"recovering from a pulled cable would take {plan.Total.TotalSeconds:F1}s of waiting first");
        Assert(
            plan.StopVideo < Teardown.For(TeardownReason.UserDisconnect).StopVideo,
            "a dead tunnel was given the same patience as a live one");
    }

    /// <summary>Every path has to release the touch it may be holding, or the phone keeps it.</summary>
    /// <summary>
    /// A teardown that asks the agent to stop must outlast the agent's own stop budget. Bounding
    /// that wait more tightly is worse than not waiting at all: it returns while the agent is still
    /// running, the next attempt starts a second one, and the phone talks to only one of them —
    /// the reconnect loop that only restarting the app escaped. The interrupted plan allowed 1s
    /// against a 2s budget, and is now resolved the other way, by killing rather than asking.
    /// </summary>
    internal static void AGoodbyeIsEitherWaitedForOrNotAttempted()
    {
        foreach (TeardownReason reason in Enum.GetValues<TeardownReason>())
        {
            TeardownPlan plan = Teardown.For(reason);
            if (!plan.TunnelPresumedAlive)
            {
                // No stop is sent, so there is nothing to outlast; the budget covers a kill.
                continue;
            }

            Assert(
                plan.AgentExit >= CoreDeviceAgentClient.StopBudget,
                $"{reason} asks the agent to stop but allows only {plan.AgentExit.TotalSeconds:N1}s, "
                    + $"and stopping takes up to {CoreDeviceAgentClient.StopBudget.TotalSeconds:N1}s; "
                    + "the wait would end while it is still alive");
        }
    }

    internal static void AlwaysReleasesTheHeldTouch()
    {
        foreach (TeardownReason reason in Enum.GetValues<TeardownReason>())
        {
            Assert(
                Teardown.For(reason).InputRelease > TimeSpan.Zero,
                $"{reason} left a touch contact held on the phone");
        }
    }

    /// <summary>No path may be unbounded, or a wedged agent traps the window.</summary>
    internal static void BoundsEveryPath()
    {
        foreach (TeardownReason reason in Enum.GetValues<TeardownReason>())
        {
            TeardownPlan plan = Teardown.For(reason);

            Assert(
                plan.Total > TimeSpan.Zero && plan.Total <= TimeSpan.FromSeconds(12),
                $"{reason} has a total budget of {plan.Total.TotalSeconds:F1}s");
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
