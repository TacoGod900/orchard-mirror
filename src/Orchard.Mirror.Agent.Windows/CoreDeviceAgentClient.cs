using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>Supervises the separate GPL CoreDevice agent process.</summary>
public sealed class CoreDeviceAgentClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentResponse>> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Queue<string> stderrTail = new();
    private readonly object stderrLock = new();
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private long nextRequestId;
    private bool disposed;
    private bool killImmediately;

    private CoreDeviceAgentClient(Process process)
    {
        this.process = process;
        stdoutTask = ReadStdoutAsync();
        stderrTask = ReadStderrAsync();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => FailPending(new AgentProtocolException(ExitMessage()));
    }

    /// <summary>Launch the agent and prove protocol compatibility with a hello exchange.</summary>
    public static async Task<CoreDeviceAgentClient> StartAsync(
        AgentLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= AgentLaunchOptions.Resolve();
        ProcessStartInfo startInfo = new()
        {
            FileName = options.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // A UTF-8 BOM becomes U+FEFF on Python's first stdin line and makes that JSON
            // request invalid. The process protocol is explicitly BOM-free JSON Lines.
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom,
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(options.AgentScript);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the Orchard Mirror CoreDevice agent.");

        // Registered before anything can go wrong with it. An agent that outlives this app keeps
        // the phone's DisplayService channel open, and the next launch cannot get one.
        AgentProcessRegistry.Register(process);
        MirrorLog.Write("agent", $"started pid {process.Id} ({AgentProcessRegistry.LiveCount} live)");
        CoreDeviceAgentClient client = new(process);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.SendAsync("hello", cancellationToken: timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Mount the boot-specific Developer Disk Image, or report that it is already mounted.</summary>
    public Task<JsonElement> MountDeveloperDiskImageAsync(string? udid = null, CancellationToken cancellationToken = default) =>
        SendAsync("mount-ddi", new { udid }, cancellationToken);

    /// <summary>Open a USB or paired Wi-Fi userspace tunnel and return device, display, and lock-state information.</summary>
    public Task<JsonElement> ConnectAsync(string? udid = null, CancellationToken cancellationToken = default) =>
        SendAsync("connect", new { udid }, cancellationToken);

    /// <summary>Query device, display, and lock-state information over the current tunnel.</summary>
    public Task<JsonElement> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync("device-info", cancellationToken: cancellationToken);

    /// <summary>Query the current CoreDevice lock state.</summary>
    public Task<JsonElement> GetLockStateAsync(CancellationToken cancellationToken = default) =>
        SendAsync("lockstate", cancellationToken: cancellationToken);

    /// <summary>Start the device HEVC stream and relay its RTP datagrams to a bound loopback port.</summary>
    public Task<JsonElement> StartVideoAsync(
        int relayPort,
        int? audioRelayPort = null,
        int displayId = 1,
        bool pairedAudioEnabled = false,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "start-video",
            new
            {
                relayHost = "127.0.0.1",
                relayPort,
                audioRelayHost = audioRelayPort.HasValue ? "127.0.0.1" : null,
                audioRelayPort = audioRelayPort ?? 0,
                displayId,
                pairedAudioEnabled,
            },
            cancellationToken);

    /// <summary>Stop video relay and the corresponding device media stream.</summary>
    public Task<JsonElement> StopVideoAsync(CancellationToken cancellationToken = default) =>
        SendAsync("stop-video", cancellationToken: cancellationToken);

    /// <summary>Ask the iPhone encoder for a fresh random-access picture after decode loss.</summary>
    public Task<JsonElement> RequestKeyframeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("request-keyframe", cancellationToken: cancellationToken);

    /// <summary>Send one contact or release sample in normalized touchscreen coordinates.</summary>
    public Task<JsonElement> SendTouchAsync(
        bool contact,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        SendAsync("touch", new { state = contact ? "contact" : "release", x, y }, cancellationToken);

    /// <summary>Send a complete, continuous touchscreen drag.</summary>
    /// <param name="x1">Normalized start column.</param>
    /// <param name="y1">Normalized start row.</param>
    /// <param name="x2">Normalized end column.</param>
    /// <param name="y2">Normalized end row.</param>
    /// <param name="steps">Interpolated contact samples between the two points.</param>
    /// <param name="duration">Seconds spent travelling between the two points.</param>
    /// <param name="settle">
    /// Seconds to hold the contact still at the destination before releasing. iOS reads fling
    /// velocity from the final samples, so a non-zero settle ends the gesture without momentum.
    /// </param>
    /// <param name="cancellationToken">Abandons the request; the phone may still have acted.</param>
    public Task<JsonElement> SendDragAsync(
        int x1,
        int y1,
        int x2,
        int y2,
        int steps = 20,
        double duration = 0.25,
        double settle = 0.0,
        CancellationToken cancellationToken = default) =>
        SendAsync("drag", new { x1, y1, x2, y2, steps, duration, settle }, cancellationToken);

    /// <summary>Type printable US-layout text through the virtual keyboard surface.</summary>
    public Task<JsonElement> TypeTextAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync("type", new { text }, cancellationToken);

    /// <summary>Press one named non-text keyboard key.</summary>
    public Task<JsonElement> SendKeyAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync("key", new { name }, cancellationToken);

    /// <summary>Press one named iPhone hardware button through Indigo HID.</summary>
    public Task<JsonElement> PressButtonAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync("button", new { name }, cancellationToken);

    /// <summary>Ask whether an iPhone is on the cable, and whether Developer Mode is already on.</summary>
    /// <param name="cancellationToken">Abandons the query.</param>
    /// <remarks>
    /// USB only, deliberately. This answers questions about a phone that cannot yet be reached any
    /// other way: before Developer Mode is on there is no CoreDevice tunnel to ask over.
    /// </remarks>
    public Task<JsonElement> GetUsbStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync("usb-status", cancellationToken: cancellationToken);

    /// <summary>Turn on Developer Mode, restart the phone, and confirm the prompt that follows.</summary>
    /// <param name="cancellationToken">Abandons waiting; the phone will still restart.</param>
    /// <remarks>
    /// Takes as long as a reboot, so this must not be given a short deadline. Fails with
    /// <c>passcode-set</c> when the iPhone has a passcode, which iOS will not let a computer work
    /// around.
    /// </remarks>
    public Task<JsonElement> EnableDeveloperModeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("enable-developer-mode", cancellationToken: cancellationToken);

    /// <summary>Walk the phone's volume down to silence.</summary>
    /// <param name="steps">Volume-down presses. iOS has sixteen steps, so sixteen reaches zero from anywhere.</param>
    /// <param name="cancellationToken">Abandons the request; the phone may already be partly quieter.</param>
    /// <remarks>
    /// This changes the phone's own volume and cannot be undone: nothing reports the level it had
    /// before, so there is nothing to put back.
    /// </remarks>
    public Task<JsonElement> SilenceAsync(int steps = 16, CancellationToken cancellationToken = default) =>
        SendAsync("silence", new { steps }, cancellationToken);

    /// <summary>Toggle the gated VoiceOver Screen Curtain privacy mode.</summary>
    public Task<JsonElement> SetScreenCurtainAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync("screen-curtain", new { enabled }, cancellationToken);

    /// <summary>Read VoiceOver and the agent's best-known Screen Curtain state.</summary>
    public Task<JsonElement> GetPrivacyStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync("privacy-status", cancellationToken: cancellationToken);

    /// <summary>Close the current tunnel while keeping the agent ready for another connection.</summary>
    public Task<JsonElement> DisconnectAsync(CancellationToken cancellationToken = default) =>
        SendAsync("disconnect", cancellationToken: cancellationToken);

    /// <summary>Send one command and return its result, failing with the agent's stable error code.</summary>
    public async Task<JsonElement> SendAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string id = Interlocked.Increment(ref nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TaskCompletionSource<AgentResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate CoreDevice agent request id was generated.");
        }

        // Timed and recorded, because "which command was it sitting in" is the first question any
        // report of a stuck connection asks, and nothing used to be able to answer it.
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string request = AgentResponse.SerializeRequest(id, command, arguments);
                await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }

            AgentResponse response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                MirrorLog.Write("command", $"{command} failed after {Elapsed(startedAt)} — {response.Error!.Code}: {response.Error.Message}");
                throw new AgentCommandException(response.Error!);
            }

            if (IsWorthLogging(command))
            {
                MirrorLog.Write("command", $"{command} ok after {Elapsed(startedAt)}");
            }

            return response.Result;
        }
        catch (OperationCanceledException)
        {
            // The one that matters: the attempt's budget ran out while this command was still in
            // flight. Without this line the log shows a command starting and simply never ending.
            MirrorLog.Write("command", $"{command} abandoned after {Elapsed(startedAt)} — gave up waiting");
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The longest <see cref="DisposeAsync"/> will spend asking the agent to stop before killing it.
    /// </summary>
    /// <remarks>
    /// Public because a caller that bounds the disposal with less than this gets the worst outcome
    /// available: it stops waiting, carries on, and starts the next agent while this one is still
    /// running — and the phone will only talk to one of them.
    /// </remarks>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// End the agent without asking it to stop first, for when the phone cannot hear a goodbye.
    /// </summary>
    /// <remarks>
    /// The polite stop in <see cref="DisposeAsync"/> costs up to <see cref="StopBudget"/>, and a
    /// caller that cannot afford that must not simply bound the wait more tightly: doing so returns
    /// while the agent is still running, and the next attempt then starts a second one for a phone
    /// that will only talk to one. Killing outright is both faster and actually finished.
    /// </remarks>
    public ValueTask KillAsync()
    {
        killImmediately = true;
        return DisposeAsync();
    }

    /// <summary>Stop the child process and release all protocol resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            if (killImmediately && !process.HasExited)
            {
                MirrorLog.Write("agent", $"killing pid {process.Id} without a goodbye");
                process.Kill(entireProcessTree: true);
            }
            else if (!process.HasExited)
            {
                using CancellationTokenSource timeout = new(StopBudget);
                try
                {
                    await SendAsync("stop", cancellationToken: timeout.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or AgentProtocolException or AgentCommandException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            disposed = true;
            FailPending(new ObjectDisposedException(nameof(CoreDeviceAgentClient)));
            await Task.WhenAll(IgnoreFailure(stdoutTask), IgnoreFailure(stderrTask)).ConfigureAwait(false);
            AgentProcessRegistry.Forget(process);
            writeLock.Dispose();
            process.Dispose();
        }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                AgentResponse response = AgentResponse.Parse(line);
                if (pending.TryGetValue(response.Id, out TaskCompletionSource<AgentResponse>? completion))
                {
                    completion.TrySetResult(response);
                }
            }

            FailPending(new AgentProtocolException(ExitMessage()));
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
    }

    /// <summary>
    /// Whether a successful command is worth a line. Pointer traffic is not: one drag across the
    /// phone is hundreds of touches, and logging them all would roll the connection story — the
    /// only reason this log exists — out of the file within seconds. Failures are always logged,
    /// however chatty the command, because a touch that starts failing is worth knowing about.
    /// </summary>
    private static bool IsWorthLogging(string command) =>
        command is not ("touch" or "scroll" or "lockstate");

    private static string Elapsed(long startedAt) =>
        $"{Stopwatch.GetElapsedTime(startedAt).TotalSeconds.ToString("N1", System.Globalization.CultureInfo.InvariantCulture)}s";

    private async Task ReadStderrAsync()
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Written out as well as retained. The ring below is only ever read when the process
            // exits, so an agent that hangs instead of dying used to take its explanation with it.
            MirrorLog.Write("agent", line);
            lock (stderrLock)
            {
                stderrTail.Enqueue(line);
                while (stderrTail.Count > 16)
                {
                    stderrTail.Dequeue();
                }
            }
        }
    }

    private string ExitMessage()
    {
        string tail;
        lock (stderrLock)
        {
            tail = string.Join(Environment.NewLine, stderrTail);
        }

        string code = process.HasExited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        return string.IsNullOrWhiteSpace(tail)
            ? $"The CoreDevice agent exited (code {code})."
            : $"The CoreDevice agent exited (code {code}).{Environment.NewLine}{tail}";
    }

    private void FailPending(Exception exception)
    {
        foreach (TaskCompletionSource<AgentResponse> completion in pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
