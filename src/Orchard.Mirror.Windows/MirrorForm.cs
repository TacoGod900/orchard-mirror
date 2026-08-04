using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Orchard.Mirror.Agent.Windows;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Windows;

internal sealed class MirrorForm : Form
{
    private const int DropShadowClassStyle = 0x00020000;

    /// <summary>
    /// Width divided by height of the mirrored screen. Seeded with the iPhone 16e panel and
    /// replaced by the connected phone's reported resolution so nothing is ever stretched.
    /// </summary>
    private static double phoneAspectRatio = 1170.0 / 2532.0;
    private static readonly Color CanvasColor = Color.FromArgb(8, 10, 14);

    private readonly FramePanel videoHost = new();
    private readonly NativeVideoSurface videoSurface = new();
    private readonly OnboardingView overlay = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly MirrorConnectionMachine connection = new();
    private readonly ControlStripWindow strip;
    private readonly ToastWindow toast;

    /// <summary>
    /// The shape each layer was last given, so an unchanged one is not handed the same region again.
    /// </summary>
    /// <remarks>
    /// Three entries, living exactly as long as the window that owns the controls in it.
    /// </remarks>
    private readonly Dictionary<Control, (Size Size, int Radius)> appliedRegions = [];

    private CoreDeviceAgentClient? coreDeviceAgent;
    private CoreDeviceMirrorSession? coreDeviceMirrorSession;
    private CoreDeviceInputController? coreDeviceInput;
    private int connectionGeneration;
    private bool pictureLive;
    private DateTimeOffset? heldSince;
    private MirrorNotice? heldNotice;
    private CancellationTokenSource? heldFrameTimer;
    private int wakeInFlight;
    private CancellationTokenSource? retryTimer;
    private bool teardownComplete;

    /// <summary>
    /// Serializes the lifecycle work the machine asks for, so a teardown always finishes before the
    /// attempt that follows it starts. The commands themselves are ordered; without this the delay
    /// before a retry could be shorter than the teardown it is meant to follow.
    /// </summary>
    private Task lifecycleChain = Task.CompletedTask;

    /// <summary>The phone's screen is off, so a click cannot reach it as a touch.</summary>
    private bool PhoneAsleep => connection.State == ConnectionState.Asleep;

    /// <summary>
    /// What is on the mirror surface right now, which decides how loudly anything may be said.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so the held frame cannot outlive its own grace period through a
    /// path that forgot to clear a flag. The two fields it reads are each written from one place.
    /// </remarks>
    private PictureState Picture
    {
        get
        {
            if (pictureLive)
            {
                return PictureState.Live;
            }

            return heldSince is { } since && HeldFrame.StillBelievable(DateTimeOffset.UtcNow - since)
                ? PictureState.Held
                : PictureState.None;
        }
    }

    /// <summary>The window is going away; nothing may start anything new.</summary>
    private bool IsShuttingDown =>
        connection.State == ConnectionState.ShuttingDown || lifetime.IsCancellationRequested;

    public MirrorForm()
    {
        Text = "Orchard Mirror";
        BackColor = CanvasColor;
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(277, 600);
        Size = new Size(390, 844);
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            workingArea.Right - Width - 24,
            workingArea.Bottom - Height - 24);

        strip = new ControlStripWindow(this) { Font = new Font("Segoe UI", 8.5f) };
        strip.DisconnectRequested += (_, _) => Disconnect();
        strip.PinChanged += (_, wanted) => TopMost = wanted;

        toast = new ToastWindow(this) { Font = new Font("Segoe UI", 8.5f) };

        BuildInterface();

