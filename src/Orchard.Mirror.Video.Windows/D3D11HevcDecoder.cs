namespace Orchard.Mirror.Video.Windows;

/// <summary>
/// Owns a driver-provided D3D11 HEVC Main decoder and its NV12 decoded-picture surfaces. Creation is
/// capability-gated: <see cref="TryCreate"/> returns null when the process has no usable hardware
/// decoder, while failures after a supported decoder has been selected remain visible.
/// </summary>
public sealed unsafe class D3D11HevcDecoder : IDisposable
{
    private const int PictureParameters = 0;
    private const int InverseQuantizationMatrix = 4;
    private const int SliceControl = 5;
    private const int Bitstream = 6;

    private nint device;
    private nint context;
    private nint videoDevice;
    private nint videoContext;
    private nint decoder;
    private nint outputTexture;
    private nint lumaStagingTexture;
    private readonly nint[] outputViews;

    private D3D11HevcDecoder(
        int width,
        int height,
        nint device,
        nint context,
        nint videoDevice,
        nint videoContext,
        nint decoder,
        nint outputTexture,
        nint[] outputViews)
    {
        Width = width;
        Height = height;
        this.device = device;
        this.context = context;
        this.videoDevice = videoDevice;
        this.videoContext = videoContext;
        this.decoder = decoder;
        this.outputTexture = outputTexture;
        this.outputViews = outputViews;
    }

    public int Width { get; }

    public int Height { get; }

    public int SurfaceCount => outputViews.Length;

    /// <summary>The NV12 texture array populated by the decoder.</summary>
    public nint OutputTexture => outputTexture;

    internal nint Device => device;

    internal nint Context => context;

    internal nint VideoDevice => videoDevice;

    internal nint VideoContext => videoContext;

    public static D3D11HevcDecoder? TryCreate(int width, int height, int surfaceCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("NV12 decode dimensions must be even.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(surfaceCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(surfaceCount, 16);

        nint device = 0;
        nint context = 0;
        nint videoDevice = 0;
        nint videoContext = 0;
        nint decoder = 0;
        nint outputTexture = 0;
        nint[] outputViews = new nint[surfaceCount];

        try
        {
            int createResult = VideoInterop.D3D11CreateDevice(
                0,
                VideoInterop.D3DDriverTypeHardware,
                0,
                VideoInterop.D3D11CreateDeviceBgraSupport | VideoInterop.D3D11CreateDeviceVideoSupport,
                0,
                0,
                VideoInterop.D3D11SdkVersion,
                out device,
                out _,
                out context);
            if (createResult < 0 || device == 0 || context == 0)
            {
                return null;
            }

            Guid videoDeviceIid = VideoInterop.Id3D11VideoDevice;
            Guid videoContextIid = VideoInterop.Id3D11VideoContext;
            if (VideoInterop.QueryInterface(device, in videoDeviceIid, out videoDevice) < 0 ||
                VideoInterop.QueryInterface(context, in videoContextIid, out videoContext) < 0)
            {
                return null;
            }

            if (!SupportsHevcMainNv12(videoDevice))
            {
                return null;
            }

            VideoInterop.VideoDecoderDescription decoderDescription = new()
            {
                Profile = VideoInterop.HevcMainProfile,
                SampleWidth = checked((uint)width),
                SampleHeight = checked((uint)height),
                OutputFormat = VideoInterop.DxgiFormatNv12,
            };

            if (!TryChooseConfiguration(videoDevice, in decoderDescription, out VideoInterop.VideoDecoderConfiguration configuration))
            {
                return null;
            }

            delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, VideoInterop.VideoDecoderConfiguration*, nint*, int>
                createDecoder = (delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, VideoInterop.VideoDecoderConfiguration*, nint*, int>)
                    VideoInterop.Method(videoDevice, 3);
            int decoderResult = createDecoder(videoDevice, &decoderDescription, &configuration, &decoder);
            if (decoderResult < 0 || decoder == 0)
            {
                return null;
            }

            VideoInterop.Texture2DDescription textureDescription = new()
            {
                Width = checked((uint)width),
                Height = checked((uint)height),
                MipLevels = 1,
                ArraySize = checked((uint)surfaceCount),
                Format = VideoInterop.DxgiFormatNv12,
                SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
                Usage = 0,
                BindFlags = VideoInterop.D3D11BindDecoder,
            };
            delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int> createTexture =
                (delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int>)
                    VideoInterop.Method(device, 5);
            VideoInterop.ThrowIfFailed(
                createTexture(device, &textureDescription, 0, &outputTexture),
                "ID3D11Device.CreateTexture2D(NV12 decoder surfaces)");

            for (int index = 0; index < outputViews.Length; index++)
            {
                outputViews[index] = CreateOutputView(videoDevice, outputTexture, checked((uint)index));
            }

            nint[] createdOutputViews = outputViews;
            outputViews = [];
            D3D11HevcDecoder result = new(
                width,
                height,
                device,
                context,
                videoDevice,
                videoContext,
                decoder,
                outputTexture,
                createdOutputViews);
            device = context = videoDevice = videoContext = decoder = outputTexture = 0;
            return result;
        }
        finally
        {
            for (int index = outputViews.Length - 1; index >= 0; index--)
            {
                VideoInterop.Release(ref outputViews[index]);
            }
            VideoInterop.Release(ref outputTexture);
            VideoInterop.Release(ref decoder);
            VideoInterop.Release(ref videoContext);
            VideoInterop.Release(ref videoDevice);
            VideoInterop.Release(ref context);
            VideoInterop.Release(ref device);
        }
    }

    public void Decode(HevcDecodeSubmission submission, int outputIndex)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ObjectDisposedException.ThrowIf(decoder == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)outputIndex, (uint)outputViews.Length);
        if (submission.PictureParameters.IsEmpty || submission.SliceControl.IsEmpty || submission.Bitstream.IsEmpty)
        {
            throw new ArgumentException("Picture parameters, slice control and bitstream data are required.", nameof(submission));
        }

