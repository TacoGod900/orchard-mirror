using System.Collections.Frozen;

namespace Orchard.Protocol;

/// <summary>Wire-level limits and identifiers for Orchard protocol version 1.</summary>
public static class ProtocolConstants
{
    public const int Version1 = 1;
    public const int MinimumSupportedVersion = Version1;
    public const int MaximumSupportedVersion = Version1;

    public const int FrameHeaderBytes = sizeof(uint);
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    public const int MaximumJsonDepth = 384;

    public const int MaximumTreeDepth = 128;
    public const int MaximumNodeCount = 5_000;
    public const int MaximumChildrenPerNode = 5_000;
    public const int MaximumPropertiesPerNode = 64;
    public const int MaximumEventsPerNode = 16;

    public const int MaximumSessionIdBytes = 128;
    public const int MaximumIdentifierBytes = 128;
    public const int MaximumKindBytes = 64;
    public const int MaximumPropertyNameBytes = 64;
    public const int MaximumPropertyValueBytes = 64 * 1024;
    public const int MaximumAuthenticationTokenBytes = 4 * 1024;
    public const int MaximumNonceBytes = 128;
    public const int MaximumCapabilityCount = 128;
    public const int MaximumCapabilityBytes = 128;
    public const int MaximumLocaleBytes = 64;
    public const int MaximumDiagnosticCodeBytes = 64;
    public const int MaximumDiagnosticMessageBytes = 16 * 1024;
    public const int MaximumReasonBytes = 4 * 1024;
    public const int MaximumEventValueBytes = 64 * 1024;
}

/// <summary>Stable message type names used in version 1 envelopes.</summary>
public static class ProtocolMessageTypes
{
    public const string Authenticate = "authenticate";
    public const string Hello = "hello";
    public const string Configure = "configure";
    public const string Render = "render";
    public const string Event = "event";
    public const string EventResult = "eventResult";
    public const string Diagnostic = "diagnostic";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Shutdown = "shutdown";

    public static IReadOnlySet<string> All { get; } = new[]
    {
        Authenticate,
        Hello,
        Configure,
        Render,
        Event,
        EventResult,
        Diagnostic,
        Ping,
        Pong,
        Shutdown
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>Stable machine-readable error codes emitted by the protocol layer.</summary>
public static class ProtocolErrorCodes
{
    public const string InvalidFrame = "ORP1001";
    public const string FrameTooLarge = "ORP1002";
    public const string InvalidUtf8OrJson = "ORP1003";
    public const string InvalidEnvelope = "ORP1004";
    public const string UnknownMessageType = "ORP1005";
    public const string InvalidPayload = "ORP1006";
    public const string UnsupportedVersion = "ORP1007";
    public const string NoCommonVersion = "ORP1008";
    public const string InvalidTree = "ORP1009";
    public const string LimitExceeded = "ORP1010";
}
