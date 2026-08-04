using Orchard.Protocol;

namespace Orchard.Transport.Windows;

/// <summary>Finite resource and version limits for a local pipe connection.</summary>
public sealed record OrchardTransportOptions
{
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MinimumVersion { get; init; } = ProtocolConstants.MinimumSupportedVersion;

    public int MaximumVersion { get; init; } = ProtocolConstants.MaximumSupportedVersion;

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    internal TransportOptionsSnapshot ValidateAndSnapshot(ProtocolPeerRole localRole)
    {
        if (!Enum.IsDefined(localRole))
        {
            throw Invalid("The local protocol role is invalid.");
        }

        ValidateTimeout(ConnectionTimeout, nameof(ConnectionTimeout), TimeSpan.FromMinutes(5));
        ValidateTimeout(HandshakeTimeout, nameof(HandshakeTimeout), TimeSpan.FromMinutes(5));
        ValidateTimeout(OperationTimeout, nameof(OperationTimeout), TimeSpan.FromMinutes(10));

        if (MinimumVersion < ProtocolConstants.MinimumSupportedVersion ||
            MaximumVersion > ProtocolConstants.MaximumSupportedVersion)
        {
            throw Invalid(
                $"The local version range must stay within the compiled protocol range " +
                $"[{ProtocolConstants.MinimumSupportedVersion}, {ProtocolConstants.MaximumSupportedVersion}].");
        }

        var capabilities = Capabilities?.ToArray() ?? throw Invalid("Capabilities cannot be null.");
        var hello = new HelloPayload
        {
            MinimumVersion = MinimumVersion,
            MaximumVersion = MaximumVersion,
            Role = localRole,
            Capabilities = capabilities
        };
        try
        {
            ProtocolValidator.Validate(ProtocolEnvelope.Create("options-validation", 0, hello));
        }
        catch (ProtocolException exception)
        {
            throw Invalid($"The protocol options are invalid: {exception.Message}", exception);
        }

        return new TransportOptionsSnapshot(
            ConnectionTimeout,
            HandshakeTimeout,
            OperationTimeout,
            MinimumVersion,
            MaximumVersion,
            capabilities,
            localRole);
    }

    private static void ValidateTimeout(TimeSpan value, string name, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw Invalid($"{name} must be greater than zero and no greater than {maximum}.");
        }
    }

    private static OrchardTransportException Invalid(string message, Exception? innerException = null) =>
        innerException is null
            ? new OrchardTransportException(OrchardTransportErrorCodes.InvalidOptions, message)
            : new OrchardTransportException(OrchardTransportErrorCodes.InvalidOptions, message, innerException);
}

internal sealed record TransportOptionsSnapshot(
    TimeSpan ConnectionTimeout,
    TimeSpan HandshakeTimeout,
    TimeSpan OperationTimeout,
    int MinimumVersion,
    int MaximumVersion,
    string[] Capabilities,
    ProtocolPeerRole LocalRole);
