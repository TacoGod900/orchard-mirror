using System.Text.Json;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>A stable error returned by the CoreDevice agent.</summary>
public sealed record AgentError(string Code, string Message);

/// <summary>A parsed response from the CoreDevice agent.</summary>
public sealed record AgentResponse(string Id, bool Ok, JsonElement Result, AgentError? Error)
{
    /// <summary>Protocol version implemented by this application.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Parse and validate one JSON-line response.</summary>
    public static AgentResponse Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AgentProtocolException("Agent response must be a JSON object.");
        }

        if (!root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != ProtocolVersion)
        {
            throw new AgentProtocolException($"Agent response does not use protocol version {ProtocolVersion}.");
        }

        string id = RequiredString(root, "id");
        if (!root.TryGetProperty("ok", out JsonElement okElement)
            || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new AgentProtocolException("Agent response is missing its boolean ok field.");
        }

        bool ok = okElement.GetBoolean();
        JsonElement result = root.TryGetProperty("result", out JsonElement resultElement)
            ? resultElement.Clone()
            : default;
        AgentError? error = null;
        if (!ok)
        {
            if (!root.TryGetProperty("error", out JsonElement errorElement)
                || errorElement.ValueKind != JsonValueKind.Object)
            {
                throw new AgentProtocolException("Failed agent response is missing its error object.");
            }

            error = new AgentError(
                RequiredString(errorElement, "code"),
                RequiredString(errorElement, "message"));
        }

        return new AgentResponse(id, ok, result, error);
    }

    /// <summary>Build one compact JSON-line request.</summary>
    public static string SerializeRequest(string id, string command, object? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return JsonSerializer.Serialize(
            new
            {
                version = ProtocolVersion,
                id,
                command,
                arguments = arguments ?? new { },
            });
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString()))
        {
            throw new AgentProtocolException($"Agent response is missing its non-empty {name} field.");
        }

        return value.GetString()!;
    }
}

/// <summary>Raised when the agent emits a malformed or incompatible protocol message.</summary>
public sealed class AgentProtocolException : Exception
{
    /// <summary>Create a protocol exception.</summary>
    public AgentProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when an agent command returns a stable product-facing failure.</summary>
public sealed class AgentCommandException : Exception
{
    /// <summary>Create a command exception from an agent error.</summary>
    public AgentCommandException(AgentError error)
        : base(error.Message)
    {
        Code = error.Code;
    }

    /// <summary>The stable protocol error code.</summary>
    public string Code { get; }
}
