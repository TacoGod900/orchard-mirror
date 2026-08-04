using System.Runtime.InteropServices;

namespace Orchard.Mirror.Video.Windows;

internal static unsafe class VideoInterop
{
    internal const int SOk = 0;
    internal const int D3DDriverTypeHardware = 1;
    internal const uint D3D11CreateDeviceBgraSupport = 0x20;
    internal const uint D3D11CreateDeviceVideoSupport = 0x800;
    internal const uint D3D11SdkVersion = 7;
    internal const int DxgiFormatNv12 = 103;
    internal const int DxgiFormatBgra8Unorm = 87;
    internal const uint D3D11BindDecoder = 0x200;
    internal const int D3D11UsageStaging = 3;
    internal const uint D3D11CpuAccessRead = 0x20000;
    internal const int D3D11MapRead = 1;
    internal const int D3D11QueryEvent = 0;
    internal const uint DxgiUsageRenderTargetOutput = 0x20;
    internal const uint DxgiSwapChainFlagFrameLatencyWaitableObject = 0x40;
    internal const uint DxgiSwapChainFlagYuvVideo = 0x200;
    internal const int DxgiSwapEffectFlipSequential = 3;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;

    internal static readonly Guid HevcMainProfile =
        new("5B11D51B-2F4C-4452-BCC3-09F2A1160CC0");
    internal static readonly Guid Id3D11VideoDevice =
        new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    internal static readonly Guid Id3D11VideoContext =
        new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");
    internal static readonly Guid IdxgiFactory2 =
        new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    internal static readonly Guid IdxgiSwapChain2 =
        new("A8BE2AC4-199F-4946-B331-79599FB98DE7");
    internal static readonly Guid Id3D11Texture2D =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll")]
    internal static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("dxgi.dll")]
    internal static extern int CreateDXGIFactory2(
        uint flags,
        in Guid iid,
        out nint factory);

    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    internal static nint Method(nint instance, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);

    internal static int QueryInterface(nint instance, in Guid iid, out nint result)
    {
        fixed (Guid* iidPointer = &iid)
        fixed (nint* resultPointer = &result)
        {
            delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> method =
                (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(instance, 0);
            return method(instance, iidPointer, resultPointer);
        }
    }

    internal static void Release(ref nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        delegate* unmanaged[Stdcall]<nint, uint> release =
            (delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2);
        _ = release(instance);
        instance = 0;
    }

    internal static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new VideoHardwareException(operation, hresult);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleDescription
    {
        internal uint Count;
        internal uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture2DDescription
    {
        internal uint Width;
        internal uint Height;
        internal uint MipLevels;
        internal uint ArraySize;
        internal int Format;
        internal SampleDescription SampleDescription;
        internal int Usage;
        internal uint BindFlags;
        internal uint CpuAccessFlags;
        internal uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MappedSubresource
    {
        internal nint Data;
        internal uint RowPitch;
        internal uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryDescription
    {
        internal int Query;
        internal uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoDecoderDescription
    {
        internal Guid Profile;
        internal uint SampleWidth;
        internal uint SampleHeight;
        internal int OutputFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoDecoderConfiguration
    {
        internal Guid BitstreamEncryption;
        internal Guid MacroblockControlEncryption;
        internal Guid ResidualDifferenceEncryption;
        internal uint BitstreamRaw;
        internal uint MacroblockControlRasterOrder;
        internal uint ResidualDifferenceHost;
        internal uint SpatialResidual8;
        internal uint Residual8Subtraction;
        internal uint SpatialHost8Or9Clipping;
        internal uint SpatialResidualInterleaved;
        internal uint IntraResidualUnsigned;
        internal uint ResidualDifferenceAccelerator;
        internal uint HostInverseScan;
        internal uint SpecificIdct;
        internal uint FourGroupedCoefficients;
        internal ushort MinimumRenderTargetBufferCount;
        internal ushort DecoderSpecific;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DecoderOutputViewDescription
    {
        internal Guid Profile;
        internal int ViewDimension;
        internal uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DecoderBufferDescription
    {
        internal int BufferType;
        internal uint BufferIndex;
        internal uint DataOffset;
        internal uint DataSize;
        internal uint FirstMacroblockAddress;
        internal uint MacroblocksInBuffer;
        internal uint Width;
        internal uint Height;
        internal uint Stride;
        internal uint ReservedBits;
        internal nint InitializationVector;
        internal uint InitializationVectorSize;
        internal int PartialEncryption;
        internal EncryptedBlockInformation EncryptedBlockInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EncryptedBlockInformation
    {
        internal uint EncryptedBytesAtBeginning;
        internal uint BytesInSkipPattern;
        internal uint BytesInEncryptPattern;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SwapChainDescription
    {
        internal uint Width;
        internal uint Height;
        internal int Format;
        internal int Stereo;
        internal SampleDescription SampleDescription;
        internal uint BufferUsage;
        internal uint BufferCount;
        internal int Scaling;
        internal int SwapEffect;
        internal int AlphaMode;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorContentDescription
    {
        internal int InputFrameFormat;
        internal Rational InputFrameRate;
        internal uint InputWidth;
        internal uint InputHeight;
        internal Rational OutputFrameRate;
        internal uint OutputWidth;
        internal uint OutputHeight;
        internal int Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorInputViewDescription
    {
        internal uint FourCharacterCode;
        internal int ViewDimension;
        internal uint MipSlice;
        internal uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorOutputViewDescription
    {
        internal int ViewDimension;
        internal uint MipSlice;
        internal uint FirstArraySlice;
        internal uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorStream
    {
        internal int Enable;
        internal uint OutputIndex;
        internal uint InputFrameOrField;
        internal uint PastFrames;
        internal uint FutureFrames;
        internal nint PastSurfaces;
        internal nint InputSurface;
        internal nint FutureSurfaces;
        internal nint PastSurfacesRight;
        internal nint InputSurfaceRight;
        internal nint FutureSurfacesRight;
    }
}

/// <summary>A Direct3D video call failed after a hardware decoder was selected.</summary>
public sealed class VideoHardwareException : Exception
{
    public VideoHardwareException(string operation, int hresult)
        : base($"{operation} failed (HRESULT 0x{hresult:X8}).") => HResult = hresult;
}
