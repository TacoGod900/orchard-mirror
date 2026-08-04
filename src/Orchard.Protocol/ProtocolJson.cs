using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchard.Protocol;

/// <summary>Strict, case-sensitive version 1 JSON encoding and decoding.</summary>
public static class ProtocolJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly Dictionary<string, Type> PayloadTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [ProtocolMessageTypes.Authenticate] = typeof(AuthenticatePayload),
            [ProtocolMessageTypes.Hello] = typeof(HelloPayload),
            [ProtocolMessageTypes.Configure] = typeof(ConfigurePayload),
            [ProtocolMessageTypes.Render] = typeof(RenderPayload),
            [ProtocolMessageTypes.Event] = typeof(EventPayload),
            [ProtocolMessageTypes.EventResult] = typeof(EventResultPayload),
            [ProtocolMessageTypes.Diagnostic] = typeof(DiagnosticPayload),
            [ProtocolMessageTypes.Ping] = typeof(PingPayload),
            [ProtocolMessageTypes.Pong] = typeof(PongPayload),
            [ProtocolMessageTypes.Shutdown] = typeof(ShutdownPayload)
        };

    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.Ordinal)
    {
        "version",
        "type",
        "sessionId",
        "sequence",
        "payload"
    };

    public static byte[] Serialize(ProtocolEnvelope envelope)
    {
        ProtocolValidator.Validate(envelope);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", envelope.Version);
            writer.WriteString("type", envelope.Type);
            writer.WriteString("sessionId", envelope.SessionId);
            writer.WriteNumber("sequence", envelope.Sequence);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, envelope.Payload, envelope.Payload.GetType(), SerializerOptions);
            writer.WriteEndObject();
        }

        if (output.WrittenCount > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.FrameTooLarge,
                $"Serialized payload is {output.WrittenCount} bytes; the maximum is {ProtocolConstants.MaximumFrameBytes} bytes.");
        }

        return output.WrittenSpan.ToArray();
    }

    public static ProtocolEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidUtf8OrJson, "The JSON payload is empty.");
        }

        if (utf8Json.Length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.FrameTooLarge,
                $"JSON payload is {utf8Json.Length} bytes; the maximum is {ProtocolConstants.MaximumFrameBytes} bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ProtocolConstants.MaximumJsonDepth
                });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Envelope("$", "The envelope must be a JSON object.");
            }

            RejectDuplicateProperties(root, "$", depth: 1);
            ValidateEnvelopePropertySet(root);

            var version = ReadInt32(root, "version");
            var type = ReadString(root, "type");
            var sessionId = ReadString(root, "sessionId");
            var sequence = ReadInt64(root, "sequence");
            var payloadElement = root.GetProperty("payload");
            if (payloadElement.ValueKind != JsonValueKind.Object)
            {
                throw Envelope("$.payload", "The payload must be a JSON object.");
            }

            if (!PayloadTypes.TryGetValue(type, out var payloadType))
            {
                throw new ProtocolValidationException(
                    ProtocolErrorCodes.UnknownMessageType,
                    "$.type",
                    $"Message type '{type}' is not part of protocol version 1.");
            }

            var payload = JsonSerializer.Deserialize(payloadElement, payloadType, SerializerOptions) as IProtocolPayload;
            if (payload is null)
            {
                throw Envelope("$.payload", "The payload could not be decoded.");
            }

            var envelope = new ProtocolEnvelope
            {
                Version = version,
                Type = type,
                SessionId = sessionId,
                Sequence = sequence,
                Payload = payload
            };
            ProtocolValidator.Validate(envelope);
            return envelope;
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidUtf8OrJson,
                $"The payload is not strict protocol JSON: {exception.Message}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidEnvelope,
                $"The envelope contains a value of the wrong JSON type: {exception.Message}",
                exception);
        }
    }

    private static void ValidateEnvelopePropertySet(JsonElement root)
    {
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            count++;
            if (!EnvelopeProperties.Contains(property.Name))
            {
                throw Envelope($"$.{property.Name}", "Unknown envelope properties are rejected.");
            }
        }

        if (count != EnvelopeProperties.Count)
        {
            foreach (var property in EnvelopeProperties)
            {
                if (!root.TryGetProperty(property, out _))
                {
                    throw Envelope($"$.{property}", "The required envelope property is missing.");
                }
            }
        }
    }

    private static int ReadInt32(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw Envelope($"$.{name}", "The property must be a 32-bit integer.");
        }

        return value;
    }

    private static long ReadInt64(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw Envelope($"$.{name}", "The property must be a 64-bit integer.");
        }

        return value;
    }

    private static string ReadString(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Envelope($"$.{name}", "The property must be a string.");
        }

        return element.GetString() ?? throw Envelope($"$.{name}", "The string cannot be null.");
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, int depth)
    {
        if (depth > ProtocolConstants.MaximumJsonDepth)
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.LimitExceeded,
                path,
                $"JSON nesting exceeds {ProtocolConstants.MaximumJsonDepth} levels.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Envelope($"{path}.{property.Name}", "Duplicate JSON properties are rejected.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static ProtocolValidationException Envelope(string path, string message) =>
        new(ProtocolErrorCodes.InvalidEnvelope, path, message);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = ProtocolConstants.MaximumJsonDepth,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(new ExactCamelCaseEnumConverter<ProtocolPeerRole>());
        options.Converters.Add(new ExactCamelCaseEnumConverter<SimulatorAppearance>());
        options.Converters.Add(new ExactCamelCaseEnumConverter<ProtocolDiagnosticSeverity>());
        options.Converters.Add(new ExactCamelCaseEnumConverter<ShutdownDisposition>());
        return options;
    }
}

/// <summary>
/// Reads only the exact lower-camel spelling emitted by the protocol. The built-in string-enum
/// converter intentionally performs a case-insensitive fallback when reading, which would make
/// malformed values valid on .NET but invalid in the Swift implementation.
/// </summary>
internal sealed class ExactCamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ValuesByWireName =
        Enum.GetValues<TEnum>().ToDictionary(
            value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
            StringComparer.Ordinal);

    private static readonly Dictionary<TEnum, string> WireNamesByValue =
        ValuesByWireName.ToDictionary(pair => pair.Value, pair => pair.Key);

    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"The {typeof(TEnum).Name} value must be a string.");
        }

        var wireName = reader.GetString();
        if (wireName is null || !ValuesByWireName.TryGetValue(wireName, out var value))
        {
            throw new JsonException($"The {typeof(TEnum).Name} value is not an exact protocol spelling.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options)
    {
        if (!WireNamesByValue.TryGetValue(value, out var wireName))
        {
            throw new JsonException($"The {typeof(TEnum).Name} value is not defined.");
        }

        writer.WriteStringValue(wireName);
    }
}
