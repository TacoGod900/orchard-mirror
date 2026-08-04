using System.Buffers.Binary;

namespace Orchard.Mirror.Media;

/// <summary>
/// One parsed RTP packet (RFC 3550 §5.1). Only the fields Orchard Mirror needs are surfaced: the
/// sequence number, to notice loss and reordering; the timestamp, to group packets into an access
/// unit; the marker bit, which HEVC uses to flag the last packet of an access unit; and the payload.
///
/// <para><see cref="Payload"/> is a slice of the caller's buffer, not a copy, so it is only valid
/// while that buffer is.</para>
/// </summary>
public readonly ref struct RtpPacket
{
    private RtpPacket(
        byte payloadType,
        bool marker,
        ushort sequenceNumber,
        uint timestamp,
        uint synchronizationSource,
        ReadOnlySpan<byte> payload)
    {
        PayloadType = payloadType;
        Marker = marker;
        SequenceNumber = sequenceNumber;
        Timestamp = timestamp;
        SynchronizationSource = synchronizationSource;
        Payload = payload;
    }

    public byte PayloadType { get; }

    /// <summary>For HEVC, set on the final packet of an access unit (RFC 7798 §4.4).</summary>
    public bool Marker { get; }

    /// <summary>Increments by one per packet and wraps at 16 bits.</summary>
    public ushort SequenceNumber { get; }

    public uint Timestamp { get; }

    public uint SynchronizationSource { get; }

    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>
    /// Parse one datagram. Returns false for anything that is not a well-formed version-2 RTP
    /// packet, rather than throwing: these arrive from a socket, so a malformed or truncated
    /// datagram is an expected input and must not be able to stop the stream.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket packet)
    {
        packet = default;

        // Fixed header: V(2) P(1) X(1) CC(4) | M(1) PT(7) | sequence(16) | timestamp(32) | SSRC(32)
        const int FixedHeaderLength = 12;
        if (datagram.Length < FixedHeaderLength)
        {
            return false;
        }

        int version = datagram[0] >> 6;
        if (version != 2)
        {
            return false;
        }

        bool hasPadding = (datagram[0] & 0x20) != 0;
        bool hasExtension = (datagram[0] & 0x10) != 0;
        int contributingSourceCount = datagram[0] & 0x0F;

        int offset = FixedHeaderLength + (contributingSourceCount * 4);
        if (datagram.Length < offset)
        {
            return false;
        }

        if (hasExtension)
        {
            // Extension header: profile(16) | length-in-32-bit-words(16) | data
            if (datagram.Length < offset + 4)
            {
                return false;
            }

            int extensionWords = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            offset += 4 + (extensionWords * 4);
            if (datagram.Length < offset)
            {
                return false;
            }
        }

        int end = datagram.Length;
        if (hasPadding)
        {
            // The final octet counts the padding bytes, itself included (RFC 3550 §5.1).
            int paddingLength = datagram[^1];
            if (paddingLength == 0 || paddingLength > end - offset)
            {
                return false;
            }

            end -= paddingLength;
        }

        if (end < offset)
        {
            return false;
        }

        packet = new RtpPacket(
            payloadType: (byte)(datagram[1] & 0x7F),
            marker: (datagram[1] & 0x80) != 0,
            sequenceNumber: BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            timestamp: BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            synchronizationSource: BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]),
            payload: datagram[offset..end]);
        return true;
    }
}
