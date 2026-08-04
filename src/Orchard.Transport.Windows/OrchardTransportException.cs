namespace Orchard.Transport.Windows;

/// <summary>An expected local-transport failure with a stable machine-readable code.</summary>
public sealed class OrchardTransportException : Exception
{
    public OrchardTransportException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public OrchardTransportException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Stable error codes produced by the Windows local transport.</summary>
public static class OrchardTransportErrorCodes
{
    public const string InvalidEndpoint = "ORT1001";
    public const string ConnectionTimeout = "ORT1002";
    public const string HandshakeTimeout = "ORT1003";
    public const string AuthenticationFailed = "ORT1004";
    public const string VersionMismatch = "ORT1005";
    public const string UnexpectedHandshakeMessage = "ORT1006";
    public const string SessionMismatch = "ORT1007";
    public const string SequenceViolation = "ORT1008";
    public const string ConnectionClosed = "ORT1009";
    public const string IoFailure = "ORT1010";
    public const string Disposed = "ORT1011";
    public const string OperationTimeout = "ORT1012";
    public const string InvalidOptions = "ORT1013";
}
