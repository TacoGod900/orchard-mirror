using System.Text.Json.Serialization;

namespace Orchard.Protocol;

public enum ProtocolPeerRole
{
    Application,
    Host,
    TestAgent
}

public enum SimulatorAppearance
{
    Light,
    Dark
}

public enum ProtocolDiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Fatal
}

public enum ShutdownDisposition
{
    Normal,
    Restart,
    AuthenticationFailed,
    ProtocolError,
    HostRequested
}

/// <summary>The typed payload carried by a protocol envelope.</summary>
public interface IProtocolPayload
{
    [JsonIgnore]
    string MessageType { get; }
}

public sealed record ProtocolEnvelope
{
    public int Version { get; init; } = ProtocolConstants.Version1;

    public required string Type { get; init; }

    public required string SessionId { get; init; }

    public required long Sequence { get; init; }

    public required IProtocolPayload Payload { get; init; }

    public static ProtocolEnvelope Create(string sessionId, long sequence, IProtocolPayload payload) => new()
    {
        Type = payload?.MessageType ?? throw new ArgumentNullException(nameof(payload)),
        SessionId = sessionId,
        Sequence = sequence,
        Payload = payload
    };
}

public sealed record AuthenticatePayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Authenticate;

    public required string Token { get; init; }

    public required string ClientNonce { get; init; }
}

public sealed record HelloPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Hello;

    public required int MinimumVersion { get; init; }

    public required int MaximumVersion { get; init; }

    public required ProtocolPeerRole Role { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record ConfigurePayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Configure;

    public required string DeviceProfileId { get; init; }

    public required int LogicalWidth { get; init; }

    public required int LogicalHeight { get; init; }

    public required double DisplayScale { get; init; }

    public required SimulatorAppearance Appearance { get; init; }

    public required string Locale { get; init; }

    public required bool AccessibilityEnabled { get; init; }
}

public sealed record RenderPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Render;

    public required long Revision { get; init; }

    public required ProtocolViewNode Root { get; init; }
}

public sealed record EventPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Event;

    public required string EventId { get; init; }

    public required long RenderRevision { get; init; }

    public required string NodeId { get; init; }

    public required string Event { get; init; }

    public string? Value { get; init; }
}

public sealed record EventResultPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.EventResult;

    public required string EventId { get; init; }

    public required bool Accepted { get; init; }

    public required long RenderRevision { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }
}

public sealed record DiagnosticPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Diagnostic;

    public required string Code { get; init; }

    public required ProtocolDiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    public string? NodeId { get; init; }

    public long? RenderRevision { get; init; }
}

public sealed record PingPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Ping;

    public required string Nonce { get; init; }

    public required long SentAtUnixMilliseconds { get; init; }
}

public sealed record PongPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Pong;

    public required string Nonce { get; init; }

    public required long SentAtUnixMilliseconds { get; init; }

    public required long RespondedAtUnixMilliseconds { get; init; }
}

public sealed record ShutdownPayload : IProtocolPayload
{
    [JsonIgnore]
    public string MessageType => ProtocolMessageTypes.Shutdown;

    public required ShutdownDisposition Disposition { get; init; }

    public required string Reason { get; init; }

    public int? ExitCode { get; init; }
}

/// <summary>A retained view-tree node transferred in a render message.</summary>
public sealed record ProtocolViewNode
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> Events { get; init; } = [];

    public IReadOnlyList<ProtocolViewNode> Children { get; init; } = [];
}
