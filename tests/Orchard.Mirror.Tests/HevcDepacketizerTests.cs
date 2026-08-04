using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// RFC 7798 §4.4 depacketization. Each payload below is built from the RFC's wire layout by hand,
/// so a depacketizer that reassembled incorrectly could not also make these expectations pass.
/// </summary>
internal static class HevcDepacketizerTests
{
    /// <summary>
    /// Build a 2-byte HEVC NAL unit header: F(1) Type(6) LayerId(6) TID(3).
    /// With layer id 0 and TID 1 that is <c>[type &lt;&lt; 1, 1]</c>.
    /// </summary>
    private static byte[] NalHeader(int type) => [(byte)(type << 1), 0x01];

    private static byte[] Nal(int type, params byte[] body) => [.. NalHeader(type), .. body];

    internal static void PassesThroughASingleNalPacket()
    {
        // A payload whose type is below 48 is one whole NAL unit (RFC 7798 §4.4.1).
        byte[] sps = Nal(HevcNalType.SequenceParameterSet, 0x01, 0x02, 0x03);
        List<byte[]> output = [];

        new HevcDepacketizer().Depacketize(sps, output);

        Assert(output.Count == 1, $"Expected 1 NAL unit, got {output.Count}.");
        Assert(output[0].SequenceEqual(sps), "A single-NAL packet was altered in transit.");
        Assert(
            HevcNalType.Of(output[0]) == HevcNalType.SequenceParameterSet,
            "The NAL type was not preserved.");
    }

    internal static void SplitsAnAggregationPacketIntoItsNalUnits()
    {
        // §4.4.2: payload header of type 48, then repeated 16-bit length + NAL unit.
        byte[] vps = Nal(HevcNalType.VideoParameterSet, 0xAA);
        byte[] pps = Nal(HevcNalType.PictureParameterSet, 0xBB, 0xCC);
        byte[] payload =
        [
            .. NalHeader(HevcNalType.AggregationPacket),
            0x00, (byte)vps.Length, .. vps,
            0x00, (byte)pps.Length, .. pps,
        ];

        List<byte[]> output = [];
        new HevcDepacketizer().Depacketize(payload, output);

        Assert(output.Count == 2, $"Expected 2 aggregated NAL units, got {output.Count}.");
        Assert(output[0].SequenceEqual(vps), "The first aggregated NAL unit was wrong.");
        Assert(output[1].SequenceEqual(pps), "The second aggregated NAL unit was wrong.");
    }

    internal static void RejectsAnAggregationPacketWithABadLength()
    {
        // A length that runs past the payload must abandon the packet, not read out of bounds.
        byte[] payload = [.. NalHeader(HevcNalType.AggregationPacket), 0x00, 0x40, 0x01, 0x02];

        List<byte[]> output = [];
        new HevcDepacketizer().Depacketize(payload, output);

        Assert(output.Count == 0, "An aggregation packet with an over-long size emitted a NAL unit.");
    }

    /// <summary>
    /// §4.4.3: the reassembled NAL unit header is the payload header with its 6-bit type replaced by
    /// the FU header's type, so layer id and temporal id survive the round trip.
    /// </summary>
    internal static void ReassemblesAFragmentedNalUnit()
    {
        const int SliceType = HevcNalType.IdrWithRadl;
        List<byte[]> output = [];
        HevcDepacketizer depacketizer = new();

        // Start fragment: S=1, E=0.
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)(0x80 | SliceType), 0x11, 0x22],
            output);
        Assert(output.Count == 0, "A start fragment alone completed a NAL unit.");

        // Middle fragment: S=0, E=0.
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)SliceType, 0x33],
            output);
        Assert(output.Count == 0, "A middle fragment completed a NAL unit early.");

        // End fragment: S=0, E=1.
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)(0x40 | SliceType), 0x44],
            output);

        Assert(output.Count == 1, $"Expected 1 reassembled NAL unit, got {output.Count}.");
        Assert(
            output[0].SequenceEqual(Nal(SliceType, 0x11, 0x22, 0x33, 0x44)),
            $"Reassembly produced {Convert.ToHexString(output[0])}; expected the original NAL unit "
                + "with its own type restored and fragment payloads concatenated in order.");
        Assert(HevcNalType.IsRandomAccessPoint(HevcNalType.Of(output[0])), "The IDR type was lost.");
    }

    internal static void DiscardsAFragmentWhoseStartWasLost()
    {
        List<byte[]> output = [];
        HevcDepacketizer depacketizer = new();

        // A continuation arriving with no start fragment cannot be reassembled.
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)HevcNalType.IdrWithRadl, 0x33],
            output);
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)(0x40 | HevcNalType.IdrWithRadl), 0x44],
            output);

        Assert(output.Count == 0, "A NAL unit was emitted from fragments whose start was lost.");
    }

    internal static void CountsAFragmentedUnitAbandonedMidWay()
    {
        List<byte[]> output = [];
        HevcDepacketizer depacketizer = new();

        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)(0x80 | HevcNalType.IdrWithRadl), 0x11],
            output);
        Assert(depacketizer.DroppedFragments == 0, "Nothing has been lost yet.");

        // A second start bit before the first unit ended means its end fragment never arrived.
        depacketizer.Depacketize(
            [.. NalHeader(HevcNalType.FragmentationUnit), (byte)(0x80 | HevcNalType.IdrWithRadl), 0x22],
            output);

        Assert(output.Count == 0, "An incomplete NAL unit was emitted.");
        Assert(
            depacketizer.DroppedFragments == 1,
            $"Expected 1 dropped fragment, got {depacketizer.DroppedFragments}. Silent loss would "
                + "show up as decoder corruption with no way to attribute it.");
    }

    /// <summary>
    /// Apple's DisplayService appends a fixed 14-byte footer that is not part of the HEVC bitstream.
    /// Apple's own receiver strips it; a plain RFC 7798 reassembly would feed it to the decoder.
    /// </summary>
    internal static void StripsTheDisplayServiceTrailer()
    {
        byte[] trailer = Convert.FromHexString("04F00AC0000003000004EC0AB003");
        Assert(trailer.Length == 14, $"The trailer should be 14 bytes, not {trailer.Length}.");

        byte[] slice = Nal(HevcNalType.IdrWithRadl, 0x11, 0x22);
        List<byte[]> output = [];

        new HevcDepacketizer().Depacketize([.. slice, .. trailer], output);

        Assert(output.Count == 1, $"Expected 1 NAL unit, got {output.Count}.");
        Assert(
            output[0].SequenceEqual(slice),
            $"The trailer was not stripped: got {Convert.ToHexString(output[0])}.");
    }

    internal static void LeavesANalUnitWithoutTheTrailerUntouched()
    {
        // The trailer is matched as an exact suffix, so a stream that never carries it is unaffected.
        byte[] slice = Nal(HevcNalType.IdrWithRadl, 0x04, 0xF0, 0x0A, 0xC0);
        List<byte[]> output = [];

        new HevcDepacketizer().Depacketize(slice, output);

        Assert(output.Count == 1, $"Expected 1 NAL unit, got {output.Count}.");
        Assert(output[0].SequenceEqual(slice), "A NAL unit without the trailer was truncated.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
