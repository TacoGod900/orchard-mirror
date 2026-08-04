using System.Text.Json;

namespace Orchard.Tooling.Protocol;

public enum JsonRpcMessageKind
{
    Request,
    Notification,
    SuccessResponse,
    ErrorResponse
}

public readonly record struct JsonRpcId
{
    private JsonRpcId(long number, string? text, bool isString)
    {
        Number = number;
        Text = text;
        IsString = isString;
    }

    public bool IsString { get; }

    public long Number { get; }

    public string? Text { get; }

    public static JsonRpcId FromNumber(long value) => new(value, null, false);

    public static JsonRpcId FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(0, value, true);
    }

    public override string ToString() => IsString ? Text ?? string.Empty : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record JsonRpcError
{
    public required int Code { get; init; }

    public required string Message { get; init; }

    public JsonElement? Data { get; init; }
}

public sealed record JsonRpcMessage
{
    public required JsonRpcMessageKind Kind { get; init; }

    public JsonRpcId? Id { get; init; }

    public string? Method { get; init; }

    public JsonElement? Parameters { get; init; }

    public JsonElement? Result { get; init; }

    public JsonRpcError? Error { get; init; }
}
