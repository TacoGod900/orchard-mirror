namespace Orchard.Tooling.Protocol;

/// <summary>Identifies ambiguous, malformed, or over-budget tooling JSON.</summary>
public sealed class ToolingJsonException : Exception
{
    public ToolingJsonException(string message)
        : base(message)
    {
    }

    public ToolingJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
