namespace Orchard.Protocol;

/// <summary>Deterministically selects the highest mutually supported protocol version.</summary>
public static class ProtocolNegotiator
{
    public static int Negotiate(HelloPayload peerHello) =>
        Negotiate(
            ProtocolConstants.MinimumSupportedVersion,
            ProtocolConstants.MaximumSupportedVersion,
            peerHello?.MinimumVersion ?? throw new ArgumentNullException(nameof(peerHello)),
            peerHello.MaximumVersion);

    public static int Negotiate(
        int localMinimum,
        int localMaximum,
        int peerMinimum,
        int peerMaximum)
    {
        ValidateRange(localMinimum, localMaximum, "local");
        ValidateRange(peerMinimum, peerMaximum, "peer");

        var commonMinimum = Math.Max(localMinimum, peerMinimum);
        var commonMaximum = Math.Min(localMaximum, peerMaximum);
        if (commonMinimum > commonMaximum)
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.NoCommonVersion,
                "$.payload",
                $"No common protocol version exists between local [{localMinimum}, {localMaximum}] and peer [{peerMinimum}, {peerMaximum}].");
        }

        return commonMaximum;
    }

    internal static void ValidateRange(int minimum, int maximum, string path)
    {
        if (minimum <= 0 || maximum <= 0 || minimum > maximum)
        {
            throw new ProtocolValidationException(
                ProtocolErrorCodes.InvalidPayload,
                path,
                $"Invalid protocol version range [{minimum}, {maximum}].");
        }
    }
}
