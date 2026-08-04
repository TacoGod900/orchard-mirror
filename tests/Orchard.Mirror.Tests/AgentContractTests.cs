namespace Orchard.Mirror.Tests;

/// <summary>
/// The .NET client and the Python agent are two halves of one protocol that no compiler checks.
/// A command the client can send but the agent does not dispatch fails at runtime, on a device, in
/// whatever state the app happened to be in.
/// </summary>
/// <remarks>
/// These read the agent's source as text, in the spirit of the solution-boundary guard in
/// <c>Program.cs</c>. That makes them cheap and hermetic, and it also bounds what they can claim:
/// they prove a command is <b>declared and dispatched</b>, not that it does the right thing.
/// </remarks>
internal static class AgentContractTests
{
    /// <summary>Every command the client sends must be dispatched and advertised by the agent.</summary>
    internal static void AgentDispatchesEveryCommandTheClientSends()
    {
        string agent = ReadRepositoryFile("src/Orchard.Mirror.Agent/agent.py");
        string client = ReadRepositoryFile("src/Orchard.Mirror.Agent.Windows/CoreDeviceAgentClient.cs");

        foreach (string command in CommandsSentBy(client))
        {
            Assert(
                agent.Contains($"if command == \"{command}\"", StringComparison.Ordinal),
                $"the client can send '{command}' but the agent never dispatches it");
            Assert(
                agent.Contains($"\"{command}\",", StringComparison.Ordinal),
                $"the agent dispatches '{command}' but does not advertise it in its hello reply");
        }
    }

    /// <summary>
    /// The stopAll must be sent from both places that end a media session.
    /// </summary>
    /// <remarks>
    /// This is the wedge, and it is worth a guard even though the guard is weak. Losing the stopAll
    /// can leave the phone's media daemon unable to accept another DisplayService channel until the
    /// device restarts. It has been lost twice for different reasons: once because the service
    /// field was cleared before the await, so the retry saw nothing to stop, and once because the
    /// failure path of starting a video leg closed the service without stopping it.
    /// <para><b>What this proves is only that the call is present at both sites</b> — not that it is
    /// reached, and not that the phone received it. That needs a device. It exists to catch the
    /// exact regression that has already happened, and nothing more.</para>
    /// </remarks>
    internal static void StopsTheMediaSessionFromBothPathsThatEndIt()
    {
        string agent = ReadRepositoryFile("src/Orchard.Mirror.Agent/agent.py");

        int stopVideo = agent.IndexOf("async def stop_video", StringComparison.Ordinal);
        int startVideo = agent.IndexOf("async def start_video", StringComparison.Ordinal);
        Assert(stopVideo > 0 && startVideo > 0, "could not find the two media lifecycle methods");

        Assert(
            Body(agent, stopVideo).Contains("_stop_all_media_streams", StringComparison.Ordinal),
            "stop_video no longer sends the stopAll that ends the paired session");
        Assert(
            Body(agent, startVideo).Contains("_stop_all_media_streams", StringComparison.Ordinal),
            "start_video's failure path abandons a negotiated session without stopping it");
    }

    /// <summary>
    /// The stopAll must be sent before the fields naming the session are cleared.
    /// </summary>
    /// <remarks>
    /// The specific defect: <c>stop_video</c> cleared <c>self._video_service</c> and the session id
    /// before awaiting the stop, so when <c>disconnect</c> called it a second time as a safety net,
    /// it found nothing to stop and silently did nothing.
    /// </remarks>
    internal static void SendsTheStopBeforeForgettingWhatToStop()
    {
        string agent = ReadRepositoryFile("src/Orchard.Mirror.Agent/agent.py");
        string body = Body(agent, agent.IndexOf("async def stop_video", StringComparison.Ordinal));

        int stop = body.IndexOf("_stop_all_media_streams", StringComparison.Ordinal);

        // Both spellings of "forget the service". The tuple swap is the one the code actually uses,
        // and looking only for the plain assignment made this test pass against the very defect it
        // was written for.
        int clear = FirstIndexOfAny(
            body,
            "self._video_service = None",
            "self._video_service = self._video_service, None",
            "self._video_service, self._video_transport");

        Assert(stop >= 0, "stop_video does not send a stopAll at all");
        Assert(clear >= 0, "could not find where stop_video clears the service, so this proves nothing");
        Assert(
            stop < clear,
            "stop_video forgets which session to stop before it has stopped it");
    }

    /// <summary>The earliest position at which any of the candidates appears, or -1.</summary>
    private static int FirstIndexOfAny(string source, params string[] candidates)
    {
        int best = -1;
        foreach (string candidate in candidates)
        {
            int at = source.IndexOf(candidate, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
            }
        }

        return best;
    }

    /// <summary>Roughly one method's worth of source starting at an offset.</summary>
    private static string Body(string source, int start)
    {
        int next = source.IndexOf("\n    async def ", start + 10, StringComparison.Ordinal);
        if (next < 0)
        {
            next = source.IndexOf("\n    def ", start + 10, StringComparison.Ordinal);
        }

        return next < 0 ? source[start..] : source[start..next];
    }

    /// <summary>The command names the client passes to its request helpers.</summary>
    private static HashSet<string> CommandsSentBy(string client)
    {
        // Commands appear as the second argument to the send helpers, e.g. SendAsync("stop-video".
        HashSet<string> found = [];
        foreach (string marker in (string[])["SendAsync(\"", "RequestAsync(\"", "InvokeAsync(\""])
        {
            int at = 0;
            while ((at = client.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + marker.Length;
                int end = client.IndexOf('"', start);
                if (end > start)
                {
                    found.Add(client[start..end]);
                }

                at = end < 0 ? start : end;
            }
        }

        Assert(found.Count > 0, "no commands were found in the client at all, so this proves nothing");
        return found;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find '{relativePath}' above '{AppContext.BaseDirectory}'.");
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
