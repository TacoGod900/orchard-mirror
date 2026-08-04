using Orchard.Mirror.Agent.Windows;
using Orchard.Mirror.Audio.Windows;
using Orchard.Mirror.Media;
using Orchard.Mirror.Shell;
using Orchard.Mirror.Video.Windows;

namespace Orchard.Mirror.Windows;

/// <summary>One thing the session has to report.</summary>
/// <remarks>
/// This used to be a message plus five parallel booleans, two of which (<c>Asleep</c>/<c>Awake</c>)
/// encoded one tri-state and could both be false, both be set, or disagree with the flag the form
/// kept alongside them. A <see cref="NoticeKind"/> says the same things without any of those
/// combinations existing, and it can be routed — deciding how loudly to say something by inspecting
/// its wording is what left errors reading "All set".
/// </remarks>
/// <param name="Notice">What happened.</param>
internal sealed record MirrorSessionUpdate(MirrorNotice Notice);

/// <summary>Runs the low-latency CoreDevice RTP, DXVA decode, and DXGI presentation pipeline.</summary>
internal sealed class CoreDeviceMirrorSession : IAsyncDisposable
{
    private const int DecoderSurfaceCount = 16;

    private readonly CoreDeviceAgentClient agent;
    private readonly nint window;
    private readonly Action<MirrorSessionUpdate> update;
    private readonly int displayWidth;
    private readonly int displayHeight;
    private long presentationSize;
    private readonly HevcAccessUnitAssembler assembler = new();
    private readonly HevcIdrSubmissionBuilder submissionBuilder = new(DecoderSurfaceCount);
    private readonly LatestFrameBuffer<HevcAccessUnit> frames = new(16);
    private readonly SemaphoreSlim frameAvailable = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private RtpLoopbackReceiver? receiver;
    private Task? receiveTask;
    private Task? decodeTask;
    private Task? lifecycleTask;
    private PairedAudioPlayer? audioPlayer;
    private AacEldAudioCapture? audioCapture;
    private D3D11HevcDecoder? decoder;
    private DxgiFlipPresenter? presenter;
    private int nextSurfaceIndex;
    private long presentedFrames;
    private bool disposed;
    private long lastPacketUtcTicks;
    private long lastPresentedUtcTicks;
    private long lastKeyframeRequestUtcTicks;
    private bool lastReportedLocked;
    private readonly MirrorStreamMonitor monitor = new();
    private long darkSinceUtcTicks;
    private long lastLumaCheckUtcTicks;
    private bool silencedDevice;
    private readonly string? diagnosticBgraPath =
        Environment.GetEnvironmentVariable("ORCHARD_MIRROR_CAPTURE_PRESENTED_BGRA");
    private readonly long diagnosticCaptureFrame = 120;

    internal long PresentedFrameCount => Interlocked.Read(ref presentedFrames);

