using System.IO.Pipes;
using Orchard.Protocol;
using Orchard.Transport.Windows;

namespace Orchard.Transport.Windows.Tests;

internal static class Program
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

    private static readonly (string Name, Func<Task> Body)[] Tests =
    [
        ("creates unguessable redacted endpoint credentials", EndpointCredentialsAreRandomAndRedacted),
        ("authenticates and exchanges full-duplex protocol frames", AuthenticatedSuccess),
        ("rejects an incorrect 256-bit authentication token", RejectsWrongToken),
        ("rejects a protocol version mismatch on both peers", RejectsVersionMismatch),
        ("bounds connection and handshake waits with deterministic timeouts", EnforcesTimeouts),
        ("preserves caller cancellation", PreservesCancellation),
        ("accepts handshake frames fragmented to single-byte writes", AcceptsFragmentedFrames),
        ("reports peer closure and local disposal deterministically", ReportsConnectionDisposal),
        ("accepts sequential authenticated sessions on one listener", AcceptsSequentialSessions)
    ];

    private static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (var (name, body) in Tests)
        {
            try
            {
                await body().WaitAsync(TestDeadline).ConfigureAwait(false);
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
        Console.WriteLine($"Executed {Tests.Length} transport tests; {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static Task EndpointCredentialsAreRandomAndRedacted()
    {
        var first = OrchardPipeEndpoint.Create();
        var second = OrchardPipeEndpoint.Create();
        Assert(first.PipeName.StartsWith("orchard-v1-", StringComparison.Ordinal), "Pipe name prefix is missing.");
        Assert(first.AuthenticationToken.Length == 43, "A 256-bit unpadded base64url token must contain 43 characters.");
        Assert(!string.Equals(first.PipeName, second.PipeName, StringComparison.Ordinal), "Pipe names unexpectedly matched.");
        Assert(
            !string.Equals(first.AuthenticationToken, second.AuthenticationToken, StringComparison.Ordinal),
            "Authentication tokens unexpectedly matched.");
        Assert(!first.ToString().Contains(first.AuthenticationToken, StringComparison.Ordinal), "ToString leaked the token.");
        Assert(!first.ToString().Contains(first.PipeName, StringComparison.Ordinal), "ToString leaked the pipe name.");
        Assert(first.ToString().Contains("[REDACTED]", StringComparison.Ordinal), "ToString did not mark the token redacted.");
        return Task.CompletedTask;
    }

    private static async Task AuthenticatedSuccess()
    {
        var endpoint = OrchardPipeEndpoint.Create();
        var serverOptions = FastOptions(["host-render-v1"]);
        var clientOptions = FastOptions(["app-events-v1"]);
        await using var server = new OrchardPipeServer(endpoint, ProtocolPeerRole.Host, serverOptions);

        var acceptTask = server.AcceptAsync().AsTask();
        await using var client = await OrchardPipeClient.ConnectAsync(
            endpoint,
            ProtocolPeerRole.Application,
            clientOptions).ConfigureAwait(false);
        await using var host = await acceptTask.ConfigureAwait(false);

        Assert(client.NegotiatedVersion == 1 && host.NegotiatedVersion == 1, "Protocol version 1 was not negotiated.");
        Assert(client.SessionId == host.SessionId, "The peers did not agree on a session identifier.");
        Assert(client.PeerRole == ProtocolPeerRole.Host, "The client did not observe the host role.");
        Assert(host.PeerRole == ProtocolPeerRole.Application, "The server did not observe the application role.");
        Assert(client.PeerCapabilities.SequenceEqual(["host-render-v1"]), "Server capabilities were not preserved.");
        Assert(host.PeerCapabilities.SequenceEqual(["app-events-v1"]), "Client capabilities were not preserved.");

        var ping = new PingPayload { Nonce = "ping-1", SentAtUnixMilliseconds = 100 };
        await client.SendAsync(ping).ConfigureAwait(false);
        var receivedPing = await host.ReceiveAsync().ConfigureAwait(false);
        Assert(
            receivedPing.Sequence == 2 && (PingPayload)receivedPing.Payload == ping,
            "The server did not receive client sequence 2.");

        var pong = new PongPayload
        {
            Nonce = "ping-1",
            SentAtUnixMilliseconds = 100,
            RespondedAtUnixMilliseconds = 101
        };
        await host.SendAsync(pong).ConfigureAwait(false);
        var receivedPong = await client.ReceiveAsync().ConfigureAwait(false);
        Assert(
            receivedPong.Sequence == 1 && (PongPayload)receivedPong.Payload == pong,
            "The client did not receive server sequence 1.");
    }

    private static async Task RejectsWrongToken()
    {
        var serverEndpoint = OrchardPipeEndpoint.Create();
        var unrelatedSecret = OrchardPipeEndpoint.Create().AuthenticationToken;
        var clientEndpoint = OrchardPipeEndpoint.FromCredentials(serverEndpoint.PipeName, unrelatedSecret);
        await using var server = new OrchardPipeServer(serverEndpoint, options: FastOptions());

        var serverFailure = CaptureTransportFailureAsync(server.AcceptAsync().AsTask());
        var clientFailure = CaptureTransportFailureAsync(
            OrchardPipeClient.ConnectAsync(clientEndpoint, options: FastOptions()).AsTask());

        var clientCode = await clientFailure.ConfigureAwait(false);
        var serverCode = await serverFailure.ConfigureAwait(false);
        Assert(
            clientCode == OrchardTransportErrorCodes.AuthenticationFailed,
            $"The client received {clientCode} instead of the authentication failure code.");
        Assert(
            serverCode == OrchardTransportErrorCodes.AuthenticationFailed,
            $"The server received {serverCode} instead of the authentication failure code.");
    }

    private static async Task RejectsVersionMismatch()
    {
        var listenerEndpoint = OrchardPipeEndpoint.Create();
        await using (var server = new OrchardPipeServer(listenerEndpoint, options: FastOptions()))
        {
            var serverFailure = CaptureTransportFailureAsync(server.AcceptAsync().AsTask());
            await using var futureClient = CreateRawClient(listenerEndpoint.PipeName);
            await futureClient.ConnectAsync(1_000).ConfigureAwait(false);
            const string sessionId = "future-client";
            await ProtocolFrameCodec.WriteAsync(
                futureClient,
                ProtocolEnvelope.Create(
                    sessionId,
                    0,
                    new AuthenticatePayload
                    {
                        Token = listenerEndpoint.AuthenticationToken,
                        ClientNonce = "future-client-nonce"
                    })).ConfigureAwait(false);
            await ProtocolFrameCodec.WriteAsync(
                futureClient,
                ProtocolEnvelope.Create(
                    sessionId,
                    1,
                    new HelloPayload
                    {
                        MinimumVersion = 2,
                        MaximumVersion = 2,
                        Role = ProtocolPeerRole.Application
                    })).ConfigureAwait(false);
            var rejection = await ProtocolFrameCodec.ReadAsync(futureClient).ConfigureAwait(false);
            Assert(
                rejection.Payload is ShutdownPayload { Disposition: ShutdownDisposition.ProtocolError },
                "The server did not send a protocol-error shutdown.");
            Assert(
                await serverFailure.ConfigureAwait(false) == OrchardTransportErrorCodes.VersionMismatch,
                "The server did not report the version-mismatch code.");
        }

        var clientEndpoint = OrchardPipeEndpoint.Create();
        await using var futureServer = CreateRawServer(clientEndpoint.PipeName);
        var futureServerTask = ReplyWithFutureVersionAsync(futureServer, clientEndpoint);
        var clientFailure = await CaptureTransportFailureAsync(
            OrchardPipeClient.ConnectAsync(clientEndpoint, options: FastOptions()).AsTask()).ConfigureAwait(false);
        await futureServerTask.ConfigureAwait(false);
        Assert(
            clientFailure == OrchardTransportErrorCodes.VersionMismatch,
            "The client did not report the version-mismatch code.");
    }

    private static async Task EnforcesTimeouts()
    {
        var noClientEndpoint = OrchardPipeEndpoint.Create();
        await using (var server = new OrchardPipeServer(
            noClientEndpoint,
            options: FastOptions() with { ConnectionTimeout = TimeSpan.FromMilliseconds(80) }))
        {
            await AssertTransportFailureAsync(
                server.AcceptAsync().AsTask(),
                OrchardTransportErrorCodes.ConnectionTimeout).ConfigureAwait(false);
        }

        var silentClientEndpoint = OrchardPipeEndpoint.Create();
        await using var handshakeServer = new OrchardPipeServer(
            silentClientEndpoint,
            options: FastOptions() with { HandshakeTimeout = TimeSpan.FromMilliseconds(80) });
        var acceptTask = handshakeServer.AcceptAsync().AsTask();
        await using var silentClient = CreateRawClient(silentClientEndpoint.PipeName);
        await silentClient.ConnectAsync(1_000).ConfigureAwait(false);
        await AssertTransportFailureAsync(
            acceptTask,
            OrchardTransportErrorCodes.HandshakeTimeout).ConfigureAwait(false);

        var operationEndpoint = OrchardPipeEndpoint.Create();
        var shortOperation = FastOptions() with { OperationTimeout = TimeSpan.FromMilliseconds(80) };
        await using var operationServer = new OrchardPipeServer(operationEndpoint, options: shortOperation);
        var operationAccept = operationServer.AcceptAsync().AsTask();
        await using var operationClient = await OrchardPipeClient.ConnectAsync(
            operationEndpoint,
            options: shortOperation).ConfigureAwait(false);
        await using var operationHost = await operationAccept.ConfigureAwait(false);
        await AssertTransportFailureAsync(
            operationHost.ReceiveAsync().AsTask(),
            OrchardTransportErrorCodes.OperationTimeout).ConfigureAwait(false);
    }

    private static async Task PreservesCancellation()
    {
        var endpoint = OrchardPipeEndpoint.Create();
        await using var server = new OrchardPipeServer(
            endpoint,
            options: FastOptions() with { ConnectionTimeout = TimeSpan.FromSeconds(2) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        try
        {
            _ = await server.AcceptAsync(cancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Expected caller cancellation.");
        }
        catch (OperationCanceledException exception)
        {
            Assert(exception.CancellationToken == cancellation.Token, "The caller cancellation token was not preserved.");
        }

        var operationEndpoint = OrchardPipeEndpoint.Create();
        await using var operationServer = new OrchardPipeServer(operationEndpoint, options: FastOptions());
        var operationAccept = operationServer.AcceptAsync().AsTask();
        await using var operationClient = await OrchardPipeClient.ConnectAsync(
            operationEndpoint,
            options: FastOptions()).ConfigureAwait(false);
        await using var operationHost = await operationAccept.ConfigureAwait(false);
        using var operationCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        try
        {
            _ = await operationHost.ReceiveAsync(operationCancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Expected caller cancellation during a session read.");
        }
        catch (OperationCanceledException exception)
        {
            Assert(
                exception.CancellationToken == operationCancellation.Token,
                "The session read did not preserve its caller cancellation token.");
        }
    }

    private static async Task AcceptsFragmentedFrames()
    {
        var endpoint = OrchardPipeEndpoint.Create();
        await using var server = new OrchardPipeServer(endpoint, options: FastOptions());
        var acceptTask = server.AcceptAsync().AsTask();

        await using var rawClient = CreateRawClient(endpoint.PipeName);
        await rawClient.ConnectAsync(1_000).ConfigureAwait(false);
        const string sessionId = "fragmented-session";
        var authenticate = ProtocolEnvelope.Create(
            sessionId,
            0,
            new AuthenticatePayload { Token = endpoint.AuthenticationToken, ClientNonce = "fragmented-nonce" });
        var hello = ProtocolEnvelope.Create(
            sessionId,
            1,
            new HelloPayload
            {
                MinimumVersion = 1,
                MaximumVersion = 1,
                Role = ProtocolPeerRole.TestAgent,
                Capabilities = ["fragmented-writes"]
            });

        await WriteOneByteAtATimeAsync(rawClient, EncodeFrame(authenticate)).ConfigureAwait(false);
        await WriteOneByteAtATimeAsync(rawClient, EncodeFrame(hello)).ConfigureAwait(false);
        var serverHello = await ProtocolFrameCodec.ReadAsync(rawClient).ConfigureAwait(false);
        Assert(serverHello.Type == ProtocolMessageTypes.Hello, "The fragmented handshake did not receive a server hello.");

        await using var session = await acceptTask.ConfigureAwait(false);
        Assert(session.PeerRole == ProtocolPeerRole.TestAgent, "The fragmented peer role was not retained.");
        Assert(session.PeerCapabilities.SequenceEqual(["fragmented-writes"]), "Fragmented capabilities were lost.");
    }

    private static async Task ReportsConnectionDisposal()
    {
        var endpoint = OrchardPipeEndpoint.Create();
        await using var server = new OrchardPipeServer(endpoint, options: FastOptions());
        var acceptTask = server.AcceptAsync().AsTask();
        var client = await OrchardPipeClient.ConnectAsync(endpoint, options: FastOptions()).ConfigureAwait(false);
        await using var host = await acceptTask.ConfigureAwait(false);

        await client.DisposeAsync().ConfigureAwait(false);
        await AssertTransportFailureAsync(
            host.ReceiveAsync().AsTask(),
            OrchardTransportErrorCodes.ConnectionClosed).ConfigureAwait(false);
        await AssertTransportFailureAsync(
            client.SendAsync(new PingPayload { Nonce = "after-close", SentAtUnixMilliseconds = 1 }).AsTask(),
            OrchardTransportErrorCodes.Disposed).ConfigureAwait(false);
    }

    private static async Task AcceptsSequentialSessions()
    {
        var endpoint = OrchardPipeEndpoint.Create();
        await using var server = new OrchardPipeServer(endpoint, options: FastOptions());

        var firstId = await RunOneSessionAsync(server, endpoint, "first").ConfigureAwait(false);
        var secondId = await RunOneSessionAsync(server, endpoint, "second").ConfigureAwait(false);
        Assert(!string.Equals(firstId, secondId, StringComparison.Ordinal), "Sequential sessions reused an identifier.");
    }

    private static async Task<string> RunOneSessionAsync(
        OrchardPipeServer server,
        OrchardPipeEndpoint endpoint,
        string nonce)
    {
        var acceptTask = server.AcceptAsync().AsTask();
        await using var client = await OrchardPipeClient.ConnectAsync(endpoint, options: FastOptions()).ConfigureAwait(false);
        await using var host = await acceptTask.ConfigureAwait(false);
        await client.SendAsync(new PingPayload { Nonce = nonce, SentAtUnixMilliseconds = 1 }).ConfigureAwait(false);
        var received = await host.ReceiveAsync().ConfigureAwait(false);
        Assert(((PingPayload)received.Payload).Nonce == nonce, "A sequential session received the wrong payload.");
        return client.SessionId;
    }

    private static OrchardTransportOptions FastOptions(IReadOnlyList<string>? capabilities = null) => new()
    {
        ConnectionTimeout = TimeSpan.FromSeconds(2),
        HandshakeTimeout = TimeSpan.FromSeconds(2),
        OperationTimeout = TimeSpan.FromSeconds(2),
        Capabilities = capabilities ?? []
    };

    private static NamedPipeClientStream CreateRawClient(string pipeName) => new(
        ".",
        pipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static NamedPipeServerStream CreateRawServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        64 * 1024,
        64 * 1024);

    private static async Task ReplyWithFutureVersionAsync(
        NamedPipeServerStream server,
        OrchardPipeEndpoint endpoint)
    {
        await server.WaitForConnectionAsync().ConfigureAwait(false);
        var authenticate = await ProtocolFrameCodec.ReadAsync(server).ConfigureAwait(false);
        var hello = await ProtocolFrameCodec.ReadAsync(server).ConfigureAwait(false);
        Assert(authenticate.Payload is AuthenticatePayload payload && payload.Token == endpoint.AuthenticationToken,
            "The raw server received the wrong authentication frame.");
        Assert(hello.Payload is HelloPayload, "The raw server did not receive a hello frame.");
        await ProtocolFrameCodec.WriteAsync(
            server,
            ProtocolEnvelope.Create(
                authenticate.SessionId,
                0,
                new HelloPayload
                {
                    MinimumVersion = 2,
                    MaximumVersion = 2,
                    Role = ProtocolPeerRole.Host
                })).ConfigureAwait(false);
    }

    private static byte[] EncodeFrame(ProtocolEnvelope envelope)
    {
        using var stream = new MemoryStream();
        ProtocolFrameCodec.Write(stream, envelope);
        return stream.ToArray();
    }

    private static async Task WriteOneByteAtATimeAsync(Stream stream, byte[] frame)
    {
        for (var index = 0; index < frame.Length; index++)
        {
            await stream.WriteAsync(frame.AsMemory(index, 1)).ConfigureAwait(false);
        }
    }

    private static async Task AssertTransportFailureAsync(Task operation, string expectedCode)
    {
        var actual = await CaptureTransportFailureAsync(operation).ConfigureAwait(false);
        Assert(actual == expectedCode, $"Expected transport code {expectedCode}; received {actual}.");
    }

    private static async Task<string> CaptureTransportFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
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
