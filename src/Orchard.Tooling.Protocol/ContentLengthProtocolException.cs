namespace Orchard.Tooling.Protocol;

/// <summary>Identifies malformed or out-of-policy LSP/DAP stream input.</summary>
public sealed class ContentLengthProtocolException : Exception
{
    public ContentLengthProtocolException(string message)
        : base(message)
    {
    }

    public ContentLengthProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
