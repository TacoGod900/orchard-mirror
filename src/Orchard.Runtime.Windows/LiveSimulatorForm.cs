using System.Globalization;
using Orchard.Core;
using Orchard.NativeHost.Windows;

namespace Orchard.Runtime.Windows;

/// <summary>
/// Bootstrap WinForms projection of a persistent native OrchardUI session. This is not a production iOS renderer.
/// </summary>
internal sealed class LiveSimulatorForm : Form
{
    private static readonly Color OrchardGreen = Color.FromArgb(45, 146, 90);
    private readonly NativeHostLaunchOptions _launchOptions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LiveSemanticIntentScheduler _intentQueue;
    private readonly LiveLogBuffer _log = new();
    private readonly Panel _workspace = new();
    private readonly Panel _phoneFrame = new();
    private readonly Panel _screen = new();
    private readonly RichTextBox _console = new();
    private readonly ToolStripComboBox _devicePicker = new();
    private readonly ToolStripButton _rotateButton = new("Rotate");
    private readonly ToolStripButton _themeButton = new("Dark appearance") { CheckOnClick = true };
    private readonly ToolStripLabel _statusLabel = new("Waiting to connect");
    private readonly Label _scopeLabel = new();
    private DeviceProfile _device;
    private LiveSimulatorController? _controller;
    private Task? _startupTask;
    private bool _landscape;
    private bool _rendering;
    private bool _closeStarted;
    private bool _closeCommitted;
    private string _lastRendererDisclosure = string.Empty;

    public LiveSimulatorForm(NativeHostLaunchOptions launchOptions, DeviceProfile device)
    {
        _launchOptions = launchOptions ?? throw new ArgumentNullException(nameof(launchOptions));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _intentQueue = new LiveSemanticIntentScheduler(
            DispatchIntentAsync,
            PublishIntentAsync,
            PublishIntentErrorAsync);
        Text = "Native OrchardUI \u2014 Orchard Simulator";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 680);
        Size = new Size(1180, 900);
        BackColor = Color.FromArgb(30, 30, 32);
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;

        BuildChrome();
        ConfigureDevice();
        RenderWaitingState("Launching authenticated native child\u2026");
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _startupTask ??= StartSessionAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (_closeCommitted)
        {
            base.OnFormClosing(eventArgs);
            return;
        }

        eventArgs.Cancel = true;
        base.OnFormClosing(eventArgs);
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        _lifetime.Cancel();
        _statusLabel.Text = "Stopping native child\u2026";
        SetInteractionEnabled(false);
        _ = FinishCloseAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task StartSessionAsync()
    {
        try
        {
            _statusLabel.Text = "Connecting\u2026";
            var controller = await LiveSimulatorController.LaunchAsync(
                _launchOptions,
                _lifetime.Token);
            if (_closeStarted)
            {
                await controller.DisposeAsync();
                return;
            }

            _controller = controller;
            _statusLabel.Text = $"Live revision {controller.CurrentRender.Revision}";
            AppendLog("runtime", $"Connected to native child process {controller.ProcessId}.");
            Render(controller.CurrentRender);
        }
        catch (OperationCanceledException) when (_closeStarted)
        {
            AppendLog("runtime", "Native startup cancelled during close.");
        }
        catch (Exception exception)
        {
            ShowStableError(exception);
            RenderWaitingState("Native session could not start. See the debug console.");
        }
    }

