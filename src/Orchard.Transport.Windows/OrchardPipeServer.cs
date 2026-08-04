using System.IO.Pipes;
using Orchard.Protocol;

namespace Orchard.Transport.Windows;

/// <summary>Listens for authenticated Orchard sessions on a current-user-only local named pipe.</summary>
public sealed class OrchardPipeServer : IDisposable, IAsyncDisposable
{
    private const int PipeBufferBytes = 64 * 1024;

    private readonly OrchardPipeEndpoint _endpoint;
    private readonly TransportOptionsSnapshot _options;
    private readonly SemaphoreSlim _acceptGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _disposed;

    public OrchardPipeServer(
        OrchardPipeEndpoint endpoint,
        ProtocolPeerRole localRole = ProtocolPeerRole.Host,
        OrchardTransportOptions? options = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = (options ?? new OrchardTransportOptions()).ValidateAndSnapshot(localRole);
        _lifetimeToken = _lifetime.Token;
    }

    public string PipeName => _endpoint.PipeName;

    /// <summary>
    /// Accepts one connection and completes its authentication and version handshake.
    /// Accepted sessions own their pipe independently of the listener.
    /// </summary>
    public async ValueTask<OrchardPipeSession> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var connectionTimeout = TransportCancellation.Create(
            _options.ConnectionTimeout,
            _lifetimeToken,
            cancellationToken);

        try
        {
            await _acceptGate.WaitAsync(connectionTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            TransportCancellation.ThrowTranslated(
                exception,
                OrchardTransportErrorCodes.ConnectionTimeout,
                "No process connected to the pipe before the deadline.",
                _lifetimeToken,
                cancellationToken);
            throw;
        }

        NamedPipeServerStream? stream = null;
        try
        {
            ThrowIfDisposed();
            stream = new NamedPipeServerStream(
                _endpoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                PipeBufferBytes,
                PipeBufferBytes);
            try
            {
                await stream.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                TransportCancellation.ThrowTranslated(
                    exception,
                    OrchardTransportErrorCodes.ConnectionTimeout,
                    "No process connected to the pipe before the deadline.",
                    _lifetimeToken,
                    cancellationToken);
                throw;
            }

            var session = await TransportHandshake.AcceptAsync(
                stream,
                _endpoint,
                _options,
                cancellationToken,
                _lifetimeToken).ConfigureAwait(false);
            stream = null;
            return session;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.IoFailure,
                "Windows denied access while creating the current-user-only named pipe.",
                exception);
        }
        catch (IOException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.IoFailure,
                "Windows could not create or accept the local named pipe.",
                exception);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _acceptGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.Disposed,
                "The named-pipe listener is disposed.");
        }
    }
}
