using System.Diagnostics;
using System.ComponentModel;
using Orchard.Protocol;
using Orchard.Transport.Windows;

namespace Orchard.NativeHost.Windows;

public sealed record NativeEventDispatch(EventResultPayload Result, RenderPayload? Render);

/// <summary>The native child exited without completing the required graceful-shutdown exchange.</summary>
public sealed class NativeChildExitedException : Exception
{
    public NativeChildExitedException(int exitCode, Exception? innerException = null)
        : base($"The native child exited unexpectedly with code {exitCode} before a normal shutdown acknowledgement.", innerException)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>A headless, authenticated session controlling one native Swift application process.</summary>
public sealed class NativeHostSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly OrchardPipeSession _transport;
    private readonly BoundedOutputCapture _stdout;
    private readonly BoundedOutputCapture _stderr;
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _protocolGate = new(1, 1);
    private long _currentRenderRevision;
    private bool _shutdownCompleted;
    private int _faulted;
    private int _disposed;

    internal NativeHostSession(
        Process process,
        OrchardPipeSession transport,
        BoundedOutputCapture stdout,
        BoundedOutputCapture stderr,
        Task stdoutTask,
        Task stderrTask,
        TimeSpan shutdownTimeout)
    {
        _process = process;
        _transport = transport;
        _stdout = stdout;
        _stderr = stderr;
        _stdoutTask = stdoutTask;
        _stderrTask = stderrTask;
        _shutdownTimeout = shutdownTimeout;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public long? CurrentRenderRevision
    {
        get
        {
            var revision = Interlocked.Read(ref _currentRenderRevision);
            return revision == 0 ? null : revision;
        }
    }

    public async Task<RenderPayload> ReceiveRenderAsync(CancellationToken cancellationToken = default)
    {
        await _protocolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            var envelope = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var render = envelope.Payload as RenderPayload
                ?? throw Unexpected(envelope.Type, ProtocolMessageTypes.Render);
            ValidateAndPublishRender(render, requireFirstRevision: true);
            return render;
        }
        catch (Exception exception) when (MarkFaulted(exception))
        {
            throw;
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    public async Task<NativeEventDispatch> DispatchEventAsync(
        EventPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ProtocolValidator.Validate(ProtocolEnvelope.Create("event-validation", 0, payload));
        await _protocolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (_currentRenderRevision == 0)
            {
                throw new InvalidOperationException("An initial render must be received before dispatching events.");
            }

            await _transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            var resultEnvelope = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (resultEnvelope.Payload is not EventResultPayload result)
            {
                throw Unexpected(resultEnvelope.Type, ProtocolMessageTypes.EventResult);
            }

            if (!string.Equals(result.EventId, payload.EventId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The native child returned a result for a different event.");
            }

            if (!result.Accepted)
            {
                if (result.RenderRevision != _currentRenderRevision)
                {
                    throw new InvalidDataException("A rejected event result did not preserve the published render revision.");
                }
                return new NativeEventDispatch(result, null);
            }

            if (payload.RenderRevision != _currentRenderRevision ||
                result.RenderRevision <= _currentRenderRevision)
            {
                throw new InvalidDataException("An accepted event did not advance the current render revision.");
            }

            var renderEnvelope = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (renderEnvelope.Payload is not RenderPayload render)
            {
                throw Unexpected(renderEnvelope.Type, ProtocolMessageTypes.Render);
            }

            if (render.Revision != result.RenderRevision)
            {
                throw new InvalidDataException("The event result and replacement render revisions differ.");
            }

            ValidateAndPublishRender(render, requireFirstRevision: false);

            return new NativeEventDispatch(result, render);
        }
        catch (Exception exception) when (MarkFaulted(exception))
        {
            throw;
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    public async Task<PongPayload> PingAsync(CancellationToken cancellationToken = default)
    {
        await _protocolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ping = new PingPayload
            {
                Nonce = "ping-" + Guid.NewGuid().ToString("N"),
                SentAtUnixMilliseconds = sentAt
            };
            await _transport.SendAsync(ping, cancellationToken).ConfigureAwait(false);
            var envelope = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.Payload is not PongPayload pong)
            {
                throw Unexpected(envelope.Type, ProtocolMessageTypes.Pong);
            }

            if (!string.Equals(pong.Nonce, ping.Nonce, StringComparison.Ordinal) ||
                pong.SentAtUnixMilliseconds != ping.SentAtUnixMilliseconds)
            {
                throw new InvalidDataException("The native child returned a pong for a different ping.");
            }

            return pong;
        }
        catch (Exception exception) when (MarkFaulted(exception))
        {
            throw;
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _protocolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (_shutdownCompleted)
            {
                return;
            }

            if (_process.HasExited)
            {
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new NativeChildExitedException(_process.ExitCode);
            }

            await _transport.SendAsync(
                new ShutdownPayload
                {
                    Disposition = ShutdownDisposition.HostRequested,
                    Reason = "Host requested graceful shutdown."
                },
                cancellationToken).ConfigureAwait(false);
            ProtocolEnvelope envelope;
            try
            {
                envelope = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OrchardTransportException exception)
            {
                var unexpectedExit = await ObserveUnexpectedExitAsync(exception).ConfigureAwait(false);
                if (unexpectedExit is not null)
                {
                    throw unexpectedExit;
                }

                throw;
            }
            if (envelope.Payload is not ShutdownPayload shutdown || shutdown.Disposition != ShutdownDisposition.Normal)
            {
                throw Unexpected(envelope.Type, ProtocolMessageTypes.Shutdown);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_shutdownTimeout);
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException("The native child returned a non-zero graceful-shutdown exit code.");
            }
            _shutdownCompleted = true;
        }
        catch (Exception exception) when (MarkFaulted(exception))
        {
            throw;
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    public async Task<NativeChildOutput> GetOutputAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            _stdoutTask.WaitAsync(cancellationToken),
            _stderrTask.WaitAsync(cancellationToken)).ConfigureAwait(false);
        return new NativeChildOutput(_stdout.Snapshot(), _stderr.Snapshot());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited && Volatile.Read(ref _faulted) == 0)
            {
                using var deadline = new CancellationTokenSource(_shutdownTimeout);
                try
                {
                    await ShutdownAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsNonFatalCleanupFailure(exception))
                {
                    // Disposal is terminal. Cleanup below kills a peer after any expected protocol,
                    // transport, cancellation, process, or I/O failure during graceful shutdown.
                }
            }
        }
        finally
        {
            try
            {
                await TerminateAndReapAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (IsNonFatalCleanupFailure(exception))
                {
                    // The child is already terminated; transport disposal is best effort.
                }
                finally
                {
                    _process.Dispose();
                    _protocolGate.Dispose();
                }
            }
        }
    }

    private static InvalidDataException Unexpected(string actual, string expected) =>
        new($"The native child sent '{actual}' where '{expected}' was required.");

    private void ValidateAndPublishRender(RenderPayload render, bool requireFirstRevision)
    {
        if (_currentRenderRevision == 0)
        {
            if (requireFirstRevision && render.Revision != 1)
            {
                throw new InvalidDataException("The first native render must use revision 1.");
            }
        }
        else if (render.Revision <= _currentRenderRevision)
        {
            throw new InvalidDataException("The native child replayed or rolled back a render revision.");
        }

        Interlocked.Exchange(ref _currentRenderRevision, render.Revision);
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException("The native host session is faulted and cannot process more protocol messages.");
        }
    }

    private bool MarkFaulted(Exception exception)
    {
        if (exception is InvalidOperationException)
        {
            return false;
        }

        Interlocked.Exchange(ref _faulted, 1);
        return true;
    }

    private async Task<NativeChildExitedException?> ObserveUnexpectedExitAsync(Exception innerException)
    {
        if (!_process.HasExited)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return new NativeChildExitedException(_process.ExitCode, innerException);
    }

    private async Task TerminateAndReapAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process exited concurrently or Windows had already torn down its handle.
            }
        }

        if (!_process.HasExited)
        {
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposal remains bounded even if Windows has not reaped the child yet.
            }
        }
    }

    private static bool IsNonFatalCleanupFailure(Exception exception) =>
        exception is ProtocolException or OrchardTransportException or IOException or OperationCanceledException or
            InvalidOperationException or ObjectDisposedException or Win32Exception;
}
