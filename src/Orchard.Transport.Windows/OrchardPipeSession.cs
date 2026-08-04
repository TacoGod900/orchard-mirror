using System.IO.Pipes;
using Orchard.Protocol;

namespace Orchard.Transport.Windows;

/// <summary>An authenticated, version-negotiated, full-duplex Orchard protocol session.</summary>
public sealed class OrchardPipeSession : IDisposable, IAsyncDisposable
{
    private readonly PipeStream _stream;
    private readonly TimeSpan _operationTimeout;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private long _nextOutboundSequence;
    private long _nextInboundSequence;
    private int _disposed;

    internal OrchardPipeSession(
        PipeStream stream,
        string sessionId,
        int negotiatedVersion,
        ProtocolPeerRole localRole,
        ProtocolPeerRole peerRole,
        IReadOnlyList<string> peerCapabilities,
        long nextOutboundSequence,
        long nextInboundSequence,
        TimeSpan operationTimeout)
    {
        _stream = stream;
        _lifetimeToken = _lifetime.Token;
        SessionId = sessionId;
        NegotiatedVersion = negotiatedVersion;
        LocalRole = localRole;
        PeerRole = peerRole;
        PeerCapabilities = peerCapabilities.ToArray();
        _nextOutboundSequence = nextOutboundSequence;
        _nextInboundSequence = nextInboundSequence;
        _operationTimeout = operationTimeout;
    }

    public string SessionId { get; }

    public int NegotiatedVersion { get; }

    public ProtocolPeerRole LocalRole { get; }

    public ProtocolPeerRole PeerRole { get; }

    public IReadOnlyList<string> PeerCapabilities { get; }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _stream.IsConnected;

    /// <summary>Writes one framed protocol payload. Concurrent writers are serialized in sequence order.</summary>
    public async ValueTask SendAsync(IProtocolPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfDisposed();

        using var operation = TransportCancellation.Create(_operationTimeout, _lifetimeToken, cancellationToken);
        try
        {
            await _writeGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var envelope = ProtocolEnvelope.Create(SessionId, _nextOutboundSequence, payload) with
                {
                    Version = NegotiatedVersion
                };
                await ProtocolFrameCodec.WriteAsync(_stream, envelope, operation.Token).ConfigureAwait(false);
                _nextOutboundSequence = checked(_nextOutboundSequence + 1);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException exception)
        {
            TransportCancellation.ThrowTranslated(
                exception,
                OrchardTransportErrorCodes.OperationTimeout,
                "The transport write exceeded its operation timeout.",
                _lifetimeToken,
                cancellationToken);
            throw;
        }
        catch (OrchardTransportException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw ConnectionFailure("The pipe closed while writing a protocol frame.", exception);
        }
        catch (ObjectDisposedException exception)
        {
            throw Disposed(exception);
        }
    }

    /// <summary>Reads and validates one framed envelope. Concurrent readers are serialized.</summary>
    public async ValueTask<ProtocolEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = TransportCancellation.Create(_operationTimeout, _lifetimeToken, cancellationToken);
        try
        {
            await _readGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var envelope = await ProtocolFrameCodec.ReadAsync(_stream, operation.Token).ConfigureAwait(false);
                ValidateInboundEnvelope(envelope);
                _nextInboundSequence = checked(_nextInboundSequence + 1);
                return envelope;
            }
            finally
            {
                _readGate.Release();
            }
        }
        catch (OperationCanceledException exception)
        {
            TransportCancellation.ThrowTranslated(
                exception,
                OrchardTransportErrorCodes.OperationTimeout,
                "The transport read exceeded its operation timeout.",
                _lifetimeToken,
                cancellationToken);
            throw;
        }
        catch (OrchardTransportException)
        {
            throw;
        }
        catch (ProtocolException exception) when (exception.Code == ProtocolErrorCodes.InvalidFrame)
        {
            throw ConnectionFailure("The pipe closed before a complete protocol frame was received.", exception);
        }
        catch (IOException exception)
        {
            throw ConnectionFailure("The pipe closed while reading a protocol frame.", exception);
        }
        catch (ObjectDisposedException exception)
        {
            throw Disposed(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _stream.Dispose();
        _lifetime.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void ValidateInboundEnvelope(ProtocolEnvelope envelope)
    {
        if (!string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal))
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SessionMismatch,
                "The peer sent a frame for a different session.");
        }

        if (envelope.Version != NegotiatedVersion)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.VersionMismatch,
                $"The peer sent protocol version {envelope.Version}; negotiated version {NegotiatedVersion} is required.");
        }

        if (envelope.Sequence != _nextInboundSequence)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SequenceViolation,
                $"The peer sent sequence {envelope.Sequence}; sequence {_nextInboundSequence} was required.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw Disposed();
        }
    }

    private OrchardTransportException ConnectionFailure(string message, Exception innerException) =>
        Volatile.Read(ref _disposed) != 0
            ? Disposed(innerException)
            : new OrchardTransportException(OrchardTransportErrorCodes.ConnectionClosed, message, innerException);

    private static OrchardTransportException Disposed(Exception? innerException = null) =>
        innerException is null
            ? new OrchardTransportException(OrchardTransportErrorCodes.Disposed, "The transport session is disposed.")
            : new OrchardTransportException(OrchardTransportErrorCodes.Disposed, "The transport session is disposed.", innerException);
}

internal static class TransportCancellation
{
    public static CancellationTokenSource Create(
        TimeSpan timeout,
        CancellationToken lifetimeToken,
        CancellationToken callerToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetimeToken);
        source.CancelAfter(timeout);
        return source;
    }

    public static void ThrowTranslated(
        OperationCanceledException exception,
        string timeoutCode,
        string timeoutMessage,
        CancellationToken lifetimeToken,
        CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The transport operation was canceled by the caller.", exception, callerToken);
        }

        if (lifetimeToken.IsCancellationRequested)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.Disposed,
                "The transport was disposed while an operation was pending.",
                exception);
        }

        throw new OrchardTransportException(timeoutCode, timeoutMessage, exception);
    }
}
