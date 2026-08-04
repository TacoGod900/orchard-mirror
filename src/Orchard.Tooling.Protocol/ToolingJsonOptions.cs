namespace Orchard.Tooling.Protocol;

/// <summary>Bounds JSON accepted from a language server or debug adapter.</summary>
public sealed record ToolingJsonOptions
{
    public int MaximumDepth { get; init; } = 64;

    public int MaximumElementCount { get; init; } = 200_000;

    public int MaximumMembersPerObject { get; init; } = 8_192;

    public int MaximumItemsPerArray { get; init; } = 100_000;

    internal ToolingJsonOptions Validate()
    {
        if (MaximumDepth is < 4 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDepth));
        }

        if (MaximumElementCount is < 16 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumElementCount));
        }

        if (MaximumMembersPerObject is < 4 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMembersPerObject));
        }

        if (MaximumItemsPerArray is < 4 or > 500_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumItemsPerArray));
        }

        return this;
    }
}
