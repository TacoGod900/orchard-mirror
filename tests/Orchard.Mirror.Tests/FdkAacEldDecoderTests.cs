using Orchard.Mirror.Audio.Windows;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// Decoding the paired audio leg for real. These need libfdk-aac present, and the recorded-stream
/// test needs a capture to point at, so both skip loudly rather than passing on absence.
/// </summary>
internal static class FdkAacEldDecoderTests
{
    /// <summary>
    /// Set <c>ORCHARD_MIRROR_AUDIO_FIXTURE</c> to a capture written by
    /// <c>ORCHARD_MIRROR_AUDIO_CAPTURE</c> or by the probe. It is not committed: a capture is a
    /// recording of whatever the owner's phone was playing.
    /// </summary>
    private const string FixtureVariable = "ORCHARD_MIRROR_AUDIO_FIXTURE";

    /// <summary>
    /// The whole point of the decoder: real access units in, audible samples out.
    /// </summary>
    /// <remarks>
    /// Asserting on peak amplitude rather than on a frame count is deliberate. A decoder configured
    /// with the wrong frame length still returns success for a while — it drifts and degrades
    /// instead of failing — so a test that only counted frames would pass on a stream nobody could
    /// listen to.
    /// </remarks>
    internal static void DecodesARecordedStreamToAudibleSamples()
    {
        string? path = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new Program.SkipException($"Set {FixtureVariable} to a recorded audio capture to run this.");
        }

        using FdkAacEldDecoder? decoder = FdkAacEldDecoder.TryOpen()
            ?? throw new Program.SkipException("libfdk-aac is not available on this machine.");

        AacEldAudioLeg leg = new();
        int decoded = 0;
        int peak = 0;
        foreach (byte[] datagram in LengthPrefixedRecords(path))
        {
            if (!leg.TryReadAccessUnit(datagram, out ReadOnlySpan<byte> accessUnit)
                || !decoder.TryDecode(accessUnit, out ReadOnlySpan<short> samples))
            {
                continue;
            }

            decoded++;
            foreach (short sample in samples)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
            }
        }

        Assert(decoded >= 100, $"Only {decoded} frames decoded; the capture or the config is wrong.");
        Assert(
            decoder.FramesFailed * 4 < decoded,
            $"{decoder.FramesFailed} of {decoded + decoder.FramesFailed} access units failed to decode.");

        // A quarter of full scale. Silence, a wrong frame length and a channel-count mismatch all
        // land far below this; real programme material sits far above it.
        Assert(peak > 8192, $"Peak sample was {peak}, which is not audible programme material.");
    }

    /// <summary>
    /// The renderer has to open the real default output device, because "no sound" and "no device"
    /// are indistinguishable from the counters alone.
    /// </summary>
    internal static void OpensTheDefaultOutputDevice()
    {
        using WaveOutRenderer? renderer = WaveOutRenderer.TryOpen(
            AacEldAudioLeg.SampleRate, AacEldAudioLeg.Channels, AacEldAudioLeg.SamplesPerFrame)
            ?? throw new Program.SkipException("No Windows audio output device is available.");

        short[] silence = new short[AacEldAudioLeg.SamplesPerFrame * AacEldAudioLeg.Channels];
        Assert(renderer.Write(silence), "The first frame must be accepted by an idle device.");
        Assert(renderer.FramesPlayed == 1, $"FramesPlayed was {renderer.FramesPlayed}, expected 1.");
    }

    private static IEnumerable<byte[]> LengthPrefixedRecords(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            offset += 4;
            if (length < 0 || offset + length > data.Length)
            {
                yield break;
            }

            yield return data.AsSpan(offset, length).ToArray();
            offset += length;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
