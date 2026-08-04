using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Windows;

/// <summary>
/// The window's own controls: disconnect, pin, minimise and close, plus what is connected.
/// </summary>
/// <remarks>
/// <para>Before this there was no visible way out. The mirror window is borderless, permanently
/// always-on-top and has no title bar, so the only disconnect was a Shift-Escape shortcut described
/// on a screen you could not reach without already knowing the shortcut, and the only way to move
/// the window was an invisible 30x30 pixel square in a corner that the rounded region mostly cut
/// away.</para>
/// <para>It is an owned top-level window rather than a control inside the mirror. DWM composes the
/// video's flip-model swap chain for its own HWND independently of z-order within the parent — the
/// reason the setup overlay is a child of the form and not of the video host — so a sibling control
/// here would be betting on the same behaviour at a different depth, and would additionally need
/// per-pixel alpha to fade. An owned window's <see cref="Form.Opacity"/> is composited by DWM, so
/// the fade costs nothing and never touches the present path.</para>
/// </remarks>
internal sealed class ControlStripWindow : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>
    /// Opaque on purpose. A Form's BackColor cannot carry an alpha channel — assigning one throws
    /// "Control does not support transparent background colors" — so the strip's translucency comes
    /// from <see cref="Form.Opacity"/>, which DWM composites, and its shape from a region.
    /// </summary>
    private static readonly Color Bar = Palette.Panel;
    private static readonly Color Ink = Palette.TextPrimary;
    private static readonly Color Muted = Palette.TextSecondary;
    private static readonly Color Danger = Palette.Failure;

    /// <summary>What each button does. Order is left to right after the label.</summary>
    private enum Control
    {
        Disconnect,
        Pin,
        Minimise,
        Close,
    }

    private readonly System.Windows.Forms.Timer fade = new();
    private readonly ControlStripPolicy policy = new();
    private readonly Form owner;
    private readonly Rectangle[] hitBoxes = new Rectangle[4];

    private double opacity;
    private double target;
    private Control? hot;
    private bool pinned = true;

    internal ControlStripWindow(Form owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // No TransparencyKey. A colour key and Opacity both drive the layered-window style and
        // fighting over it left the strip never painting at all; the rounded corners come from a
        // region instead, set with the bounds.
        BackColor = Bar;
        Opacity = 0;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        // Slow enough to cost nothing, fast enough that the controls feel attached to the pointer.
        // This is a hit test and an opacity step, not the sixty-times-a-second full repaint the
        // setup surface runs.
        fade.Interval = 50;
        fade.Tick += (_, _) => Step();
    }

    /// <summary>Raised when the owner asks to stop mirroring but keep the window.</summary>
    internal event EventHandler? DisconnectRequested;

    /// <summary>Raised when the owner turns always-on-top off or back on.</summary>
    internal event EventHandler<bool>? PinChanged;

    /// <summary>What is connected, shown on the left of the strip.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string DeviceSummary { get; set; } = string.Empty;

    /// <summary>Whether the mirror window is pinned above everything else.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Pinned
    {
        get => pinned;
        set
        {
            pinned = value;
            Invalidate();
        }
    }

    /// <summary>Never steal focus from the phone by appearing.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    /// <summary>Follow the mirror window and begin watching for the reveal gesture.</summary>
    internal void Attach()
    {
        Owner = owner;
        Show();
        Reposition();
        fade.Start();
    }

    /// <summary>Sit across the top of the mirror window, inset so the rounded corners stay clear.</summary>
    internal void Reposition()
    {
        if (owner.IsDisposed || !owner.Visible)
        {
            return;
        }

        int inset = Math.Max(10, owner.Width / 26);
        int height = Math.Max(38, owner.Height / 20);
        Rectangle wanted = new(
            owner.Left + inset,
            owner.Top + inset,
            Math.Max(120, owner.Width - (inset * 2)),
            height);

        if (wanted == Bounds)
        {
            return;
        }

        bool resized = wanted.Size != Size;
        Bounds = wanted;

        // Only when the size actually changed.
        //
        // This is called from the owner's Move as well as its Resize, and the owner is dragged by
        // this very window, so a drag ran it on every mouse message. Rebuilding the region of a
        // layered window re-lays its shape and forces a full non-client repaint; doing that sixty
        // times a second under the pointer is what made the strip flicker while it was being used
        // to move the window. Moving needs new bounds and nothing else — the shape is identical.
        if (resized)
        {
            // Assign and let WinForms dispose the previous one. Disposing it here first leaves the
            // property holding a disposed object that the setter then disposes again.
            using GraphicsPath shape = Rounded(new Rectangle(0, 0, Width, Height), height / 2);
            Region = new Region(shape);
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fade.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Poll the pointer, ask the policy, and ease towards whatever it said.</summary>
    private void Step()
    {
        if (owner.IsDisposed)
        {
            return;
        }

        Point cursor = Cursor.Position;
        Rectangle ownerBounds = owner.Bounds;
        bool inside = ownerBounds.Contains(cursor);
        double fraction = ownerBounds.Height > 0
            ? (cursor.Y - ownerBounds.Top) / (double)ownerBounds.Height
            : 1;

        // Keep both windows where the pin says they should be, read from Windows rather than from
        // the Form property.
        //
        // TopMost is a cached bool: it goes on claiming true while the window has quietly been
        // demoted out of the always-on-top band, which another application can do to it simply by
        // making itself topmost. Comparing the property against itself could never notice that, and
        // "always on top sometimes doesn't work" is what it looks like from the outside. The style
        // bit is the truth, so that is what is checked, every tick, for a few nanoseconds.
        //
        // The strip follows the owner because topmost windows sit in a band above every ordinary
        // one, and a topmost owner covers its own owned window: it would stay mapped, correctly
        // placed and fully painted, with not one pixel of it on screen.
        if (Pinned && !NativeMethods.IsTopMost(owner.Handle))
        {
            NativeMethods.ReassertTopMost(owner.Handle);
        }

        if (TopMost != Pinned)
        {
            TopMost = Pinned;
        }
        else if (Pinned && !NativeMethods.IsTopMost(Handle))
        {
            NativeMethods.ReassertTopMost(Handle);
        }

        Control? previousHot = hot;
        hot = Visible && Bounds.Contains(cursor) ? HitTest(PointToClient(cursor)) : null;

        // Resting on a control holds the strip open: a button sits below the reveal band once the
        // pointer is on it, and fading out from under a press would make it unclickable.
        target = policy.Update(new StripInput(inside, fraction, hot is not null, DateTimeOffset.UtcNow))
            ? StripFade.Shown
            : 0;

        // Nothing to do once the fade has arrived, unless the pointer moved onto a different
        // control and the strip owes it a highlight.
        if (StripFade.Settled(opacity, target) && previousHot == hot)
        {
            return;
        }

        opacity = StripFade.Step(opacity, target);

        // Show and hide explicitly rather than assigning Visible. A hidden form does not reliably
        // keep an Opacity set while it was down, so the order matters: become visible first, then
        // set the level. Fully faded it is hidden outright rather than left at zero opacity, so it
        // cannot swallow a click meant for the phone underneath.
        if (opacity <= 0.01)
        {
            if (Visible)
            {
                Hide();
            }

            return;
        }

        if (!Visible)
        {
            Show();
        }

        Opacity = opacity;
        Invalidate();
    }

    /// <summary>
    /// What pointing at a control promises it will do. The pin reads as its next action rather than
    /// its current state, because "Keep on top" while it is already on top is a question, not a
    /// label — the glyph carries the state, the words carry the consequence.
    /// </summary>
    private string LabelFor(Control control) => control switch
    {
        Control.Disconnect => "Disconnect iPhone",
        Control.Pin => Pinned ? "Stop keeping on top" : "Keep on top",
        Control.Minimise => "Minimise",
        Control.Close => "Close Orchard Mirror",
        _ => DeviceSummary,
    };

    private Control? HitTest(Point point)
    {
        for (int index = 0; index < hitBoxes.Length; index++)
        {
            if (hitBoxes[index].Contains(point))
            {
                return (Control)index;
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = HitTest(e.Location) is null ? Cursors.SizeAll : Cursors.Hand;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            base.OnMouseDown(e);
            return;
        }

        Control? pressed = HitTest(e.Location);
        if (pressed is null)
        {
            // The bar itself is the window's grip. This replaces an invisible corner square that
            // the rounded region cut most of away.
            //
            // lParam stays 0 deliberately. It looks like a missing grab point, and passing the real
            // one was tried: DefWindowProc ignores it for HTCAPTION and tracks the live cursor, so
            // supplying a point anchors the move loop on stale coordinates and the window jumps the
            // moment the button goes down. Measured both ways — with 0 the window follows the
            // pointer exactly, twenty pixels per twenty pixels, and lands where it was dropped.
            NativeMethods.ReleaseCapture();
            _ = NativeMethods.SendMessageW(owner.Handle, NativeMethods.WmNcLButtonDown, NativeMethods.HtCaption, 0);
            return;
        }

        Activate(pressed.Value);
        base.OnMouseDown(e);
    }

    private void Activate(Control control)
    {
        switch (control)
        {
            case Control.Disconnect:
                DisconnectRequested?.Invoke(this, EventArgs.Empty);
                break;

            case Control.Pin:
                Pinned = !Pinned;
                PinChanged?.Invoke(this, Pinned);
                // The glyph and the label both describe the pin, and the pointer has not moved, so
                // nothing else here would ask for the repaint that shows it changed.
                Invalidate();
                break;

            case Control.Minimise:
                owner.WindowState = FormWindowState.Minimized;
                break;

            case Control.Close:
                owner.Close();
                break;

            default:
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Rectangle bounds = new(0, 0, Width - 1, Height - 1);
        using (GraphicsPath shape = Rounded(bounds, Height / 2))
        using (SolidBrush fill = new(Bar))
        using (Pen rim = new(Palette.HairlineStrong, 1f))
        {
            graphics.FillPath(fill, shape);
            graphics.DrawPath(rim, shape);
        }

        int size = Height - 14;
        int gap = 6;
        int right = Width - 10 - size;
        for (int index = hitBoxes.Length - 1; index >= 0; index--)
        {
            hitBoxes[index] = new Rectangle(right, (Height - size) / 2, size, size);
            right -= size + gap;
        }

        // Four unlabelled glyphs on a bar are a guessing game -- a power ring in particular reads as
        // "turn the phone off" rather than "let go of it". The strip already owns a line of text, so
        // pointing at a control spends it on saying what that control does, and it costs no space.
        string caption = hot is { } hovered ? LabelFor(hovered) : DeviceSummary;
        if (caption.Length > 0)
        {
            Rectangle label = new(14, 0, Math.Max(0, hitBoxes[0].Left - 22), Height);
            TextRenderer.DrawText(
                graphics,
                caption,
                Font,
                label,
                hot is null ? Muted : Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding);
        }

        for (int index = 0; index < hitBoxes.Length; index++)
        {
            PaintControl(graphics, (Control)index, hitBoxes[index]);
        }
    }

    private void PaintControl(Graphics graphics, Control control, Rectangle box)
    {
        bool isHot = hot == control;
        if (isHot)
        {
            using SolidBrush wash = new(Color.FromArgb(46, 255, 255, 255));
            using GraphicsPath shape = Rounded(box, box.Height / 3);
            graphics.FillPath(wash, shape);
        }

        Color ink = control == Control.Disconnect && isHot ? Danger : Ink;
        if (control == Control.Pin && !Pinned)
        {
            ink = Muted;
        }

        RectangleF mark = RectangleF.Inflate(box, -box.Width * 0.30f, -box.Height * 0.30f);
        using Pen stroke = new(ink, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (control)
        {
            case Control.Disconnect:
                // A power symbol: the break in the ring is the point, so it does not read as "stop".
                graphics.DrawArc(stroke, mark.X, mark.Y, mark.Width, mark.Height, -60, 300);
                graphics.DrawLine(stroke, mark.X + (mark.Width / 2), mark.Y - 1, mark.X + (mark.Width / 2), mark.Y + (mark.Height * 0.45f));
                break;

            case Control.Pin:
                graphics.DrawLine(stroke, mark.X + (mark.Width / 2), mark.Y + (mark.Height * 0.55f), mark.X + (mark.Width / 2), mark.Bottom);
                graphics.DrawEllipse(stroke, mark.X + (mark.Width * 0.18f), mark.Y, mark.Width * 0.64f, mark.Height * 0.62f);
                if (!Pinned)
                {
                    graphics.DrawLine(stroke, mark.X, mark.Bottom, mark.Right, mark.Y);
                }

                break;

            case Control.Minimise:
                graphics.DrawLine(stroke, mark.X, mark.Y + (mark.Height / 2), mark.Right, mark.Y + (mark.Height / 2));
                break;

            case Control.Close:
                graphics.DrawLine(stroke, mark.X, mark.Y, mark.Right, mark.Bottom);
                graphics.DrawLine(stroke, mark.Right, mark.Y, mark.X, mark.Bottom);
                break;

            default:
                break;
        }
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(2, radius * 2);
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
