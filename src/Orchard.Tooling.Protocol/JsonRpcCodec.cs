using System.Buffers;
using System.Text.Json;

namespace Orchard.Tooling.Protocol;

/// <summary>Strict JSON-RPC 2.0 codec used by the SourceKit-LSP boundary.</summary>
public static class JsonRpcCodec
{
    private const int MaximumMethodLength = 512;
    private const int MaximumStringIdLength = 256;
    private const int MaximumErrorMessageLength = 16 * 1024;

    public static JsonRpcMessage Decode(
        ReadOnlyMemory<byte> utf8Json,
        ToolingJsonOptions? options = null)
    {
        using var document = StrictToolingJson.ParseObject(utf8Json, options);
        var root = document.RootElement;
        RequireProtocolVersion(root);

        var hasMethod = root.TryGetProperty("method", out var methodElement);
        var hasId = root.TryGetProperty("id", out var idElement);
        var hasParameters = root.TryGetProperty("params", out var parametersElement);
        var hasResult = root.TryGetProperty("result", out var resultElement);
        var hasError = root.TryGetProperty("error", out var errorElement);

        if (hasMethod)
        {
            if (hasResult || hasError)
            {
                throw new ToolingJsonException("A JSON-RPC call cannot also be a response.");
            }

            var method = RequireBoundedString(methodElement, "method", MaximumMethodLength, allowEmpty: false);
            JsonElement? parameters = null;
            if (hasParameters)
            {
                if (parametersElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    throw new ToolingJsonException("JSON-RPC params must be an object or array when present.");
                }

                parameters = parametersElement.Clone();
            }

            if (!hasId)
            {
                return new JsonRpcMessage
                {
                    Kind = JsonRpcMessageKind.Notification,
                    Method = method,
                    Parameters = parameters
                };
            }

            return new JsonRpcMessage
            {
                Kind = JsonRpcMessageKind.Request,
                Id = ParseId(idElement),
                Method = method,
                Parameters = parameters
            };
        }

        if (hasParameters)
        {
            throw new ToolingJsonException("A JSON-RPC response cannot contain params.");
        }

        if (!hasId || hasResult == hasError)
        {
            throw new ToolingJsonException("A JSON-RPC response needs an id and exactly one of result or error.");
        }

        var id = ParseId(idElement);
        if (hasResult)
        {
            return new JsonRpcMessage
            {
                Kind = JsonRpcMessageKind.SuccessResponse,
                Id = id,
                Result = resultElement.Clone()
            };
        }

        return new JsonRpcMessage
        {
            Kind = JsonRpcMessageKind.ErrorResponse,
            Id = id,
            Error = ParseError(errorElement)
        };
    }

    public static byte[] EncodeRequest(long id, string method, JsonElement? parameters = null) =>
        EncodeCall(JsonRpcId.FromNumber(id), method, parameters);

    public static byte[] EncodeNotification(string method, JsonElement? parameters = null) =>
        EncodeCall(null, method, parameters);

    public static byte[] EncodeSuccess(JsonRpcId id, JsonElement? result = null) =>
        Encode(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WritePropertyName("result");
            WriteOptionalValue(writer, result);
            writer.WriteEndObject();
        });

    public static byte[] EncodeError(JsonRpcId id, JsonRpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ValidateError(error);
        return Encode(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", error.Code);
            writer.WriteString("message", error.Message);
            if (error.Data is { } data)
            {
                writer.WritePropertyName("data");
                data.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static byte[] EncodeCall(JsonRpcId? id, string method, JsonElement? parameters)
    {
        ValidateBoundedString(method, nameof(method), MaximumMethodLength, allowEmpty: false);
        if (parameters is { } value && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new ArgumentException("JSON-RPC params must be an object or array.", nameof(parameters));
        }

        return Encode(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (id is { } presentId)
            {
                WriteId(writer, presentId);
            }

            writer.WriteString("method", method);
            if (parameters is { } presentParameters)
            {
                writer.WritePropertyName("params");
                presentParameters.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void RequireProtocolVersion(JsonElement root)
    {
        if (!root.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            !version.ValueEquals("2.0"u8))
        {
            throw new ToolingJsonException("A JSON-RPC message must declare jsonrpc exactly as '2.0'.");
        }
    }

    private static JsonRpcId ParseId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return JsonRpcId.FromNumber(number);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return JsonRpcId.FromString(
                RequireBoundedString(element, "id", MaximumStringIdLength, allowEmpty: false));
        }

        throw new ToolingJsonException("A JSON-RPC id must be an integer or a non-empty bounded string.");
    }

    private static JsonRpcError ParseError(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("code", out var codeElement) ||
            !codeElement.TryGetInt32(out var code) ||
            !element.TryGetProperty("message", out var messageElement))
        {
            throw new ToolingJsonException("A JSON-RPC error must contain an Int32 code and string message.");
        }

        JsonElement? data = element.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : null;
        return new JsonRpcError
        {
            Code = code,
            Message = RequireBoundedString(
                messageElement,
                "error.message",
                MaximumErrorMessageLength,
                allowEmpty: true),
            Data = data
        };
    }

    private static string RequireBoundedString(
        JsonElement element,
        string property,
        int maximumLength,
        bool allowEmpty)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ToolingJsonException($"JSON-RPC {property} must be a string.");
        }

        var value = element.GetString()
            ?? throw new ToolingJsonException($"JSON-RPC {property} was null.");
        try
        {
            ValidateBoundedString(value, property, maximumLength, allowEmpty);
        }
        catch (ArgumentException exception)
        {
            throw new ToolingJsonException($"JSON-RPC {property} was not a safe bounded string.", exception);
        }

        return value;
    }

    private static void ValidateBoundedString(
        string value,
        string property,
        int maximumLength,
        bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!allowEmpty && value.Length == 0) || value.Length > maximumLength)
        {
            throw new ArgumentException($"{property} exceeded its length policy.", property);
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u202C' or
                '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                throw new ArgumentException($"{property} contained a control or bidirectional formatting character.", property);
            }
        }
    }

    private static void ValidateError(JsonRpcError error)
    {
        ValidateBoundedString(
            error.Message,
            nameof(error.Message),
            MaximumErrorMessageLength,
            allowEmpty: true);
        if (error.Data is { ValueKind: JsonValueKind.Undefined })
        {
            throw new ArgumentException("JSON-RPC error data cannot be undefined.", nameof(error));
        }
    }

    private static void WriteId(Utf8JsonWriter writer, JsonRpcId id)
    {
        if (id.IsString)
        {
            var text = id.Text ?? throw new ArgumentException("A string JSON-RPC id had no value.", nameof(id));
            ValidateBoundedString(text, nameof(id), MaximumStringIdLength, allowEmpty: false);
            writer.WriteString("id", text);
        }
        else
        {
            writer.WriteNumber("id", id.Number);
        }
    }

    private static void WriteOptionalValue(Utf8JsonWriter writer, JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A JSON-RPC value cannot be undefined.", nameof(value));
        }

        value.Value.WriteTo(writer);
    }
}