        delegate* unmanaged[Stdcall]<nint, nint, nint, uint, nint, int> beginFrame =
            (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, nint, int>)VideoInterop.Method(videoContext, 9);
        delegate* unmanaged[Stdcall]<nint, nint, int> endFrame =
            (delegate* unmanaged[Stdcall]<nint, nint, int>)VideoInterop.Method(videoContext, 10);
        delegate* unmanaged[Stdcall]<nint, nint, uint, VideoInterop.DecoderBufferDescription*, int> submitBuffers =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, VideoInterop.DecoderBufferDescription*, int>)
                VideoInterop.Method(videoContext, 11);

        int beginResult;
        int retries = 0;
        do
        {
            beginResult = beginFrame(videoContext, decoder, outputViews[outputIndex], 0, 0);
            if (beginResult == unchecked((int)0x8000000A))
            {
                Thread.Sleep(1);
            }
        }
        while (beginResult == unchecked((int)0x8000000A) && ++retries < 50);
        VideoInterop.ThrowIfFailed(beginResult, "ID3D11VideoContext.DecoderBeginFrame");
        try
        {
            Span<VideoInterop.DecoderBufferDescription> descriptions = stackalloc VideoInterop.DecoderBufferDescription[4];
            int count = 0;
            descriptions[count++] = UploadBuffer(PictureParameters, submission.PictureParameters.Span);
            if (!submission.InverseQuantizationMatrix.IsEmpty)
            {
                descriptions[count++] = UploadBuffer(InverseQuantizationMatrix, submission.InverseQuantizationMatrix.Span);
            }

            descriptions[count++] = UploadBuffer(SliceControl, submission.SliceControl.Span);
            descriptions[count++] = UploadBuffer(Bitstream, submission.Bitstream.Span);
            fixed (VideoInterop.DecoderBufferDescription* descriptionPointer = descriptions)
            {
                VideoInterop.ThrowIfFailed(
                    submitBuffers(videoContext, decoder, checked((uint)count), descriptionPointer),
                    "ID3D11VideoContext.SubmitDecoderBuffers");
            }
        }
        finally
        {
            VideoInterop.ThrowIfFailed(
                endFrame(videoContext, decoder),
                "ID3D11VideoContext.DecoderEndFrame");
        }
    }

    public nint GetOutputView(int outputIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)outputIndex, (uint)outputViews.Length);
        return outputViews[outputIndex];
    }

    /// <summary>
    /// Copies one decoded NV12 surface to tightly packed CPU memory. The live presenter never uses
    /// this path; it exists so recorded-stream diagnostics can compare Orchard's hardware output
    /// byte-for-byte with an independent decoder.
    /// </summary>
    public byte[] ReadOutputNv12(int outputIndex)
    {
        ObjectDisposedException.ThrowIf(decoder == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)outputIndex, (uint)outputViews.Length);

        nint stagingTexture = 0;
        try
        {
            VideoInterop.Texture2DDescription description = new()
            {
                Width = checked((uint)Width),
                Height = checked((uint)Height),
                MipLevels = 1,
                ArraySize = 1,
                Format = VideoInterop.DxgiFormatNv12,
                SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
                Usage = VideoInterop.D3D11UsageStaging,
                CpuAccessFlags = VideoInterop.D3D11CpuAccessRead,
            };
            delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int> createTexture =
                (delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int>)
                    VideoInterop.Method(device, 5);
            VideoInterop.ThrowIfFailed(
                createTexture(device, &description, 0, &stagingTexture),
                "ID3D11Device.CreateTexture2D(NV12 staging readback)");

            delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void> copySubresource =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)
                    VideoInterop.Method(context, 46);
            copySubresource(context, stagingTexture, 0, 0, 0, 0, outputTexture, checked((uint)outputIndex), 0);

            VideoInterop.MappedSubresource mapped;
            delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int> map =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int>)
                    VideoInterop.Method(context, 14);
            delegate* unmanaged[Stdcall]<nint, nint, uint, void> unmap =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)VideoInterop.Method(context, 15);
            VideoInterop.ThrowIfFailed(
                map(context, stagingTexture, 0, VideoInterop.D3D11MapRead, 0, &mapped),
                "ID3D11DeviceContext.Map(NV12 staging readback)");
            try
            {
                int lumaBytes = checked(Width * Height);
                byte[] result = new byte[checked(lumaBytes + (lumaBytes / 2))];
                byte* source = (byte*)mapped.Data;
                for (int row = 0; row < Height; row++)
                {
                    new ReadOnlySpan<byte>(source + (row * mapped.RowPitch), Width)
                        .CopyTo(result.AsSpan(row * Width, Width));
                }

                byte* chroma = source + (Height * mapped.RowPitch);
                for (int row = 0; row < Height / 2; row++)
                {
                    new ReadOnlySpan<byte>(chroma + (row * mapped.RowPitch), Width)
                        .CopyTo(result.AsSpan(lumaBytes + (row * Width), Width));
                }

                return result;
            }
            finally
            {
                unmap(context, stagingTexture, 0);
            }
        }
        finally
        {
            VideoInterop.Release(ref stagingTexture);
        }
    }

    /// <summary>
    /// Brightest luma sample in the decoded picture, over a sparse grid.
    /// </summary>
    /// <param name="outputIndex">The decoder surface holding the picture.</param>
    /// <returns>0 to 255, or -1 when the surface could not be read.</returns>
    /// <remarks>
    /// <para>This exists to tell an iPhone whose display has been switched off from one that merely
    /// has something dark on it. Measured against the device, an off display decodes to a peak of
    /// about 5 while a lit lock screen peaks at 255, so the two do not overlap.</para>
    /// <para>Peak rather than mean, deliberately: a dark-themed app is mostly black but its text
    /// and icons still reach full white, so its mean is low while its peak is not. Averaging would
    /// call that screen off and press Home under someone who was reading.</para>
    /// <para>Only NV12's first plane is touched, which is luma, and only every fourth pixel of
    /// every fourth row. The staging texture is kept between calls because the alternative is
    /// creating and destroying a full-frame surface several times a second.</para>
    /// </remarks>
    public int ReadOutputPeakLuma(int outputIndex)
    {
        ObjectDisposedException.ThrowIf(decoder == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)outputIndex, (uint)outputViews.Length);

        if (lumaStagingTexture == 0)
        {
            VideoInterop.Texture2DDescription description = new()
            {
                Width = checked((uint)Width),
                Height = checked((uint)Height),
                MipLevels = 1,
                ArraySize = 1,
                Format = VideoInterop.DxgiFormatNv12,
                SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
                Usage = VideoInterop.D3D11UsageStaging,
                CpuAccessFlags = VideoInterop.D3D11CpuAccessRead,
            };
            delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int> createTexture =
                (delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int>)
                    VideoInterop.Method(device, 5);
            nint created = 0;
            if (createTexture(device, &description, 0, &created) < 0)
            {
                return -1;
            }

            lumaStagingTexture = created;
        }

        delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void> copySubresource =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)
                VideoInterop.Method(context, 46);
        copySubresource(context, lumaStagingTexture, 0, 0, 0, 0, outputTexture, checked((uint)outputIndex), 0);

        VideoInterop.MappedSubresource mapped;
        delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int> map =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int>)
                VideoInterop.Method(context, 14);
        delegate* unmanaged[Stdcall]<nint, nint, uint, void> unmap =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)VideoInterop.Method(context, 15);
        if (map(context, lumaStagingTexture, 0, VideoInterop.D3D11MapRead, 0, &mapped) < 0)
        {
            return -1;
        }

        try
        {
            byte* source = (byte*)mapped.Data;
            int peak = 0;
            for (int row = 0; row < Height; row += 4)
            {
                byte* line = source + (row * mapped.RowPitch);
                for (int column = 0; column < Width; column += 4)
                {
                    if (line[column] > peak)
                    {
                        peak = line[column];
                    }
                }
            }

            return peak;
        }
        finally
        {
            unmap(context, lumaStagingTexture, 0);
        }
    }

    public void Dispose()
    {
        for (int index = outputViews.Length - 1; index >= 0; index--)
        {
            VideoInterop.Release(ref outputViews[index]);
        }
        VideoInterop.Release(ref lumaStagingTexture);
        VideoInterop.Release(ref outputTexture);
        VideoInterop.Release(ref decoder);
        VideoInterop.Release(ref videoContext);
        VideoInterop.Release(ref videoDevice);
        VideoInterop.Release(ref context);
        VideoInterop.Release(ref device);
    }

    private static bool SupportsHevcMainNv12(nint videoDevice)
    {
        delegate* unmanaged[Stdcall]<nint, uint> profileCount =
            (delegate* unmanaged[Stdcall]<nint, uint>)VideoInterop.Method(videoDevice, 11);
        delegate* unmanaged[Stdcall]<nint, uint, Guid*, int> profileAt =
            (delegate* unmanaged[Stdcall]<nint, uint, Guid*, int>)VideoInterop.Method(videoDevice, 12);
        bool profileFound = false;
        uint count = profileCount(videoDevice);
        for (uint index = 0; index < count; index++)
        {
            Guid profile;
            if (profileAt(videoDevice, index, &profile) >= 0 && profile == VideoInterop.HevcMainProfile)
            {
                profileFound = true;
                break;
            }
        }

        if (!profileFound)
        {
            return false;
        }

        delegate* unmanaged[Stdcall]<nint, Guid*, int, int*, int> checkFormat =
            (delegate* unmanaged[Stdcall]<nint, Guid*, int, int*, int>)VideoInterop.Method(videoDevice, 13);
        Guid hevcMain = VideoInterop.HevcMainProfile;
        int supported = 0;
        return checkFormat(videoDevice, &hevcMain, VideoInterop.DxgiFormatNv12, &supported) >= 0 && supported != 0;
    }

    private static bool TryChooseConfiguration(
        nint videoDevice,
        in VideoInterop.VideoDecoderDescription description,
        out VideoInterop.VideoDecoderConfiguration configuration)
    {
        configuration = default;
        delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, uint*, int> getCount =
            (delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, uint*, int>)
                VideoInterop.Method(videoDevice, 14);
        delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, uint, VideoInterop.VideoDecoderConfiguration*, int>
            getConfiguration = (delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoDecoderDescription*, uint, VideoInterop.VideoDecoderConfiguration*, int>)
                VideoInterop.Method(videoDevice, 15);

        fixed (VideoInterop.VideoDecoderDescription* descriptionPointer = &description)
        {
            uint count = 0;
            if (getCount(videoDevice, descriptionPointer, &count) < 0)
            {
                return false;
            }

            for (uint index = 0; index < count; index++)
            {
                VideoInterop.VideoDecoderConfiguration candidate;
                if (getConfiguration(videoDevice, descriptionPointer, index, &candidate) >= 0 &&
                    candidate.BitstreamRaw == 1)
                {
                    configuration = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static nint CreateOutputView(nint videoDevice, nint texture, uint arraySlice)
    {
        VideoInterop.DecoderOutputViewDescription description = new()
        {
            Profile = VideoInterop.HevcMainProfile,
            ViewDimension = 1,
            ArraySlice = arraySlice,
        };
        nint view = 0;
        delegate* unmanaged[Stdcall]<nint, nint, VideoInterop.DecoderOutputViewDescription*, nint*, int> createView =
            (delegate* unmanaged[Stdcall]<nint, nint, VideoInterop.DecoderOutputViewDescription*, nint*, int>)
                VideoInterop.Method(videoDevice, 7);
        VideoInterop.ThrowIfFailed(
            createView(videoDevice, texture, &description, &view),
            $"ID3D11VideoDevice.CreateVideoDecoderOutputView(slice {arraySlice})");
        return view;
    }

    private VideoInterop.DecoderBufferDescription UploadBuffer(int type, ReadOnlySpan<byte> source)
    {
        delegate* unmanaged[Stdcall]<nint, nint, int, uint*, nint*, int> getBuffer =
            (delegate* unmanaged[Stdcall]<nint, nint, int, uint*, nint*, int>)VideoInterop.Method(videoContext, 7);
        delegate* unmanaged[Stdcall]<nint, nint, int, int> releaseBuffer =
            (delegate* unmanaged[Stdcall]<nint, nint, int, int>)VideoInterop.Method(videoContext, 8);

        uint capacity = 0;
        nint destination = 0;
        VideoInterop.ThrowIfFailed(
            getBuffer(videoContext, decoder, type, &capacity, &destination),
            $"ID3D11VideoContext.GetDecoderBuffer(type {type})");
        try
        {
            if (source.Length > capacity)
            {
                throw new VideoHardwareException(
                    $"Decoder buffer type {type} has {capacity} bytes but the picture needs {source.Length}",
                    unchecked((int)0x8007007A));
            }

            source.CopyTo(new Span<byte>((void*)destination, source.Length));
        }
        finally
        {
            VideoInterop.ThrowIfFailed(
                releaseBuffer(videoContext, decoder, type),
                $"ID3D11VideoContext.ReleaseDecoderBuffer(type {type})");
        }

        return new VideoInterop.DecoderBufferDescription
        {
            BufferType = type,
            DataSize = checked((uint)source.Length),
        };
    }
}
