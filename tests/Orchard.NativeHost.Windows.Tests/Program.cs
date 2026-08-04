using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Orchard.NativeHost.Windows;
using Orchard.Protocol;
using Orchard.Transport.Windows;

namespace Orchard.NativeHost.Windows.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Body)[] Tests =
    [
        ("runs a stateful native Swift application over the real pipe", RunsStatefulNativeApplication),
        ("contains a bad-token child and keeps the listener usable", ContainsBadTokenChild),
        ("contains an unexpected child exit and launches a replacement", ContainsUnexpectedExit),
        ("rejects a replayed render revision from an untrusted child", RejectsReplayedRevision),
        ("rejects an invalid first render revision", RejectsInvalidFirstRevision),
        ("rejects a rolled-back rejected-event revision", RejectsRejectedResultRollback),
        ("classifies exit 86 before shutdown acknowledgement", ClassifiesExitBeforeShutdownAck),
        ("contains adaptation failure without a second event result", ContainsAdaptationFailure),
        ("disposal reaps a child after an invalid shutdown response", DisposesAfterInvalidShutdownResponse),
        ("bounds a child that never starts the live protocol", BoundsNonConnectingChild)
    ];

    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.SequenceEqual(["--fixture-invalid-shutdown-response"]))
        {
            return await RunInvalidShutdownFixture().ConfigureAwait(false);
        }

        var failures = new List<string>();
        foreach (var (name, body) in Tests)
        {
            try
            {
                await body().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Executed {Tests.Length} tests; {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task RunsStatefulNativeApplication()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live"),
            deadline.Token).ConfigureAwait(false);

        try
        {
            await session.DispatchEventAsync(
                new EventPayload
                {
                    EventId = "event-before-render",
                    RenderRevision = 1,
                    NodeId = "not-published",
                    Event = "press"
                },
                deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("The host sent an event before receiving the initial render.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("initial render", StringComparison.Ordinal))
        {
            // The buffered initial render remains untouched for ReceiveRenderAsync below.
        }

        var first = await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
        Assert(first.Revision == 1, "The first native render did not use revision 1.");
        Assert(session.CurrentRenderRevision == 1, "The host did not retain the first published revision.");
        var textField = FindSingle(first.Root, "textField");
        var button = FindSingle(first.Root, "button");
        Assert(textField.Events.SequenceEqual(["change"]), "The text field did not expose change.");
        Assert(button.Events.SequenceEqual(["press"]), "The button did not expose press.");

        var changed = await session.DispatchEventAsync(
            new EventPayload
            {
                EventId = "event-change-1",
                RenderRevision = first.Revision,
                NodeId = textField.Id,
                Event = "change",
                Value = "Ada"
            },
            deadline.Token).ConfigureAwait(false);
        Assert(changed.Result.Accepted, "The native text-field change was rejected.");
        Assert(changed.Result.RenderRevision == 2 && changed.Render?.Revision == 2,
            "The text-field change did not publish revision 2.");
        Assert(session.CurrentRenderRevision == 2, "The host did not advance its trusted revision.");
        Assert(FindSingle(changed.Render!.Root, "textField").Properties["value"] == "Ada",
            "The native binding did not retain the text-field value.");
        Assert(Flatten(changed.Render.Root).Any(node =>
                node.Kind == "text" && node.Properties.GetValueOrDefault("text") == "Hello, Ada"),
            "Dependent native text did not rerender after the binding changed.");

        button = FindSingle(changed.Render.Root, "button");
        var pressed = await session.DispatchEventAsync(
            new EventPayload
            {
                EventId = "event-press-1",
                RenderRevision = changed.Render.Revision,
                NodeId = button.Id,
                Event = "press"
            },
            deadline.Token).ConfigureAwait(false);
        Assert(pressed.Result.Accepted && pressed.Render?.Revision == 3,
            "The native button press did not publish revision 3.");
        Assert(FindSingle(pressed.Render!.Root, "textField").Properties["value"] == "Orchard Developer",
            "The button closure did not update persistent native state.");

        var stale = await session.DispatchEventAsync(
            new EventPayload
            {
                EventId = "event-stale-1",
                RenderRevision = changed.Render.Revision,
                NodeId = button.Id,
                Event = "press"
            },
            deadline.Token).ConfigureAwait(false);
        Assert(!stale.Result.Accepted && stale.Render is null,
            "A stale native event was accepted or caused a render.");
        Assert(stale.Result.ErrorCode == "ORL1002" && stale.Result.RenderRevision == 3,
            "The stale native event did not report the current revision.");
        Assert(session.CurrentRenderRevision == 3, "A rejected event changed the host revision.");

        var pong = await session.PingAsync(deadline.Token).ConfigureAwait(false);
        Assert(pong.RespondedAtUnixMilliseconds >= pong.SentAtUnixMilliseconds,
            "The native pong timestamp was invalid.");

        await session.ShutdownAsync(deadline.Token).ConfigureAwait(false);
        var output = await session.GetOutputAsync(deadline.Token).ConfigureAwait(false);
        Assert(output.StandardOutput.Text.Length == 0,
            "Live protocol data or diagnostics leaked to standard output.");
        Assert(output.StandardError.Text.Length == 0,
            "A successful live session wrote diagnostics to standard error.");
        Assert(!output.StandardOutput.Truncated && !output.StandardError.Truncated,
            "Successful native output capture unexpectedly truncated data.");
    }

    private static async Task ContainsBadTokenChild()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endpoint = OrchardPipeEndpoint.Create();
        await using var listener = new OrchardPipeServer(endpoint, options: FastTransportOptions());
        var accept = listener.AcceptAsync(deadline.Token).AsTask();
        using var child = StartRawChild("--orchard-live");
        var wrongToken = (endpoint.AuthenticationToken[0] == 'A' ? "B" : "A")
            + endpoint.AuthenticationToken[1..];
        await child.StandardInput.WriteLineAsync("ORCHARD-LIVE-V1".AsMemory(), deadline.Token).ConfigureAwait(false);
        await child.StandardInput.WriteLineAsync(endpoint.PipeName.AsMemory(), deadline.Token).ConfigureAwait(false);
        await child.StandardInput.WriteLineAsync(wrongToken.AsMemory(), deadline.Token).ConfigureAwait(false);
        child.StandardInput.Close();

        var serverCode = await CaptureTransportCode(accept).ConfigureAwait(false);
        Assert(serverCode == OrchardTransportErrorCodes.AuthenticationFailed,
            $"The bad-token server result was {serverCode}.");
        var stdoutTask = child.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = child.StandardError.ReadToEndAsync(deadline.Token);
        await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        Assert(child.ExitCode != 0, "The bad-token child exited successfully.");
        Assert(stdout.Length == 0, "The bad-token child wrote protocol data to stdout.");
        Assert(!stderr.Contains(endpoint.AuthenticationToken, StringComparison.Ordinal) &&
            !stderr.Contains(wrongToken, StringComparison.Ordinal) &&
            !stderr.Contains(endpoint.PipeName, StringComparison.Ordinal),
            "The bad-token child disclosed endpoint credentials.");
        Assert(stderr.Contains(OrchardTransportErrorCodes.AuthenticationFailed, StringComparison.Ordinal),
            "The bad-token child did not emit the stable redacted error code.");

        var replacementAccept = listener.AcceptAsync(deadline.Token).AsTask();
        await using var replacement = await OrchardPipeClient.ConnectAsync(
            endpoint,
            options: FastTransportOptions(),
            cancellationToken: deadline.Token).ConfigureAwait(false);
        await using var replacementHost = await replacementAccept.ConfigureAwait(false);
        Assert(replacementHost.IsConnected, "The listener was unusable after rejecting a bad token.");
    }

    private static async Task ContainsUnexpectedExit()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var crashed = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-exit-after-render"),
            deadline.Token).ConfigureAwait(false))
        {
            var render = await crashed.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
            Assert(render.Revision == 1, "The crash fixture did not publish its first render.");
            var code = await CaptureTransportCode(crashed.PingAsync(deadline.Token)).ConfigureAwait(false);
            Assert(code == OrchardTransportErrorCodes.ConnectionClosed,
                $"The crashed child produced transport code {code}.");
        }

        await using var replacement = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live"),
            deadline.Token).ConfigureAwait(false);
        Assert((await replacement.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false)).Revision == 1,
            "A replacement child could not start after the prior child exited.");
        await replacement.ShutdownAsync(deadline.Token).ConfigureAwait(false);
    }

    private static async Task BoundsNonConnectingChild()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        try
        {
            await NativeChildHost.LaunchAsync(
                LaunchOptions("--orchard-test-no-connect") with
                {
                    ConnectionTimeout = TimeSpan.FromSeconds(1),
                    HandshakeTimeout = TimeSpan.FromSeconds(1)
                },
                deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("A no-connect child unexpectedly established a live session.");
        }
        catch (OrchardTransportException exception)
        {
            Assert(exception.Code == OrchardTransportErrorCodes.ConnectionTimeout,
                $"The non-connecting child returned {exception.Code}.");
        }

        Assert(watch.Elapsed < TimeSpan.FromSeconds(5),
            "The non-connecting child was not contained within the bounded deadline.");
    }

    private static async Task RejectsReplayedRevision()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-replay-after-event"),
            deadline.Token).ConfigureAwait(false);
        var first = await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
        var textField = FindSingle(first.Root, "textField");
        try
        {
            await session.DispatchEventAsync(
                new EventPayload
                {
                    EventId = "event-replay-1",
                    RenderRevision = first.Revision,
                    NodeId = textField.Id,
                    Event = "change",
                    Value = "Replay"
                },
                deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("The host accepted a replayed render revision.");
        }
        catch (InvalidDataException)
        {
            Assert(session.CurrentRenderRevision == 1,
                "The host committed an untrusted replayed render revision.");
        }

        try
        {
            await session.PingAsync(deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("A faulted host session accepted another protocol operation.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("faulted", StringComparison.Ordinal))
        {
            // A protocol invariant failure is terminal; buffered frames cannot be misinterpreted.
        }
    }

    private static async Task RejectsInvalidFirstRevision()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-zero-first-render"),
            deadline.Token).ConfigureAwait(false);
        try
        {
            await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("The host accepted a zero first render revision.");
        }
        catch (InvalidDataException)
        {
            Assert(session.CurrentRenderRevision is null,
                "The host committed an invalid first render revision.");
        }
    }

    private static async Task RejectsRejectedResultRollback()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-rejected-result-rollback"),
            deadline.Token).ConfigureAwait(false);
        var first = await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
        var textField = FindSingle(first.Root, "textField");
        try
        {
            await session.DispatchEventAsync(
                new EventPayload
                {
                    EventId = "event-rollback-1",
                    RenderRevision = first.Revision,
                    NodeId = textField.Id,
                    Event = "change",
                    Value = "Rollback"
                },
                deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("The host accepted a rolled-back rejected-event revision.");
        }
        catch (InvalidDataException)
        {
            Assert(session.CurrentRenderRevision == 1,
                "The host committed a rolled-back rejected-event revision.");
        }
    }

    private static async Task ClassifiesExitBeforeShutdownAck()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-exit-on-shutdown"),
            deadline.Token).ConfigureAwait(false);
        _ = await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await session.ShutdownAsync(deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Exit 86 was treated as graceful shutdown.");
        }
        catch (NativeChildExitedException exception)
        {
            Assert(exception.ExitCode == 86,
                $"The premature native exit was classified as {exception.ExitCode} instead of 86.");
        }
    }

    private static async Task ContainsAdaptationFailure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await NativeChildHost.LaunchAsync(
            LaunchOptions("--orchard-live", "--orchard-test-adaptation-failure-after-event"),
            deadline.Token).ConfigureAwait(false);
        var first = await session.ReceiveRenderAsync(deadline.Token).ConfigureAwait(false);
        var textField = FindSingle(first.Root, "textField");
        try
        {
            await session.DispatchEventAsync(
                new EventPayload
                {
                    EventId = "event-adaptation-failure",
                    RenderRevision = first.Revision,
                    NodeId = textField.Id,
                    Event = "change",
                    Value = "Unsupported"
                },
                deadline.Token).ConfigureAwait(false);
            throw new InvalidOperationException("An adaptation failure emitted an event result.");
        }
        catch (NativeChildExitedException exception)
        {
            Assert(exception.ExitCode == 70,
                $"The adaptation failure exited with {exception.ExitCode} instead of 70.");
        }
    }

    private static async Task DisposesAfterInvalidShutdownResponse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await NativeChildHost.LaunchAsync(
            SelfFixtureOptions("--fixture-invalid-shutdown-response"),
            deadline.Token).ConfigureAwait(false);
        var processId = session.ProcessId;
        await session.DisposeAsync().ConfigureAwait(false);
        AssertProcessExited(processId);
    }

    private static NativeHostLaunchOptions LaunchOptions(params string[] arguments) => new()
    {
        ExecutablePath = FindNativeExecutable(),
        Arguments = arguments,
        RuntimeSearchPaths = [FindSwiftRuntimeDirectory()],
        ConnectionTimeout = TimeSpan.FromSeconds(3),
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        OperationTimeout = TimeSpan.FromSeconds(3),
        ShutdownTimeout = TimeSpan.FromSeconds(3)
    };

    private static NativeHostLaunchOptions SelfFixtureOptions(params string[] arguments) => new()
    {
        ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Orchard.NativeHost.Windows.Tests.exe"),
        Arguments = arguments,
        WorkingDirectory = AppContext.BaseDirectory,
        ConnectionTimeout = TimeSpan.FromSeconds(3),
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        OperationTimeout = TimeSpan.FromSeconds(3),
        ShutdownTimeout = TimeSpan.FromSeconds(2)
    };

    private static async Task<int> RunInvalidShutdownFixture()
    {
        if (Console.ReadLine() != "ORCHARD-LIVE-V1")
        {
            return 65;
        }

        var pipeName = Console.ReadLine();
        var authenticationToken = Console.ReadLine();
        if (pipeName is null || authenticationToken is null)
        {
            return 65;
        }

        const string sessionId = "session-invalid-shutdown-fixture";
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3_000).ConfigureAwait(false);
        await ProtocolFrameCodec.WriteAsync(
            pipe,
            ProtocolEnvelope.Create(
                sessionId,
                0,
                new AuthenticatePayload
                {
                    Token = authenticationToken,
                    ClientNonce = "nonce-invalid-shutdown-fixture"
                })).ConfigureAwait(false);
        authenticationToken = string.Empty;
        await ProtocolFrameCodec.WriteAsync(
            pipe,
            ProtocolEnvelope.Create(
                sessionId,
                1,
                new HelloPayload
                {
                    MinimumVersion = 1,
                    MaximumVersion = 1,
                    Role = ProtocolPeerRole.Application,
                    Capabilities = ["invalid-shutdown-fixture"]
                })).ConfigureAwait(false);
        Assert((await ProtocolFrameCodec.ReadAsync(pipe).ConfigureAwait(false)).Payload is HelloPayload,
            "The invalid-shutdown fixture did not receive host hello.");
        Assert((await ProtocolFrameCodec.ReadAsync(pipe).ConfigureAwait(false)).Payload is ConfigurePayload,
            "The invalid-shutdown fixture did not receive configuration.");
        Assert((await ProtocolFrameCodec.ReadAsync(pipe).ConfigureAwait(false)).Payload is ShutdownPayload,
            "The invalid-shutdown fixture did not receive shutdown.");

        var invalidPayload = Encoding.UTF8.GetBytes(
            $"{{\"version\":1,\"type\":\"shutdown\",\"sessionId\":\"{sessionId}\",\"sequence\":2," +
            "\"payload\":{\"disposition\":\"normal\",\"reason\":\"\"}}}");
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)invalidPayload.Length));
        await pipe.WriteAsync(header).ConfigureAwait(false);
        await pipe.WriteAsync(invalidPayload).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return 0;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert(process.HasExited, $"Child process {processId} remained alive after session disposal.");
        }
        catch (ArgumentException)
        {
            // Windows has already removed the reaped process from the process table.
        }
    }

    private static Process StartRawChild(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindNativeExecutable(),
            WorkingDirectory = Path.GetDirectoryName(FindNativeExecutable())!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, false),
            StandardErrorEncoding = new UTF8Encoding(false, false)
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        if (inheritedPath is null)
        {
            startInfo.Environment.TryGetValue("Path", out inheritedPath);
        }
        startInfo.Environment.Remove("Path");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            FindSwiftRuntimeDirectory(),
            inheritedPath ?? string.Empty);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the native child test process.");
    }

    private static OrchardTransportOptions FastTransportOptions() => new()
    {
        ConnectionTimeout = TimeSpan.FromSeconds(3),
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        OperationTimeout = TimeSpan.FromSeconds(3)
    };

    private static string FindNativeExecutable()
    {
        var root = FindRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(root, "samples", "NativeHello", ".build", "x86_64-unknown-windows-msvc", "release", "NativeHello.exe"),
            Path.Combine(root, "samples", "NativeHello", ".build", "x86_64-unknown-windows-msvc", "debug", "NativeHello.exe"),
            Path.Combine(root, "artifacts", "native-live-sample-build", "x86_64-unknown-windows-msvc", "debug", "NativeHello.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Build samples/NativeHello before running native-host integration tests.");
    }

    private static string FindSwiftRuntimeDirectory()
    {
        var lockPath = Path.Combine(FindRepositoryRoot(), "toolchains", "orchard-toolchain.lock.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        var runtimeVersion = document.RootElement.GetProperty("swift").GetProperty("runtimeDirectory").GetString()
            ?? throw new InvalidDataException("The toolchain lock has no Swift runtime directory.");
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Swift",
            "Runtimes",
            runtimeVersion,
            "usr",
            "bin");
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException("The pinned Swift runtime directory is not installed.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Orchard repository root.");
    }

    private static ProtocolViewNode FindSingle(ProtocolViewNode root, string kind)
    {
        var matches = Flatten(root).Where(node => node.Kind == kind).ToArray();
        Assert(matches.Length == 1, $"Expected one {kind} node; found {matches.Length}.");
        return matches[0];
    }

    private static IEnumerable<ProtocolViewNode> Flatten(ProtocolViewNode root)
    {
        var pending = new Stack<ProtocolViewNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    private static async Task<string> CaptureTransportCode(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OrchardTransportException exception)
        {
            return exception.Code;
        }

        throw new InvalidOperationException("Expected an OrchardTransportException.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
