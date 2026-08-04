using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The paired audio leg carries one AAC-ELD access unit per RTP payload, raw. Every datagram below
/// is assembled from the RFC 3550 header layout rather than by asking the parser what it wants.
/// </summary>
internal static class AacEldAudioLegTests
{
    internal static void ReadsOneAccessUnitPerDatagram()
    {
        AacEldAudioLeg stream = new();

        Assert(
            stream.TryReadAccessUnit(Datagram(sequence: 100, payload: [0x01, 0x02, 0x03]), out ReadOnlySpan<byte> unit),
            "A well-formed audio datagram was rejected.");
        Assert(unit.SequenceEqual<byte>([0x01, 0x02, 0x03]), "The access unit is the RTP payload, unchanged.");
        Assert(stream.AccessUnitsReceived == 1, $"Received count was {stream.AccessUnitsReceived}, expected 1.");
        Assert(stream.AccessUnitsLost == 0, $"Lost count was {stream.AccessUnitsLost}, expected 0.");
    }

    /// <summary>
    /// The video leg is payload type 96-ish and shares nothing with this one; RTCP arrives on the
    /// same socket in the 64-95 range. Decoding either as audio feeds the decoder noise.
    /// </summary>
    internal static void RejectsAnythingThatIsNotTheAudioPayloadType()
    {
        AacEldAudioLeg stream = new();

        Assert(!stream.TryReadAccessUnit(Datagram(1, [0xAA], payloadType: 96), out _), "A video payload type was accepted.");
        Assert(!stream.TryReadAccessUnit(Datagram(2, [0xAA], payloadType: 200), out _), "An RTCP payload type was accepted.");
        Assert(!stream.TryReadAccessUnit(Datagram(3, []), out _), "An empty payload was accepted as an access unit.");
        Assert(!stream.TryReadAccessUnit([0x80, 101, 0x00], out _), "A truncated datagram was accepted.");
        Assert(stream.AccessUnitsReceived == 0, "Nothing above is an access unit.");
        Assert(stream.DatagramsRejected == 4, $"Rejected count was {stream.DatagramsRejected}, expected 4.");
    }

    /// <summary>
    /// Loss has to be counted, not concealed: it is the only signal that separates "nothing is
    /// playing" from "the stream is coming apart", and it feeds the RTCP report the device needs.
    /// </summary>
    internal static void CountsTheFramesTheNetworkLost()
    {
        AacEldAudioLeg stream = new();

        _ = stream.TryReadAccessUnit(Datagram(10, [0x01]), out _);
        _ = stream.TryReadAccessUnit(Datagram(14, [0x02]), out _);   // 11, 12 and 13 never arrived
        _ = stream.TryReadAccessUnit(Datagram(15, [0x03]), out _);

        Assert(stream.AccessUnitsReceived == 3, $"Received count was {stream.AccessUnitsReceived}, expected 3.");
        Assert(stream.AccessUnitsLost == 3, $"Lost count was {stream.AccessUnitsLost}, expected 3.");
    }

    /// <summary>
    /// A packet that overtakes its neighbours must not read as a huge gap followed by huge loss,
    /// and must not drag the highest-sequence figure backwards.
    /// </summary>
    internal static void TreatsAReorderedPacketAsLateRatherThanLost()
    {
        AacEldAudioLeg stream = new();

        _ = stream.TryReadAccessUnit(Datagram(500, [0x01]), out _);
        _ = stream.TryReadAccessUnit(Datagram(502, [0x02]), out _);  // one genuine gap at 501
        _ = stream.TryReadAccessUnit(Datagram(501, [0x03]), out _);  // 501 turns up late

        Assert(stream.AccessUnitsLost == 1, $"Lost count was {stream.AccessUnitsLost}, expected 1.");
        Assert(
            stream.ExtendedHighestSequenceNumber == 502,
            $"Highest sequence was {stream.ExtendedHighestSequenceNumber}, expected it to stay at 502.");
    }

