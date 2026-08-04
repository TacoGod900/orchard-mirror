using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchard.Core;

public static class OrchardJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RejectDuplicateProperties(json);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException("The Orchard document was empty or invalid.");
    }

    private static void RejectDuplicateProperties(string json)
    {
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException("The Orchard document contains invalid Unicode.", exception);
        }

        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = Options.MaxDepth
            });
        var scopes = new Stack<HashSet<string>?>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (!scopes.TryPop(out _))
                    {
                        throw new JsonException("The Orchard document has unbalanced JSON containers.");
                    }
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.TryPeek(out var properties) || properties is null)
                    {
                        throw new JsonException("A JSON property appeared outside an object.");
                    }

                    var propertyName = reader.GetString()
                        ?? throw new JsonException("A JSON property name was null.");
                    if (!properties.Add(propertyName))
                    {
                        throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    break;
            }
        }

        if (scopes.Count != 0)
        {
            throw new JsonException("The Orchard document has an unterminated JSON container.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 384
        };
        options.Converters.Add(new StrictCamelCaseEnumConverter<DiagnosticSeverity>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<CompatibilityStatus>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<StateValueKind>());
        return options;
    }

    private sealed class StrictCamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly Dictionary<string, TEnum> FromWire =
            Enum.GetValues<TEnum>().ToDictionary(
                value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
                value => value,
                StringComparer.Ordinal);

        private static readonly Dictionary<TEnum, string> ToWire =
            FromWire.ToDictionary(pair => pair.Value, pair => pair.Key);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Expected a string value for {typeof(TEnum).Name}.");
            }

            var wireValue = reader.GetString();
            if (wireValue is null || !FromWire.TryGetValue(wireValue, out var value))
            {
                throw new JsonException(
                    $"Unknown or incorrectly cased {typeof(TEnum).Name} value '{wireValue}'.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!ToWire.TryGetValue(value, out var wireValue))
            {
                throw new JsonException($"Cannot serialize undefined {typeof(TEnum).Name} value '{value}'.");
            }

            writer.WriteStringValue(wireValue);
        }
    }
}
