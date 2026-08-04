using System.Text;

namespace Orchard.Protocol;

/// <summary>Validates all semantic constraints in the version 1 wire contract.</summary>
public static class ProtocolValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void Validate(ProtocolEnvelope envelope)
    {
        if (envelope is null)
        {
            throw Invalid("$", "The envelope is required.");
        }

        if (envelope.Version != ProtocolConstants.Version1)
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.UnsupportedVersion,
                "$.version",
                $"Version {envelope.Version} is unsupported; version 1 is required.");
        }

        ValidateSafeIdentifier(
            envelope.SessionId,
            "$.sessionId",
            ProtocolConstants.MaximumSessionIdBytes);

        if (envelope.Sequence < 0)
        {
            throw Invalid("$.sequence", "The sequence must be non-negative.");
        }

        ValidateString(envelope.Type, "$.type", ProtocolConstants.MaximumKindBytes, allowEmpty: false);
        if (!ProtocolMessageTypes.All.Contains(envelope.Type))
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.UnknownMessageType,
                "$.type",
                $"Message type '{envelope.Type}' is not part of protocol version 1.");
        }

        if (envelope.Payload is null)
        {
            throw Invalid("$.payload", "The payload is required.");
        }

        if (!string.Equals(envelope.Type, envelope.Payload.MessageType, StringComparison.Ordinal))
        {
            throw Invalid(
                "$.type",
                $"Envelope type '{envelope.Type}' does not match payload type '{envelope.Payload.MessageType}'.");
        }

        switch (envelope.Payload)
        {
            case AuthenticatePayload authenticate:
                ValidateAuthenticate(authenticate);
                break;
            case HelloPayload hello:
                ValidateHello(hello);
                break;
            case ConfigurePayload configure:
                ValidateConfigure(configure);
                break;
            case RenderPayload render:
                ValidateRender(render);
                break;
            case EventPayload @event:
                ValidateEvent(@event);
                break;
            case EventResultPayload eventResult:
                ValidateEventResult(eventResult);
                break;
            case DiagnosticPayload diagnostic:
                ValidateDiagnostic(diagnostic);
                break;
            case PingPayload ping:
                ValidatePing(ping);
                break;
            case PongPayload pong:
                ValidatePong(pong);
                break;
            case ShutdownPayload shutdown:
                ValidateShutdown(shutdown);
                break;
            default:
                throw Invalid("$.payload", $"Payload CLR type '{envelope.Payload.GetType().Name}' is unsupported.");
        }
    }

    public static void ValidateTree(ProtocolViewNode root)
    {
        if (root is null)
        {
            throw InvalidTree("$.payload.root", "The root node is required.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(ProtocolViewNode Node, int Depth, string Path)>();
        stack.Push((root, 1, "$.payload.root"));
        var nodeCount = 0;

        while (stack.Count > 0)
        {
            var (node, depth, path) = stack.Pop();
            nodeCount++;

            if (nodeCount > ProtocolConstants.MaximumNodeCount)
            {
                throw Limit(
                    "$.payload.root",
                    $"The tree exceeds {ProtocolConstants.MaximumNodeCount} nodes.");
            }

            if (depth > ProtocolConstants.MaximumTreeDepth)
            {
                throw Limit(
                    path,
                    $"The tree exceeds depth {ProtocolConstants.MaximumTreeDepth}.");
            }

            ValidateSafeIdentifier(node.Id, $"{path}.id", ProtocolConstants.MaximumIdentifierBytes);
            if (!identifiers.Add(node.Id))
            {
                throw InvalidTree($"{path}.id", $"Node identifier '{node.Id}' is duplicated.");
            }

            ValidateString(node.Kind, $"{path}.kind", ProtocolConstants.MaximumKindBytes, allowEmpty: false);
            if (!ProtocolAllowlists.NodeKinds.Contains(node.Kind))
            {
                throw InvalidTree($"{path}.kind", $"Node kind '{node.Kind}' is not allowlisted for version 1.");
            }

            ValidateProperties(node, path);
            ValidateEvents(node, path);

            if (node.Children is null)
            {
                throw InvalidTree($"{path}.children", "The children collection cannot be null.");
            }

            if (node.Children.Count > ProtocolConstants.MaximumChildrenPerNode)
            {
                throw Limit(
                    $"{path}.children",
                    $"A node cannot have more than {ProtocolConstants.MaximumChildrenPerNode} children.");
            }

            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                var child = node.Children[index];
                if (child is null)
                {
                    throw InvalidTree($"{path}.children[{index}]", "A child node cannot be null.");
                }

                stack.Push((child, depth + 1, $"{path}.children[{index}]"));
            }
        }
    }

    private static void ValidateAuthenticate(AuthenticatePayload payload)
    {
        ValidateString(
            payload.Token,
            "$.payload.token",
            ProtocolConstants.MaximumAuthenticationTokenBytes,
            allowEmpty: false);
        ValidateSafeIdentifier(
            payload.ClientNonce,
            "$.payload.clientNonce",
            ProtocolConstants.MaximumNonceBytes);
    }

    private static void ValidateHello(HelloPayload payload)
    {
        ProtocolNegotiator.ValidateRange(payload.MinimumVersion, payload.MaximumVersion, "$.payload");
        if (!Enum.IsDefined(payload.Role))
        {
            throw Invalid("$.payload.role", "The peer role is invalid.");
        }

        ValidateBoundedUniqueIdentifiers(
            payload.Capabilities,
            "$.payload.capabilities",
            ProtocolConstants.MaximumCapabilityCount,
            ProtocolConstants.MaximumCapabilityBytes);
    }

    private static void ValidateConfigure(ConfigurePayload payload)
    {
        ValidateSafeIdentifier(
            payload.DeviceProfileId,
            "$.payload.deviceProfileId",
            ProtocolConstants.MaximumIdentifierBytes);
        ValidateRange(payload.LogicalWidth, 1, 16_384, "$.payload.logicalWidth");
        ValidateRange(payload.LogicalHeight, 1, 16_384, "$.payload.logicalHeight");
        if (!double.IsFinite(payload.DisplayScale) || payload.DisplayScale < 0.25 || payload.DisplayScale > 8)
        {
            throw Invalid("$.payload.displayScale", "The display scale must be finite and between 0.25 and 8.");
        }

        if (!Enum.IsDefined(payload.Appearance))
        {
            throw Invalid("$.payload.appearance", "The appearance is invalid.");
        }

        ValidateLocale(payload.Locale, "$.payload.locale");
    }

    private static void ValidateRender(RenderPayload payload)
    {
        ValidateRevision(payload.Revision, "$.payload.revision");
        ValidateTree(payload.Root);
    }

    private static void ValidateEvent(EventPayload payload)
    {
        ValidateSafeIdentifier(payload.EventId, "$.payload.eventId", ProtocolConstants.MaximumIdentifierBytes);
        ValidateRevision(payload.RenderRevision, "$.payload.renderRevision");
        ValidateSafeIdentifier(payload.NodeId, "$.payload.nodeId", ProtocolConstants.MaximumIdentifierBytes);
        ValidateString(payload.Event, "$.payload.event", ProtocolConstants.MaximumKindBytes, allowEmpty: false);
        if (!ProtocolAllowlists.Events.Contains(payload.Event))
        {
            throw Invalid("$.payload.event", $"Event '{payload.Event}' is not allowlisted for version 1.");
        }

        ValidateOptionalString(
            payload.Value,
            "$.payload.value",
            ProtocolConstants.MaximumEventValueBytes,
            allowEmpty: true);
    }

    private static void ValidateEventResult(EventResultPayload payload)
    {
        ValidateSafeIdentifier(payload.EventId, "$.payload.eventId", ProtocolConstants.MaximumIdentifierBytes);
        ValidateRevision(payload.RenderRevision, "$.payload.renderRevision");
        ValidateOptionalSafeIdentifier(
            payload.ErrorCode,
            "$.payload.errorCode",
            ProtocolConstants.MaximumDiagnosticCodeBytes);
        ValidateOptionalString(
            payload.Message,
            "$.payload.message",
            ProtocolConstants.MaximumDiagnosticMessageBytes,
            allowEmpty: false);

        if (payload.Accepted && (payload.ErrorCode is not null || payload.Message is not null))
        {
            throw Invalid("$.payload", "An accepted event result cannot carry error details.");
        }

        if (!payload.Accepted && payload.ErrorCode is null)
        {
            throw Invalid("$.payload.errorCode", "A rejected event result requires an error code.");
        }
    }

    private static void ValidateDiagnostic(DiagnosticPayload payload)
    {
        ValidateSafeIdentifier(payload.Code, "$.payload.code", ProtocolConstants.MaximumDiagnosticCodeBytes);
        if (!Enum.IsDefined(payload.Severity))
        {
            throw Invalid("$.payload.severity", "The diagnostic severity is invalid.");
        }

        ValidateString(
            payload.Message,
            "$.payload.message",
            ProtocolConstants.MaximumDiagnosticMessageBytes,
            allowEmpty: false);
        ValidateOptionalSafeIdentifier(
            payload.NodeId,
            "$.payload.nodeId",
            ProtocolConstants.MaximumIdentifierBytes);
        if (payload.RenderRevision is { } revision)
        {
            ValidateRevision(revision, "$.payload.renderRevision");
        }
    }

    private static void ValidatePing(PingPayload payload)
    {
        ValidateSafeIdentifier(payload.Nonce, "$.payload.nonce", ProtocolConstants.MaximumNonceBytes);
        ValidateTimestamp(payload.SentAtUnixMilliseconds, "$.payload.sentAtUnixMilliseconds");
    }

    private static void ValidatePong(PongPayload payload)
    {
        ValidateSafeIdentifier(payload.Nonce, "$.payload.nonce", ProtocolConstants.MaximumNonceBytes);
        ValidateTimestamp(payload.SentAtUnixMilliseconds, "$.payload.sentAtUnixMilliseconds");
        ValidateTimestamp(payload.RespondedAtUnixMilliseconds, "$.payload.respondedAtUnixMilliseconds");
        if (payload.RespondedAtUnixMilliseconds < payload.SentAtUnixMilliseconds)
        {
            throw Invalid("$.payload.respondedAtUnixMilliseconds", "The response timestamp precedes the ping timestamp.");
        }
    }

    private static void ValidateShutdown(ShutdownPayload payload)
    {
        if (!Enum.IsDefined(payload.Disposition))
        {
            throw Invalid("$.payload.disposition", "The shutdown disposition is invalid.");
        }

        ValidateString(
            payload.Reason,
            "$.payload.reason",
            ProtocolConstants.MaximumReasonBytes,
            allowEmpty: false);
    }

    private static void ValidateProperties(ProtocolViewNode node, string path)
    {
        if (node.Properties is null)
        {
            throw InvalidTree($"{path}.properties", "The properties collection cannot be null.");
        }

        if (node.Properties.Count > ProtocolConstants.MaximumPropertiesPerNode)
        {
            throw Limit(
                $"{path}.properties",
                $"A node cannot have more than {ProtocolConstants.MaximumPropertiesPerNode} properties.");
        }

        foreach (var (name, value) in node.Properties)
        {
            ValidateString(
                name,
                $"{path}.properties",
                ProtocolConstants.MaximumPropertyNameBytes,
                allowEmpty: false);
            if (!ProtocolAllowlists.PropertyNames.Contains(name))
            {
                throw InvalidTree(
                    $"{path}.properties.{name}",
                    $"Property '{name}' is not allowlisted for version 1.");
            }

            ValidateString(
                value,
                $"{path}.properties.{name}",
                ProtocolConstants.MaximumPropertyValueBytes,
                allowEmpty: true);
        }
    }

    private static void ValidateEvents(ProtocolViewNode node, string path)
    {
        if (node.Events is null)
        {
            throw InvalidTree($"{path}.events", "The events collection cannot be null.");
        }

        if (node.Events.Count > ProtocolConstants.MaximumEventsPerNode)
        {
            throw Limit(
                $"{path}.events",
                $"A node cannot expose more than {ProtocolConstants.MaximumEventsPerNode} events.");
        }

        var uniqueEvents = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < node.Events.Count; index++)
        {
            var eventName = node.Events[index];
            ValidateString(
                eventName,
                $"{path}.events[{index}]",
                ProtocolConstants.MaximumKindBytes,
                allowEmpty: false);
            if (!ProtocolAllowlists.Events.Contains(eventName))
            {
                throw InvalidTree(
                    $"{path}.events[{index}]",
                    $"Event '{eventName}' is not allowlisted for version 1.");
            }

            if (!uniqueEvents.Add(eventName))
            {
                throw InvalidTree($"{path}.events[{index}]", $"Event '{eventName}' is duplicated on the node.");
            }
        }
    }

    private static void ValidateBoundedUniqueIdentifiers(
        IReadOnlyList<string> values,
        string path,
        int maximumCount,
        int maximumBytes)
    {
        if (values is null)
        {
            throw Invalid(path, "The collection cannot be null.");
        }

        if (values.Count > maximumCount)
        {
            throw Limit(path, $"The collection cannot contain more than {maximumCount} entries.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            ValidateSafeIdentifier(values[index], $"{path}[{index}]", maximumBytes);
            if (!unique.Add(values[index]))
            {
                throw Invalid($"{path}[{index}]", $"Value '{values[index]}' is duplicated.");
            }
        }
    }

    private static void ValidateSafeIdentifier(string value, string path, int maximumBytes)
    {
        ValidateString(value, path, maximumBytes, allowEmpty: false);
        foreach (var character in value)
        {
            if (!IsSafeIdentifierCharacter(character))
            {
                throw Invalid(path, "Identifiers may contain only ASCII letters, digits, '.', '_', '-', and ':'.");
            }
        }
    }

    private static void ValidateOptionalSafeIdentifier(string? value, string path, int maximumBytes)
    {
        if (value is not null)
        {
            ValidateSafeIdentifier(value, path, maximumBytes);
        }
    }

    private static bool IsSafeIdentifierCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or ':';

    private static void ValidateLocale(string value, string path)
    {
        ValidateString(value, path, ProtocolConstants.MaximumLocaleBytes, allowEmpty: false);
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
            {
                throw Invalid(path, "The locale must be a BCP-47-shaped ASCII language tag.");
            }
        }
    }

    private static void ValidateOptionalString(
        string? value,
        string path,
        int maximumBytes,
        bool allowEmpty)
    {
        if (value is not null)
        {
            ValidateString(value, path, maximumBytes, allowEmpty);
        }
    }

    private static void ValidateString(string value, string path, int maximumBytes, bool allowEmpty)
    {
        if (value is null)
        {
            throw Invalid(path, "The string is required.");
        }

        if (!allowEmpty && value.Length == 0)
        {
            throw Invalid(path, "The string cannot be empty.");
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.InvalidPayload,
                path,
                $"The string contains invalid Unicode: {exception.Message}");
        }

        if (byteCount > maximumBytes)
        {
            throw Limit(path, $"The string exceeds {maximumBytes} UTF-8 bytes.");
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw Invalid(path, "NUL characters are not permitted.");
        }
    }

    private static void ValidateRange(int value, int minimum, int maximum, string path)
    {
        if (value < minimum || value > maximum)
        {
            throw Invalid(path, $"The value must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateRevision(long value, string path)
    {
        if (value < 0)
        {
            throw Invalid(path, "The revision must be non-negative.");
        }
    }

    private static void ValidateTimestamp(long value, string path)
    {
        if (value < 0)
        {
            throw Invalid(path, "The Unix timestamp must be non-negative.");
        }
    }

    private static ProtocolValidationException Invalid(string path, string message) =>
        new(ProtocolErrorCodes.InvalidPayload, path, message);

    private static ProtocolValidationException InvalidTree(string path, string message) =>
        new(ProtocolErrorCodes.InvalidTree, path, message);

    private static ProtocolValidationException Limit(string path, string message) =>
        new(ProtocolErrorCodes.LimitExceeded, path, message);
}
