namespace Orchard.Mirror.Media;

/// <summary>
/// Turns the paired CoreDevice audio leg's RTP datagrams into AAC-ELD access units.
/// </summary>
/// <remarks>
/// <para>The device answers <c>start-audio-stream</c> with <c>RxPayloadType=101</c> and
/// <c>AudioStreamMode=8</c>, which is Apple AAC-ELD at 48 kHz stereo — see
/// <see cref="SamplesPerFrame"/> for the frame length, which is not what it is usually reported to
/// be. Unlike the video leg there is no fragmentation and no aggregation scheme:
/// <b>one RTP payload is exactly one access unit</b>, carried raw with no AU-header section, so
/// this class is framing and loss accounting rather than a depacketizer.</para>
/// <para>Loss is counted rather than concealed. A dropped 10 ms frame is inaudible on its own, but
/// the count is what tells an operator whether silence means "no audio playing" or "the stream is
/// falling apart", and the highest sequence number is what the agent's RTCP receiver reports back —
/// without which the device reaps the audio session after about twenty seconds.</para>
/// </remarks>
public sealed class AacEldAudioLeg
{
    /// <summary>Payload type the device assigns to the paired audio leg.</summary>
    public const byte PayloadType = 101;

    /// <summary>Sample rate of the decoded stream, and the RTP clock rate of this leg.</summary>
    public const int SampleRate = 48000;

    /// <summary>Decoded channel count.</summary>
    public const int Channels = 2;

    /// <summary>Samples per channel in one access unit. 480 at 48 kHz is a 10 ms frame.</summary>
    /// <remarks>
    /// Measured off the device, not read from a cookie. The RTP timestamp advances by exactly 480
    /// per packet on a 48 kHz clock, and feeding 200 real access units to a reference decoder
    /// configured for 480 decodes all 200 cleanly at full scale, while configuring it for 512
    /// manages 50 before failing to parse. See <see cref="AudioSpecificConfig"/>.
    /// </remarks>
    public const int SamplesPerFrame = 480;

    /// <summary>
    /// The AudioSpecificConfig an AAC-ELD decoder must be initialised with for this stream:
    /// AOT 39, 48 kHz, stereo, and the frame length in <see cref="SamplesPerFrame"/>.
    /// </summary>
    /// <remarks>
    /// <para>Apple calls this the magic cookie. It is fixed for the mode the device negotiates, so
    /// it is stated here rather than parsed out of a stream that never carries it.</para>
    /// <para><b>This is not the value the ecosystem uses.</b> pymobiledevice3 hardcodes
    /// <c>F8 E6 40 00</c> — as does ADR 0014 §6, which took it from there — and that value sets
    /// <c>frameLengthFlag</c> to 0, declaring 512-sample frames. The device sends 480. A reference
    /// decoder given <c>F8 E6 40 00</c> fails on real access units; given <c>F8 E6 50 00</c> it
    /// decodes them all. pymobiledevice3 gets away with it because it also passes AudioToolbox
    /// <c>mFramesPerPacket = 480</c> alongside the cookie.</para>
    /// </remarks>
    public static ReadOnlySpan<byte> AudioSpecificConfig => [0xF8, 0xE6, 0x50, 0x00];

    private ushort lastSequenceNumber;
    private bool started;

    /// <summary>Access units delivered to the caller.</summary>
    public long AccessUnitsReceived { get; private set; }

    /// <summary>Access units the network lost, inferred from gaps in the sequence numbers.</summary>
    public long AccessUnitsLost { get; private set; }

    /// <summary>Datagrams rejected as malformed, wrong payload type, or empty.</summary>
    public long DatagramsRejected { get; private set; }

    /// <summary>Extended highest sequence number seen, as RTCP receiver reports define it.</summary>
    /// <remarks>High 16 bits count sequence-number wraps; low 16 bits are the sequence number.</remarks>
    public uint ExtendedHighestSequenceNumber { get; private set; }

    /// <summary>
    /// Extract the access unit from one received datagram.
    /// </summary>
    /// <param name="datagram">A datagram as it arrived from the relay socket.</param>
    /// <param name="accessUnit">The AAC-ELD access unit, as a slice of <paramref name="datagram"/>.</param>
    /// <returns>
    /// <see langword="false"/> for anything that is not a payload-bearing audio packet — malformed
    /// RTP, RTCP, another payload type, or an empty payload. These arrive from a socket, so they
    /// are expected inputs and never throw.
    /// </returns>
    public bool TryReadAccessUnit(ReadOnlySpan<byte> datagram, out ReadOnlySpan<byte> accessUnit)
    {
        accessUnit = default;
        if (!RtpPacket.TryParse(datagram, out RtpPacket packet) || packet.PayloadType != PayloadType)
        {
            DatagramsRejected++;
            return false;
        }

        if (packet.Payload.IsEmpty)
        {
            DatagramsRejected++;
            return false;
        }

        Advance(packet.SequenceNumber);
        AccessUnitsReceived++;
        accessUnit = packet.Payload;
        return true;
    }

    private void Advance(ushort sequenceNumber)
    {
        if (!started)
        {
            started = true;
            lastSequenceNumber = sequenceNumber;
            ExtendedHighestSequenceNumber = sequenceNumber;
            return;
        }

        // Distance forward from the last packet, modulo the 16-bit sequence space. Anything in the
        // top half of that space is a packet arriving late rather than a gap, and must not be
        // counted as loss or allowed to drag the highest sequence number backwards.
        int forward = (ushort)(sequenceNumber - lastSequenceNumber);
        if (forward == 0 || forward > 0x7FFF)
        {
            return;
        }

        AccessUnitsLost += forward - 1;
        if (sequenceNumber < lastSequenceNumber)
        {
            ExtendedHighestSequenceNumber += 0x10000;
        }

        lastSequenceNumber = sequenceNumber;
        ExtendedHighestSequenceNumber = (ExtendedHighestSequenceNumber & 0xFFFF0000) | sequenceNumber;
    }
}
