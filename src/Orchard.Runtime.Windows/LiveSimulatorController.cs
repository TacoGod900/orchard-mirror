using Orchard.NativeHost.Windows;
using Orchard.Protocol;
using Orchard.Transport.Windows;

namespace Orchard.Runtime.Windows;

public sealed class LiveSimulatorException : Exception
{
    public LiveSimulatorException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record LiveError(string Code, string Message)
{
    public static LiveError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (code, fallback) = exception switch
        {
            LiveSimulatorException live => (live.Code, live.Message),
            OrchardTransportException transport => (transport.Code, "The native transport session failed."),
            ProtocolValidationException protocol => (protocol.Code, "The native protocol message was invalid."),
            OperationCanceledException => ("ORU1001", "The live operation was cancelled or timed out."),
            FileNotFoundException => ("ORU1002", "The native child executable was not found."),
            DirectoryNotFoundException => ("ORU1003", "A native runtime directory was not found."),
            UnauthorizedAccessException => ("ORU1004", "Windows denied access to a native runtime resource."),
            InvalidDataException => ("ORU1005", "The native child violated the live-session contract."),
            InvalidOperationException => ("ORU1006", "The live simulator operation was not valid in its current state."),
            _ => ("ORU1099", "The live simulator encountered an unexpected error.")
        };
        return new LiveError(code, LiveUiText.SanitizeControlText(fallback, 512));
    }
}

/// <summary>Owns one persistent native child and serializes all UI protocol operations.</summary>
public sealed class LiveSimulatorController : IAsyncDisposable
{
    private readonly NativeHostSession _session;
    private readonly LiveRenderState _renderState;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _eventSequence;
    private int _disposed;

    private LiveSimulatorController(NativeHostSession session, LiveRenderState renderState)
    {
        _session = session;
        _renderState = renderState;
    }

    public LiveRenderViewModel CurrentRender => _renderState.Current
        ?? throw new InvalidOperationException("The native child has not published a render.");

    public int ProcessId => _session.ProcessId;

    public bool HasExited => _session.HasExited;

    public static async Task<LiveSimulatorController> LaunchAsync(
        NativeHostLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var session = await NativeChildHost.LaunchAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            var initial = await session.ReceiveRenderAsync(cancellationToken).ConfigureAwait(false);
            var state = new LiveRenderState();
            var update = state.AcceptInitial(initial);
            if (update.Kind != LiveRenderUpdateKind.Applied)
            {
                throw new LiveSimulatorException(update.Code, update.Message);
            }

            return new LiveSimulatorController(session, state);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LiveRenderUpdate> DispatchAsync(
        string nodeId,
        string eventName,
        string? value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = CurrentRender;
            var sequence = Interlocked.Increment(ref _eventSequence);
            var request = LiveEventFactory.Create(
                current,
                $"ui-{sequence:x16}",
                nodeId,
                eventName,
                value);
            var dispatch = await _session.DispatchEventAsync(request, cancellationToken).ConfigureAwait(false);
            var update = _renderState.ApplyEventResult(request, dispatch.Result, dispatch.Render);
            if (update.Kind == LiveRenderUpdateKind.Invalid)
            {
                throw new LiveSimulatorException(update.Code, update.Message);
            }

            return update;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<NativeChildOutput?> CaptureOutputIfExitedAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_session.HasExited)
        {
            return null;
        }

        return await _session.GetOutputAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeChildOutput> ShutdownAndCaptureOutputAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _session.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            return await _session.GetOutputAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
