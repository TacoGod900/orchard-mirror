using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Windows;

/// <summary>
/// The small self-dismissing note that appears over a running mirror.
/// </summary>
/// <remarks>
/// <para>This is the surface <see cref="StatusRouter"/> has been routing to since it was written.
/// Until now nothing drew one, so everything the router judged routine — the phone locking, sound
/// starting, a reconnection that succeeded — was silent. Silent was the right behaviour while the
/// only alternative was covering live video with a full-surface panel, but it meant the mirror
/// never acknowledged anything that did not stop it working.</para>
/// <para>An owned top-level window, for the reason recorded on <see cref="ControlStripWindow"/>:
/// DWM composes the video's flip-model swap chain for its own HWND independently of z-order within
/// the parent, so a sibling control would be painted and then buried. An owned window's
/// <see cref="Form.Opacity"/> is composited by DWM, so the fade never touches the present path.</para>
/// <para>It is also click-through. A note that appears over the phone for two seconds must not be
/// able to swallow a tap meant for the screen underneath it, and a toast has nothing to click.</para>
/// </remarks>
internal sealed class ToastWindow : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>Never take the pointer. The phone is underneath.</summary>
    private const int WsExTransparent = 0x00000020;

    /// <summary>How often the fade steps. The same interval as the control strip.</summary>
    private const int MotionInterval = 16;

    private static readonly Color Panel = Palette.Panel;

    private readonly System.Windows.Forms.Timer motion = new();
    private readonly ToastPolicy policy = new();
    private readonly Form owner;

    private Font? headlineFont;
    private long lastTickMilliseconds;
    private Size lastRegionSize;

    internal ToastWindow(Form owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Panel;
        Opacity = 0;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint,
            true);

        motion.Interval = MotionInterval;
        motion.Tick += (_, _) => Step();
    }

    /// <summary>Never steal focus from the phone by appearing.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent;
            return parameters;
        }
    }

    /// <summary>Become the mirror window's toast surface.</summary>
    internal void Attach()
    {
        Owner = owner;
        lastTickMilliseconds = Environment.TickCount64;
    }

    /// <summary>
    /// Offer one routed presentation.
    /// </summary>
    /// <remarks>
    /// Handed everything, not just toasts: a full-surface status dismisses whatever note is up,
    /// which is a decision <see cref="ToastPolicy"/> owns rather than one each caller repeats.
    /// </remarks>
    internal void Present(StatusPresentation presentation)
    {
        if (IsDisposed || owner.IsDisposed)
        {
            return;
        }

        if (policy.Present(presentation))
        {
            // Size and place it for the new words before the first frame of the fade. Reposition
            // decides for itself whether the shape actually changed, so a note that happens to
            // measure the same width as the last one costs nothing.
            Reposition();
        }

        // Ticking only while something is moving keeps an idle mirror at no timers at all. The
        // clock is reset with it so the first step after an idle period is one interval and not
        // however long the window sat quiet, which would otherwise skip the whole fade.
        if (!motion.Enabled)
        {
            lastTickMilliseconds = Environment.TickCount64;
            motion.Start();
        }
    }

    /// <summary>Drop any note immediately. For teardown, where there is nothing left to fade over.</summary>
    internal void Clear()
    {
        policy.Clear();
        motion.Stop();
        if (!IsDisposed && Visible)
        {
            Hide();
        }
    }

    /// <summary>Sit across the bottom of the mirror window, clear of the rounded corners.</summary>
    /// <remarks>
    /// The bottom because the control strip owns the top. Two things fading in and out of the same
    /// band would collide precisely when both are wanted — a status arriving while the pointer is
    /// over the controls is the ordinary case, not the rare one.
    /// </remarks>
    internal void Reposition()
    {
        // Phase rather than visibility: a note that has been accepted but has not yet faded up is
        // at zero opacity, and it has to be sized and placed before it is shown or it arrives in
        // last time's position at this time's width.
        if (IsDisposed || owner.IsDisposed || !owner.Visible || policy.Phase == ToastPhase.Hidden)
        {
            return;
        }

        int margin = Math.Max(12, owner.Width / 22);
        int available = Math.Max(120, owner.Width - (margin * 2));
        int width = Math.Min(available, Math.Max(140, DesiredWidth()));
        int height = DesiredHeight();

        Rectangle wanted = new(
            owner.Left + ((owner.Width - width) / 2),
            owner.Bottom - height - Math.Max(18, owner.Height / 16),
            width,
            height);

        if (wanted != Bounds)
        {
            Bounds = wanted;
        }

        // Only when the size actually changed, for the reason recorded on the control strip's
        // Reposition: rebuilding the region of a layered window forces a full non-client repaint,
        // and this runs from the owner's Move as well as its Resize. Kept separate from the bounds
        // check above because a move needs new bounds and the identical shape.
        if (wanted.Size != lastRegionSize)
        {
            lastRegionSize = wanted.Size;
            using GraphicsPath shape = Rounded(new Rectangle(0, 0, Width, Height), Height / 2);
            Region = new Region(shape);
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            motion.Dispose();
            headlineFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        headlineFont?.Dispose();
        headlineFont = new Font(Font, FontStyle.Bold);
        base.OnFontChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!policy.Visible)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        StatusPresentation shown = policy.Content;
        Rectangle bounds = new(0, 0, Width - 1, Height - 1);

        using (GraphicsPath shape = Rounded(bounds, bounds.Height / 2))
        using (SolidBrush fill = new(Panel))
        using (Pen rim = new(Palette.HairlineStrong, 1f))
        {
            graphics.FillPath(fill, shape);
            graphics.DrawPath(rim, shape);
        }

        int inset = Height / 4;
        int markSize = Height / 3;
        Rectangle mark = new(inset, (Height - markSize) / 2, markSize, markSize);
        PaintGlyph(graphics, shown, mark);

        int textLeft = mark.Right + (inset / 2);
        Rectangle text = new(textLeft, 0, Math.Max(0, Width - textLeft - inset), Height);
        bool hasDetail = shown.Detail.Length > 0;

        if (!hasDetail)
        {
            TextRenderer.DrawText(
                graphics,
                shown.Headline,
                headlineFont ?? Font,
                text,
                Palette.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding);
            return;
        }

        int half = Height / 2;
        TextRenderer.DrawText(
            graphics,
            shown.Headline,
            headlineFont ?? Font,
            new Rectangle(text.Left, text.Top + (Height / 8), text.Width, half - (Height / 10)),
            Palette.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            shown.Detail,
            Font,
            new Rectangle(text.Left, half, text.Width, half - (Height / 8)),
            Palette.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// The mark beside the words, stroked in the same idiom as the control strip's controls.
    /// </summary>
    /// <remarks>
    /// Colour comes from the presentation's accent, which follows severity alone, so the same
    /// seriousness always looks the same here as it does on the full surface.
    /// </remarks>
    private static void PaintGlyph(Graphics graphics, StatusPresentation shown, Rectangle box)
    {
        Color ink = shown.Accent switch
        {
            ColorRole.Progress => Palette.Accent,
            ColorRole.Warning => Palette.Warning,
            ColorRole.Failure => Palette.Failure,
            ColorRole.TextSecondary => Palette.TextSecondary,
            _ => Palette.TextPrimary,
        };

        using Pen stroke = new(ink, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        RectangleF mark = box;
        switch (shown.Glyph)
        {
            case GlyphRole.Ready:
                graphics.DrawLines(
                    stroke,
                    [
                        new PointF(mark.Left, mark.Top + (mark.Height * 0.55f)),
                        new PointF(mark.Left + (mark.Width * 0.36f), mark.Bottom - (mark.Height * 0.16f)),
                        new PointF(mark.Right, mark.Top + (mark.Height * 0.18f)),
                    ]);
                break;

            case GlyphRole.Asleep:
                // A crescent: the phone is dark, not broken.
                graphics.DrawArc(stroke, mark.X, mark.Y, mark.Width, mark.Height, 40, 280);
                break;

            case GlyphRole.Blocked:
                graphics.DrawLine(
                    stroke,
                    mark.X + (mark.Width / 2),
                    mark.Y,
                    mark.X + (mark.Width / 2),
                    mark.Y + (mark.Height * 0.58f));
                graphics.DrawLine(
                    stroke,
                    mark.X + (mark.Width / 2),
                    mark.Bottom - 1,
                    mark.X + (mark.Width / 2),
                    mark.Bottom);
                break;

            case GlyphRole.Working:
            case GlyphRole.Display:
            case GlyphRole.Link:
            default:
                // An open ring, which reads as under way without needing to turn: a toast is gone
                // in under three seconds, and a spinner that completes half a revolution in its
                // whole life looks like a stall rather than like progress.
                graphics.DrawArc(stroke, mark.X, mark.Y, mark.Width, mark.Height, -50, 280);
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

    private int DesiredHeight()
    {
        int line = Math.Max(16, Font.Height);
        bool hasDetail = policy.Content.Detail.Length > 0;
        int wanted = hasDetail ? (line * 2) + (line / 2) : line + line;
        return Math.Max(34, wanted);
    }

    /// <summary>
    /// Measured against a screen device context rather than this window's own.
    /// </summary>
    /// <remarks>
    /// <c>CreateGraphics</c> would force the handle into existence, and the first status can be
    /// routed here before <see cref="Attach"/> has run — which would create the window before it
    /// has an owner, briefly as a top-level one of its own.
    /// </remarks>
    private int DesiredWidth()
    {
        StatusPresentation shown = policy.Content;
        Size headline = TextRenderer.MeasureText(
            shown.Headline,
            headlineFont ?? Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        int widest = headline.Width;
        if (shown.Detail.Length > 0)
        {
            Size detail = TextRenderer.MeasureText(
                shown.Detail,
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            widest = Math.Max(widest, detail.Width);
        }

        int height = DesiredHeight();
        int inset = height / 4;
        int mark = height / 3;
        return widest + mark + (inset / 2) + (inset * 2) + 8;
    }

    /// <summary>Advance the fade, and show or hide the window as it crosses either end.</summary>
    private void Step()
    {
        if (owner.IsDisposed || IsDisposed)
        {
            motion.Stop();
            return;
        }

        long now = Environment.TickCount64;
        TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - lastTickMilliseconds));
        lastTickMilliseconds = now;

        ToastFrame frame = policy.Advance(elapsed);

        if (frame.Phase == ToastPhase.Hidden)
        {
            if (Visible)
            {
                Hide();
            }

            // Nothing is moving and nothing is shown, so stop asking.
            motion.Stop();
            return;
        }

        // Follow the owner into and out of the always-on-top band, for the reason recorded on the
        // control strip: a topmost owner covers its own owned window outright.
        if (TopMost != owner.TopMost)
        {
            TopMost = owner.TopMost;
        }

        Reposition();

        if (!Visible)
        {
            Show();
        }

        Opacity = frame.Opacity;
        Invalidate();
    }
}