    /// <param name="agent">The CoreDevice supervisor owning the phone session.</param>
    /// <param name="window">The HWND that DXGI presents the decoded frames into.</param>
    /// <param name="update">Receives status and reconnect requests on a background thread.</param>
    /// <param name="presentationWidth">Initial client width of the presentation window.</param>
    /// <param name="presentationHeight">Initial client height of the presentation window.</param>
    /// <param name="displayWidth">
    /// The iPhone's reported screen width, or 0 when unknown. CoreDevice pads the encoded picture
    /// beyond the screen, so this is what distinguishes real pixels from black padding.
    /// </param>
    /// <param name="displayHeight">The iPhone's reported screen height, or 0 when unknown.</param>
    internal CoreDeviceMirrorSession(
        CoreDeviceAgentClient agent,
        nint window,
        Action<MirrorSessionUpdate> update,
        int presentationWidth,
        int presentationHeight,
        int displayWidth = 0,
        int displayHeight = 0)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.window = window != 0 ? window : throw new ArgumentException("A presentation HWND is required.", nameof(window));
        this.update = update ?? throw new ArgumentNullException(nameof(update));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentationWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentationHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(displayHeight);
        this.displayWidth = displayWidth;
        this.displayHeight = displayHeight;
        presentationSize = Pack(presentationWidth, presentationHeight);
    }

    /// <summary>
    /// Records the window size the next presented frame should fill. The decode thread owns the
    /// presenter, so the swap chain is rebuilt there rather than from the UI thread.
    /// </summary>
    internal void RequestPresentationSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            Interlocked.Exchange(ref presentationSize, Pack(width, height));
        }
    }

    private static long Pack(int width, int height) => ((long)width << 32) | (uint)height;

    private async Task DisposeAudioAsync()
    {
        if (audioPlayer is not null)
        {
            await audioPlayer.DisposeAsync().ConfigureAwait(false);
            audioPlayer = null;
        }

        if (audioCapture is not null)
        {
            await audioCapture.DisposeAsync().ConfigureAwait(false);
            audioCapture = null;
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (receiver is not null)
        {
            throw new InvalidOperationException("The CoreDevice mirror session is already running.");
        }

        receiver = new RtpLoopbackReceiver();
        try
        {
            // Play the phone's audio when this machine can, and fall back to recording the raw
            // access units when ORCHARD_MIRROR_AUDIO_CAPTURE asks for them instead. Only one of the
            // two can own the leg, because only one socket receives it. If neither is possible the
            // leg is not requested at all, so the phone stops encoding a stream nothing consumes.
            string? capturePath = Environment.GetEnvironmentVariable("ORCHARD_MIRROR_AUDIO_CAPTURE");
            audioPlayer = capturePath is null ? PairedAudioPlayer.TryStart() : null;
            audioCapture = audioPlayer is null ? AacEldAudioCapture.TryStart(capturePath) : null;
            int? audioPort = audioPlayer?.Port ?? audioCapture?.Port;
            await agent.StartVideoAsync(
                receiver.Port,
                audioPort,
                pairedAudioEnabled: audioPort is not null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            receiveTask = ReceiveLoopAsync(stop.Token);
            decodeTask = DecodeLoopAsync(stop.Token);
            long nowTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref lastPacketUtcTicks, nowTicks);
            Interlocked.Exchange(ref lastPresentedUtcTicks, nowTicks);
            lifecycleTask = LifecycleLoopAsync(stop.Token);
            update(new MirrorSessionUpdate(new MirrorNotice(NoticeKind.Connected, "Starting the picture")));
        }
        catch
        {
            await DisposeAudioAsync().ConfigureAwait(false);

            receiver.Dispose();
            receiver = null;
            throw;
        }
    }

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.FromMilliseconds(700));

    /// <summary>
    /// Stop the session, giving the phone <paramref name="stopVideoBudget"/> to acknowledge that
    /// its paired media session is over.
    /// </summary>
    /// <remarks>
    /// The budget is a parameter because it is genuinely a different question on different paths.
    /// A pulled cable cannot hear the stop and waiting on it is what froze the window for seconds;
    /// a deliberate disconnect can, and abandoning it there is what leaves the phone's media daemon
    /// holding a session nothing will ever end.
    /// </remarks>
    internal async ValueTask StopAsync(TimeSpan stopVideoBudget)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stop.Cancel();
        receiver?.Dispose();
        frameAvailable.ReleaseSafely();
        await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
        await IgnoreCancellationAsync(decodeTask).ConfigureAwait(false);
        await IgnoreCancellationAsync(lifecycleTask).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource timeout = new(stopVideoBudget);
            await agent.StopVideoAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await DisposeAudioAsync().ConfigureAwait(false);

        presenter?.Dispose();
        decoder?.Dispose();
        frames.Clear();
        frameAvailable.Dispose();
        stop.Dispose();
        receiver = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] datagram = await receiver!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref lastPacketUtcTicks, DateTime.UtcNow.Ticks);
                if (!RtpPacket.TryParse(datagram, out RtpPacket packet))
                {
                    continue;
                }

                HevcAccessUnit? accessUnit = assembler.Push(in packet);
                if (accessUnit is null)
                {
                    continue;
                }

                frames.Enqueue(accessUnit);
                frameAvailable.ReleaseSafely();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            update(new MirrorSessionUpdate(new MirrorNotice(
                NoticeKind.Interrupted,
                "Reconnecting iPhone",
                $"The video stream stopped: {exception.Message}",
                NoticeSeverity.Warning)));
            stop.Cancel();
            frameAvailable.ReleaseSafely();
        }
    }

    private async Task DecodeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await frameAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!frames.TryDequeueOldest(out HevcAccessUnit? accessUnit) || accessUnit is null)
                {
                    continue;
                }

                if (!submissionBuilder.TryBuild(accessUnit, nextSurfaceIndex, out HevcDecodeSubmission? built)
                    || built is null)
                {
                    if (submissionBuilder.RequiresRandomAccessPicture)
                    {
                        await RequestKeyframeIfDueAsync(cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                EnsureVideoObjects(built);
                decoder!.Decode(built, built.OutputSurfaceIndex);
                // Decode every predictive picture to preserve the DPB. When the receiver has
                // already queued something newer, skip only this stale presentation and drain
                // immediately; the coded picture itself is never skipped.
                if (frames.Count == 0)
                {
                    presenter!.PrepareDecodedSurface(built.OutputSurfaceIndex);
                    string? capturePath = presentedFrames + 1 == diagnosticCaptureFrame
                        ? diagnosticBgraPath
                        : null;
                    if (presenter.TryPresent(
                            built.OutputSurfaceIndex,
                            timeoutMilliseconds: 4,
                            diagnosticBgraPath: capturePath))
                    {
                        presentedFrames++;
                        Interlocked.Exchange(ref lastPresentedUtcTicks, DateTime.UtcNow.Ticks);
                        SampleBrightness(decoder!, built.OutputSurfaceIndex);
                        Interlocked.Exchange(ref lastKeyframeRequestUtcTicks, 0);
                        if (presentedFrames == 1)
                        {
                            update(new MirrorSessionUpdate(new MirrorNotice(NoticeKind.FirstFrame, "You're good to go")));
                        }
                        else if (presentedFrames % 300 == 0)
                        {
                            update(new MirrorSessionUpdate(new MirrorNotice(
                                NoticeKind.StreamHealth,
                                $"Live • {presentedFrames} frames • {frames.DroppedCount} stale dropped")));
                        }
                    }
                }

                nextSurfaceIndex = (built.OutputSurfaceIndex + 1) % DecoderSurfaceCount;
                if (frames.Count > 0)
                {
                    frameAvailable.ReleaseSafely();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            update(new MirrorSessionUpdate(new MirrorNotice(
                NoticeKind.Interrupted,
                "Reconnecting iPhone",
                $"The video decoder stopped: {exception.Message}",
                NoticeSeverity.Warning)));
            stop.Cancel();
            receiver?.Dispose();
        }
    }

    private async Task LifecycleLoopAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        bool lockStatePollingSupported = true;
        try
        {
            // A 60 fps stream delivers a packet every ~16 ms, so silence is obvious almost
            // immediately. Polling four times a second is what turns an unplugged cable into a
            // one-second gap instead of the several seconds it used to take to even notice.
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(400));
            bool audioReported = false;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Say once, when it is actually true, that sound is coming through. Silence has too
                // many causes -- nothing playing, no output device, no codec -- for the user to be
                // left guessing which one they have.
                if (!audioReported && audioPlayer is { FramesPlayed: > 25 })
                {
                    audioReported = true;
                    update(new MirrorSessionUpdate(new MirrorNotice(NoticeKind.AudioStarted, "iPhone audio playing")));
                }

                // Muting waits for the phone to actually be making a sound. iOS volume buttons move
                // whichever audio category is live at the time, so pressing them at connect -- when
                // nothing is playing -- walks the ringer down and leaves media untouched, which is
                // exactly what happened when this was done at startup.
                if (!silencedDevice && audioPlayer is { HasHeardAudio: true })
                {
                    silencedDevice = true;
                    await SilenceDeviceAsync(cancellationToken).ConfigureAwait(false);
                }

                if (lockStatePollingSupported)
                {
                    try
                    {
                        using CancellationTokenSource queryTimeout =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        queryTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        System.Text.Json.JsonElement lockState =
                            await agent.GetLockStateAsync(queryTimeout.Token).ConfigureAwait(false);
                        if (lockState.ValueKind == System.Text.Json.JsonValueKind.Object
                            && lockState.TryGetProperty("supported", out System.Text.Json.JsonElement supported)
                            && supported.ValueKind == System.Text.Json.JsonValueKind.False)
                        {
                            // iOS 27 removed getlockstate. Reopening that unsupported
                            // feature every two seconds can reset a Wi-Fi RemotePairing
                            // session, so use the RTP health checks below from now on.
                            lockStatePollingSupported = false;
                        }
                        else
                        {
                            bool locked = CoreDeviceLockState.IsLocked(lockState);
                            if (locked != lastReportedLocked)
                            {
                                lastReportedLocked = locked;
                                // The lock screen is right there in the picture; saying so twice
                                // more is what made a toast feel like an interruption.
                                update(new MirrorSessionUpdate(new MirrorNotice(
                                    NoticeKind.LockChanged,
                                    locked ? "iPhone locked" : "iPhone unlocked",
                                    Severity: NoticeSeverity.Neutral)));
                            }
                        }

                        consecutiveFailures = 0;
                    }
                    catch (Exception exception) when (exception is AgentCommandException or AgentProtocolException or IOException)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= 3)
                        {
                            update(new MirrorSessionUpdate(new MirrorNotice(
                                NoticeKind.Interrupted,
                                "Reconnecting iPhone",
                                $"The CoreDevice connection was lost: {exception.Message}",
                                NoticeSeverity.Warning)));
                            stop.Cancel();
                            receiver?.Dispose();
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        consecutiveFailures++;
                    }
                }

                if (lastReportedLocked)
                {
                    continue;
                }

                DateTime lastPacket = new(Interlocked.Read(ref lastPacketUtcTicks), DateTimeKind.Utc);
                DateTime lastPresented = new(Interlocked.Read(ref lastPresentedUtcTicks), DateTimeKind.Utc);
                TimeSpan packetAge = DateTime.UtcNow - lastPacket;
                TimeSpan frameAge = DateTime.UtcNow - lastPresented;

                // The tunnel is only worth a round trip when its answer changes the decision.
                bool tunnelAlive = !MirrorStreamMonitor.NeedsTunnelProbe(packetAge)
                    || await IsTunnelAliveAsync(cancellationToken).ConfigureAwait(false);
                bool wasAsleep = monitor.IsAsleep;
                long darkSince = Interlocked.Read(ref darkSinceUtcTicks);
                TimeSpan darkFor = darkSince == 0
                    ? TimeSpan.Zero
                    : new TimeSpan(DateTime.UtcNow.Ticks - darkSince);

                // What the phone said about itself, when it is still answering.
                //
                // Reaching this line already means the last lockstate poll came back unlocked: a
                // locked phone takes the `continue` above and never gets here. So when polling is
                // supported, the device has effectively just reported that it is up, and a gap in
                // the stream is a stall rather than a sleep. Where polling is unsupported — iOS 27
                // removed getlockstate — nothing is claimed and the monitor keeps its old fallback.
                bool? deviceAwake = lockStatePollingSupported ? true : null;

                switch (monitor.Evaluate(packetAge, frameAge, tunnelAlive, darkFor, deviceAwake))
                {
                    case MirrorStreamAction.Asleep:
                        if (!wasAsleep)
                        {
                            update(new MirrorSessionUpdate(new MirrorNotice(
                                NoticeKind.Asleep,
                                "iPhone asleep",
                                Severity: NoticeSeverity.Neutral)));
                        }

                        break;

                    case MirrorStreamAction.Woke:
                        // The frame clock restarts here: the gap belongs to the sleep, not a stall.
                        Interlocked.Exchange(ref lastPresentedUtcTicks, DateTime.UtcNow.Ticks);
                        Interlocked.Exchange(ref darkSinceUtcTicks, 0);
                        update(new MirrorSessionUpdate(new MirrorNotice(NoticeKind.Awake, "iPhone awake", Severity: NoticeSeverity.Neutral)));
                        break;

                    case MirrorStreamAction.RequestKeyframe:
                        // RTP is alive, so keep the current media session and request a fresh
                        // random-access picture. A full stop/start takes several seconds and can
                        // wedge DisplayService over Wi-Fi.
                        await RequestKeyframeIfDueAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case MirrorStreamAction.Reconnect:
                        RequestReconnect(
                            tunnelAlive ? "Video recovery timed out" : "The iPhone stream was interrupted");
                        return;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Wake the phone by tapping Home through Indigo HID.
    /// </summary>
    /// <remarks>
    /// The touchscreen is off while the phone sleeps, so a tap in the window cannot reach it and
    /// some hardware button has to stand in for it. The power button is the obvious choice and does
    /// not work: tried against the device, the screen stayed dark until it was woken by hand. Home
    /// does wake it, which matches the earlier finding that Home is what brings this phone back
    /// from a locked screen.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the press; the phone may still have acted.</param>
    internal async Task<bool> WakeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            _ = await agent.PressButtonAsync("home", timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or AgentCommandException
                or AgentProtocolException
                or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Track how long the phone's picture has been dark, sampling a few times a second.
    /// </summary>
    /// <remarks>
    /// The phone keeps streaming while its display is off, so silence never arrives and the picture
    /// itself is the only evidence. Sampling every frame would put a GPU readback on the decode
    /// path for a question that changes on the scale of seconds, so it runs at 4 Hz.
    /// </remarks>
    private void SampleBrightness(D3D11HevcDecoder source, int outputSurfaceIndex)
    {
        long now = DateTime.UtcNow.Ticks;
        if (new TimeSpan(now - Interlocked.Read(ref lastLumaCheckUtcTicks)) < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        Interlocked.Exchange(ref lastLumaCheckUtcTicks, now);
        int peak;
        try
        {
            peak = source.ReadOutputPeakLuma(outputSurfaceIndex);
        }
        catch (Exception)
        {
            // Brightness is a convenience, never a reason to drop a session that is otherwise fine.
            return;
        }

        if (peak < 0)
        {
            return;
        }

        if (peak > MirrorStreamMonitor.DarkPeakLuma)
        {
            Interlocked.Exchange(ref darkSinceUtcTicks, 0);
        }
        else
        {
            Interlocked.CompareExchange(ref darkSinceUtcTicks, now, 0);
        }
    }

    /// <summary>Walk the phone's volume down to silence, best effort.</summary>
    /// <remarks>
    /// Failing to mute is not worth losing a session over: the user gets an echo, which they can
    /// fix with the phone's own buttons, rather than no mirror at all.
    /// </remarks>
    private async Task SilenceDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            _ = await agent.SilenceAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or AgentCommandException
                or AgentProtocolException
                or IOException)
        {
        }
    }

    /// <summary>Is the CoreDevice tunnel still usable, whatever the stream is doing?</summary>
    private async Task<bool> IsTunnelAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            _ = await agent.GetDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or AgentCommandException
                or AgentProtocolException
                or IOException)
        {
            return false;
        }
    }

    private async Task RequestKeyframeIfDueAsync(CancellationToken cancellationToken)
    {
        long now = DateTime.UtcNow.Ticks;
        long previous = Interlocked.Read(ref lastKeyframeRequestUtcTicks);
        if (new TimeSpan(now - previous) < TimeSpan.FromMilliseconds(650))
        {
            return;
        }

        Interlocked.Exchange(ref lastKeyframeRequestUtcTicks, now);
        try
        {
            _ = await agent.RequestKeyframeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCommandException exception) when (exception.Code == "feedback-unavailable")
        {
            // Older devices may not negotiate RTCP feedback. The bounded
            // presentation watchdog will reconnect the complete session.
        }
        catch (Exception exception) when (exception is AgentCommandException or AgentProtocolException or IOException)
        {
            // A failed feedback request must not fault this task and leave a
            // permanently frozen UI. The lifecycle watchdog owns recovery.
        }
    }

    private void RequestReconnect(string message)
    {
        update(new MirrorSessionUpdate(new MirrorNotice(
            NoticeKind.Interrupted,
            "Reconnecting iPhone",
            message,
            NoticeSeverity.Warning)));
        stop.Cancel();
        receiver?.Dispose();
    }

    private void EnsureVideoObjects(HevcDecodeSubmission submission)
    {
        long requested = Interlocked.Read(ref presentationSize);
        int width = (int)(requested >> 32);
        int height = (int)(uint)requested;
        bool decoderMatches =
            decoder is not null && decoder.Width == submission.Width && decoder.Height == submission.Height;
        bool presenterMatches =
            presenter is not null && presenter.OutputWidth == width && presenter.OutputHeight == height &&
            presenter.ColourSpace ==
                DxgiFlipPresenter.StreamColourSpace(submission.VideoFullRange, submission.MatrixCoefficients);
        if (decoderMatches && presenterMatches)
        {
            return;
        }

        presenter?.Dispose();
        presenter = null;
        if (!decoderMatches)
        {
            decoder?.Dispose();
            decoder = D3D11HevcDecoder.TryCreate(submission.Width, submission.Height, DecoderSurfaceCount)
                ?? throw new InvalidOperationException(
                    $"No D3D11 HEVC Main decoder can output {submission.Width}×{submission.Height} NV12 frames.");
        }

        presenter = DxgiFlipPresenter.TryCreate(
            window,
            decoder!,
            width,
            height,
            displayWidth,
            displayHeight,
            submission.VideoFullRange,
            submission.MatrixCoefficients)
            ?? throw new InvalidOperationException("DXGI could not create the waitable flip presentation path.");
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

internal static class SemaphoreSlimExtensions
{
    internal static void ReleaseSafely(this SemaphoreSlim semaphore)
    {
        try
        {
            if (semaphore.CurrentCount == 0)
            {
                semaphore.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
