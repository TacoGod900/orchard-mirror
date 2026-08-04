using System.Globalization;
using System.Text.RegularExpressions;
using Orchard.Core;

namespace Orchard.Runtime.Windows;

internal sealed partial class SimulatorForm : Form
{
    private static readonly Color OrchardGreen = Color.FromArgb(45, 146, 90);
    private readonly OrchardApplication _application;
    private readonly Dictionary<string, string> _state;
    private readonly Panel _workspace = new();
    private readonly Panel _phoneFrame = new();
    private readonly Panel _screen = new();
    private readonly RichTextBox _console = new();
    private readonly ToolStripComboBox _devicePicker = new();
    private readonly ToolStripButton _rotateButton = new("Rotate");
    private readonly ToolStripButton _themeButton = new("Dark appearance") { CheckOnClick = true };
    private readonly ToolStripLabel _compatibilityLabel = new();
    private DeviceProfile _device;
    private bool _landscape;
    private bool _rendering;

    public SimulatorForm(OrchardApplication application, DeviceProfile device)
    {
        _application = application;
        _device = device;
        _state = application.State.ToDictionary(item => item.Name, item => item.InitialValue, StringComparer.Ordinal);
        Text = $"{application.DisplayName} \u2014 Orchard Simulator";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 680);
        Size = new Size(1180, 900);
        BackColor = Color.FromArgb(30, 30, 32);
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;

