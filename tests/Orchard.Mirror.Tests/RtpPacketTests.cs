using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// RFC 3550 §5.1 header parsing. Every packet below is assembled bit by bit from the RFC's field
/// layout, so the expectations are independent of how <see cref="RtpPacket"/> reads them.
/// </summary>
internal static class RtpPacketTests
{
    internal static void ParsesFixedHeaderFields()
    {
        // V=2, P=0, X=0, CC=0 | M=1, PT=98 | seq=0x1234 | ts=0xDEADBEEF | SSRC=0xCAFEBABE
        byte[] datagram =
        [
            0x80,
            0x80 | 98,
            0x12, 0x34,
            0xDE, 0xAD, 0xBE, 0xEF,
            0xCA, 0xFE, 0xBA, 0xBE,
            0xAA, 0xBB,
        ];

        Assert(RtpPacket.TryParse(datagram, out RtpPacket packet), "A well-formed packet failed to parse.");
        Assert(packet.PayloadType == 98, $"Payload type was {packet.PayloadType}, expected 98.");
        Assert(packet.Marker, "The marker bit was set but did not survive parsing.");
        Assert(packet.SequenceNumber == 0x1234, $"Sequence was 0x{packet.SequenceNumber:X4}, expected 0x1234.");
        Assert(packet.Timestamp == 0xDEADBEEF, $"Timestamp was 0x{packet.Timestamp:X8}.");
        Assert(packet.SynchronizationSource == 0xCAFEBABE, $"SSRC was 0x{packet.SynchronizationSource:X8}.");
        Assert(packet.Payload.SequenceEqual<byte>([0xAA, 0xBB]), "Payload bytes were wrong.");
    }

    /// <summary>
    /// Contributing sources, a header extension and padding all move the payload boundaries. Getting
    /// any of them wrong feeds the decoder header bytes or drops real ones, so all three are
    /// combined in a single packet here.
    /// </summary>
    internal static void HonoursCsrcExtensionAndPadding()
    {
        byte[] datagram =
        [
            0x80 | 0x20 | 0x10 | 0x02,  // V=2, P=1, X=1, CC=2
            96,                          // M=0, PT=96
            0x00, 0x01,                  // sequence
            0x00, 0x00, 0x00, 0x00,      // timestamp
            0x00, 0x00, 0x00, 0x01,      // SSRC
            0x11, 0x11, 0x11, 0x11,      // CSRC 1
            0x22, 0x22, 0x22, 0x22,      // CSRC 2
            0xBE, 0xDE, 0x00, 0x01,      // extension: profile 0xBEDE, one 32-bit word
            0x33, 0x33, 0x33, 0x33,      // extension word
            0x0F, 0x1E,                  // payload
            0x00, 0x00, 0x03,            // padding: 3 bytes, count in the final octet
        ];

        Assert(RtpPacket.TryParse(datagram, out RtpPacket packet), "A packet with CSRC/extension/padding failed to parse.");
        Assert(
            packet.Payload.SequenceEqual<byte>([0x0F, 0x1E]),
            $"Payload should be the 2 bytes between the extension and the padding, "
                + $"but was {Convert.ToHexString(packet.Payload)}.");
    }

    internal static void RejectsMalformedDatagrams()
    {
        // Version 1 is not RTP as we speak it.
        Assert(!RtpPacket.TryParse([0x40, 96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out _), "A version-1 packet was accepted.");

        // Shorter than the 12-byte fixed header.
        Assert(!RtpPacket.TryParse([0x80, 96, 0, 0], out _), "A truncated packet was accepted.");

        // CC claims 4 contributing sources the datagram does not contain.
        Assert(!RtpPacket.TryParse([0x84, 96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], out _), "An over-claimed CSRC count was accepted.");

        // Padding length larger than the payload.
        Assert(
            !RtpPacket.TryParse([0xA0, 96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x09], out _),
            "A padding length beyond the payload was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
