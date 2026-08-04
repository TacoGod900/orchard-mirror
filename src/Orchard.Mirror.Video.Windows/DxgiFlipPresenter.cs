namespace Orchard.Mirror.Video.Windows;

/// <summary>
/// A waitable two-buffer flip-model swap chain attached to an HWND. Decoder surfaces remain NV12;
/// D3D11's hardware video processor converts the selected surface directly into the BGRA back
/// buffer, with no CPU readback or Store codec involved.
/// </summary>
public sealed unsafe class DxgiFlipPresenter : IDisposable
{
    private readonly D3D11HevcDecoder decoder;
    private nint presentationTexture;
    private nint presentationInputView;
    private nint readbackTexture;
    private nint videoProcessorEnumerator;
    private nint videoProcessor;
    private nint swapChain;
    private nint frameLatencyWaitableObject;
    private int preparedSurfaceIndex = -1;

    private DxgiFlipPresenter(
        D3D11HevcDecoder decoder,
        nint videoProcessorEnumerator,
        nint videoProcessor,
        nint presentationTexture,
        nint presentationInputView,
        nint readbackTexture,
        nint swapChain,
        nint frameLatencyWaitableObject,
        int outputWidth,
        int outputHeight,
        int sourceWidth,
        int sourceHeight)
    {
        this.decoder = decoder;
        this.videoProcessorEnumerator = videoProcessorEnumerator;
        this.videoProcessor = videoProcessor;
        this.presentationTexture = presentationTexture;
        this.presentationInputView = presentationInputView;
        this.readbackTexture = readbackTexture;
        this.swapChain = swapChain;
        this.frameLatencyWaitableObject = frameLatencyWaitableObject;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>
    /// The handle remains owned by DXGI and is valid until this presenter is disposed. It must not
    /// be closed by the caller.
    /// </summary>
    public nint FrameLatencyWaitableObject => frameLatencyWaitableObject;

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>
    /// The width of the decoded frame that actually carries screen pixels. CoreDevice pads the
    /// encoded picture up to a coding-block multiple, so the surplus columns must be cropped away
    /// instead of being scaled into the window as a black margin.
    /// </summary>
    public int SourceWidth { get; }

    /// <summary>The height of the decoded frame that actually carries screen pixels.</summary>
    public int SourceHeight { get; }

    /// <summary>
    /// The packed <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> this presenter told the driver its input
    /// uses. It belongs to the stream, so a stream that changes its colour needs a new presenter.
    /// </summary>
    public uint ColourSpace { get; private init; }

    /// <summary>
    /// Enqueues a GPU-side snapshot of the just-decoded NV12 surface. D3D11 command ordering keeps
    /// decode, copy, video processing, and presentation in sequence without a CPU readback fence.
    /// </summary>
    public void PrepareDecodedSurface(int decoderSurfaceIndex)
    {
        ObjectDisposedException.ThrowIf(swapChain == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)decoderSurfaceIndex, (uint)decoder.SurfaceCount);

        delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void> copySubresource =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)
                VideoInterop.Method(decoder.Context, 46);
        copySubresource(
            decoder.Context,
            presentationTexture,
            0,
            0,
            0,
            0,
            decoder.OutputTexture,
            checked((uint)decoderSurfaceIndex),
            0);
        preparedSurfaceIndex = decoderSurfaceIndex;
    }

    public static DxgiFlipPresenter? TryCreate(nint windowHandle, D3D11HevcDecoder decoder) =>
        TryCreate(windowHandle, decoder, decoder.Width, decoder.Height);

    /// <summary>
    /// Creates a presenter for a decoded stream, taking the crop and the colour signalling from the
    /// submission that produced it rather than assuming either.
    /// </summary>
    public static DxgiFlipPresenter? TryCreate(
        nint windowHandle,
        D3D11HevcDecoder decoder,
        int outputWidth,
        int outputHeight,
        HevcDecodeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return TryCreate(
            windowHandle,
            decoder,
            outputWidth,
            outputHeight,
            sourceWidth: 0,
            sourceHeight: 0,
            submission.VideoFullRange,
            submission.MatrixCoefficients);
    }

    /// <param name="windowHandle">The HWND the swap chain presents into.</param>
    /// <param name="decoder">The decoder owning the NV12 surfaces to present.</param>
    /// <param name="outputWidth">Back-buffer width, matching the target window's client area.</param>
    /// <param name="outputHeight">Back-buffer height, matching the target window's client area.</param>
    /// <param name="sourceWidth">
    /// Width of the decoded region to scale into the window, or 0 for the whole decoded frame.
    /// </param>
    /// <param name="sourceHeight">
    /// Height of the decoded region to scale into the window, or 0 for the whole decoded frame.
    /// </param>
    /// <param name="videoFullRange">
    /// The stream's <c>video_full_range_flag</c>. False — the value H.265 infers when a stream says
    /// nothing — means the luma spans 16..235 and must be stretched to fill the display.
    /// </param>
    /// <param name="matrixCoefficients">The stream's <c>matrix_coeffs</c> (H.265 Table E.5).</param>
    public static DxgiFlipPresenter? TryCreate(
        nint windowHandle,
        D3D11HevcDecoder decoder,
        int outputWidth,
        int outputHeight,
        int sourceWidth = 0,
        int sourceHeight = 0,
        bool videoFullRange = false,
        int matrixCoefficients = 2)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceHeight);
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        // Padding never exceeds one coding tree unit per axis. A crop outside that window describes
        // some other stream — a stale display report, or a rotation the decoder has not caught up
        // with — and cropping to it would zoom the picture, so present the whole frame instead.
        int cropWidth = IsPlausibleCrop(sourceWidth, decoder.Width) ? sourceWidth : decoder.Width;
        int cropHeight = IsPlausibleCrop(sourceHeight, decoder.Height) ? sourceHeight : decoder.Height;

        nint processorEnumerator = 0;
        nint processor = 0;
        nint presentationTexture = 0;
        nint presentationInputView = 0;
        nint readbackTexture = 0;
        nint factory = 0;
        nint swapChain1 = 0;
        nint swapChain2 = 0;
        try
        {
            VideoInterop.VideoProcessorContentDescription contentDescription = new()
            {
                InputFrameFormat = 0,
                InputFrameRate = new VideoInterop.Rational { Numerator = 60, Denominator = 1 },
                InputWidth = checked((uint)decoder.Width),
                InputHeight = checked((uint)decoder.Height),
                OutputFrameRate = new VideoInterop.Rational { Numerator = 60, Denominator = 1 },
                OutputWidth = checked((uint)outputWidth),
                OutputHeight = checked((uint)outputHeight),
                Usage = 1,
            };
            delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoProcessorContentDescription*, nint*, int>
                createEnumerator =
                    (delegate* unmanaged[Stdcall]<nint, VideoInterop.VideoProcessorContentDescription*, nint*, int>)
                        VideoInterop.Method(decoder.VideoDevice, 10);
            if (createEnumerator(decoder.VideoDevice, &contentDescription, &processorEnumerator) < 0 ||
                processorEnumerator == 0)
            {
                return null;
            }

            if (!SupportsFormat(processorEnumerator, VideoInterop.DxgiFormatNv12, requiredFlag: 1) ||
                !SupportsFormat(processorEnumerator, VideoInterop.DxgiFormatBgra8Unorm, requiredFlag: 2))
            {
                return null;
            }

            delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int> createProcessor =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)VideoInterop.Method(decoder.VideoDevice, 4);
            if (createProcessor(decoder.VideoDevice, processorEnumerator, 0, &processor) < 0 || processor == 0)
            {
                return null;
            }

            // Say what the stream's colour actually is. Left unsaid, the driver picks — and the one
            // measured here chose to treat studio-range luma as full range, so black reached the
            // display as RGB 16 rather than 0.
            uint streamColourSpace = StreamColourSpace(videoFullRange, matrixCoefficients);
            delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, void> setStreamColorSpace =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, void>)
                    VideoInterop.Method(decoder.VideoContext, 28);
            setStreamColorSpace(decoder.VideoContext, processor, 0, &streamColourSpace);

            // The back buffer is BGRA spanning the whole 0..255 range, with no other field set.
            uint outputColourSpace = 0;
            delegate* unmanaged[Stdcall]<nint, nint, uint*, void> setOutputColorSpace =
                (delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)
                    VideoInterop.Method(decoder.VideoContext, 15);
            setOutputColorSpace(decoder.VideoContext, processor, &outputColourSpace);

            // Scale only the region that carries screen pixels. Without this the encoder's
            // block-alignment padding is stretched into the window as black edges.
            VideoInterop.Rect sourceRectangle = new()
            {
                Left = 0,
                Top = 0,
                Right = cropWidth,
                Bottom = cropHeight,
            };
            delegate* unmanaged[Stdcall]<nint, nint, uint, int, VideoInterop.Rect*, void> setStreamSourceRect =
                (delegate* unmanaged[Stdcall]<nint, nint, uint, int, VideoInterop.Rect*, void>)
                    VideoInterop.Method(decoder.VideoContext, 30);
            setStreamSourceRect(decoder.VideoContext, processor, 0, 1, &sourceRectangle);

            presentationTexture = CreatePresentationTexture(decoder);
            presentationInputView = CreateInputView(decoder, processorEnumerator, presentationTexture, 0);
            readbackTexture = CreateReadbackTexture(decoder, outputWidth, outputHeight);

            Guid factoryIid = VideoInterop.IdxgiFactory2;
            if (VideoInterop.CreateDXGIFactory2(0, in factoryIid, out factory) < 0 || factory == 0)
            {
                return null;
            }

            VideoInterop.SwapChainDescription description = new()
            {
                Width = checked((uint)outputWidth),
                Height = checked((uint)outputHeight),
                Format = VideoInterop.DxgiFormatBgra8Unorm,
                SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
                BufferUsage = VideoInterop.DxgiUsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = 0,
                SwapEffect = VideoInterop.DxgiSwapEffectFlipSequential,
                AlphaMode = 0,
                Flags = VideoInterop.DxgiSwapChainFlagFrameLatencyWaitableObject,
            };
            delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.SwapChainDescription*, nint, nint, nint*, int>
                createSwapChainForWindow =
                    (delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.SwapChainDescription*, nint, nint, nint*, int>)
                        VideoInterop.Method(factory, 15);
            if (createSwapChainForWindow(factory, decoder.Device, windowHandle, &description, 0, 0, &swapChain1) < 0 ||
                swapChain1 == 0)
            {
                return null;
            }

            Guid swapChain2Iid = VideoInterop.IdxgiSwapChain2;
            if (VideoInterop.QueryInterface(swapChain1, in swapChain2Iid, out swapChain2) < 0 || swapChain2 == 0)
            {
                return null;
            }

            delegate* unmanaged[Stdcall]<nint, uint, int> setMaximumFrameLatency =
                (delegate* unmanaged[Stdcall]<nint, uint, int>)VideoInterop.Method(swapChain2, 31);
            if (setMaximumFrameLatency(swapChain2, 1) < 0)
            {
                return null;
            }

            delegate* unmanaged[Stdcall]<nint, nint> getWaitableObject =
                (delegate* unmanaged[Stdcall]<nint, nint>)VideoInterop.Method(swapChain2, 33);
            nint waitableObject = getWaitableObject(swapChain2);
            if (waitableObject == 0)
            {
                return null;
            }

            DxgiFlipPresenter result = new(
                decoder,
                processorEnumerator,
                processor,
                presentationTexture,
                presentationInputView,
                readbackTexture,
                swapChain2,
                waitableObject,
                outputWidth,
                outputHeight,
                cropWidth,
                cropHeight)
            {
                ColourSpace = streamColourSpace,
            };
            processorEnumerator = processor = presentationTexture =
                presentationInputView = readbackTexture = swapChain2 = 0;
            return result;
        }
        finally
        {
            VideoInterop.Release(ref swapChain2);
            VideoInterop.Release(ref swapChain1);
            VideoInterop.Release(ref factory);
            VideoInterop.Release(ref readbackTexture);
            VideoInterop.Release(ref presentationInputView);
            VideoInterop.Release(ref presentationTexture);
            VideoInterop.Release(ref processor);
            VideoInterop.Release(ref processorEnumerator);
        }
    }

    /// <summary>
    /// Waits until DXGI can accept another frame, converts the selected NV12 decode surface into the
    /// current back buffer, and presents it. A timeout returns false so the caller can drop the stale
    /// frame rather than blocking the receive loop.
    /// </summary>
    public bool TryPresent(
        int decoderSurfaceIndex,
        uint timeoutMilliseconds = 100,
        string? diagnosticBgraPath = null,
        Memory<byte> bgraOutput = default)
    {
        ObjectDisposedException.ThrowIf(swapChain == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)decoderSurfaceIndex, (uint)decoder.SurfaceCount);

        if (preparedSurfaceIndex != decoderSurfaceIndex)
        {
            PrepareDecodedSurface(decoderSurfaceIndex);
        }

        uint waitResult = VideoInterop.WaitForSingleObject(frameLatencyWaitableObject, timeoutMilliseconds);
        if (waitResult == VideoInterop.WaitTimeout)
        {
            return false;
        }

        if (waitResult != VideoInterop.WaitObject0)
        {
            throw new VideoHardwareException("Waiting for the DXGI frame-latency object", unchecked((int)0x80004005));
        }

        nint backBuffer = 0;
        nint outputView = 0;
        try
        {
            Guid textureIid = VideoInterop.Id3D11Texture2D;
            delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int> getBuffer =
                (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)VideoInterop.Method(swapChain, 9);
            VideoInterop.ThrowIfFailed(
                getBuffer(swapChain, 0, &textureIid, &backBuffer),
                "IDXGISwapChain.GetBuffer");

            VideoInterop.VideoProcessorOutputViewDescription outputDescription = new()
            {
                ViewDimension = 1,
            };
            delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.VideoProcessorOutputViewDescription*, nint*, int>
                createOutputView =
                    (delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.VideoProcessorOutputViewDescription*, nint*, int>)
                        VideoInterop.Method(decoder.VideoDevice, 9);
            VideoInterop.ThrowIfFailed(
                createOutputView(
                    decoder.VideoDevice,
                    backBuffer,
                    videoProcessorEnumerator,
                    &outputDescription,
                    &outputView),
                "ID3D11VideoDevice.CreateVideoProcessorOutputView");

            VideoInterop.VideoProcessorStream stream = new()
            {
                Enable = 1,
                InputSurface = presentationInputView,
            };
            delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, VideoInterop.VideoProcessorStream*, int>
                videoProcessorBlt =
                    (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, VideoInterop.VideoProcessorStream*, int>)
                        VideoInterop.Method(decoder.VideoContext, 53);
            VideoInterop.ThrowIfFailed(
                videoProcessorBlt(decoder.VideoContext, videoProcessor, outputView, 0, 1, &stream),
                "ID3D11VideoContext.VideoProcessorBlt");

            if (!bgraOutput.IsEmpty)
            {
                ReadBackBufferBgra(backBuffer, bgraOutput.Span);
            }

            if (diagnosticBgraPath is not null)
            {
                CaptureBackBufferBgra(backBuffer, diagnosticBgraPath);
            }
        }
        finally
        {
            VideoInterop.Release(ref outputView);
            VideoInterop.Release(ref backBuffer);
        }

        delegate* unmanaged[Stdcall]<nint, uint, uint, int> present =
            (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)VideoInterop.Method(swapChain, 8);
        VideoInterop.ThrowIfFailed(present(swapChain, 0, 0), "IDXGISwapChain.Present");
        preparedSurfaceIndex = -1;
        return true;
    }

    public void Dispose()
    {
        frameLatencyWaitableObject = 0;
        VideoInterop.Release(ref swapChain);
        VideoInterop.Release(ref readbackTexture);
        VideoInterop.Release(ref presentationInputView);
        VideoInterop.Release(ref presentationTexture);
        VideoInterop.Release(ref videoProcessor);
        VideoInterop.Release(ref videoProcessorEnumerator);
    }

    /// <summary>
    /// A crop is believable only when it lands inside the decoded frame and trims no more than the
    /// encoder's block-alignment padding, which is bounded by one 64-sample coding tree unit.
    /// </summary>
    public static bool IsPlausibleCrop(int requested, int decoded) =>
        requested > 0 && requested <= decoded && decoded - requested < 64;

    /// <summary>
    /// Describes an HEVC stream's colour to Direct3D as a packed
    /// <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c>: one bit each for usage, RGB range, YCbCr matrix
    /// and xvYCC, then two bits of nominal range.
    ///
    /// <para>Told nothing, the driver measured on this machine passed studio-range luma straight
    /// through — black arrived as RGB 16 and white as 235, costing a seventh of the contrast and
    /// laying a grey haze over every black the phone displays. The stream says which range it uses;
    /// this repeats it rather than letting the driver assume.</para>
    /// </summary>
    /// <param name="videoFullRange">The stream's <c>video_full_range_flag</c>.</param>
    /// <param name="matrixCoefficients">The stream's <c>matrix_coeffs</c> (H.265 Table E.5).</param>
    public static uint StreamColourSpace(bool videoFullRange, int matrixCoefficients)
    {
        const uint matrixBt709 = 1 << 2;
        const uint nominalRangeStudio = 1 << 4;
        const uint nominalRangeFull = 2 << 4;

        // Direct3D offers only BT.601 and BT.709, so every other matrix has to land on one of them.
        // 4, 5 and 6 are the BT.601 family; everything else — including "unspecified" — is read as
        // BT.709, because an iPhone screen is HD or larger and nothing there is ever 601.
        bool bt601 = matrixCoefficients is 4 or 5 or 6;
        return (bt601 ? 0 : matrixBt709) | (videoFullRange ? nominalRangeFull : nominalRangeStudio);
    }

    private static bool SupportsFormat(nint enumerator, int format, uint requiredFlag)
    {
        delegate* unmanaged[Stdcall]<nint, int, uint*, int> checkFormat =
            (delegate* unmanaged[Stdcall]<nint, int, uint*, int>)VideoInterop.Method(enumerator, 8);
        uint flags = 0;
        return checkFormat(enumerator, format, &flags) >= 0 && (flags & requiredFlag) == requiredFlag;
    }

    private static nint CreatePresentationTexture(D3D11HevcDecoder decoder)
    {
        VideoInterop.Texture2DDescription description = new()
        {
            Width = checked((uint)decoder.Width),
            Height = checked((uint)decoder.Height),
            MipLevels = 1,
            ArraySize = 1,
            Format = VideoInterop.DxgiFormatNv12,
            SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
            Usage = 0,
            BindFlags = 0,
        };
        nint texture = 0;
        delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int> createTexture =
            (delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int>)
                VideoInterop.Method(decoder.Device, 5);
        VideoInterop.ThrowIfFailed(
            createTexture(decoder.Device, &description, 0, &texture),
            "ID3D11Device.CreateTexture2D(NV12 presentation bridge)");
        return texture;
    }

    private static nint CreateReadbackTexture(
        D3D11HevcDecoder decoder,
        int outputWidth,
        int outputHeight)
    {
        VideoInterop.Texture2DDescription description = new()
        {
            Width = checked((uint)outputWidth),
            Height = checked((uint)outputHeight),
            MipLevels = 1,
            ArraySize = 1,
            Format = VideoInterop.DxgiFormatBgra8Unorm,
            SampleDescription = new VideoInterop.SampleDescription { Count = 1 },
            Usage = VideoInterop.D3D11UsageStaging,
            CpuAccessFlags = VideoInterop.D3D11CpuAccessRead,
        };
        nint texture = 0;
        delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int> createTexture =
            (delegate* unmanaged[Stdcall]<nint, VideoInterop.Texture2DDescription*, nint, nint*, int>)
                VideoInterop.Method(decoder.Device, 5);
        VideoInterop.ThrowIfFailed(
            createTexture(decoder.Device, &description, 0, &texture),
            "ID3D11Device.CreateTexture2D(BGRA presentation readback)");
        return texture;
    }

    private static nint CreateInputView(
        D3D11HevcDecoder decoder,
        nint enumerator,
        nint texture,
        uint arraySlice)
    {
        VideoInterop.VideoProcessorInputViewDescription description = new()
        {
            ViewDimension = 1,
            ArraySlice = arraySlice,
        };
        nint view = 0;
        delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.VideoProcessorInputViewDescription*, nint*, int>
            createInputView =
                (delegate* unmanaged[Stdcall]<nint, nint, nint, VideoInterop.VideoProcessorInputViewDescription*, nint*, int>)
                    VideoInterop.Method(decoder.VideoDevice, 8);
        VideoInterop.ThrowIfFailed(
            createInputView(decoder.VideoDevice, texture, enumerator, &description, &view),
            $"ID3D11VideoDevice.CreateVideoProcessorInputView(slice {arraySlice})");
        return view;
    }

    private void CaptureBackBufferBgra(nint backBuffer, string path)
    {
        byte[] pixels = new byte[checked(OutputWidth * OutputHeight * 4)];
        ReadBackBufferBgra(backBuffer, pixels);
        File.WriteAllBytes(path, pixels);
    }

    private void ReadBackBufferBgra(nint backBuffer, Span<byte> destination)
    {
        int rowBytes = checked(OutputWidth * 4);
        int requiredBytes = checked(rowBytes * OutputHeight);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException($"BGRA output requires at least {requiredBytes} bytes.", nameof(destination));
        }

        delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void> copySubresource =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)
                VideoInterop.Method(decoder.Context, 46);
        copySubresource(decoder.Context, readbackTexture, 0, 0, 0, 0, backBuffer, 0, 0);

        VideoInterop.MappedSubresource mapped;
        delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int> map =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, VideoInterop.MappedSubresource*, int>)
                VideoInterop.Method(decoder.Context, 14);
        delegate* unmanaged[Stdcall]<nint, nint, uint, void> unmap =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)VideoInterop.Method(decoder.Context, 15);
        VideoInterop.ThrowIfFailed(
            map(decoder.Context, readbackTexture, 0, VideoInterop.D3D11MapRead, 0, &mapped),
            "ID3D11DeviceContext.Map(BGRA presentation readback)");
        try
        {
            byte* source = (byte*)mapped.Data;
            for (int row = 0; row < OutputHeight; row++)
            {
                new ReadOnlySpan<byte>(source + (row * mapped.RowPitch), rowBytes)
                    .CopyTo(destination.Slice(row * rowBytes, rowBytes));
            }
        }
        finally
        {
            unmap(decoder.Context, readbackTexture, 0);
        }
    }
}
