namespace Orchard.Tooling.Protocol;

/// <summary>Bounds one LSP or DAP content-length framed stream.</summary>
public sealed record ContentLengthProtocolOptions
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumContentBytes = 16 * 1024 * 1024;

    public int MaximumHeaderBytes { get; init; } = DefaultMaximumHeaderBytes;

    public int MaximumContentBytes { get; init; } = DefaultMaximumContentBytes;

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal ContentLengthProtocolOptions Validate()
    {
        if (MaximumHeaderBytes is < 64 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumHeaderBytes),
                "The protocol header limit must be between 64 bytes and 64 KiB.");
        }

        if (MaximumContentBytes is < 2 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumContentBytes),
                "The protocol content limit must be between 2 bytes and 64 MiB.");
        }

        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(OperationTimeout),
                "The aggregate protocol operation timeout must be positive and no more than five minutes.");
        }

        return this;
    }
}