        Shown += (_, _) =>
        {
            // A first run is instructions, not a connection attempt: the phone almost certainly is
            // not in a state to be found yet, and failing at it teaches the user nothing. Every
            // later run goes straight for the phone. Which of those happens is the machine's
            // decision, so that the walkthrough can only ever be reached from a cold start.
            Dispatch(new ConnectionEvent(ConnectionTrigger.Shown, OnboardingState.IsComplete()));
        };
        FormClosing += async (_, eventArgs) =>
        {
            if (teardownComplete)
            {
                return;
            }

            // Cancel the close and finish letting go of the phone first, then close for real.
            //
            // This replaces an `async void` FormClosed handler, which returned to WinForms at its
            // first suspending await -- releasing the held touch -- so everything after that point,
            // including the stop-video that ends the phone's media session and the agent shutdown,
            // was a continuation posted to a message pump that had already stopped. It only ever
            // appeared to work because the agent notices its stdin close and tears itself down.
            //
            // Cancelling keeps the pump running, which is the one thing that makes the rest of the
            // teardown reachable at all.
            eventArgs.Cancel = true;
            Dispatch(new ConnectionEvent(ConnectionTrigger.WindowClosing));
            Show(new MirrorNotice(
                NoticeKind.UserDisconnected,
                "Disconnecting",
                Severity: NoticeSeverity.Progress));

            await ShutdownAsync();
            teardownComplete = true;
            Close();
        };
        FormClosed += (_, _) =>
        {
            connectionGate.Dispose();
            lifetime.Dispose();
        };
        Shown += (_, _) =>
        {
            strip.Attach();
            toast.Attach();
        };
        Move += (_, _) =>
        {
            strip.Reposition();
            toast.Reposition();
        };
        Resize += (_, _) =>
        {
            UpdateWindowRegion();
            strip.Reposition();
            toast.Reposition();
            coreDeviceMirrorSession?.RequestPresentationSize(
                videoHost.ClientSize.Width,
                videoHost.ClientSize.Height);
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Control && eventArgs.Shift && eventArgs.KeyCode == Keys.V)
            {
                // Ctrl+Shift+V, not Ctrl+V: a plain paste is a keystroke the owner may want to send
                // through to an app on the phone, so the PC-side paste takes the shifted chord and
                // leaves the ordinary one to reach the phone untouched.
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
                _ = PasteFromPcAsync();
            }
            else if (eventArgs.KeyCode == Keys.Escape && eventArgs.Shift)
            {
                // Shift-Escape rather than Escape alone: plain Escape is a key the phone should
                // receive, and disconnecting by accident mid-typing would be worse than not having
                // a shortcut at all.
                eventArgs.Handled = true;
                Disconnect();
            }
            else if (eventArgs.KeyCode == Keys.Escape)
            {
                ActiveControl = null;
            }
            else if (coreDeviceInput is not null)
            {
                string? key = eventArgs.KeyCode switch
                {
                    Keys.Back => "backspace",
                    Keys.Tab => "tab",
                    Keys.Left => "left",
                    Keys.Right => "right",
                    Keys.Up => "up",
                    Keys.Down => "down",
                    _ => null,
                };
                if (key is not null)
                {
                    _ = coreDeviceInput.KeyAsync(key);
                    eventArgs.Handled = true;
                    eventArgs.SuppressKeyPress = true;
                }
            }
        };
        KeyPress += (_, eventArgs) =>
        {
            if (coreDeviceInput is null || eventArgs.KeyChar is '\b' or '\t' or (char)27)
            {
                return;
            }

            string text = eventArgs.KeyChar == '\r' ? "\n" : eventArgs.KeyChar.ToString();
            _ = coreDeviceInput.TypeAsync(text);
            eventArgs.Handled = true;
        };
    }

    /// <summary>
    /// Walk the phone through setup: wait for a cable, turn on Developer Mode, ride out the
    /// restart, then connect.
    /// </summary>
    /// <remarks>
    /// <para>The Developer Mode step is done through AMFI, a lockdown service reachable over plain
    /// USB. That matters: it is the only way round a genuine circularity. Driving Settings by hand
    /// would need the screen and the touchscreen, and both of those are developer services, so
    /// neither exists until the thing being enabled is already on.</para>
    /// <para>USB only for the same reason. AMFI is reached over usbmux, and a phone that has never
    /// had Developer Mode turned on has no CoreDevice tunnel to reach it any other way.</para>
    /// </remarks>
    private async Task RunGuidedSetupAsync()
    {
        CoreDeviceAgentClient? agent = null;
        try
        {
            overlay.ShowWalkthrough(OnboardingView.WalkthroughPage.WaitingForCable);
            agent = await CoreDeviceAgentClient.StartAsync(cancellationToken: lifetime.Token);

            JsonElement status = await WaitForCableAsync(agent, lifetime.Token);
            string name = JsonFieldLookup.FindString(status, "deviceName") ?? "iPhone";
            bool developerMode = status.TryGetProperty("developerMode", out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;

            if (!developerMode)
            {
                Show(new MirrorNotice(
                    NoticeKind.SetupStep,
                    "Turning on\nDeveloper Mode",
                    $"Orchard is setting up {name} and will restart it.\n\nLeave the cable in. This takes "
                        + "about a minute."));

                // A reboot sits inside this call, so it gets no deadline of its own beyond the
                // window's lifetime.
                _ = await agent.EnableDeveloperModeAsync(lifetime.Token);
            }

            Show(new MirrorNotice(NoticeKind.SetupStep, "Almost there", "Preparing developer services."));
            OnboardingState.MarkComplete();
        }
        catch (AgentCommandException exception)
        {
            Show(new MirrorNotice(
                NoticeKind.SetupBlocked,
                exception.Code == "passcode-set" ? "Turn off the\npasscode" : "Setup stopped",
                exception.Message,
                NoticeSeverity.Failure,
                exception.Code));
            return;
        }
        catch (Exception exception) when (exception is OperationCanceledException or AgentProtocolException or IOException)
        {
            Show(new MirrorNotice(
                NoticeKind.SetupBlocked,
                "Setup stopped",
                exception.Message,
                NoticeSeverity.Failure));
            return;
        }
        finally
        {
            if (agent is not null)
            {
                await agent.DisposeAsync();
            }
        }

        Dispatch(new ConnectionEvent(ConnectionTrigger.SetupFinished));
    }

    /// <summary>Poll until an iPhone is on the cable, reporting what it is when one appears.</summary>
    private static async Task<JsonElement> WaitForCableAsync(CoreDeviceAgentClient agent, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement status = await agent.GetUsbStatusAsync(cancellationToken);
            if (status.TryGetProperty("present", out JsonElement present) && present.ValueKind == JsonValueKind.True)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        }
    }

    /// <summary>How long one whole connection attempt may take before it is abandoned.</summary>
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(25);

    private async Task<bool> StartCoreDeviceAsync()
    {
        CoreDeviceAgentClient? candidateAgent = null;
        CoreDeviceMirrorSession? candidateSession = null;
        CoreDeviceInputController? candidateInput = null;
        int generation = Interlocked.Increment(ref connectionGeneration);
        using CancellationTokenSource attempt =
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        // Every phase below is bounded well inside this. A whole attempt that overruns is stuck,
        // and abandoning it to start a fresh one recovers sooner than waiting it out.
        attempt.CancelAfter(AttemptBudget);

        MirrorLog.Write("attempt", $"#{generation} starting (budget {AttemptBudget.TotalSeconds:N0}s)");
        Show(new MirrorNotice(
            NoticeKind.Connecting,
            "Looking for iPhone",
            "Unlock your paired iPhone\n\nUSB or the same Wi-Fi network can be used."));
        try
        {
            candidateAgent = await CoreDeviceAgentClient.StartAsync(cancellationToken: attempt.Token);
            Show(new MirrorNotice(NoticeKind.Connecting, "Opening secure iPhone tunnel"));
            JsonElement result = await ConnectMountingIfNeededAsync(candidateAgent, attempt.Token);

            string transport = string.Equals(JsonFieldLookup.FindString(result, "transport"), "wifi", StringComparison.OrdinalIgnoreCase)
                ? "Wi-Fi"
                : "USB";

            // The transport, and deliberately nothing else. This label used to read
            // "{deviceName} - {productVersion} - {transport}", was assigned to a Label that was
            // never added to the form, and was therefore never looked at. Putting it on the strip
            // showed what it had always been: the connect reply carries no device name and no OS
            // version anywhere in it -- measured, not assumed. Its `device` object holds
            // snapshotBootState, cpuCount, supportsSiri, hasActionButton and the like, so both
            // lookups had always fallen through to their "iPhone" and "iOS" defaults while the
            // generic `name` search found a display's name and reported the phone as "primary".
            // The transport is real, is in the reply, and is the one thing here the owner could not
            // otherwise find out.
            strip.DeviceSummary = $"Connected over {transport}";
            // "CoreDevice" and "HEVC" are Apple's word and a codec's; neither is the owner's. The
            // strip already reads "Connected over {transport}", so the detail said it a third time.
            Show(new MirrorNotice(NoticeKind.Connected, "iPhone connected"));
            _ = CoreDeviceDisplaySize.TryFind(result, out int displayWidth, out int displayHeight);
            ApplyPhoneAspect(displayWidth, displayHeight);
            candidateSession = new CoreDeviceMirrorSession(
                candidateAgent,
                videoSurface.Handle,
                update => UpdateCoreDeviceSession(generation, update),
                Math.Max(1, videoHost.ClientSize.Width),
                Math.Max(1, videoHost.ClientSize.Height),
                displayWidth,
                displayHeight);
            using CancellationTokenSource mediaTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            mediaTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            await candidateSession.StartAsync(mediaTimeout.Token);
            candidateInput = new CoreDeviceInputController(
                candidateAgent,
                message => UpdateCoreDeviceSession(
                    generation,
                    new MirrorSessionUpdate(new MirrorNotice(
                        NoticeKind.Interrupted,
                        "Reconnecting iPhone",
                        message,
                        NoticeSeverity.Warning))));

            if (IsShuttingDown || generation != Volatile.Read(ref connectionGeneration))
            {
                return false;
            }

            coreDeviceAgent = candidateAgent;
            coreDeviceMirrorSession = candidateSession;
            coreDeviceInput = candidateInput;
            candidateAgent = null;
            candidateSession = null;
            candidateInput = null;
            MirrorLog.Write("attempt", $"#{generation} connected");
            return true;
        }
        catch (AgentCommandException exception)
        {
            MirrorLog.Write("attempt", $"#{generation} failed — {exception.Code}: {exception.Message}");
            Show(new MirrorNotice(
                NoticeKind.AttemptFailed,
                exception.Code switch
                {
                    "no-device" => "Waiting for iPhone",
                    "not-paired" => "Pair iPhone once over USB",
                    "developer-mode-off" => "Developer Mode is off",
                    "ddi-unavailable" => "Developer image is unavailable",
                    _ => "Connection interrupted",
                },
                RetryHint(exception.Message),
                NoticeSeverity.Warning,
                exception.Code));
        }
        catch (OperationCanceledException) when (IsShuttingDown)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            MirrorLog.Write("attempt", $"#{generation} timed out after {AttemptBudget.TotalSeconds:N0}s");
            Show(new MirrorNotice(
                NoticeKind.AttemptFailed,
                "Connection timed out",
                RetryHint("The iPhone did not finish connecting in time."),
                NoticeSeverity.Warning));
        }
        catch (Exception exception)
        {
            MirrorLog.Write("attempt", $"#{generation} failed — {exception.GetType().Name}: {exception.Message}");
            Show(new MirrorNotice(
                NoticeKind.AttemptFailed,
                "iPhone unavailable",
                RetryHint(exception.Message),
                NoticeSeverity.Warning));
        }
        finally
        {
            if (candidateInput is not null)
            {
                await candidateInput.DisposeAsync();
            }

            if (candidateSession is not null)
            {
                await candidateSession.DisposeAsync();
            }

            if (candidateAgent is not null)
            {
                await candidateAgent.DisposeAsync();
            }
        }

        return false;
    }

    /// <summary>
    /// Open the CoreDevice tunnel, mounting the Developer Disk Image only if the phone turns out
    /// to need it.
    /// </summary>
    /// <remarks>
    /// The image is per-boot, so it is usually already mounted and the USB probe is pure latency —
    /// well over a second on every attempt, including the reconnects that follow an unplugged
    /// cable. Connecting first makes the common path fast and still recovers after a reboot,
    /// because a phone without the image reports its CoreDevice services as unavailable.
    /// </remarks>
    private async Task<JsonElement> ConnectMountingIfNeededAsync(
        CoreDeviceAgentClient agent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectOnceAsync(agent, cancellationToken);
        }
        catch (AgentCommandException exception) when (
            exception.Code is "ddi-unavailable" or "no-device" or "tunnel-unavailable")
        {
            Show(new MirrorNotice(NoticeKind.Connecting, "Preparing developer services"));
            using CancellationTokenSource ddiTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ddiTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                _ = await agent.MountDeveloperDiskImageAsync(cancellationToken: ddiTimeout.Token);
            }
            catch (AgentCommandException mountFailure) when (mountFailure.Code == "no-device")
            {
                // No USB endpoint to mount through. Surface the original tunnel failure, which
                // describes what the phone was actually missing.
                throw exception;
            }

            Show(new MirrorNotice(NoticeKind.Connecting, "Opening secure iPhone tunnel"));
            return await ConnectOnceAsync(agent, cancellationToken);
        }
    }

    private static async Task<JsonElement> ConnectOnceAsync(
        CoreDeviceAgentClient agent,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource connectTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await agent.ConnectAsync(cancellationToken: connectTimeout.Token);
    }

    /// <summary>
    /// Match the window to the connected phone's panel so the picture is neither letterboxed nor
    /// stretched. Grows or shrinks height around the existing width and keeps the window on screen.
    /// </summary>
    private void ApplyPhoneAspect(int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return;
        }

        phoneAspectRatio = displayWidth / (double)displayHeight;
        int height = (int)Math.Round(Width / phoneAspectRatio);
        if (height == Height || height <= 0)
        {
            return;
        }

        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        int top = Math.Max(workingArea.Top, Math.Min(Top, workingArea.Bottom - height));
        SetBounds(Left, top, Width, height);
    }

    private static string RetryHint(string detail) =>
        $"{detail}\n\nOrchard will keep retrying automatically. " +
        "If the iPhone restarted, connect USB once to remount developer services.";

    private void UpdateCoreDeviceSession(int generation, MirrorSessionUpdate update)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateCoreDeviceSession(generation, update));
            return;
        }

        if (generation != Volatile.Read(ref connectionGeneration))
        {
            return;
        }

        UpdateCoreDeviceSession(update);
    }

    /// <summary>
    /// Route one notice and show whatever comes back.
    /// </summary>
    /// <remarks>
    /// Every status in this window goes through here. The alternative — which is what this replaces
    /// — was fifteen call sites each assigning a title, a detail and a colour field directly, none
    /// of which agreed about when the panel should be visible, and one of which (the colour) was
    /// never read at all.
    /// </remarks>
    private void Show(MirrorNotice notice)
    {
        StatusPresentation presentation = StatusRouter.Route(
            new RouteInput(
                notice,
                Picture,
                connection.State is ConnectionState.Connecting or ConnectionState.WaitingToRetry));

        // Both surfaces are offered every status and each keeps only its own: the overlay ignores
        // anything that is not full-surface, the toast dismisses anything that is. Deciding here
        // which one to call would put the routing rule in two places and let them disagree, which
        // is the fault StatusRouter was written to end.
        overlay.Present(presentation);
        toast.Present(presentation);
    }

    private void UpdateCoreDeviceSession(MirrorSessionUpdate update)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateCoreDeviceSession(update));
            return;
        }

        MirrorNotice notice = update.Notice;

        // Whether a picture is up decides how loudly anything after this may be said, so it is
        // settled before the notice is routed rather than after. Sleep is no longer tracked here
        // as well: the machine owns it, and two fields that could disagree about the same fact is
        // what this phase exists to remove.
        switch (notice.Kind)
        {
            case NoticeKind.FirstFrame:
                // A frame on screen is the only honest signal that the mirror is up, so the handover
                // is driven by that rather than by a connection succeeding. It is also the only
                // honest moment to record that setup worked -- marking it when the owner pressed
                // "I've done this before" suppressed the walkthrough forever even if the connection
                // that followed immediately failed.
                MarkPictureLive();
                OnboardingState.MarkComplete();
                Dispatch(new ConnectionEvent(ConnectionTrigger.FirstFrame));
                break;

            case NoticeKind.StreamHealth:
                MarkPictureLive();
                break;

            case NoticeKind.Asleep:
                // The screen is off, so there is no picture to protect and nothing to reveal. Not
                // held either: a dark phone is not a frame worth keeping, and the owner turned it
                // off deliberately, so there is nothing to conceal from them.
                ReleasePicture();
                Dispatch(new ConnectionEvent(ConnectionTrigger.PhoneAsleep));
                break;

            case NoticeKind.Awake:
                MarkPictureLive();
                Dispatch(new ConnectionEvent(ConnectionTrigger.PhoneAwake));
                overlay.RevealMirror();
                break;

            case NoticeKind.Interrupted:
                // The stream has stopped, but its last frame is still on the swap chain and still
                // being composited, so the phone is still on screen. Keeping it there for the few
                // hundred milliseconds a retry usually takes is the difference between an
                // intermittent connection and an application that appears to break.
                HoldPicture(notice);

                // Dispatched before the notice is shown, so the router sees a retry in flight and
                // gives it a spinner. A motionless "Reconnecting" is indistinguishable from a hang.
                Dispatch(new ConnectionEvent(ConnectionTrigger.StreamInterrupted));
                break;

            default:
                break;
        }

        Show(notice);
    }

    /// <summary>
    /// Press the phone's power button, once, however many times the user clicks.
    /// </summary>
    /// <remarks>
    /// A sleeping phone invites repeated clicking. The guard drops clicks while a press is
    /// outstanding, and the asleep state clears only when RTP resumes, so a burst of clicks is one
    /// press rather than a queue of them arriving after the phone is already back.
    /// </remarks>
    private async Task WakePhoneAsync()
    {
        CoreDeviceMirrorSession? session = coreDeviceMirrorSession;
        if (session is null || Interlocked.Exchange(ref wakeInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            ShowTransientStatus(new MirrorNotice(
                NoticeKind.Asleep,
                "Waking iPhone",
                Severity: NoticeSeverity.Progress));
            _ = await session.WakeAsync();
        }
        finally
        {
            Interlocked.Exchange(ref wakeInFlight, 0);
        }
    }

    /// <summary>
    /// Type the PC's clipboard into the phone's focused field.
    /// </summary>
    /// <remarks>
    /// <para>The honest half of a shared clipboard. iOS exposes no pasteboard to a host, so reading
    /// what was copied <em>on</em> the phone would need an app on the phone — but sending the PC's
    /// clipboard the other way is only typing, and the phone already accepts typing. So this reads
    /// the Windows clipboard, reduces it to what the virtual keyboard can produce, and sends it as
    /// keystrokes.</para>
    /// <para>The reduction is not cosmetic: the agent's typing throws on the first character it
    /// cannot map, after typing everything before it, so pasting raw clipboard text with a smart
    /// quote in it would land half a line and then fail. <see cref="ClipboardText"/> settles that
    /// before a key is sent, and reports what it had to drop so the confirmation can be truthful.</para>
    /// </remarks>
    private async Task PasteFromPcAsync()
    {
        CoreDeviceInputController? input = coreDeviceInput;
        if (input is null || PhoneAsleep)
        {
            // Nothing to type into: no session, or a dark screen that would swallow the keystrokes.
            return;
        }

        string raw;
        try
        {
            raw = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (ExternalException)
        {
            // The clipboard is momentarily owned by another process. There is nothing to paste this
            // instant, and throwing over a hotkey the owner can simply press again would be worse.
            raw = string.Empty;
        }

        ClipboardPaste paste = ClipboardText.Sanitize(raw);
        if (!paste.HasText)
        {
            ShowTransientStatus(new MirrorNotice(
                NoticeKind.Info, "Nothing to paste", Severity: NoticeSeverity.Neutral));
            return;
        }

        await input.TypeAsync(paste.Text);
        ShowTransientStatus(new MirrorNotice(
            NoticeKind.Info, PasteHeadline(paste), Severity: NoticeSeverity.Neutral));
    }

    /// <summary>Say what happened to a paste, honestly, in one line.</summary>
    private static string PasteHeadline(ClipboardPaste paste)
    {
        if (paste.Truncated)
        {
            return $"Pasted the first {ClipboardText.MaxLength} characters";
        }

        if (paste.Dropped == 1)
        {
            return "Pasted — 1 character couldn't be typed";
        }

        return paste.Dropped > 0
            ? $"Pasted — {paste.Dropped} characters couldn't be typed"
            : "Pasted from PC";
    }

    /// <summary>
    /// Put the phone down: end the session and stop trying to get it back.
    /// </summary>
    /// <remarks>
    /// Distinct from losing the connection, which retries. This is someone standing up to leave, so
    /// a retry already in flight must not quietly pull the phone back a second later — which is
    /// exactly what used to happen, because the loop checked a different flag from the one this set.
    /// </remarks>
    internal void Disconnect()
    {
        // Released rather than held: someone standing up to leave is not waiting for the picture to
        // come back, and holding their last frame would be Orchard showing them a phone it has
        // deliberately let go of.
        ReleasePicture();
        Dispatch(new ConnectionEvent(ConnectionTrigger.UserDisconnect));
        // No detail: the headline says what happened and the Reconnect button says what to do next.
        Show(new MirrorNotice(
            NoticeKind.UserDisconnected,
            "Disconnected",
            Severity: NoticeSeverity.Neutral));
    }

    /// <summary>Feed one thing that happened to the machine, and carry out what it asks for.</summary>
    private void Dispatch(ConnectionEvent connectionEvent)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => Dispatch(connectionEvent));
            return;
        }

        foreach (ConnectionCommand command in connection.Handle(connectionEvent))
        {
            Execute(command);
        }
    }

    private void Execute(ConnectionCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.RunGuidedSetup:
                overlay.ShowWalkthrough(OnboardingView.WalkthroughPage.Welcome);
                break;

            case CommandKind.StartAttempt:
                Chain(AttemptAsync);
                break;

            case CommandKind.TearDown:
                // The machine has already moved to the state that says why, so the budget follows
                // from it: a window closing or a deliberate disconnect can still reach the phone
                // and is worth waiting on; anything else is presumed unreachable.
                Chain(() => TearDownCoreDeviceAsync(connection.State switch
                {
                    ConnectionState.ShuttingDown => TeardownReason.WindowClosing,
                    ConnectionState.UserDisconnected => TeardownReason.UserDisconnect,
                    _ => TeardownReason.StreamInterrupted,
                }));
                break;

            case CommandKind.ScheduleRetry:
                ScheduleRetry(command.Delay);
                break;

            case CommandKind.CancelRetry:
                CancelRetry();
                break;

            default:
                break;
        }
    }

    /// <summary>Queue lifecycle work behind whatever is already running.</summary>
    private void Chain(Func<Task> work) => lifecycleChain = lifecycleChain.ContinueWith(
        _ => work(),
        CancellationToken.None,
        TaskContinuationOptions.None,
        TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();

    private async Task AttemptAsync()
    {
        Show(new MirrorNotice(
            NoticeKind.Connecting,
            "Looking for iPhone",
            "Unlock your paired iPhone\n\nUSB or the same Wi-Fi network can be used."));

        if (!await StartCoreDeviceAsync())
        {
            Dispatch(new ConnectionEvent(ConnectionTrigger.AttemptFailed));
        }
    }

    /// <summary>Frames are flowing. Nothing is being held and nothing is owed an explanation.</summary>
    private void MarkPictureLive()
    {
        pictureLive = true;
        heldSince = null;
        heldNotice = null;
        CancelHeldFrameExpiry();
    }

    /// <summary>Keep the stopped stream's last frame on screen, and start its grace period.</summary>
    private void HoldPicture(MirrorNotice notice)
    {
        pictureLive = false;
        heldNotice = notice;

        // Not restarted if one is already running. A stream that breaks, half-recovers and breaks
        // again inside the same grace would otherwise keep pushing the deadline out, and the owner
        // would sit in front of an increasingly old photograph of their phone with a note over it.
        if (heldSince is not null)
        {
            return;
        }

        heldSince = DateTimeOffset.UtcNow;
        ScheduleHeldFrameExpiry();
    }

    /// <summary>Stop claiming there is a picture. The surface is free for whatever comes next.</summary>
    private void ReleasePicture()
    {
        pictureLive = false;
        heldSince = null;
        heldNotice = null;
        CancelHeldFrameExpiry();
    }

    /// <summary>
    /// Come back when the held frame stops being worth believing.
    /// </summary>
    /// <remarks>
    /// A timer rather than an evaluation at the next status, because the retry ladder goes quiet:
    /// its later waits are four seconds long, so an owner could sit in front of a frozen picture
    /// well past the grace with nothing arriving to notice that it had passed.
    /// </remarks>
    private void ScheduleHeldFrameExpiry()
    {
        CancelHeldFrameExpiry();
        if (IsShuttingDown)
        {
            return;
        }

        CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        heldFrameTimer = timer;
        _ = Task.Delay(HeldFrame.Believable, timer.Token).ContinueWith(
            task =>
            {
                if (!task.IsCanceled)
                {
                    ExpireHeldFrame();
                }

                timer.Dispose();
            },
            TaskScheduler.Default);
    }

    private void CancelHeldFrameExpiry()
    {
        CancellationTokenSource? timer = heldFrameTimer;
        heldFrameTimer = null;
        if (timer is null)
        {
            return;
        }

        try
        {
            timer.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delay already elapsed and disposed it. Nothing to cancel.
        }
    }

    /// <summary>
    /// The grace has run out. Say what is happening, on the whole surface this time.
    /// </summary>
    /// <remarks>
    /// The notice re-shown is the one that started the hold, so the owner is told the same thing
    /// they would have been told immediately before -- only now with a picture behind it that has
    /// stopped being worth protecting, which is what promotes it from a note to the surface.
    /// </remarks>
    private void ExpireHeldFrame()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(ExpireHeldFrame);
            return;
        }

        if (pictureLive || heldSince is null)
        {
            return;
        }

        MirrorNotice notice = heldNotice ?? new MirrorNotice(
            NoticeKind.Interrupted,
            "Reconnecting iPhone",
            Severity: NoticeSeverity.Warning);

        ReleasePicture();
        Show(notice);
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        CancelRetry();
        if (IsShuttingDown)
        {
            return;
        }

        CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        retryTimer = timer;
        _ = Task.Delay(delay, timer.Token).ContinueWith(
            task =>
            {
                if (!task.IsCanceled)
                {
                    Dispatch(new ConnectionEvent(ConnectionTrigger.RetryDue));
                }

                timer.Dispose();
            },
            TaskScheduler.Default);
    }

    private void CancelRetry()
    {
        CancellationTokenSource? timer = retryTimer;
        retryTimer = null;
        if (timer is null)
        {
            return;
        }

        try
        {
            timer.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delay already elapsed and disposed it. Nothing to cancel.
        }
    }

    /// <summary>
    /// Let go of the phone completely, bounded so a wedged agent cannot trap the window.
    /// </summary>
    /// <remarks>
    /// The bound is the point. Waiting properly for the teardown is what this phase is for, but a
    /// close that can never finish is a worse bug than the one being fixed, so the agent is killed
    /// and the window closed anyway if the budget runs out.
    /// </remarks>
    private async Task ShutdownAsync()
    {
        // Nothing left to fade over: the window is going, and a note easing out on top of a
        // closing mirror is two seconds of animation nobody is waiting for.
        toast.Clear();

        try
        {
            // Dispatching WindowClosing already queued the teardown onto the lifecycle chain, so
            // this waits for that work rather than starting a second, competing one.
            await lifecycleChain.WaitAsync(Teardown.For(TeardownReason.WindowClosing).Total);
        }
        catch (TimeoutException)
        {
            // It had its chance and did not take it.
        }
        catch (Exception)
        {
        }

        lifetime.Cancel();
        AgentProcessRegistry.KillAll();
    }

    private Task TearDownCoreDeviceAsync() => TearDownCoreDeviceAsync(TeardownReason.StreamInterrupted);

    /// <summary>
    /// Release the phone, spending as long on it as the reason deserves.
    /// </summary>
    /// <remarks>
    /// Every path used to share one budget, and that budget was chosen for a pulled cable — the one
    /// case where the phone genuinely cannot hear the goodbye. Applied to a deliberate disconnect
    /// or a window close it abandoned the stop-video that ends the phone's media session, which is
    /// the message that must not be lost.
    /// </remarks>
    private async Task TearDownCoreDeviceAsync(TeardownReason reason)
    {
        TeardownPlan plan = Teardown.For(reason);
        await connectionGate.WaitAsync();
        try
        {
            Interlocked.Increment(ref connectionGeneration);
            CoreDeviceInputController? input = coreDeviceInput;
            CoreDeviceMirrorSession? session = coreDeviceMirrorSession;
            CoreDeviceAgentClient? agent = coreDeviceAgent;
            coreDeviceInput = null;
            coreDeviceMirrorSession = null;
            coreDeviceAgent = null;

            if (input is not null)
            {
                await BoundedAsync(input.DisposeAsync().AsTask(), plan.InputRelease);
            }

            if (session is not null)
            {
                await BoundedAsync(
                    session.StopAsync(plan.StopVideo).AsTask(),
                    plan.StopVideo + plan.SessionStop);
            }

            if (agent is not null)
            {
                // A phone that can still hear it gets asked to stop; one that cannot is not worth
                // the wait, and half-waiting is the worst of both — it leaves the agent alive for
                // the next attempt to collide with.
                Task release = plan.TunnelPresumedAlive
                    ? agent.DisposeAsync().AsTask()
                    : agent.KillAsync().AsTask();
                await BoundedAsync(release, plan.AgentExit);
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>
    /// Await a teardown step, giving up once it is clearly not going to finish. Nothing here is
    /// worth a frozen window: the underlying process is terminated either way.
    /// </summary>
    private static async Task BoundedAsync(Task work, TimeSpan limit)
    {
        try
        {
            await work.WaitAsync(limit);
        }
        catch (Exception)
        {
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= DropShadowClassStyle;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);

        int enabled = 1;
        _ = NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmWindowAttributeUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));

        // The window region already cuts the phone's corner radius, which is far larger than the
        // system's. Letting DWM round as well only draws its own frame line along a different arc.
        int rounded = NativeMethods.DwmWindowCornerPreferenceDoNotRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmWindowCornerPreference,
            ref rounded,
            sizeof(int));

        // Windows 11 paints a light border on every window. On a phone mirror it reads as a grey
        // outline around the screen, so suppress it and let the video be the edge.
        int borderColor = unchecked((int)NativeMethods.DwmColorNone);
        _ = NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmWindowAttributeBorderColor,
            ref borderColor,
            sizeof(int));

        UpdateWindowRegion();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmSizing &&
            message.LParam != 0)
        {
            ConstrainPhoneAspect(message.WParam, message.LParam);
            message.Result = 1;
            return;
        }

        base.WndProc(ref message);
        if (message.Msg != NativeMethods.WmNcHitTest ||
            WindowState == FormWindowState.Maximized)
        {
            return;
        }

        Point cursor = PointToClient(Cursor.Position);
        const int dragCorner = 30;
        if (cursor.X < dragCorner && cursor.Y < dragCorner)
        {
            message.Result = NativeMethods.HtCaption;
            return;
        }

        const int edge = 8;
        bool left = cursor.X < edge;
        bool right = cursor.X >= ClientSize.Width - edge;
        bool top = cursor.Y < edge;
        bool bottom = cursor.Y >= ClientSize.Height - edge;

        message.Result = (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HtTopLeft,
            (_, true, true, _) => NativeMethods.HtTopRight,
            (true, _, _, true) => NativeMethods.HtBottomLeft,
            (_, true, _, true) => NativeMethods.HtBottomRight,
            (true, _, _, _) => NativeMethods.HtLeft,
            (_, true, _, _) => NativeMethods.HtRight,
            (_, _, true, _) => NativeMethods.HtTop,
            (_, _, _, true) => NativeMethods.HtBottom,
            _ => message.Result
        };
    }

    private static void ConstrainPhoneAspect(nint sizingEdge, nint rectanglePointer)
    {
        NativeMethods.Rect rectangle =
            Marshal.PtrToStructure<NativeMethods.Rect>(rectanglePointer);
        int width = Math.Max(277, rectangle.Right - rectangle.Left);
        int height = Math.Max(600, rectangle.Bottom - rectangle.Top);
        int edge = sizingEdge.ToInt32();

        bool heightDriven = edge is NativeMethods.WmszTop or NativeMethods.WmszBottom;
        if (heightDriven)
        {
            width = (int)Math.Round(height * phoneAspectRatio);
        }
        else
        {
            height = (int)Math.Round(width / phoneAspectRatio);
        }

        if (edge is NativeMethods.WmszLeft or
            NativeMethods.WmszTopLeft or
            NativeMethods.WmszBottomLeft)
        {
            rectangle.Left = rectangle.Right - width;
        }
        else
        {
            rectangle.Right = rectangle.Left + width;
        }

        if (edge is NativeMethods.WmszTop or
            NativeMethods.WmszTopLeft or
            NativeMethods.WmszTopRight)
        {
            rectangle.Top = rectangle.Bottom - height;
        }
        else
        {
            rectangle.Bottom = rectangle.Top + height;
        }

        Marshal.StructureToPtr(rectangle, rectanglePointer, false);
    }

    private void BuildInterface()
    {
        // No inset bezel: the video reaches the rounded edge exactly like Apple's iPhone Mirroring.
        // An inset panel with its own corner radius left grey wedges peeking out at each corner.
        videoHost.Dock = DockStyle.Fill;
        videoHost.BackColor = Color.Black;
        videoHost.Resize += (_, _) =>
        {
            SetRoundedRegion(videoHost, CornerRadius(videoHost.Width));
        };
        videoHost.MouseDown += async (_, eventArgs) =>
        {
            videoHost.Focus();
            if (eventArgs.Button == MouseButtons.Left &&
                ModifierKeys.HasFlag(Keys.Alt))
            {
                DragWindow(videoHost, eventArgs);
            }
            else if (eventArgs.Button == MouseButtons.Left && PhoneAsleep)
            {
                // The touchscreen is off, so this click cannot reach the phone as a touch. Press a
                // hardware button instead, and swallow the click rather than delivering a tap the
                // sleeping phone would ignore anyway.
                await WakePhoneAsync();
            }
            else if (eventArgs.Button == MouseButtons.Left && coreDeviceInput is not null)
            {
                videoHost.Capture = true;
                await coreDeviceInput.TouchDownAsync(eventArgs.Location, videoHost.ClientSize);
            }
        };
        videoHost.MouseMove += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left && !PhoneAsleep)
            {
                coreDeviceInput?.TouchMove(eventArgs.Location, videoHost.ClientSize);
            }
        };
        videoHost.MouseUp += async (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left && coreDeviceInput is not null && !PhoneAsleep)
            {
                videoHost.Capture = false;
                await coreDeviceInput.TouchUpAsync(eventArgs.Location, videoHost.ClientSize);
            }
        };
        videoHost.MouseEnter += (_, _) => videoHost.Focus();
        videoHost.MouseWheel += (_, eventArgs) =>
        {
            if (coreDeviceInput is null)
            {
                return;
            }

            if (PhoneAsleep)
            {
                _ = WakePhoneAsync();
                return;
            }

            _ = coreDeviceInput.ScrollAsync(eventArgs.Location, videoHost.ClientSize, eventArgs.Delta);
        };

        videoSurface.Dock = DockStyle.Fill;
        videoSurface.BackColor = Color.Black;
        videoSurface.Enabled = false;
        videoSurface.Visible = true;
        // DWM composes a flip-model swap chain for this child HWND independently of the parent, so
        // it needs its own matching corner region or the video squares off the window's corners.
        videoSurface.Resize += (_, _) =>
            SetRoundedRegion(videoSurface, CornerRadius(videoSurface.Width));

        overlay.Dock = DockStyle.Fill;
        overlay.Visible = false;
        overlay.SetupCompleted += async (_, _) => await RunGuidedSetupAsync();
        // Reconnect, never the walkthrough. This used to be wired straight to the first-run setup,
        // which waits for a USB cable -- so an owner mirroring over Wi-Fi who disconnected could
        // not get back without plugging in.
        overlay.RetryRequested += (_, _) =>
            Dispatch(new ConnectionEvent(ConnectionTrigger.UserReconnect));

        // A sleeping phone covers the picture, so clicking the video underneath cannot reach the
        // wake handler any more. The button on the status is now the way to it.
        overlay.WakeRequested += async (_, _) => await WakePhoneAsync();
        overlay.SetupSkipped += async (_, _) =>
        {
            OnboardingState.MarkComplete();
            Show(new MirrorNotice(NoticeKind.Connecting, "Looking for iPhone"));
            await StartCoreDeviceAsync();
        };

        videoHost.Controls.Add(videoSurface);
        Controls.Add(videoHost);

        // The overlay belongs to the form rather than to videoHost, because videoSurface is a
        // native child window with its own swap chain: DWM composes it over any WinForms sibling
        // whatever the z-order says, so an overlay inside videoHost is painted and then buried.
        Controls.Add(overlay);
        overlay.BringToFront();
    }

    private void DragWindow(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        _ = NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessageW(
            Handle,
            NativeMethods.WmNcLButtonDown,
            NativeMethods.HtCaption,
            0);
    }

    /// <summary>
    /// The single corner radius shared by the window and every layer inside it. One value keeps the
    /// arcs coincident; two different radii are what produced the visible corner wedges.
    /// </summary>
    private static int CornerRadius(int width) => Math.Clamp(width / 10, 28, 58);

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            SetRoundedRegion(this, CornerRadius(ClientSize.Width));
        }
    }

    /// <summary>Say something short, wherever the router judges it belongs.</summary>
    /// <remarks>
    /// This used to drop the notice outright whenever the overlay was not already covering the
    /// picture, because there was nowhere for a note to go over a running mirror. That was the
    /// right call while the toast surface did not exist and the only alternative was covering live
    /// video to announce "Connected". Now that <see cref="ToastWindow"/> draws one, the decision
    /// belongs to <see cref="StatusRouter"/> alone — which already declines to interrupt for the
    /// routine cases this guard was standing in for, and does it per notice rather than per
    /// surface.
    /// </remarks>
    private void ShowTransientStatus(MirrorNotice notice) => Show(notice);

    /// <summary>
    /// Give one layer the shared corner radius, but only when its shape has actually changed.
    /// </summary>
    /// <remarks>
    /// <para>The guard is the whole of this. <c>Resize</c> fires for every <c>WM_SIZE</c>, and a
    /// window being dragged by its edge produces those continuously, so each of the three layers
    /// was building a <see cref="GraphicsPath"/>, converting it to a region and handing it to a
    /// control on every mouse message of a resize drag. Setting a region re-lays the window's shape
    /// and forces a non-client repaint, and one of the three layers here is the HWND hosting the
    /// flip-model swap chain — so the cost landed next to the present path, which is the one place
    /// in this window where a stall is visible as a dropped frame rather than as a flicker.</para>
    /// <para>The same reasoning is recorded on <see cref="ControlStripWindow.Reposition"/>, which
    /// already skips its own rebuild on a move. This is that fix applied to the layer where it
    /// matters most.</para>
    /// </remarks>
    private void SetRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        Size size = new(control.Width, control.Height);
        if (appliedRegions.TryGetValue(control, out (Size Size, int Radius) applied)
            && applied.Size == size
            && applied.Radius == radius)
        {
            return;
        }

        Rectangle bounds = new(0, 0, size.Width, size.Height);
        using GraphicsPath path = RoundedRectanglePath(bounds, radius);
        Region? oldRegion = control.Region;
        control.Region = new Region(path);
        oldRegion?.Dispose();
        appliedRegions[control] = (size, radius);
    }

    private static GraphicsPath RoundedRectanglePath(
        Rectangle bounds,
        int radius)
    {
        GraphicsPath path = new();
        int safeRadius = Math.Clamp(
            radius,
            1,
            Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 2));
        int diameter = safeRadius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

}

internal sealed class FramePanel : Panel
{
    private const int WindowStyleClipChildren = 0x02000000;

    public FramePanel()
    {
        // No double buffering and no resize-redraw. This panel draws nothing: it exists to clip and
        // to hold focus, and the picture inside it belongs to a DXGI swap chain on its own child
        // HWND. Double buffering allocated a full-window back buffer that was only ever cleared to
        // black, and ResizeRedraw invalidated all of it on every size change to paint that nothing.
        //
        // It used to carry a CurrentFrame bitmap and letterbox it here, from the days before the
        // swap chain. The field was only ever assigned null, so the whole paint path was dead.
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style |= WindowStyleClipChildren;
            return parameters;
        }
    }
}

/// <summary>
/// A paint-free native child HWND dedicated to DXGI. Keeping the swap chain off the WinForms
/// panel prevents GDI double-buffer paints from racing DWM's flip-model presentation.
/// </summary>
internal sealed class NativeVideoSurface : Control
{
    private const int WindowStyleClipSiblings = 0x04000000;

    public NativeVideoSurface()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            false);
        TabStop = false;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style |= WindowStyleClipSiblings;
            return parameters;
        }
    }
}