    private async Task FinishCloseAsync()
    {
        try
        {
            await _intentQueue.DisposeAsync();
            if (_startupTask is not null)
            {
                await _startupTask;
            }

            if (_controller is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var output = await _controller.ShutdownAndCaptureOutputAsync(deadline.Token);
                    AppendChildOutput(output);
                }
                catch (Exception exception)
                {
                    var error = LiveError.FromException(exception);
                    AppendLog(error.Code, error.Message);
                }
                finally
                {
                    await _controller.DisposeAsync();
                    _controller = null;
                }
            }
        }
        finally
        {
            _closeCommitted = true;
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(Close);
            }
            else
            {
                Dispose();
            }
        }
    }

    private void BuildChrome()
    {
        var toolStrip = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            Padding = new Padding(8, 5, 8, 5),
            RenderMode = ToolStripRenderMode.System
        };
        toolStrip.Items.Add(new ToolStripLabel("Device"));
        foreach (var profile in DeviceProfile.BuiltIn)
        {
            _devicePicker.Items.Add(new DeviceChoice(profile));
        }

        _devicePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _devicePicker.AutoSize = false;
        _devicePicker.Width = 220;
        _devicePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_devicePicker.SelectedItem is not DeviceChoice choice)
            {
                return;
            }

            _device = choice.Profile;
            ConfigureDevice();
            RenderCurrent();
            AppendLog("chrome", $"Local device frame changed to {_device.DisplayName}; native configuration is unchanged.");
        };
        toolStrip.Items.Add(_devicePicker);

        _rotateButton.Click += (_, _) =>
        {
            _landscape = !_landscape;
            ConfigureDevice();
            RenderCurrent();
            AppendLog("chrome", "Local orientation changed; native configuration is unchanged.");
        };
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_rotateButton);

        _themeButton.Click += (_, _) =>
        {
            RenderCurrent();
            AppendLog("chrome", "Local appearance changed; native configuration is unchanged.");
        };
        toolStrip.Items.Add(_themeButton);

        _statusLabel.Alignment = ToolStripItemAlignment.Right;
        _statusLabel.ForeColor = Color.LightSkyBlue;
        toolStrip.Items.Add(_statusLabel);

        _scopeLabel.Text =
            "Bootstrap OrchardUI projection \u2022 device, rotation, and appearance changes currently affect local chrome only";
        _scopeLabel.Dock = DockStyle.Top;
        _scopeLabel.Height = 28;
        _scopeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _scopeLabel.ForeColor = Color.Gold;
        _scopeLabel.BackColor = Color.FromArgb(63, 52, 22);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            SplitterDistance = 650,
            Panel1MinSize = 400,
            Panel2MinSize = 110,
            BackColor = Color.FromArgb(55, 55, 58)
        };

        _workspace.Dock = DockStyle.Fill;
        _workspace.AutoScroll = true;
        _workspace.BackColor = Color.FromArgb(38, 38, 41);
        _workspace.Resize += (_, _) => CentrePhone();
        _phoneFrame.BackColor = Color.FromArgb(12, 12, 13);
        _phoneFrame.Padding = new Padding(12);
        _phoneFrame.Paint += PaintPhoneFrame;
        _workspace.Controls.Add(_phoneFrame);
        _screen.Dock = DockStyle.Fill;
        _screen.AutoScroll = true;
        _phoneFrame.Controls.Add(_screen);
        split.Panel1.Controls.Add(_workspace);

        var consoleHeader = new Label
        {
            Text = "  Bounded debug console",
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(45, 45, 48),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _console.Dock = DockStyle.Fill;
        _console.ReadOnly = true;
        _console.BorderStyle = BorderStyle.None;
        _console.BackColor = Color.FromArgb(24, 24, 26);
        _console.ForeColor = Color.Gainsboro;
        _console.Font = new Font("Cascadia Mono", 9F);
        split.Panel2.Controls.Add(_console);
        split.Panel2.Controls.Add(consoleHeader);

        Controls.Add(split);
        Controls.Add(_scopeLabel);
        Controls.Add(toolStrip);
        toolStrip.Dock = DockStyle.Top;

        var selected = DeviceProfile.BuiltIn
            .Select((profile, index) => (profile, index))
            .FirstOrDefault(item => item.profile.Id == _device.Id);
        _devicePicker.SelectedIndex = selected.profile is null ? 0 : selected.index;
    }

    private void ConfigureDevice()
    {
        var logicalWidth = _landscape ? _device.LogicalHeight : _device.LogicalWidth;
        var logicalHeight = _landscape ? _device.LogicalWidth : _device.LogicalHeight;
        var availableHeight = Math.Max(420, _workspace.ClientSize.Height - 44);
        var scale = Math.Clamp(availableHeight / (double)logicalHeight, 0.48, 0.82);
        _phoneFrame.Size = new Size(
            (int)Math.Round(logicalWidth * scale) + 24,
            (int)Math.Round(logicalHeight * scale) + 24);
        var regionHandle = CreateRoundRectRgn(0, 0, _phoneFrame.Width, _phoneFrame.Height, 38, 38);
        var previousRegion = _phoneFrame.Region;
        _phoneFrame.Region = Region.FromHrgn(regionHandle);
        previousRegion?.Dispose();
        DeleteObject(regionHandle);
        CentrePhone();
    }

    private void CentrePhone()
    {
        _phoneFrame.Left = Math.Max(20, (_workspace.ClientSize.Width - _phoneFrame.Width) / 2);
        _phoneFrame.Top = Math.Max(20, (_workspace.ClientSize.Height - _phoneFrame.Height) / 2);
    }

    private void RenderCurrent()
    {
        if (_controller is not null)
        {
            Render(_controller.CurrentRender);
        }
    }

    private void Render(LiveRenderViewModel render)
    {
        if (_rendering || _closeStarted)
        {
            return;
        }

        _rendering = true;
        try
        {
            ReplaceScreen(() =>
            {
                var dark = _themeButton.Checked;
                var safeArea = CreateSafeArea(dark);
                var root = RenderNode(render.Root, dark);
                root.Dock = DockStyle.Top;
                safeArea.Controls.Add(root);
                return safeArea;
            });
            _statusLabel.Text = $"Live revision {render.Revision}";
            _statusLabel.ForeColor = Color.LightSkyBlue;
            PublishRendererDisclosure(render);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderWaitingState(string message)
    {
        ReplaceScreen(() =>
        {
            var dark = _themeButton.Checked;
            var safeArea = CreateSafeArea(dark);
            safeArea.Controls.Add(new Label
            {
                Text = LiveUiText.SanitizeControlText(message, 512),
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                ForeColor = dark ? Color.Gainsboro : Color.FromArgb(50, 50, 54),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 12F),
                Padding = new Padding(12)
            });
            return safeArea;
        });
    }

    private void ReplaceScreen(Func<Control> contentFactory)
    {
        _screen.SuspendLayout();
        try
        {
            foreach (var existing in _screen.Controls.Cast<Control>().ToArray())
            {
                _screen.Controls.Remove(existing);
                existing.Dispose();
            }

            _screen.Controls.Add(contentFactory());
        }
        finally
        {
            _screen.ResumeLayout(true);
        }
    }

    private Panel CreateSafeArea(bool dark)
    {
        _screen.BackColor = dark ? Color.FromArgb(20, 20, 22) : Color.FromArgb(249, 249, 249);
        _screen.ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28);
        return new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, ScaleSafeArea(_device.SafeAreaTop), 18, ScaleSafeArea(_device.SafeAreaBottom)),
            BackColor = _screen.BackColor,
            ForeColor = _screen.ForeColor,
            AutoScroll = true
        };
    }

    private Control RenderNode(LiveNodeViewModel node, bool dark)
    {
        Control control = node.Kind switch
        {
            LiveNodeKind.Root or LiveNodeKind.VStack or LiveNodeKind.Group or LiveNodeKind.Conditional =>
                RenderFlow(node, FlowDirection.TopDown, dark),
            LiveNodeKind.ScrollView or LiveNodeKind.List or LiveNodeKind.NavigationStack =>
                RenderPlaceholderFlow(node, dark),
            LiveNodeKind.HStack => RenderFlow(node, FlowDirection.LeftToRight, dark),
            LiveNodeKind.ZStack => RenderOverlay(node, dark),
            LiveNodeKind.Text => RenderText(node, dark),
            LiveNodeKind.Button => RenderButton(node),
            LiveNodeKind.TextField => RenderTextField(node, secure: false, dark),
            LiveNodeKind.SecureField => RenderTextField(node, secure: true, dark),
            LiveNodeKind.Toggle => RenderToggle(node, dark),
            LiveNodeKind.Slider => RenderSlider(node),
            LiveNodeKind.Image => RenderImagePlaceholder(node, dark),
            LiveNodeKind.Divider => new Panel
            {
                Height = 1,
                Width = 300,
                BackColor = dark ? Color.DimGray : Color.LightGray
            },
            LiveNodeKind.Spacer => new Panel { Height = 18, Width = 18, BackColor = Color.Transparent },
            LiveNodeKind.ProgressView => RenderProgressPlaceholder(dark),
            _ => RenderUnsupported(node, dark)
        };

        ApplyCommonProperties(control, node, dark);
        return control;
    }

    private FlowLayoutPanel RenderFlow(LiveNodeViewModel node, FlowDirection direction, bool dark)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = direction,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Padding = new Padding(2),
            Width = Math.Max(240, _screen.ClientSize.Width - 40)
        };
        foreach (var child in node.Children)
        {
            panel.Controls.Add(RenderNode(child, dark));
        }

        return panel;
    }

    private FlowLayoutPanel RenderPlaceholderFlow(LiveNodeViewModel node, bool dark)
    {
        var panel = RenderFlow(node, FlowDirection.TopDown, dark);
        panel.Controls.Add(new Label
        {
            Text = $"Bootstrap placeholder: {node.ProtocolKind} semantics are flattened below.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Padding = new Padding(6),
            BackColor = dark ? Color.FromArgb(80, 55, 15) : Color.LemonChiffon,
            ForeColor = dark ? Color.Gold : Color.DarkGoldenrod
        });
        panel.Controls.SetChildIndex(panel.Controls[panel.Controls.Count - 1], 0);
        return panel;
    }

    private Panel RenderOverlay(LiveNodeViewModel node, bool dark)
    {
        var panel = new Panel { Width = 300, Height = 240, BackColor = Color.Transparent };
        foreach (var child in node.Children)
        {
            var rendered = RenderNode(child, dark);
            rendered.Location = Point.Empty;
            panel.Controls.Add(rendered);
            rendered.BringToFront();
        }

        return panel;
    }

    private static Label RenderText(LiveNodeViewModel node, bool dark) => new()
    {
        Text = node.Property("text", "Text"),
        AutoSize = true,
        MaximumSize = new Size(340, 0),
        Font = new Font("Segoe UI", 12F),
        BackColor = Color.Transparent,
        ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28)
    };

    private Button RenderButton(LiveNodeViewModel node)
    {
        var button = new Button
        {
            Text = node.Property("text", "Button"),
            AutoSize = true,
            MinimumSize = new Size(112, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = OrchardGreen,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        if (node.RendererSupportsEvent("press"))
        {
            button.Click += (_, _) => QueueSemanticIntent(node.Id, "press", null);
        }

        return button;
    }

    private TextBox RenderTextField(LiveNodeViewModel node, bool secure, bool dark)
    {
        var box = new TextBox
        {
            PlaceholderText = node.Property("placeholder"),
            Text = node.Property("value"),
            UseSystemPasswordChar = secure,
            Width = 280,
            Height = 34,
            MaxLength = LiveUiText.MaximumEventValueCharacters,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = dark ? Color.FromArgb(42, 42, 45) : Color.White,
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28)
        };
        if (node.RendererSupportsEvent("change"))
        {
            box.TextChanged += (_, _) =>
            {
                if (!_rendering)
                {
                    QueueSemanticIntent(node.Id, "change", box.Text);
                }
            };
        }

        if (node.RendererSupportsEvent("submit"))
        {
            box.KeyDown += (_, eventArgs) =>
            {
                if (eventArgs.KeyCode == Keys.Enter && !_rendering)
                {
                    eventArgs.SuppressKeyPress = true;
                    QueueSemanticIntent(node.Id, "submit", box.Text);
                }
            };
        }

        return box;
    }

    private CheckBox RenderToggle(LiveNodeViewModel node, bool dark)
    {
        var toggle = new CheckBox
        {
            Text = node.Property("text", "Toggle"),
            AutoSize = true,
            Checked = bool.TryParse(node.Property("value"), out var current) && current,
            BackColor = Color.Transparent,
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28)
        };
        if (node.RendererSupportsEvent("change"))
        {
            toggle.CheckedChanged += (_, _) =>
            {
                if (!_rendering)
                {
                    QueueSemanticIntent(
                        node.Id,
                        "change",
                        toggle.Checked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
                }
            };
        }

        return toggle;
    }

    private TrackBar RenderSlider(LiveNodeViewModel node)
    {
        var slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Width = 280,
            Value = (int)Math.Round(node.DecimalProperty("value", 0, 0, 1) * 100)
        };
        if (node.RendererSupportsEvent("change"))
        {
            slider.ValueChanged += (_, _) => QueueSemanticIntent(
                node.Id,
                "change",
                (slider.Value / 100M).ToString(CultureInfo.InvariantCulture));
        }

        return slider;
    }

    private static Label RenderImagePlaceholder(LiveNodeViewModel node, bool dark)
    {
        var imageName = node.Property("systemName", node.Property("resourceName", "image"));
        return new Label
        {
            Text = $"Image placeholder: \u25a3  {imageName}",
            AutoSize = true,
            Font = new Font("Segoe UI Symbol", 18F),
            BackColor = Color.Transparent,
            ForeColor = dark ? Color.LightSkyBlue : Color.RoyalBlue
        };
    }

    private static Label RenderProgressPlaceholder(bool dark) => new()
    {
        Text = "ProgressView placeholder (progress semantics are not rendered)",
        AutoSize = true,
        MaximumSize = new Size(340, 0),
        Padding = new Padding(6),
        BackColor = dark ? Color.FromArgb(80, 55, 15) : Color.LemonChiffon,
        ForeColor = dark ? Color.Gold : Color.DarkGoldenrod
    };

    private static Label RenderUnsupported(LiveNodeViewModel node, bool dark) => new()
    {
        Text = $"Unsupported bootstrap node: {node.ProtocolKind}",
        AutoSize = true,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(7),
        BackColor = dark ? Color.FromArgb(80, 55, 15) : Color.LemonChiffon,
        ForeColor = dark ? Color.Gold : Color.DarkGoldenrod
    };

    private static void ApplyCommonProperties(Control control, LiveNodeViewModel node, bool dark)
    {
        control.Name = node.Id;
        control.Margin = new Padding(4, 5, 4, 5);
        control.Padding = new Padding(node.IntegerProperty("padding", 0, 0, 128));
        control.Enabled = node.BooleanProperty("enabled", true);
        control.Visible = !node.BooleanProperty("hidden", false);
        control.AccessibleName = node.Property("accessibilityLabel", control.AccessibleName ?? string.Empty);
        control.AccessibleDescription = node.Property("accessibilityHint", control.AccessibleDescription ?? string.Empty);
        if (node.RendererUnsupportedEvents.Count > 0)
        {
            var unavailable = "Bootstrap renderer does not dispatch: " +
                string.Join(", ", node.RendererUnsupportedEvents) + ".";
            control.AccessibleDescription = string.IsNullOrWhiteSpace(control.AccessibleDescription)
                ? unavailable
                : control.AccessibleDescription + " " + unavailable;
        }
        control.Tag = new LiveControlState(control.Enabled);

        var width = node.IntegerProperty("width", 0, 0, 2_048);
        var height = node.IntegerProperty("height", 0, 0, 2_048);
        if (width > 0)
        {
            control.Width = width;
        }
        if (height > 0)
        {
            control.Height = height;
        }

        if (control is not TextBoxBase and not Button)
        {
            control.ForeColor = ResolveColor(
                node.Property("foregroundColor"),
                dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28));
        }
        control.BackColor = ResolveColor(node.Property("backgroundColor"), control.BackColor);
        ApplyFont(control, node.Property("font"), node.Property("fontSize"), node.Property("fontWeight"));
    }

    private static void ApplyFont(Control control, string font, string fontSize, string fontWeight)
    {
        var size = font switch
        {
            ".largeTitle" => 28F,
            ".title" => 24F,
            ".title2" => 20F,
            ".headline" => 15F,
            ".subheadline" => 13F,
            ".caption" => 10F,
            _ => 12F
        };
        if (float.TryParse(fontSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize))
        {
            size = Math.Clamp(parsedSize, 6F, 72F);
        }

        var bold = string.Equals(font, ".headline", StringComparison.Ordinal) ||
            string.Equals(fontWeight.TrimStart('.'), "bold", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fontWeight.TrimStart('.'), "semibold", StringComparison.OrdinalIgnoreCase);
        control.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    private void QueueSemanticIntent(string nodeId, string eventName, string? value)
    {
        if (_rendering || _closeStarted || _controller is null)
        {
            return;
        }

        var result = _intentQueue.Enqueue(new LiveSemanticIntent(nodeId, eventName, value));
        var safeEvent = LiveUiText.SanitizeControlText(eventName, 64);
        switch (result)
        {
            case LiveIntentEnqueueResult.Enqueued:
                _statusLabel.Text = $"Queued {safeEvent}\u2026";
                break;
            case LiveIntentEnqueueResult.Coalesced:
                _statusLabel.Text = $"Updated queued {safeEvent}\u2026";
                break;
            default:
                AppendLog("ORU3001", $"The bounded semantic-intent queue rejected {safeEvent}.");
                _statusLabel.Text = "The event queue is unavailable or full.";
                break;
        }
    }

    private Task<LiveRenderUpdate> DispatchIntentAsync(
        LiveSemanticIntent intent,
        CancellationToken cancellationToken)
    {
        var controller = _controller
            ?? throw new InvalidOperationException("The native child is not connected.");
        // DispatchAsync resolves the node and event from CurrentRender after all prior queued intents complete.
        return controller.DispatchAsync(intent.NodeId, intent.Event, intent.Value, cancellationToken);
    }

    private Task PublishIntentAsync(LiveSemanticIntent intent, LiveRenderUpdate update)
    {
        if (_closeStarted)
        {
            return Task.CompletedTask;
        }

        switch (update.Kind)
        {
            case LiveRenderUpdateKind.Applied when update.Current is not null:
                Render(update.Current);
                AppendLog("event", $"{intent.Event} accepted; revision {update.Current.Revision} published.");
                break;
            case LiveRenderUpdateKind.Rejected:
                AppendLog(update.Code, update.Message);
                _statusLabel.Text = $"Event rejected at revision {update.Current?.Revision}";
                break;
            case LiveRenderUpdateKind.IgnoredStale:
                AppendLog(update.Code, update.Message);
                _statusLabel.Text = $"Live revision {update.Current?.Revision}";
                break;
            default:
                throw new LiveSimulatorException(update.Code, update.Message);
        }

        return Task.CompletedTask;
    }

    private async Task PublishIntentErrorAsync(LiveSemanticIntent intent, Exception exception)
    {
        if (_closeStarted)
        {
            return;
        }

        ShowStableError(exception);
        AppendLog("event", $"{LiveUiText.SanitizeControlText(intent.Event, 64)} could not be dispatched.");
        await AppendExitedChildOutputAsync();
    }

    private void SetInteractionEnabled(bool enabled)
    {
        _devicePicker.Enabled = enabled;
        _rotateButton.Enabled = enabled;
        _themeButton.Enabled = enabled;
        foreach (var control in Descendants(_screen))
        {
            if (control.Tag is LiveControlState state)
            {
                control.Enabled = enabled && state.BaseEnabled;
            }
        }
    }

    private async Task AppendExitedChildOutputAsync()
    {
        if (_controller is null || !_controller.HasExited)
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var output = await _controller.CaptureOutputIfExitedAsync(deadline.Token);
            if (output is not null)
            {
                AppendChildOutput(output);
            }
        }
        catch (Exception exception)
        {
            var error = LiveError.FromException(exception);
            AppendLog(error.Code, "Native child output could not be captured.");
        }
    }

    private void AppendChildOutput(NativeChildOutput output)
    {
        if (output.StandardOutput.Text.Length > 0)
        {
            AppendLog("child stdout", output.StandardOutput.Text);
        }
        if (output.StandardError.Text.Length > 0)
        {
            AppendLog("child stderr", output.StandardError.Text);
        }
        if (output.StandardOutput.Truncated || output.StandardError.Truncated)
        {
            AppendLog("runtime", "Native child output reached its configured capture bound and was truncated.");
        }
    }

    private void ShowStableError(Exception exception)
    {
        var error = LiveError.FromException(exception);
        _statusLabel.Text = $"{error.Code}: {error.Message}";
        _statusLabel.ForeColor = Color.Salmon;
        AppendLog(error.Code, error.Message);
    }

    private void AppendLog(string category, string message)
    {
        _log.Append(category, message);
        _console.Text = _log.Text;
        _console.SelectionStart = _console.TextLength;
        _console.ScrollToCaret();
    }

    private void PublishRendererDisclosure(LiveRenderViewModel render)
    {
        var placeholders = new SortedSet<string>(StringComparer.Ordinal);
        var unavailableEvents = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<LiveNodeViewModel>();
        pending.Push(render.Root);
        while (pending.TryPop(out var node))
        {
            if (node.RendererSupport == LiveRendererSupport.PlaceholderOnly)
            {
                placeholders.Add(node.ProtocolKind);
            }
            foreach (var eventName in node.RendererUnsupportedEvents)
            {
                unavailableEvents.Add($"{node.ProtocolKind}.{eventName}");
            }
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }

        var parts = new List<string>();
        if (placeholders.Count > 0)
        {
            parts.Add("placeholder kinds: " + string.Join(", ", placeholders));
        }
        if (unavailableEvents.Count > 0)
        {
            parts.Add("events not dispatched: " + string.Join(", ", unavailableEvents));
        }

        var disclosure = string.Join("; ", parts);
        if (disclosure.Length == 0 || string.Equals(disclosure, _lastRendererDisclosure, StringComparison.Ordinal))
        {
            return;
        }

        _lastRendererDisclosure = disclosure;
        AppendLog("bootstrap fidelity", disclosure);
    }

    private void PaintPhoneFrame(object? sender, PaintEventArgs eventArgs)
    {
        using var pen = new Pen(Color.FromArgb(70, 70, 74), 2);
        eventArgs.Graphics.DrawRoundedRectangle(
            pen,
            new Rectangle(1, 1, _phoneFrame.Width - 3, _phoneFrame.Height - 3),
            new Size(35, 35));
        if (!_landscape && _device.HasHomeIndicator)
        {
            using var brush = new SolidBrush(Color.FromArgb(95, 95, 98));
            eventArgs.Graphics.FillRoundedRectangle(
                brush,
                new Rectangle((_phoneFrame.Width - 95) / 2, _phoneFrame.Height - 10, 95, 4),
                new Size(4, 4));
        }
    }

    private int ScaleSafeArea(int logicalValue)
    {
        var logicalHeight = _landscape ? _device.LogicalWidth : _device.LogicalHeight;
        return Math.Max(4, (int)Math.Round(logicalValue * (_screen.Height / (double)Math.Max(1, logicalHeight))));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        var pending = new Stack<Control>();
        foreach (Control child in root.Controls)
        {
            pending.Push(child);
        }
        while (pending.TryPop(out var current))
        {
            yield return current;
            foreach (Control child in current.Controls)
            {
                pending.Push(child);
            }
        }
    }

    private static Color ResolveColor(string value, Color fallback) => value.Trim().TrimStart('.') switch
    {
        "red" => Color.IndianRed,
        "orange" => Color.DarkOrange,
        "yellow" => Color.Goldenrod,
        "green" => OrchardGreen,
        "blue" => Color.RoyalBlue,
        "purple" => Color.MediumPurple,
        "pink" => Color.HotPink,
        "gray" or "secondary" => Color.Gray,
        "white" => Color.White,
        "black" => Color.Black,
        _ => fallback
    };

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private sealed record DeviceChoice(DeviceProfile Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }

    private sealed record LiveControlState(bool BaseEnabled);
}
