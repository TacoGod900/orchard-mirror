using System.Runtime.InteropServices;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Audio.Windows;

/// <summary>
/// Decodes the device's AAC-ELD access units to 16-bit stereo PCM through libfdk-aac.
/// </summary>
/// <remarks>
/// <para>Windows has no AAC-ELD decoder of its own — Media Foundation's AAC decoder covers LC and
/// HE only — so something has to supply one. libfdk-aac is used here rather than FFmpeg or FAAD2
/// because it is not copyleft, which is what ADR 0014 §4 actually requires of the media path.</para>
/// <para><b>This deviates from ADR 0014 §6</b>, which says the decoder will be Orchard's own. That
/// decision was taken when no other non-copyleft option had been identified. The deviation is
/// deliberate and needs the ADR amended or this replaced; see
/// <c>docs/orchard-mirror/AUDIO_2026-08-02.md</c>. Note that writing our own would not change the
/// AAC patent position, which is the same either way.</para>
/// <para>The type deliberately exposes nothing of libfdk-aac, so replacing it later is a one-file
/// change.</para>
/// </remarks>
public sealed class FdkAacEldDecoder : IDisposable
{
    private const string LibraryName = "libfdk-aac-2";

    /// <summary>Raw access units, with the configuration supplied out of band.</summary>
    private const int TransportTypeRawMp4 = 0;

    private static int resolverInstalled;

    private readonly nint handle;
    private readonly short[] pcm;
    private bool disposed;

    private FdkAacEldDecoder(nint handle)
    {
        this.handle = handle;
        // Two extra frames of headroom: libfdk refuses to decode into a buffer it considers too
        // small, and it sizes that check from the stream rather than from our expectations.
        pcm = new short[AacEldAudioLeg.SamplesPerFrame * AacEldAudioLeg.Channels * 3];
    }

    /// <summary>Decoded frames handed back to the caller.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>Access units libfdk-aac refused. A handful at startup is normal.</summary>
    public long FramesFailed { get; private set; }

    /// <summary>
    /// Open a decoder configured for the paired audio leg, or return <see langword="null"/> when
    /// libfdk-aac is not present.
    /// </summary>
    /// <remarks>
    /// Absence is a normal outcome rather than an error: it means this machine plays no phone audio,
    /// which must not stop the screen from mirroring.
    /// </remarks>
    public static FdkAacEldDecoder? TryOpen()
    {
        InstallResolver();
        nint handle;
        try
        {
            handle = aacDecoder_Open(TransportTypeRawMp4, 1);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        if (handle == 0)
        {
            return null;
        }

        FdkAacEldDecoder decoder = new(handle);
        if (!decoder.Configure())
        {
            decoder.Dispose();
            return null;
        }

        return decoder;
    }

    /// <summary>
    /// Decode one access unit.
    /// </summary>
    /// <param name="accessUnit">One complete AAC-ELD access unit.</param>
    /// <param name="samples">Interleaved 16-bit stereo PCM, valid until the next call.</param>
    /// <returns><see langword="false"/> when this unit produced nothing; the stream continues.</returns>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, out ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        samples = default;
        if (accessUnit.IsEmpty)
        {
            return false;
        }

        unsafe
        {
            fixed (byte* unit = accessUnit)
            {
                byte* buffers = unit;
                uint size = (uint)accessUnit.Length;
                uint valid = size;
                if (aacDecoder_Fill(handle, &buffers, &size, &valid) != 0 || valid != 0)
                {
                    FramesFailed++;
                    return false;
                }

                fixed (short* output = pcm)
                {
                    if (aacDecoder_DecodeFrame(handle, output, pcm.Length, 0) != 0)
                    {
                        FramesFailed++;
                        return false;
                    }
                }
            }
        }

        FramesDecoded++;
        samples = pcm.AsSpan(0, AacEldAudioLeg.SamplesPerFrame * AacEldAudioLeg.Channels);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        aacDecoder_Close(handle);
    }

    private bool Configure()
    {
        ReadOnlySpan<byte> config = AacEldAudioLeg.AudioSpecificConfig;
        unsafe
        {
            fixed (byte* bytes = config)
            {
                byte* pointer = bytes;
                uint length = (uint)config.Length;
                return aacDecoder_ConfigRaw(handle, &pointer, &length) == 0;
            }
        }
    }

    /// <summary>
    /// Teach the runtime where libfdk-aac lives when it is not beside the application.
    /// </summary>
    /// <remarks>
    /// <c>ORCHARD_FDK_AAC</c> overrides; otherwise the MSYS2 location is tried, which is where a
    /// development machine has it. A packaged build is expected to ship the DLL alongside the
    /// executable, in which case the default probing finds it and none of this runs.
    /// </remarks>
    private static void InstallResolver()
    {
        if (Interlocked.Exchange(ref resolverInstalled, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(FdkAacEldDecoder).Assembly, (name, assembly, path) =>
        {
            if (!string.Equals(name, LibraryName, StringComparison.Ordinal))
            {
                return 0;
            }

            foreach (string candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint loaded))
                {
                    return loaded;
                }
            }

            return NativeLibrary.TryLoad(name, assembly, path, out nint fallback) ? fallback : 0;
        });
    }

    private static IEnumerable<string> Candidates()
    {
        string? configured = Environment.GetEnvironmentVariable("ORCHARD_FDK_AAC");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "libfdk-aac-2.dll");
        yield return @"C:\msys64\ucrt64\bin\libfdk-aac-2.dll";
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint aacDecoder_Open(int transportFormat, uint layers);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int aacDecoder_ConfigRaw(nint decoder, byte** config, uint* length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int aacDecoder_Fill(nint decoder, byte** buffer, uint* bufferSize, uint* bytesValid);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int aacDecoder_DecodeFrame(nint decoder, short* pcm, int pcmSize, uint flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void aacDecoder_Close(nint decoder);
}