    /// <summary>
    /// Sequence numbers wrap every 65 536 packets — about eleven minutes at one frame per 10 ms.
    /// The receiver report carries a 32-bit extended number, so the wrap has to increment the high
    /// half instead of looking like the stream jumped backwards.
    /// </summary>
    internal static void CarriesTheSequenceWrapIntoTheExtendedNumber()
    {
        AacEldAudioLeg stream = new();

        _ = stream.TryReadAccessUnit(Datagram(65534, [0x01]), out _);
        _ = stream.TryReadAccessUnit(Datagram(65535, [0x02]), out _);
        _ = stream.TryReadAccessUnit(Datagram(0, [0x03]), out _);
        _ = stream.TryReadAccessUnit(Datagram(1, [0x04]), out _);

        Assert(stream.AccessUnitsLost == 0, $"A clean wrap lost nothing, but {stream.AccessUnitsLost} was counted.");
        Assert(
            stream.ExtendedHighestSequenceNumber == 0x10001,
            $"Extended sequence was 0x{stream.ExtendedHighestSequenceNumber:X}, expected 0x10001 after one wrap.");
    }

    /// <summary>
    /// The config is decoded here bit by bit from the MPEG-4 AudioSpecificConfig layout, so the
    /// constants cannot quietly disagree with the bytes they claim to describe. Getting the frame
    /// length wrong is silent — a decoder drifts rather than failing — and the cookie the
    /// ecosystem publishes for this stream declares the wrong one.
    /// </summary>
    internal static void StatesTheAudioSpecificConfigForTheNegotiatedMode()
    {
        ReadOnlySpan<byte> config = AacEldAudioLeg.AudioSpecificConfig;
        Assert(config.SequenceEqual<byte>([0xF8, 0xE6, 0x50, 0x00]), "The cookie that decodes this device's stream is F8 E6 50 00.");

        ulong bits = ((ulong)config[0] << 24) | ((ulong)config[1] << 16) | ((ulong)config[2] << 8) | config[3];
        int Field(int start, int length) => (int)((bits >> (32 - start - length)) & ((1UL << length) - 1));

        Assert(Field(0, 5) == 31, "An audioObjectType of 31 escapes to the six-bit form.");
        Assert(32 + Field(5, 6) == 39, $"audioObjectType was {32 + Field(5, 6)}, expected 39 (ER AAC ELD).");
        Assert(Field(11, 4) == 3, "samplingFrequencyIndex 3 is 48 kHz.");
        Assert(Field(15, 4) == 2, "channelConfiguration 2 is stereo.");

        // First bit of ELDSpecificConfig. libfdk-aac emits F8 E6 50 00 for 480-sample frames and
        // F8 E6 40 00 for 512; the device's RTP timestamp advances 480 per packet, and only the
        // 480 configuration decodes its access units.
        int frameLengthFlag = Field(19, 1);
        Assert(frameLengthFlag == 1, "frameLengthFlag 1 is the 480-sample frame length the device actually sends.");

        Assert(AacEldAudioLeg.SampleRate == 48000, "The negotiated mode is 48 kHz.");
        Assert(AacEldAudioLeg.Channels == 2, "The negotiated mode is stereo.");
        Assert(
            AacEldAudioLeg.SamplesPerFrame == (frameLengthFlag == 0 ? 512 : 480),
            $"SamplesPerFrame is {AacEldAudioLeg.SamplesPerFrame}, which contradicts the config's frameLengthFlag.");
    }

    private static byte[] Datagram(ushort sequence, byte[] payload, byte payloadType = 101)
    {
        byte[] datagram = new byte[12 + payload.Length];
        datagram[0] = 0x80;                                  // V=2, no padding, no extension, no CSRC
        datagram[1] = payloadType;                           // marker clear; audio does not use it
        datagram[2] = (byte)(sequence >> 8);
        datagram[3] = (byte)sequence;
        payload.CopyTo(datagram, 12);
        return datagram;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
