using System.Buffers.Binary;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

internal static class HevcAccessUnitAssemblerTests
{
    internal static void EmitsOnlyWhenTheMarkerCompletesThePicture()
    {
        HevcAccessUnitAssembler assembler = new();
        byte[] vps = Nal(HevcNalType.VideoParameterSet, 0xAA);
        byte[] idr = Nal(HevcNalType.IdrWithoutLeading, 0xBB);

        Assert(Push(assembler, Packet(1, 9000, false, vps)) is null, "An unmarked packet completed a picture.");
        HevcAccessUnit? result = Push(assembler, Packet(2, 9000, true, idr));

        Assert(result is not null, "The marker did not complete the access unit.");
        HevcAccessUnit accessUnit = result
            ?? throw new InvalidOperationException("The marker did not complete the access unit.");
        Assert(accessUnit.Timestamp == 9000, $"Timestamp was {accessUnit.Timestamp}, expected 9000.");
        Assert(accessUnit.NalUnits.Count == 2, $"Expected 2 NAL units, got {accessUnit.NalUnits.Count}.");
        Assert(accessUnit.IsRandomAccessPoint, "An IDR picture was not marked as random access.");
    }

    internal static void DropsAnUnfinishedPictureWhenTheTimestampChanges()
    {
        HevcAccessUnitAssembler assembler = new();

        Assert(Push(assembler, Packet(1, 100, false, Nal(1, 0x10))) is null, "An unfinished picture escaped.");
        HevcAccessUnit? result = Push(assembler, Packet(2, 200, true, Nal(1, 0x20)));

        Assert(result is not null, "The next complete picture was lost.");
        HevcAccessUnit accessUnit = result
            ?? throw new InvalidOperationException("The next complete picture was lost.");
        Assert(accessUnit.NalUnits.Count == 1 && accessUnit.NalUnits[0][2] == 0x20, "Bytes from the lost picture leaked forward.");
        Assert(assembler.DroppedAccessUnits == 1, $"Expected one dropped picture, got {assembler.DroppedAccessUnits}.");
    }

    internal static void DropsAPictureWithASequenceGap()
    {
        HevcAccessUnitAssembler assembler = new();

        Assert(Push(assembler, Packet(10, 500, false, Nal(1, 0x10))) is null, "An unfinished picture escaped.");
        Assert(Push(assembler, Packet(12, 500, true, Nal(1, 0x20))) is null, "A picture with a lost RTP packet was emitted.");
        Assert(assembler.DroppedAccessUnits == 1, $"Expected one dropped picture, got {assembler.DroppedAccessUnits}.");
    }

    private static HevcAccessUnit? Push(HevcAccessUnitAssembler assembler, byte[] datagram)
    {
        Assert(RtpPacket.TryParse(datagram, out RtpPacket packet), "Test packet did not parse.");
        return assembler.Push(in packet);
    }

    private static byte[] Packet(ushort sequence, uint timestamp, bool marker, byte[] payload)
    {
        byte[] packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(96 | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 1);
        payload.CopyTo(packet, 12);
        return packet;
    }

    private static byte[] Nal(int type, params byte[] body) => [(byte)(type << 1), 0x01, .. body];

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
