using System.Buffers.Binary;

namespace Orchard.Mirror.Media;

/// <summary>NAL unit types this code reasons about (ITU-T H.265 Table 7-1, RFC 7798 §4.4).</summary>
public static class HevcNalType
{
    public const int IdrWithRadl = 19;
    public const int IdrWithoutLeading = 20;
    public const int CleanRandomAccess = 21;
    public const int VideoParameterSet = 32;
    public const int SequenceParameterSet = 33;
    public const int PictureParameterSet = 34;

    /// <summary>RFC 7798 §4.4.2 aggregation packet — several whole NAL units in one RTP payload.</summary>
    public const int AggregationPacket = 48;

    /// <summary>RFC 7798 §4.4.3 fragmentation unit — one NAL unit split across RTP payloads.</summary>
    public const int FragmentationUnit = 49;

    /// <summary>RFC 7798 §4.4.4 PACI-carrying packet.</summary>
    public const int PayloadContentInformation = 50;

    /// <summary>True for the picture types a decoder can start from.</summary>
    public static bool IsRandomAccessPoint(int nalType) =>
        nalType is IdrWithRadl or IdrWithoutLeading or CleanRandomAccess;

    /// <summary>True for an IDR picture that clears all prior decoder references.</summary>
    public static bool IsIdr(int nalType) => nalType is IdrWithRadl or IdrWithoutLeading;

    /// <summary>True for any coded-slice NAL unit.</summary>
    public static bool IsCodedSlice(int nalType) => nalType is >= 0 and <= 31;

    /// <summary>True for a parameter set, which must be kept and replayed to start a decoder.</summary>
    public static bool IsParameterSet(int nalType) =>
        nalType is VideoParameterSet or SequenceParameterSet or PictureParameterSet;

    /// <summary>Read the 6-bit NAL unit type from a 2-byte NAL header.</summary>
    public static int Of(ReadOnlySpan<byte> nalUnit) =>
        nalUnit.Length < 2 ? -1 : (nalUnit[0] >> 1) & 0x3F;
}

/// <summary>
/// Reassembles RFC 7798 RTP/HEVC payloads into whole NAL units.
///
/// <para>Three packet shapes carry video. A payload whose type is below 48 is a single NAL unit and
/// is emitted as-is. Type 48 is an aggregation packet holding several length-prefixed NAL units.
/// Type 49 is a fragmentation unit, one NAL unit spread over consecutive packets, which this class
/// buffers until the end fragment arrives.</para>
///
/// <para>The instance is stateful because fragment reassembly spans packets. Feed payloads in
/// arrival order and call <see cref="Reset"/> when the stream restarts.</para>
/// </summary>
public sealed class HevcDepacketizer
{
    /// <summary>
    /// Apple's DisplayService appends this fixed 14-byte footer after a coded-slice NAL. It is not
    /// part of the HEVC bitstream — Apple's own receiver strips it before decoding, and a plain
    /// RFC 7798 reassembly would hand it to the decoder as trailing slice data. It is matched as an
    /// exact suffix, so this is a no-op on any stream that does not carry it.
    /// </summary>
    private static readonly byte[] DisplayServiceTrailer =
        Convert.FromHexString("04F00AC0000003000004EC0AB003");

    private readonly List<byte> fragment = [];
    private bool fragmentOpen;
    private byte fragmentHeader0;
    private byte fragmentHeader1;

    /// <summary>Number of fragmented NAL units abandoned because a fragment went missing.</summary>
    public int DroppedFragments { get; private set; }

    /// <summary>Forget any half-reassembled fragment. Call when the stream restarts.</summary>
    public void Reset()
    {
        fragment.Clear();
        fragmentOpen = false;
    }

    /// <summary>
    /// Depacketize one RTP payload, appending every NAL unit it completes to <paramref name="output"/>.
    /// A payload that only continues a fragment appends nothing. Malformed payloads are discarded
    /// rather than throwing — these come off a socket, and one bad datagram must not stop the stream.
    /// </summary>
    public void Depacketize(ReadOnlySpan<byte> payload, List<byte[]> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Every payload starts with a 2-byte NAL unit header whose type field says how to read it.
        if (payload.Length < 2)
        {
            return;
        }

        int packetType = (payload[0] >> 1) & 0x3F;
        switch (packetType)
        {
            case HevcNalType.AggregationPacket:
                ReadAggregationPacket(payload, output);
                break;

            case HevcNalType.FragmentationUnit:
                ReadFragmentationUnit(payload, output);
                break;

            case HevcNalType.PayloadContentInformation:
                // PACI wraps another payload behind a variable-length header. Apple's DisplayService
                // has not been observed to send one; refuse rather than guess at the wrapping.
                break;

            default:
                output.Add(StripTrailer(payload));
                break;
        }
    }

    /// <summary>
    /// RFC 7798 §4.4.2: a 2-byte payload header, then repeated 16-bit-length-prefixed NAL units.
    ///
    /// <para>The DONL/DOND fields are only present when <c>sprop-max-don-diff</c> is greater than
    /// zero, which requires out-of-band signalling that Apple's DisplayService does not use. A
    /// stream that did carry them would misparse here, so the sizes are bounds-checked and a
    /// payload that does not divide cleanly is abandoned rather than emitting garbage NAL units.</para>
    /// </summary>
    private static void ReadAggregationPacket(ReadOnlySpan<byte> payload, List<byte[]> output)
    {
        int offset = 2;
        while (offset + 2 <= payload.Length)
        {
            int size = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            if (size == 0 || offset + size > payload.Length)
            {
                return;
            }

            output.Add(StripTrailer(payload.Slice(offset, size)));
            offset += size;
        }
    }

    /// <summary>
    /// RFC 7798 §4.4.3: a 2-byte payload header, then a 1-byte FU header carrying a start bit, an
    /// end bit, and the real NAL unit type of the fragmented unit.
    ///
    /// <para>The reassembled NAL unit header is the payload header with its type field replaced by
    /// the FU header's type, so layer id and temporal id survive.</para>
    /// </summary>
    private void ReadFragmentationUnit(ReadOnlySpan<byte> payload, List<byte[]> output)
    {
        const int HeaderLength = 3;
        if (payload.Length <= HeaderLength)
        {
            return;
        }

        byte fuHeader = payload[2];
        bool start = (fuHeader & 0x80) != 0;
        bool end = (fuHeader & 0x40) != 0;
        int nalType = fuHeader & 0x3F;

        if (start)
        {
            if (fragmentOpen)
            {
                // The previous unit never saw its end bit, so a fragment was lost.
                DroppedFragments++;
            }

            fragment.Clear();
            fragmentOpen = true;

            // Replace the 6-bit type in place, preserving F, layer id and TID.
            fragmentHeader0 = (byte)((payload[0] & 0x81) | (nalType << 1));
            fragmentHeader1 = payload[1];
            fragment.Add(fragmentHeader0);
            fragment.Add(fragmentHeader1);
        }
        else if (!fragmentOpen)
        {
            // A continuation with no start: the first fragment was lost, so this unit is unusable.
            return;
        }

        fragment.AddRange(payload[HeaderLength..]);

        if (end)
        {
            output.Add(StripTrailer(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fragment)));
            fragment.Clear();
            fragmentOpen = false;
        }
    }

    private static byte[] StripTrailer(ReadOnlySpan<byte> nalUnit) =>
        nalUnit.EndsWith(DisplayServiceTrailer)
            ? nalUnit[..^DisplayServiceTrailer.Length].ToArray()
            : nalUnit.ToArray();
}
