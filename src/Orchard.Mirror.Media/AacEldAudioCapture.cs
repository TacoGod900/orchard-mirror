namespace Orchard.Mirror.Media;

/// <summary>
/// Receives the paired CoreDevice audio leg and records its AAC-ELD access units to a file.
/// </summary>
/// <remarks>
/// <para>This does not play anything, and deliberately so. The device sends AAC-ELD
/// (<c>AudioStreamMode=8</c>, 48 kHz stereo, 480-sample frames) and Windows has no decoder for it —
/// Media Foundation's AAC decoder covers LC and HE only. ADR 0014 §6 requires Orchard's own
/// decoder rather than a vendored codec, and that decoder does not exist yet.</para>
/// <para>What blocks writing it is a reference recording: an AAC-ELD stream cannot be synthesised
/// locally, because no encoder for it is installed and its access units carry no sync word to
/// re-frame from. So this class exists to produce the input the decoder work needs, and to prove
/// the leg is wired end to end, without pretending to be playback.</para>
/// <para>The output is length-prefixed access units — a 4-byte big-endian length before each — the
/// same shape <c>eng/orchard-mirror/analyze-rtp-capture.py</c> already reads for video.</para>
/// </remarks>
public sealed class AacEldAudioCapture : IAsyncDisposable
{
    private readonly RtpLoopbackReceiver receiver;
    private readonly FileStream output;
    private readonly AacEldAudioLeg leg = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private bool disposed;

    private AacEldAudioCapture(RtpLoopbackReceiver receiver, FileStream output)
    {
        this.receiver = receiver;
        this.output = output;
        pump = PumpAsync(stop.Token);
    }

    /// <summary>The loopback port to hand the agent as <c>audioRelayPort</c>.</summary>
    public int Port => receiver.Port;

    /// <summary>Access units written so far.</summary>
    public long AccessUnitsCaptured => leg.AccessUnitsReceived;

    /// <summary>
    /// Start capturing, or return <see langword="null"/> when no capture path is configured.
    /// </summary>
    /// <param name="path">
    /// Where to write the access units. Set through <c>ORCHARD_MIRROR_AUDIO_CAPTURE</c>; when it is
    /// unset the audio leg is never requested, which keeps the device from encoding a stream nobody
    /// consumes.
    /// </param>
    public static AacEldAudioCapture? TryStart(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        RtpLoopbackReceiver receiver = new();
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new AacEldAudioCapture(receiver, output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            receiver.Dispose();
            return null;
        }
    }

    /// <summary>Describe what was captured, for the window's status line.</summary>
    public string Describe() =>
        $"{leg.AccessUnitsReceived} AAC-ELD frames captured, {leg.AccessUnitsLost} lost";

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

        await output.DisposeAsync().ConfigureAwait(false);
        stop.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        byte[] lengthPrefix = new byte[4];
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

            // The access unit is a slice of the datagram, and a span cannot survive the await
            // below, so it is copied out before anything is written.
            byte[]? accessUnit = leg.TryReadAccessUnit(datagram, out ReadOnlySpan<byte> slice)
                ? slice.ToArray()
                : null;
            if (accessUnit is null)
            {
                continue;
            }

            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, accessUnit.Length);
            await output.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(accessUnit, cancellationToken).ConfigureAwait(false);
        }
    }
}
