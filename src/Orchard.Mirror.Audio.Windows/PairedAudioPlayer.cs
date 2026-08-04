using Orchard.Mirror.Media;

namespace Orchard.Mirror.Audio.Windows;

/// <summary>
/// Plays the phone's paired audio leg: loopback RTP in, sound out.
/// </summary>
/// <remarks>
/// Every stage is optional in the sense that its absence degrades to silence rather than to a
/// failed session. Mirroring a screen without sound is a working product; refusing to mirror
/// because a codec library is missing is not.
/// </remarks>
public sealed class PairedAudioPlayer : IAsyncDisposable
{
    private readonly RtpLoopbackReceiver receiver;
    private readonly FdkAacEldDecoder decoder;
    private readonly WaveOutRenderer renderer;
    private readonly AacEldAudioLeg leg = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private bool disposed;

    private PairedAudioPlayer(RtpLoopbackReceiver receiver, FdkAacEldDecoder decoder, WaveOutRenderer renderer)
    {
        this.receiver = receiver;
        this.decoder = decoder;
        this.renderer = renderer;
        pump = PumpAsync(stop.Token);
    }

    /// <summary>The loopback port to hand the agent as <c>audioRelayPort</c>.</summary>
    public int Port => receiver.Port;

    /// <summary>Frames decoded and handed to the output device.</summary>
    public long FramesPlayed => decoder.FramesDecoded;

    /// <summary>Whether anything actually audible has come through yet.</summary>
    /// <remarks>
    /// Not the same as frames arriving. The phone sends this leg continuously whether or not
    /// anything is playing, and its silence frames are four bytes that decode to zeroes, so a frame
    /// count says only that the stream is up. This says sound is coming out of the phone.
    /// </remarks>
    public bool HasHeardAudio { get; private set; }

    /// <summary>
    /// Start playback, or return <see langword="null"/> when this machine cannot play the stream.
    /// </summary>
    public static PairedAudioPlayer? TryStart()
    {
        FdkAacEldDecoder? decoder = FdkAacEldDecoder.TryOpen();
        if (decoder is null)
        {
            return null;
        }

        WaveOutRenderer? renderer = WaveOutRenderer.TryOpen(
            AacEldAudioLeg.SampleRate, AacEldAudioLeg.Channels, AacEldAudioLeg.SamplesPerFrame);
        if (renderer is null)
        {
            decoder.Dispose();
            return null;
        }

        RtpLoopbackReceiver receiver = new();
        return new PairedAudioPlayer(receiver, decoder, renderer);
    }

    /// <summary>Describe playback for the window's status line and the diagnostics log.</summary>
    public string Describe() =>
        $"{decoder.FramesDecoded} frames played, {leg.AccessUnitsLost} lost in transit, "
            + $"{decoder.FramesFailed} undecodable, {renderer.FramesDropped} dropped late";

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await stop.CancelAsync().ConfigureAwait(false);
        receiver.Dispose();
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }

        renderer.Dispose();
        decoder.Dispose();
        stop.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] datagram;
            try
            {
                datagram = await receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (!leg.TryReadAccessUnit(datagram, out ReadOnlySpan<byte> accessUnit))
            {
                continue;
            }

            if (!decoder.TryDecode(accessUnit, out ReadOnlySpan<short> samples))
            {
                continue;
            }

            if (!HasHeardAudio)
            {
                // A sixteenth of full scale: comfortably above the dither in a silent frame, well
                // below anything a listener would call quiet.
                const short Audible = 2048;
                foreach (short sample in samples)
                {
                    if (sample > Audible || sample < -Audible)
                    {
                        HasHeardAudio = true;
                        break;
                    }
                }
            }

            _ = renderer.Write(samples);
        }
    }
}
