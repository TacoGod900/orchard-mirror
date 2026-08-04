using System.Diagnostics;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>
/// Every agent process this app has started, so none of them can outlive it.
/// </summary>
/// <remarks>
/// <para>The agent exits on its own when its stdin reaches EOF, which covers the ordinary case and
/// is the only reason an abrupt shutdown has been survivable so far. It does not cover a crash, and
/// it does not cover a process that is part-way through its own teardown when the parent dies.</para>
/// <para>An orphaned agent matters more here than an orphaned process usually does: it holds the
/// phone's DisplayService channel open, so the next launch cannot get one, and the failure surfaces
/// as an unrelated-looking refusal rather than as "something is still running".</para>
/// </remarks>
public static class AgentProcessRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<Process> Live = [];

    /// <summary>
    /// How many agents this app still believes it owns. More than one at a time means a previous
    /// session has not let go, and the phone will only talk to one of them.
    /// </summary>
    public static int LiveCount
    {
        get
        {
            lock (Gate)
            {
                return Live.Count(process =>
                {
                    try
                    {
                        return !process.HasExited;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                });
            }
        }
    }

    /// <summary>Remember an agent process for the lifetime of the app.</summary>
    public static void Register(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (Gate)
        {
            Live.Add(process);
        }
    }

    /// <summary>Forget one that has already been shut down properly.</summary>
    public static void Forget(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (Gate)
        {
            _ = Live.Remove(process);
        }
    }

    /// <summary>Last resort: nothing that talks to the phone may survive this process.</summary>
    public static void KillAll()
    {
        lock (Gate)
        {
            foreach (Process process in Live)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Already gone, or no longer ours to kill. There is nothing useful to do about
                    // it at exit, and throwing here would replace one crash with another.
                }
            }

            Live.Clear();
        }
    }
}
