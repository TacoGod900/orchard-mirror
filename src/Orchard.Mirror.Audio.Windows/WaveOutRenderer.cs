using System.Runtime.InteropServices;

namespace Orchard.Mirror.Audio.Windows;

/// <summary>
/// Plays 16-bit stereo PCM through the default Windows output device.
/// </summary>
/// <remarks>
/// <para>Buffers are small and few on purpose. This audio accompanies a mirrored screen, so a deep
/// queue does not protect against anything a viewer wants — it just puts the sound further behind
/// the picture. The queue is a fixed ring, and when every buffer is still playing the incoming
/// frame is dropped rather than waited on, which keeps the decode thread from becoming the thing
/// that stalls the session.</para>
/// <para><c>waveOut</c> rather than WASAPI: at 10 ms per frame the extra latency is small, and the
/// API cannot deadlock the caller. If lip-sync ever needs the last few milliseconds, this type is
/// the only thing that has to change.</para>
/// </remarks>
public sealed class WaveOutRenderer : IDisposable
{
    private const int MMSYSERR_NOERROR = 0;
    private const int WAVE_MAPPER = -1;
    private const int WHDR_DONE = 0x00000001;
    private const int WHDR_PREPARED = 0x00000002;
    private const int WAVE_FORMAT_PCM = 1;

    /// <summary>Frames of buffering. Eight 10 ms frames is 80 ms, enough to ride out scheduling.</summary>
    private const int BufferCount = 8;

    private readonly nint device;
    private readonly int bufferBytes;
    private readonly nint[] headers = new nint[BufferCount];
    private readonly nint[] buffers = new nint[BufferCount];
    private readonly object sync = new();
    private int next;
    private bool disposed;

    private WaveOutRenderer(nint device, int bufferBytes)
    {
        this.device = device;
        this.bufferBytes = bufferBytes;
    }

    /// <summary>Frames handed to the device.</summary>
    public long FramesPlayed { get; private set; }

    /// <summary>Frames dropped because every buffer was still in flight.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>
    /// Open the default output device, or return <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="sampleRate">Samples per second per channel.</param>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="samplesPerFrame">Samples per channel in one write.</param>
    public static WaveOutRenderer? TryOpen(int sampleRate, int channels, int samplesPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerFrame);

        WaveFormatEx format = new()
        {
            FormatTag = WAVE_FORMAT_PCM,
            Channels = (ushort)channels,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            Size = 0,
        };
        format.BlockAlign = (ushort)(channels * 2);
        format.AverageBytesPerSecond = (uint)(sampleRate * format.BlockAlign);

        if (waveOutOpen(out nint device, WAVE_MAPPER, ref format, 0, 0, 0) != MMSYSERR_NOERROR)
        {
            return null;
        }

        WaveOutRenderer renderer = new(device, samplesPerFrame * channels * 2);
        renderer.PrepareBuffers();
        return renderer;
    }

    /// <summary>Queue one frame of interleaved PCM. Returns false when it was dropped.</summary>
    public bool Write(ReadOnlySpan<short> samples)
    {
        lock (sync)
        {
            if (disposed)
            {
                return false;
            }

            int bytes = Math.Min(samples.Length * 2, bufferBytes);
            int index = next;
            nint header = headers[index];
            WaveHeader current = Marshal.PtrToStructure<WaveHeader>(header);
            if ((current.Flags & WHDR_DONE) == 0 && FramesPlayed >= BufferCount)
            {
                FramesDropped++;
                return false;
            }

            if ((current.Flags & WHDR_DONE) != 0)
            {
                _ = waveOutUnprepareHeader(device, header, Marshal.SizeOf<WaveHeader>());
            }

            unsafe
            {
                fixed (short* source = samples)
                {
                    Buffer.MemoryCopy(source, (void*)buffers[index], bufferBytes, bytes);
                }
            }

            current.BufferLength = (uint)bytes;
            current.Flags = 0;
            current.Loops = 0;
            Marshal.StructureToPtr(current, header, fDeleteOld: false);

            if (waveOutPrepareHeader(device, header, Marshal.SizeOf<WaveHeader>()) != MMSYSERR_NOERROR
                || waveOutWrite(device, header, Marshal.SizeOf<WaveHeader>()) != MMSYSERR_NOERROR)
            {
                FramesDropped++;
                return false;
            }

            next = (index + 1) % BufferCount;
            FramesPlayed++;
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        // Reset before unpreparing: a header still owned by the device cannot be unprepared, and
        // waveOutReset is what returns them all.
        _ = waveOutReset(device);
        for (int index = 0; index < BufferCount; index++)
        {
            if (headers[index] != 0)
            {
                _ = waveOutUnprepareHeader(device, headers[index], Marshal.SizeOf<WaveHeader>());
                Marshal.FreeHGlobal(headers[index]);
            }

            if (buffers[index] != 0)
            {
                Marshal.FreeHGlobal(buffers[index]);
            }
        }

        _ = waveOutClose(device);
    }

    private void PrepareBuffers()
    {
        for (int index = 0; index < BufferCount; index++)
        {
            buffers[index] = Marshal.AllocHGlobal(bufferBytes);
            headers[index] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            WaveHeader header = new()
            {
                Data = buffers[index],
                BufferLength = (uint)bufferBytes,
                Flags = WHDR_DONE,
            };
            Marshal.StructureToPtr(header, headers[index], fDeleteOld: false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out nint device, int deviceId, ref WaveFormatEx format, nint callback, nint instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(nint device, nint header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(nint device);
}
