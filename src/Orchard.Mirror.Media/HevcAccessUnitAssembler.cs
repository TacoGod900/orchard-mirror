namespace Orchard.Mirror.Media;

/// <summary>A complete HEVC picture assembled from the RTP packets sharing one timestamp.</summary>
public sealed record HevcAccessUnit(
    uint Timestamp,
    IReadOnlyList<byte[]> NalUnits,
    bool IsRandomAccessPoint);

/// <summary>
/// Groups depacketized HEVC NAL units into access units. RFC 7798 defines the RTP marker bit as the
/// final packet of an access unit. A timestamp change before that marker means a packet was lost;
/// the incomplete picture is discarded rather than sent to a hardware decoder.
/// </summary>
public sealed class HevcAccessUnitAssembler
{
    private readonly HevcDepacketizer depacketizer = new();
    private readonly List<byte[]> current = [];
    private uint currentTimestamp;
    private bool hasTimestamp;
    private ushort lastSequenceNumber;
    private bool hasSequenceNumber;
    private bool currentDamaged;

    public long DroppedAccessUnits { get; private set; }

    public int DroppedFragments => depacketizer.DroppedFragments;

    public HevcAccessUnit? Push(in RtpPacket packet)
    {
        if (hasTimestamp && packet.Timestamp != currentTimestamp)
        {
            if (current.Count != 0)
            {
                DroppedAccessUnits++;
            }

            current.Clear();
            depacketizer.Reset();
            currentDamaged = false;
            hasSequenceNumber = false;
        }

        if (hasTimestamp && hasSequenceNumber && packet.Timestamp == currentTimestamp &&
            packet.SequenceNumber != unchecked((ushort)(lastSequenceNumber + 1)))
        {
            currentDamaged = true;
            depacketizer.Reset();
        }

        currentTimestamp = packet.Timestamp;
        hasTimestamp = true;
        lastSequenceNumber = packet.SequenceNumber;
        hasSequenceNumber = true;
        depacketizer.Depacketize(packet.Payload, current);

        if (!packet.Marker)
        {
            return null;
        }

        if (current.Count == 0 || currentDamaged)
        {
            if (currentDamaged)
            {
                DroppedAccessUnits++;
                current.Clear();
            }

            hasTimestamp = false;
            hasSequenceNumber = false;
            currentDamaged = false;
            return null;
        }

        byte[][] nalUnits = [.. current];
        current.Clear();
        hasTimestamp = false;
        hasSequenceNumber = false;
        return new HevcAccessUnit(
            packet.Timestamp,
            nalUnits,
            nalUnits.Any(nal => HevcNalType.IsRandomAccessPoint(HevcNalType.Of(nal))));
    }

    public void Reset()
    {
        current.Clear();
        hasTimestamp = false;
        hasSequenceNumber = false;
        currentDamaged = false;
        depacketizer.Reset();
    }
}
