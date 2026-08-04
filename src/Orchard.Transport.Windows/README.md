# Orchard Windows local transport

`Orchard.Transport.Windows` is the local, full-duplex process boundary between an Orchard application process and the Windows simulator host. It transports only `Orchard.Protocol` versioned frames over a Windows named pipe. The library never writes protocol data, endpoint credentials, or diagnostics to standard output or standard error.

## Connection flow

Each launch creates a fresh `OrchardPipeEndpoint`:

- the pipe name contains 128 bits from the operating-system cryptographic random-number generator;
- the bearer token contains an independent 256 bits of randomness and uses canonical, unpadded base64url encoding;
- the server and client both request `PipeOptions.CurrentUserOnly` and byte transmission mode;
- the client is restricted to the local machine (`.`); and
- the server allocates bounded 64 KiB pipe buffers while protocol framing enforces the independent 4 MiB frame limit.

The version 1 bootstrap is ordered as follows:

```text
application/client                         simulator/host
       |                                         |
       | authenticate(session, seq 0, token)     |
       |---------------------------------------->|
       | hello(session, seq 1, version range)    |
       |---------------------------------------->|
       |                                         | constant-time token check
       |                                         | highest-common-version selection
       | hello(session, seq 0, version range)    |
       |<----------------------------------------|
       |                                         |
       | authenticated, sequenced v1 frames      |
```

Authentication failures and version mismatches receive a bounded, generic `shutdown` response when the peer is still connected. Both sides then close the failed pipe. A successful session checks the negotiated version, exact session identifier, and monotonically increasing sequence on every received envelope.

## API

```csharp
using Orchard.Protocol;
using Orchard.Transport.Windows;

var endpoint = OrchardPipeEndpoint.Create();
var options = new OrchardTransportOptions
{
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    HandshakeTimeout = TimeSpan.FromSeconds(5),
    OperationTimeout = TimeSpan.FromSeconds(30),
    Capabilities = ["render-v1"]
};

await using var listener = new OrchardPipeServer(
    endpoint,
    ProtocolPeerRole.Host,
    options);

var accept = listener.AcceptAsync().AsTask();

// In the child process, reconstruct credentials received through a protected
// process-to-process bootstrap channel. Do not pass the token on its command line.
var childEndpoint = OrchardPipeEndpoint.FromCredentials(
    endpoint.PipeName,
    endpoint.AuthenticationToken);
await using var application = await OrchardPipeClient.ConnectAsync(
    childEndpoint,
    ProtocolPeerRole.Application,
    options);

await using var host = await accept;
await application.SendAsync(new PingPayload
{
    Nonce = "health-1",
    SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
});
var envelope = await host.ReceiveAsync();
```

`AcceptAsync`, `ConnectAsync`, `SendAsync`, and `ReceiveAsync` accept caller cancellation. Caller cancellation remains an `OperationCanceledException` carrying the caller's token. Internal deadlines use `OrchardTransportException` and stable `ORT` codes. Timeouts are finite and validated at construction.

The listener and accepted sessions have separate lifetimes. Disposing the listener cancels pending accepts but intentionally does not tear down sessions already handed to the caller. Disposing a session cancels pending I/O and closes its pipe. Every owning call site should use `await using`.

## Error contract

| Code | Meaning |
|---|---|
| `ORT1001` | Invalid endpoint name or non-canonical/non-256-bit credential |
| `ORT1002` | Connection or accept deadline expired |
| `ORT1003` | Authentication/version handshake deadline expired |
| `ORT1004` | Bearer-token authentication failed |
| `ORT1005` | No common protocol version, or a post-handshake version changed |
| `ORT1006` | Handshake order/type/frame was invalid |
| `ORT1007` | Session identifier changed |
| `ORT1008` | Sequence was missing, repeated, or out of order |
| `ORT1009` | Peer closed before a complete frame was received |
| `ORT1010` | Windows pipe creation/access failed |
| `ORT1011` | Listener/session was disposed |
| `ORT1012` | A session read/write deadline expired |
| `ORT1013` | Invalid timeout, role, version range, or capabilities |

Payload validation errors from `Orchard.Protocol` remain `ProtocolException` values with their existing `ORP` codes. This preserves the distinction between a valid transport that carried invalid protocol data and a transport failure.

## Security assumptions and requirements

This transport protects a local preview session against accidental cross-process attachment and processes running as another Windows user. It does not claim to isolate Orchard from a malicious process already executing with the same user identity and sufficient rights to inspect or modify either process. That stronger boundary requires process sandboxing and operating-system policy outside this library.

Production launchers must follow these rules:

1. Generate a new endpoint for every child launch; never reuse endpoint credentials across simulator runs.
2. Transfer the token through a protected bootstrap channel such as an inherited, access-controlled handle. Do not put it in command-line arguments, shell history, crash metadata, telemetry, logs, stdout, or stderr.
3. Give credentials only to the intended child and discard launcher references after connection setup. Managed strings cannot be reliably zeroed, so process lifetime and disclosure minimisation matter.
4. Keep `CurrentUserOnly` enabled on both peers. Do not add a remote server-name option or downgrade to a network transport.
5. Treat the unguessable pipe name as defence in depth, not authentication. The independent token is mandatory and is compared through fixed-size SHA-256 digests with `CryptographicOperations.FixedTimeEquals`.
6. Keep framing and semantic validation enabled. `ProtocolFrameCodec` rejects empty, truncated, and over-4-MiB frames before unbounded allocation; `ProtocolJson` performs strict UTF-8/JSON and payload validation.
7. Preserve the default short connection and handshake deadlines. Longer operation deadlines may be appropriate while debugging, but infinite waits are rejected.

The library has no logging callback by design. A caller may report stable `ORT`/`ORP` codes through Orchard's diagnostic channel after redacting exception details; it must never log `AuthenticationToken` or an `AuthenticatePayload`.

## Executable verification

`tests/Orchard.Transport.Windows.Tests` is dependency-free and exercises real Windows named pipes. Its nine bounded tests cover:

1. credential randomness, shape, and `ToString` redaction;
2. authenticated full-duplex success, roles, capabilities, sessions, and initial sequence numbers;
3. wrong-token rejection on both peers;
4. version-range mismatch on both peers;
5. connection, handshake, and established-session operation timeouts;
6. preservation of caller cancellation during accept and established-session reads;
7. authentication and hello frames fragmented into one-byte writes;
8. deterministic peer-close and local-disposal errors; and
9. two sequential authenticated sessions accepted by one listener.

Run it with:

```powershell
dotnet run --project tests/Orchard.Transport.Windows.Tests/Orchard.Transport.Windows.Tests.csproj --configuration Release
```