        BuildChrome();
        ConfigureDevice();
        RenderApplication();
        Log($"Launched {_application.ApplicationId} using Orchard IR {application.SchemaVersion}.");
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
            if (_devicePicker.SelectedItem is DeviceChoice choice)
            {
                _device = choice.Profile;
                ConfigureDevice();
                RenderApplication();
                Log($"Device changed to {_device.DisplayName}.");
            }
        };
        toolStrip.Items.Add(_devicePicker);

        _rotateButton.Click += (_, _) =>
        {
            _landscape = !_landscape;
            ConfigureDevice();
            RenderApplication();
            Log(_landscape ? "Rotated to landscape." : "Rotated to portrait.");
        };
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_rotateButton);

        _themeButton.Click += (_, _) =>
        {
            RenderApplication();
            Log(_themeButton.Checked ? "Dark appearance enabled." : "Light appearance enabled.");
        };
        toolStrip.Items.Add(_themeButton);

        _compatibilityLabel.Alignment = ToolStripItemAlignment.Right;
        _compatibilityLabel.Text = $"Local compatibility {_application.Compatibility.LocalCompatibilityPercent:0.#}%";
        _compatibilityLabel.ForeColor = _application.Compatibility.UnsupportedSymbols.Count == 0
            ? Color.LightGreen
            : Color.Gold;
        toolStrip.Items.Add(_compatibilityLabel);

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
            Text = "  Debug console",
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
        var scale = Math.Min(0.82, availableHeight / (double)logicalHeight);
        scale = Math.Max(0.48, scale);
        var width = (int)Math.Round(logicalWidth * scale);
        var height = (int)Math.Round(logicalHeight * scale);
        _phoneFrame.Size = new Size(width + 24, height + 24);
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

    private void RenderApplication()
    {
        if (_rendering)
        {
            return;
        }

        _rendering = true;
        try
        {
            _screen.SuspendLayout();
            try
            {
                foreach (var existing in _screen.Controls.Cast<Control>().ToArray())
                {
                    _screen.Controls.Remove(existing);
                    existing.Dispose();
                }

                var dark = _themeButton.Checked;
                _screen.BackColor = dark ? Color.FromArgb(20, 20, 22) : Color.FromArgb(249, 249, 249);
                _screen.ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28);

                var safeArea = new Panel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18, ScaleSafeArea(_device.SafeAreaTop), 18, ScaleSafeArea(_device.SafeAreaBottom)),
                    BackColor = _screen.BackColor,
                    ForeColor = _screen.ForeColor,
                    AutoScroll = true
                };
                var root = RenderNode(_application.RootView, dark);
                root.Dock = DockStyle.Top;
                safeArea.Controls.Add(root);
                _screen.Controls.Add(safeArea);
            }
            finally
            {
                _screen.ResumeLayout(true);
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private Control RenderNode(ViewNode node, bool dark)
    {
        Control control = node.Type switch
        {
            "VStack" or "NavigationStack" or "ScrollView" or "List" or "Form" or "Section" or "Group" =>
                RenderFlow(node, FlowDirection.TopDown, dark),
            "HStack" => RenderFlow(node, FlowDirection.LeftToRight, dark),
            "ZStack" => RenderOverlay(node, dark),
            "Text" => RenderText(node, dark),
            "Button" => RenderButton(node, dark),
            "TextField" => RenderTextBox(node, secure: false, dark),
            "SecureField" => RenderTextBox(node, secure: true, dark),
            "Toggle" => RenderToggle(node, dark),
            "Image" => RenderImage(node, dark),
            "Divider" => new Panel { Height = 1, Width = 300, BackColor = dark ? Color.DimGray : Color.LightGray },
            "Spacer" => new Panel { Height = 18, Width = 18, BackColor = Color.Transparent },
            "ProgressView" => new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 180, Height = 8 },
            _ => RenderUnsupported(node, dark)
        };

        control.Name = node.Id;
        control.Margin = new Padding(4, 5, 4, 5);
        control.ForeColor = control is TextBoxBase or Button
            ? control.ForeColor
            : dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28);
        ApplyModifiers(control, node.Modifiers, dark);
        return control;
    }

    private FlowLayoutPanel RenderFlow(ViewNode node, FlowDirection direction, bool dark)
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

    private Panel RenderOverlay(ViewNode node, bool dark)
    {
        var panel = new Panel { AutoSize = false, Width = 300, Height = 240, BackColor = Color.Transparent };
        foreach (var child in node.Children)
        {
            var rendered = RenderNode(child, dark);
            rendered.Location = new Point(0, 0);
            panel.Controls.Add(rendered);
            rendered.BringToFront();
        }

        return panel;
    }

    private Label RenderText(ViewNode node, bool dark)
    {
        var template = node.Arguments.GetValueOrDefault("_0", "Text");
        var text = ResolveText(template);
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 12F),
            BackColor = Color.Transparent,
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28),
            Tag = new BoundText(template)
        };
    }

    private Button RenderButton(ViewNode node, bool dark)
    {
        var button = new Button
        {
            Text = ResolveText(node.Arguments.GetValueOrDefault("_0", "Button")),
            AutoSize = true,
            MinimumSize = new Size(112, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = OrchardGreen,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => Execute(node.Events.FirstOrDefault(item => item.Name == "action")?.Body ?? string.Empty);
        return button;
    }

    private TextBox RenderTextBox(ViewNode node, bool secure, bool dark)
    {
        var binding = node.Arguments.GetValueOrDefault("text", string.Empty).TrimStart('$');
        var box = new TextBox
        {
            PlaceholderText = ResolveText(node.Arguments.GetValueOrDefault("_0", string.Empty)),
            Text = _state.GetValueOrDefault(binding, string.Empty),
            UseSystemPasswordChar = secure,
            Width = 280,
            Height = 34,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = dark ? Color.FromArgb(42, 42, 45) : Color.White,
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28)
        };
        if (!string.IsNullOrWhiteSpace(binding))
        {
            box.TextChanged += (_, _) =>
            {
                if (_rendering)
                {
                    return;
                }

                _state[binding] = box.Text;
                Log($"State ${binding} changed.");
                RefreshBoundText();
            };
        }

        return box;
    }

    private CheckBox RenderToggle(ViewNode node, bool dark)
    {
        var binding = node.Arguments.GetValueOrDefault("isOn", string.Empty).TrimStart('$');
        var toggle = new CheckBox
        {
            Text = ResolveText(node.Arguments.GetValueOrDefault("_0", "Toggle")),
            AutoSize = true,
            Checked = bool.TryParse(_state.GetValueOrDefault(binding), out var value) && value,
            BackColor = Color.Transparent,
            ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 28)
        };
        toggle.CheckedChanged += (_, _) =>
        {
            if (_rendering || string.IsNullOrWhiteSpace(binding))
            {
                return;
            }

            _state[binding] = toggle.Checked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
            Log($"State ${binding} = {_state[binding]}.");
            RefreshBoundText();
        };
        return toggle;
    }

    private static Label RenderImage(ViewNode node, bool dark)
    {
        var symbol = node.Arguments.GetValueOrDefault("systemName", node.Arguments.GetValueOrDefault("_0", "image"));
        return new Label
        {
            Text = $"\u25a3  {symbol}",
            AutoSize = true,
            Font = new Font("Segoe UI Symbol", 18F),
            BackColor = Color.Transparent,
            ForeColor = dark ? Color.LightSkyBlue : Color.RoyalBlue
        };
    }

    private static Label RenderUnsupported(ViewNode node, bool dark) => new Label
    {
        Text = $"Unsupported: {node.Type}",
        AutoSize = true,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(7),
        BackColor = dark ? Color.FromArgb(80, 55, 15) : Color.LemonChiffon,
        ForeColor = dark ? Color.Gold : Color.DarkGoldenrod
    };

    private void ApplyModifiers(Control control, IEnumerable<ViewModifier> modifiers, bool dark)
    {
        foreach (var modifier in modifiers)
        {
            switch (modifier.Name)
            {
                case "padding":
                    var padding = ParseInteger(modifier.Arguments.GetValueOrDefault("_0"), 12);
                    control.Padding = new Padding(padding);
                    break;
                case "font":
                    ApplyFont(control, modifier.Arguments.GetValueOrDefault("_0", string.Empty));
                    break;
                case "foregroundStyle":
                case "foregroundColor":
                case "tint":
                    control.ForeColor = ResolveColor(modifier.Arguments.GetValueOrDefault("_0"), control.ForeColor);
                    break;
                case "background":
                    control.BackColor = ResolveColor(modifier.Arguments.GetValueOrDefault("_0"), control.BackColor);
                    break;
                case "frame":
                    if (modifier.Arguments.TryGetValue("width", out var width))
                    {
                        control.Width = ParseInteger(width, control.Width);
                    }
                    if (modifier.Arguments.TryGetValue("height", out var height))
                    {
                        control.Height = ParseInteger(height, control.Height);
                    }
                    break;
                case "disabled":
                    control.Enabled = !bool.TryParse(modifier.Arguments.GetValueOrDefault("_0"), out var disabled) || !disabled;
                    break;
                case "opacity":
                    // WinForms controls do not support per-control opacity. The value is retained in IR.
                    break;
                case "navigationTitle":
                    Text = $"{ResolveText(modifier.Arguments.GetValueOrDefault("_0", _application.DisplayName))} \u2014 Orchard Simulator";
                    break;
                case "accessibilityLabel":
                    control.AccessibleName = ResolveText(modifier.Arguments.GetValueOrDefault("_0", string.Empty));
                    break;
                case "accessibilityHint":
                    control.AccessibleDescription = ResolveText(modifier.Arguments.GetValueOrDefault("_0", string.Empty));
                    break;
            }
        }
    }

    private static void ApplyFont(Control control, string swiftFont)
    {
        var (size, style) = swiftFont switch
        {
            ".largeTitle" => (28F, FontStyle.Regular),
            ".title" => (24F, FontStyle.Regular),
            ".title2" => (20F, FontStyle.Regular),
            ".headline" => (15F, FontStyle.Bold),
            ".subheadline" => (13F, FontStyle.Regular),
            ".caption" => (10F, FontStyle.Regular),
            _ => (12F, FontStyle.Regular)
        };
        control.Font = new Font("Segoe UI", size, style);
    }

    private void Execute(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            Log("Button action invoked.");
            return;
        }

        foreach (Match print in PrintExpression().Matches(body))
        {
            Log(ResolveText(print.Groups[1].Value));
        }

        var stateChanged = false;
        foreach (Match assignment in StateAssignment().Matches(body))
        {
            var name = assignment.Groups[1].Value;
            if (!_state.ContainsKey(name))
            {
                continue;
            }

            _state[name] = assignment.Groups[2].Value;
            Log($"State ${name} = {_state[name]}.");
            stateChanged = true;
        }

        if (stateChanged)
        {
            RenderApplication();
        }
    }

    private string ResolveText(string value)
    {
        var text = value;
        foreach (var state in _state)
        {
            text = text.Replace($"\\({state.Key})", state.Value, StringComparison.Ordinal);
        }

        return text.Trim('"');
    }

    private void RefreshBoundText()
    {
        foreach (var control in Descendants(_screen))
        {
            if (control is Label { Tag: BoundText binding } label)
            {
                label.Text = ResolveText(binding.Template);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        var stack = new Stack<Control>();
        foreach (Control child in root.Controls)
        {
            stack.Push(child);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (Control child in current.Controls)
            {
                stack.Push(child);
            }
        }
    }

    private void Log(string message)
    {
        _console.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        _console.SelectionStart = _console.TextLength;
        _console.ScrollToCaret();
    }

    private void PaintPhoneFrame(object? sender, PaintEventArgs eventArgs)
    {
        using var pen = new Pen(Color.FromArgb(70, 70, 74), 2);
        eventArgs.Graphics.DrawRoundedRectangle(pen, new Rectangle(1, 1, _phoneFrame.Width - 3, _phoneFrame.Height - 3), new Size(35, 35));
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

    private static int ParseInteger(string? value, int fallback) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static Color ResolveColor(string? value, Color fallback) => value?.Trim().TrimStart('.') switch
    {
        "red" => Color.IndianRed,
        "orange" => Color.DarkOrange,
        "yellow" => Color.Goldenrod,
        "green" => OrchardGreen,
        "blue" => Color.RoyalBlue,
        "purple" => Color.MediumPurple,
        "pink" => Color.HotPink,
        "gray" or "secondary" => Color.Gray,
        "primary" => fallback,
        "white" => Color.White,
        "black" => Color.Black,
        _ => fallback
    };

    [GeneratedRegex("print\\s*\\(\\s*\"([^\"]*)\"\\s*\\)")]
    private static partial Regex PrintExpression();

    [GeneratedRegex("(?:self\\.)?([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex StateAssignment();

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private sealed record DeviceChoice(DeviceProfile Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }

    private sealed record BoundText(string Template);
}
