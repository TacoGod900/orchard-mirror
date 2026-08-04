using System.Text;
using Orchard.Protocol;
using Orchard.Transport.Windows;

namespace Orchard.NativeHost.Windows;

/// <summary>Launches a native application child and establishes its authenticated local session.</summary>
public static class NativeChildHost
{
    private const string BootstrapMagic = "ORCHARD-LIVE-V1";

    public static async Task<NativeHostSession> LaunchAsync(
        NativeHostLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validated = options.Validate();
        var endpoint = OrchardPipeEndpoint.Create();
        var transportOptions = new OrchardTransportOptions
        {
            ConnectionTimeout = validated.ConnectionTimeout,
            HandshakeTimeout = validated.HandshakeTimeout,
            OperationTimeout = validated.OperationTimeout,
            Capabilities = ["configure-v1", "events-v1", "ping-v1", "shutdown-v1"]
        };

        await using var listener = new OrchardPipeServer(endpoint, ProtocolPeerRole.Host, transportOptions);
        var acceptTask = listener.AcceptAsync(cancellationToken).AsTask();
        LaunchedNativeChild? child = null;
        var stdout = new BoundedOutputCapture(validated.MaximumCapturedOutputBytes);
        var stderr = new BoundedOutputCapture(validated.MaximumCapturedOutputBytes);
        Task? stdoutTask = null;
        Task? stderrTask = null;
        OrchardPipeSession? pipeSession = null;
        try
        {
            child = WindowsNativeProcessLauncher.Start(validated);
            stdoutTask = stdout.PumpAsync(child.StandardOutput);
            stderrTask = stderr.PumpAsync(child.StandardError);
            await WriteBootstrapAsync(child.StandardInput, endpoint, cancellationToken).ConfigureAwait(false);
            pipeSession = await acceptTask.ConfigureAwait(false);
            await pipeSession.SendAsync(validated.Configuration, cancellationToken).ConfigureAwait(false);

            var session = new NativeHostSession(
                child,
                pipeSession,
                stdout,
                stderr,
                stdoutTask,
                stderrTask,
                validated.ShutdownTimeout,
                new NativeHostSandboxStatus(
                    validated.SandboxPolicy,
                    IsProductionSandboxed: false,
                    JobObjectAssignedBeforeExecution: true,
                    KillProcessTreeOnClose: true,
                    ExplicitHandleAllowlist: true,
                    ExplicitEnvironment: true,
                    validated.ActiveProcessLimit,
                    validated.ProcessMemoryLimitBytes,
                    validated.JobMemoryLimitBytes));
            pipeSession = null;
            child = null;
            return session;
        }
        catch
        {
            if (pipeSession is not null)
            {
                await pipeSession.DisposeAsync().ConfigureAwait(false);
            }

            if (child is not null)
            {
                await child.TerminateAndReapAsync().ConfigureAwait(false);
                await DrainAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            child?.Dispose();
        }
    }

    private static async Task WriteBootstrapAsync(
        Stream standardInput,
        OrchardPipeEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        await using (standardInput.ConfigureAwait(false))
        await using (var writer = new StreamWriter(
            standardInput,
            new UTF8Encoding(false),
            bufferSize: 1_024,
            leaveOpen: true)
        {
            NewLine = "\n"
        })
        {
            await writer.WriteLineAsync(BootstrapMagic.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(endpoint.PipeName.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(endpoint.AuthenticationToken.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DrainAfterTerminationAsync(params Task?[] tasks)
    {
        var pending = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Launch failure cleanup is bounded and the child has already been terminated.
        }
    }
}
