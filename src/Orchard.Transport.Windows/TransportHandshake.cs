using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Orchard.Protocol;

namespace Orchard.Transport.Windows;

internal static class TransportHandshake
{
    private const long ClientAuthenticateSequence = 0;
    private const long ClientHelloSequence = 1;
    private const long ServerHelloSequence = 0;

    public static async ValueTask<OrchardPipeSession> AcceptAsync(
        PipeStream stream,
        OrchardPipeEndpoint endpoint,
        TransportOptionsSnapshot options,
        CancellationToken callerToken,
        CancellationToken serverLifetimeToken)
    {
        using var timeout = TransportCancellation.Create(options.HandshakeTimeout, serverLifetimeToken, callerToken);
        try
        {
            var authenticateEnvelope = await ProtocolFrameCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            ValidateHandshakeEnvelope(
                authenticateEnvelope,
                ProtocolMessageTypes.Authenticate,
                ClientAuthenticateSequence,
                expectedSessionId: null);

            var authenticate = (AuthenticatePayload)authenticateEnvelope.Payload;
            if (!AuthenticationTokensEqual(endpoint.AuthenticationToken, authenticate.Token))
            {
                await TrySendShutdownAsync(
                    stream,
                    authenticateEnvelope.SessionId,
                    ShutdownDisposition.AuthenticationFailed,
                    $"{OrchardTransportErrorCodes.AuthenticationFailed}: Authentication failed.").ConfigureAwait(false);
                throw new OrchardTransportException(
                    OrchardTransportErrorCodes.AuthenticationFailed,
                    "The connecting process did not present the expected authentication token.");
            }

            var helloEnvelope = await ProtocolFrameCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            ValidateHandshakeEnvelope(
                helloEnvelope,
                ProtocolMessageTypes.Hello,
                ClientHelloSequence,
                authenticateEnvelope.SessionId);
            var peerHello = (HelloPayload)helloEnvelope.Payload;

            int negotiatedVersion;
            try
            {
                negotiatedVersion = ProtocolNegotiator.Negotiate(
                    options.MinimumVersion,
                    options.MaximumVersion,
                    peerHello.MinimumVersion,
                    peerHello.MaximumVersion);
            }
            catch (ProtocolValidationException exception) when (exception.Code == ProtocolErrorCodes.NoCommonVersion)
            {
                await TrySendShutdownAsync(
                    stream,
                    authenticateEnvelope.SessionId,
                    ShutdownDisposition.ProtocolError,
                    $"{OrchardTransportErrorCodes.VersionMismatch}: No mutually supported protocol version.").ConfigureAwait(false);
                throw new OrchardTransportException(
                    OrchardTransportErrorCodes.VersionMismatch,
                    "No mutually supported protocol version exists.",
                    exception);
            }

            var localHello = new HelloPayload
            {
                MinimumVersion = options.MinimumVersion,
                MaximumVersion = options.MaximumVersion,
                Role = options.LocalRole,
                Capabilities = options.Capabilities
            };
            await ProtocolFrameCodec.WriteAsync(
                stream,
                ProtocolEnvelope.Create(authenticateEnvelope.SessionId, ServerHelloSequence, localHello),
                timeout.Token).ConfigureAwait(false);

            return new OrchardPipeSession(
                stream,
                authenticateEnvelope.SessionId,
                negotiatedVersion,
                options.LocalRole,
                peerHello.Role,
                peerHello.Capabilities,
                nextOutboundSequence: ServerHelloSequence + 1,
                nextInboundSequence: ClientHelloSequence + 1,
                options.OperationTimeout);
        }
        catch (OperationCanceledException exception)
        {
            TransportCancellation.ThrowTranslated(
                exception,
                OrchardTransportErrorCodes.HandshakeTimeout,
                "The connecting process did not complete the handshake before the deadline.",
                serverLifetimeToken,
                callerToken);
            throw;
        }
        catch (OrchardTransportException)
        {
            throw;
        }
        catch (ProtocolException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.UnexpectedHandshakeMessage,
                $"The connecting process sent an invalid handshake frame: {exception.Code}.",
                exception);
        }
        catch (IOException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.ConnectionClosed,
                "The pipe closed during the server handshake.",
                exception);
        }
    }

    public static async ValueTask<OrchardPipeSession> ConnectAsync(
        PipeStream stream,
        OrchardPipeEndpoint endpoint,
        TransportOptionsSnapshot options,
        CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(options.HandshakeTimeout);
        var sessionId = "session-" + SecretEncoding.Encode(RandomNumberGenerator.GetBytes(16));
        try
        {
            var authenticate = new AuthenticatePayload
            {
                Token = endpoint.AuthenticationToken,
                ClientNonce = "nonce-" + SecretEncoding.Encode(RandomNumberGenerator.GetBytes(16))
            };
            await ProtocolFrameCodec.WriteAsync(
                stream,
                ProtocolEnvelope.Create(sessionId, ClientAuthenticateSequence, authenticate),
                timeout.Token).ConfigureAwait(false);

            var localHello = new HelloPayload
            {
                MinimumVersion = options.MinimumVersion,
                MaximumVersion = options.MaximumVersion,
                Role = options.LocalRole,
                Capabilities = options.Capabilities
            };
            await ProtocolFrameCodec.WriteAsync(
                stream,
                ProtocolEnvelope.Create(sessionId, ClientHelloSequence, localHello),
                timeout.Token).ConfigureAwait(false);

            var response = await ProtocolFrameCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            ValidateServerResponseEnvelope(response, sessionId);
            if (response.Payload is ShutdownPayload shutdown)
            {
                throw TranslateShutdown(shutdown);
            }

            if (response.Payload is not HelloPayload peerHello)
            {
                throw Unexpected($"The server replied with '{response.Type}' instead of hello.");
            }

            int negotiatedVersion;
            try
            {
                negotiatedVersion = ProtocolNegotiator.Negotiate(
                    options.MinimumVersion,
                    options.MaximumVersion,
                    peerHello.MinimumVersion,
                    peerHello.MaximumVersion);
            }
            catch (ProtocolValidationException exception) when (exception.Code == ProtocolErrorCodes.NoCommonVersion)
            {
                throw new OrchardTransportException(
                    OrchardTransportErrorCodes.VersionMismatch,
                    "No mutually supported protocol version exists.",
                    exception);
            }

            return new OrchardPipeSession(
                stream,
                sessionId,
                negotiatedVersion,
                options.LocalRole,
                peerHello.Role,
                peerHello.Capabilities,
                nextOutboundSequence: ClientHelloSequence + 1,
                nextInboundSequence: ServerHelloSequence + 1,
                options.OperationTimeout);
        }
        catch (OperationCanceledException exception)
        {
            if (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "The client handshake was canceled by the caller.",
                    exception,
                    callerToken);
            }

            throw new OrchardTransportException(
                OrchardTransportErrorCodes.HandshakeTimeout,
                "The server did not complete the handshake before the deadline.",
                exception);
        }
        catch (OrchardTransportException)
        {
            throw;
        }
        catch (ProtocolException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.UnexpectedHandshakeMessage,
                $"The server sent an invalid handshake frame: {exception.Code}.",
                exception);
        }
        catch (IOException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.ConnectionClosed,
                "The pipe closed during the client handshake.",
                exception);
        }
    }

    private static void ValidateHandshakeEnvelope(
        ProtocolEnvelope envelope,
        string expectedType,
        long expectedSequence,
        string? expectedSessionId)
    {
        if (!string.Equals(envelope.Type, expectedType, StringComparison.Ordinal))
        {
            throw Unexpected($"Expected '{expectedType}' but received '{envelope.Type}'.");
        }

        if (envelope.Sequence != expectedSequence)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SequenceViolation,
                $"Handshake sequence {envelope.Sequence} was received; sequence {expectedSequence} was required.");
        }

        if (expectedSessionId is not null &&
            !string.Equals(envelope.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SessionMismatch,
                "The handshake changed session identifiers between frames.");
        }
    }

    private static void ValidateServerResponseEnvelope(ProtocolEnvelope envelope, string expectedSessionId)
    {
        if (!string.Equals(envelope.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SessionMismatch,
                "The server replied with a different session identifier.");
        }

        if (envelope.Sequence != ServerHelloSequence)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.SequenceViolation,
                $"Server handshake sequence {envelope.Sequence} was received; sequence {ServerHelloSequence} was required.");
        }
    }

    private static OrchardTransportException TranslateShutdown(ShutdownPayload shutdown)
    {
        if (shutdown.Disposition == ShutdownDisposition.AuthenticationFailed)
        {
            return new OrchardTransportException(
                OrchardTransportErrorCodes.AuthenticationFailed,
                "The server rejected the connection credentials.");
        }

        if (shutdown.Disposition == ShutdownDisposition.ProtocolError &&
            shutdown.Reason.StartsWith(OrchardTransportErrorCodes.VersionMismatch + ":", StringComparison.Ordinal))
        {
            return new OrchardTransportException(
                OrchardTransportErrorCodes.VersionMismatch,
                "No mutually supported protocol version exists.");
        }

        return Unexpected("The server rejected the handshake.");
    }

    private static bool AuthenticationTokensEqual(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedDigest = SHA256.HashData(expectedBytes);
        var suppliedDigest = SHA256.HashData(suppliedBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(suppliedDigest);
        }
    }

    private static async ValueTask TrySendShutdownAsync(
        Stream stream,
        string sessionId,
        ShutdownDisposition disposition,
        string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            var payload = new ShutdownPayload { Disposition = disposition, Reason = reason };
            await ProtocolFrameCodec.WriteAsync(
                stream,
                ProtocolEnvelope.Create(sessionId, ServerHelloSequence, payload),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer may already be gone. The server caller still receives the original deterministic error.
        }
    }

    private static OrchardTransportException Unexpected(string message) =>
        new(OrchardTransportErrorCodes.UnexpectedHandshakeMessage, message);
}
