using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Windows;

/// <summary>
/// The full-surface setup and connection screen that stands in front of the mirrored phone.
/// </summary>
/// <remarks>
/// <para>This replaces the floating status pill. A pill had to sit on top of the picture forever,
/// because there was nowhere else for "connecting", "retrying" or "asleep" to live; giving those
/// states the whole surface means the mirror itself can be nothing but the phone.</para>
/// <para>Everything is drawn, and drawn flat. The backdrop used to be four saturated lobes of
/// coloured light drifting behind a sheet of glass, with the primary button rendered as a lens that
/// magnified them; the material is now a single near-black with a barely-there falloff, and the
/// button is a solid shape. That is a decision about what the product should look like rather than
/// a performance one, but it happens to remove the per-frame bitmap composite as well.</para>
/// </remarks>
internal sealed class OnboardingView : Control
{
    private static readonly Color Base = Palette.Surface;
    private static readonly Color TextPrimary = Palette.TextPrimary;
    private static readonly Color TextSecondary = Palette.TextSecondary;
    private static readonly Color Warning = Palette.Warning;
    private static readonly Color Failure = Palette.Failure;

    /// <summary>The mark drawn in the medallion above each page's title.</summary>
    private enum Glyph
    {
        Display,
        Toggle,
        Link,
        Unlocked,
        Check,
    }

    /// <summary>
    /// The two first-run pages, which are the only content this view still owns.
    /// </summary>
    /// <remarks>
    /// Everything else it draws now arrives from <see cref="StatusRouter"/> as a
    /// <see cref="StatusPresentation"/>. There used to be a five-value phase enum here that doubled
    /// as connection state, and it latched: once a frame had arrived it stuck on its Ready value
    /// forever, so every later message was drawn under that value's hardcoded "All set" headline.
    /// These two pages are genuinely static copy shown before any connection exists, so they are
    /// the one thing that does not need routing.
    /// </remarks>
    internal enum WalkthroughPage
    {
        /// <summary>The opening page, with a way past it for anyone who has done this before.</summary>
        Welcome,

        /// <summary>Waiting for a cable, because AMFI is reachable over USB and nothing else.</summary>
        WaitingForCable,
    }

    /// <summary>The kicker's ink.</summary>
    private static readonly Color KickerInk = Palette.TextTertiary;

    /// <summary>Frame interval while something is genuinely moving.</summary>
    private const int MotionInterval = 16;

    /// <summary>
    /// How long a page slide takes, and so how quickly a replacement counts as rushed.
    /// </summary>
    /// <remarks>
    /// Derived from the animation rather than picked: the slide advances 0.05 of its progress per
    /// frame at <see cref="MotionInterval"/>, which is twenty frames.
    /// </remarks>
    private const long SlideDurationMilliseconds = 20 * MotionInterval;

    private readonly System.Windows.Forms.Timer animation = new();
    private readonly TypeSetter setter = new();
    private double clock;
    private long lastPageArrivalTicks = long.MinValue / 2;
    private double slideFrom;
    private double slideProgress = 1.0;
    private double revealProgress;
    private bool revealing;
    private double holdElapsed;
    private StatusPresentation current = WelcomePage();
    private string primaryLabel = "Get started";
    private bool showSkip = true;
    private bool walkthrough = true;
    private Rectangle primaryButton;
    private Rectangle secondaryButton;
    private double primaryHot;
    private double secondaryHot;
    private double primaryTarget;
    private double secondaryTarget;
    private bool primaryDown;
    private bool pressedPrimary;
    private bool pressedSecondary;

    internal OnboardingView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Base;
        animation.Interval = MotionInterval;
        animation.Tick += (_, _) => Advance();
    }

    /// <summary>Raised when the walkthrough is finished and connecting should begin.</summary>
    internal event EventHandler? SetupCompleted;

    /// <summary>Raised when the user asks to retry a failed connection.</summary>
    internal event EventHandler? RetryRequested;

    /// <summary>Raised when the owner says they have done this before and wants to skip ahead.</summary>
    internal event EventHandler? SetupSkipped;

    /// <summary>Raised when the owner asks to wake a sleeping phone.</summary>
    internal event EventHandler? WakeRequested;

    /// <summary>
    /// Show one routed status.
    /// </summary>
    /// <remarks>
    /// <para>The important half of this method is what it does <b>not</b> do. Only a
    /// <see cref="StatusSurface.Full"/> presentation is allowed to make this control visible;
    /// anything the router judged routine leaves the picture alone. The method it replaces made
    /// itself visible and brought itself to the front unconditionally, so a phone locking — which it
    /// keeps streaming through — dropped a full sheet of glass over live video.</para>
    /// <para>It also stops resetting the animation when nothing has changed. The old one cleared the
    /// reveal progress on every call, so a status arriving mid-reveal snapped the panel back to the
    /// top of the window with a visible jump.</para>
    /// </remarks>
    internal void Present(StatusPresentation presentation)
    {
        if (presentation.Surface != StatusSurface.Full)
        {
            // Toast has no surface of its own yet; until it does, routine news simply does not
            // interrupt. That is already the behaviour worth having.
            return;
        }

        bool hidden = !Visible || revealing;

        // A page whose whole job is to get out of the way must not put itself in the way first.
        //
        // "All set" is the handover at the end of a cold start: it is shown for a moment over the
        // surface the owner has been watching, and then slides off to uncover the phone. It only
        // means anything if something was covering the phone to begin with. Once a reconnection can
        // complete without ever raising this surface -- which is the point of holding the last frame
        // -- the first frame back would otherwise raise a full panel, hold it, and slide it away
        // again, turning a recovery the owner would not have noticed into a flash of chrome at the
        // exact moment the picture returned.
        if (hidden && presentation.AutoDismiss is not null)
        {
            return;
        }

        bool differentPage = current.Kicker != presentation.Kicker
            || current.Headline != presentation.Headline;

        current = presentation;
        primaryLabel = presentation.ActionLabel;
        showSkip = false;
        walkthrough = false;
        holdElapsed = 0;

        // A page that replaces one which has only just arrived swaps in place instead of sliding.
        //
        // A good connection walks through "Looking for iPhone", "Opening secure iPhone tunnel",
        // "CoreDevice connected" and "All set" in well under a second. Each of those started its own
        // 320-millisecond slide, so what the owner saw was not four messages but a flurry of
        // half-finished animations going past too quickly to read — which looks broken, and looks
        // worse the better the connection is. The words still change; only the movement is dropped.
        long now = Environment.TickCount64;
        bool rushed = now - lastPageArrivalTicks < SlideDurationMilliseconds;
        lastPageArrivalTicks = now;

        if (hidden || (differentPage && !rushed))
        {
            slideFrom = 0.4;
            slideProgress = 0;
        }
        else if (differentPage)
        {
            // Settled, so the new text is drawn at full opacity in its final position.
            slideProgress = 1;
        }

        if (hidden)
        {
            revealing = false;
            revealProgress = 0;
            Top = 0;
        }

        Visible = true;
        BringToFront();
        Wake();
    }

    /// <summary>Slide out of the way, revealing the live phone underneath.</summary>
    /// <remarks>
    /// The picture is already being presented behind this control, so this uncovers a running mirror
    /// rather than fading into an empty panel. Called on every frame update, so it is idempotent.
    /// </remarks>
    internal void RevealMirror()
    {
        if (!Visible || revealing)
        {
            return;
        }

        revealing = true;
        revealProgress = 0;
        Wake();
    }

    /// <summary>Show one of the two first-run pages.</summary>
    internal void ShowWalkthrough(WalkthroughPage page)
    {
        current = page == WalkthroughPage.Welcome ? WelcomePage() : CablePage();
        primaryLabel = page == WalkthroughPage.Welcome ? "Get started" : string.Empty;
        showSkip = page == WalkthroughPage.Welcome;
        walkthrough = true;
        holdElapsed = 0;
        slideFrom = 0.35;
        slideProgress = 0;
        revealing = false;
        revealProgress = 0;
        Top = 0;
        Visible = true;
        BringToFront();
        Wake();
    }

    /// <summary>
    /// The opening page. Static copy, shown before any connection exists.
    /// </summary>
    /// <remarks>
    /// <para>Three things changed here, and none of them is decoration.</para>
    /// <para>The kicker was the word "ORCHARD" set above the headline. A tracked-out brand name over
    /// a headline is landing-page grammar; on the one screen the owner has already opened the
    /// application to reach, it tells them nothing they do not know. Status pages keep their kicker,
    /// because "SETTING UP" over a headline is a state and not a masthead.</para>
    /// <para>The headline carried a hard line break, which is the one thing <see cref="TextFlow"/>
    /// exists to decide, and a comma splice with it.</para>
    /// <para>The body now says what the three steps are. It used to be one sentence and a promise,
    /// which left most of the window empty and answered none of the questions somebody plugging in a
    /// phone for the first time actually has.</para>
    /// </remarks>
    private static StatusPresentation WelcomePage() => new(
        StatusSurface.Full,
        string.Empty,
        "Your iPhone on this PC",
        "Its screen, its sound and its touchscreen, in a window you can put anywhere.",
        ColorRole.TextPrimary,
        Spinner: false,
        GlyphRole.Display,
        StatusActionKind.None,
        ActionLabel: string.Empty,
        AutoDismiss: null,
        Rows:
        [
            new FeatureRow((int)Glyph.Link, "Plug it in", "Any USB cable will do."),
            new FeatureRow((int)Glyph.Check, "Tap Trust", "The iPhone asks once, on its own screen."),
            new FeatureRow((int)Glyph.Display, "That is all", "Orchard turns on Developer Mode and connects."),
        ]);

    /// <summary>Waiting for a cable, because AMFI is reachable over USB and nothing else.</summary>
    private static StatusPresentation CablePage() => new(
        StatusSurface.Full,
        "SETUP",
        "Plug in your iPhone",
        "Any USB cable will do."
            + "\n\nOrchard turns on Developer Mode, restarts the phone, and connects "
            + "once it is back. This takes about a minute.",
        ColorRole.TextPrimary,
        Spinner: true,
        GlyphRole.Link,
        StatusActionKind.None,
        ActionLabel: string.Empty,
        AutoDismiss: null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            setter.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool overPrimary = primaryButton.Contains(e.Location);
        bool overSecondary = secondaryButton.Contains(e.Location);
        Cursor = overPrimary || overSecondary ? Cursors.Hand : Cursors.Default;

        double wantPrimary = overPrimary ? 1 : 0;
        double wantSecondary = overSecondary ? 1 : 0;
        if (wantPrimary != primaryTarget || wantSecondary != secondaryTarget)
        {
            primaryTarget = wantPrimary;
            secondaryTarget = wantSecondary;
            Wake();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        // The press-origin flags are cleared here too. Leaving them set meant a press that began on
        // a button, wandered off the control and came back could still fire it on release.
        primaryDown = false;
        pressedPrimary = false;
        pressedSecondary = false;
        primaryTarget = 0;
        secondaryTarget = 0;
        Wake();
        base.OnMouseLeave(e);
    }

    /// <summary>
    /// Suppressed: every pixel is overpainted by the light field before anything else is drawn.
    /// </summary>
    /// <remarks>
    /// Without this the buffer is cleared to the background colour and then covered completely,
    /// which is a full-surface fill per frame for no visible result.
    /// </remarks>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            pressedPrimary = primaryButton.Contains(e.Location);
            pressedSecondary = secondaryButton.Contains(e.Location);
            primaryDown = pressedPrimary;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        primaryDown = false;
        if (e.Button != MouseButtons.Left)
        {
            base.OnMouseUp(e);
            return;
        }


        // The press has to have started on the button too. Releasing over a control that was not
        // the one pressed should do nothing, and without this a stray click landing anywhere in the
        // window on release advanced the page.
        if (pressedPrimary && primaryButton.Contains(e.Location))
        {
            OnPrimaryPressed();
        }
        else if (pressedSecondary && secondaryButton.Contains(e.Location) && showSkip)
        {
            SetupSkipped?.Invoke(this, EventArgs.Empty);
        }

        pressedPrimary = false;
        pressedSecondary = false;
        base.OnMouseUp(e);
    }

    protected override void OnResize(EventArgs e)
    {
        Invalidate();
        base.OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        setter.SetDpi(DeviceDpi / 96.0);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // Content slides and fades between pages; the light behind it never moves with them, which
        // is what makes the panel feel like it is floating over something rather than scrolling.
        double eased = EaseOutCubic(slideProgress);
        int offset = (int)(slideFrom * Width * (1.0 - eased));
        int alpha = (int)(255 * Math.Clamp(eased, 0, 1) * (1.0 - EaseInCubic(revealProgress)));

        // Hinted either way, which is the actual fix for type that read thin and blurry. The old
        // code set plain AntiAlias unconditionally -- grayscale, unhinted, so stems land on
        // fractional pixels -- to avoid ClearType fringing during a fade. ClearType is only a
        // problem while the surface is genuinely translucent, so it is used whenever it is not.
        graphics.TextRenderingHint = alpha >= 250
            ? TextRenderingHint.ClearTypeGridFit
            : TextRenderingHint.AntiAliasGridFit;

        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        PaintSurface(graphics);

        GraphicsState saved = graphics.Save();
        graphics.TranslateTransform(offset, 0);
        PaintPhase(graphics, alpha);
        graphics.Restore(saved);
    }

    /// <summary>
    /// Rebuild everything sized against the display when the window moves to a different one.
    /// </summary>
    /// <remarks>
    /// There was no handler for this at all, so dragging the window between displays of different
    /// scale left type sized for the one it started on.
    /// </remarks>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        setter.SetDpi(DeviceDpi / 96.0);
        Invalidate();
        base.OnDpiChangedAfterParent(e);
    }

    private void OnPrimaryPressed()
    {
        if (walkthrough)
        {
            SetupCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (current.Action)
        {
            case StatusActionKind.Wake:
                WakeRequested?.Invoke(this, EventArgs.Empty);
                break;
            case StatusActionKind.Reconnect:
                RetryRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }

    private void Advance()
    {
        // Advanced by the interval actually in force, not by a hardcoded frame time, or the
        // spinner and the drifting light run at different speeds depending on the rate.
        double elapsed = animation.Interval / 1000.0;
        clock += elapsed;
        if (slideProgress < 1.0)
        {
            slideProgress = Math.Min(1.0, slideProgress + 0.05);
        }

        if (revealing)
        {
            revealProgress += 0.045;
            if (revealProgress >= 1.0)
            {
                revealing = false;
                revealProgress = 0;
                Visible = false;
                Idle();
                return;
            }
        }

        // A presentation that carries an expiry hands the screen back when it runs out. For the
        // "all set" page that means the mirror is uncovered: the picture is already live behind
        // this, so every extra frame here is a frame of the phone being withheld.
        if (current.AutoDismiss is { } hold && !revealing)
        {
            holdElapsed += elapsed;
            if (holdElapsed >= hold.TotalSeconds)
            {
                RevealMirror();
            }
        }

        // Hover eases towards whatever the last mouse event said. It used to be read here instead,
        // by P/Invoking the cursor position and mapping it into client space every sixteen
        // milliseconds -- for a control that already overrides OnMouseMove, and whether or not the
        // pointer had moved at all, or the surface was even on screen.
        primaryHot = HoverEasing.Step(primaryHot, primaryTarget);
        secondaryHot = HoverEasing.Step(secondaryHot, secondaryTarget);

        Invalidate();

        // Nothing on this surface drifts on its own any more, so "nothing is moving" can stop the
        // timer outright rather than drop it to a slower rate. The ambient rate existed only to
        // keep the light field creeping.
        if (!Visible || !AnimationPolicy.ShouldAnimate(Motion))
        {
            Idle();
        }
    }

    /// <summary>What is currently moving, if anything.</summary>
    private AnimationNeed Motion => new(
        Sliding: slideProgress < 1.0,
        Revealing: revealing,
        Spinning: current.Spinner,
        HoverSettling: !HoverEasing.Settled(primaryHot, primaryTarget)
            || !HoverEasing.Settled(secondaryHot, secondaryTarget),
        Holding: current.AutoDismiss is not null);

    /// <summary>
    /// Stop repainting until something asks for motion again.
    /// </summary>
    /// <remarks>
    /// The timer used to be stopped in exactly one place, after a reveal finished, so the opening
    /// page and any page reporting a failure it had already given up on repainted the whole surface
    /// sixty times a second indefinitely.
    /// </remarks>
    private void Idle()
    {
        animation.Stop();
        Top = 0;
    }

    /// <summary>Repaint once, and keep repainting only while something is actually moving.</summary>
    /// <remarks>
    /// A settled page now costs nothing at all. It used to keep a slow timer running for ever so the
    /// light behind the glass could drift; with a flat surface there is nothing left to drift, so a
    /// page that has stopped genuinely stops.
    /// </remarks>
    private void Wake()
    {
        if (!Visible)
        {
            Idle();
            return;
        }

        animation.Interval = MotionInterval;
        if (AnimationPolicy.ShouldAnimate(Motion))
        {
            animation.Start();
        }
        else
        {
            animation.Stop();
        }

        Invalidate();
    }

    /// <summary>
    /// Fill the surface: one near-black, lifted very slightly at the top.
    /// </summary>
    /// <remarks>
    /// The falloff spans about ten values out of 255. That is below the threshold at which anyone
    /// reads it as a gradient, and above the one at which a tall window reads as a flat void. It is
    /// the whole of what replaced four drifting lobes, a scaled bitmap and a scrim.
    /// </remarks>
    private void PaintSurface(Graphics graphics)
    {
        int width = Math.Max(1, Width);
        int height = Math.Max(1, Height);

        // The brush rectangle is a pixel taller than the fill on both edges. A GDI+ linear gradient
        // wraps at its own bounds, so a brush sized exactly to the fill puts a hairline of the
        // opposite end colour along the first and last row.
        using LinearGradientBrush surface = new(
            new Rectangle(0, -1, width, height + 2),
            Palette.SurfaceLift,
            Palette.Surface,
            LinearGradientMode.Vertical);
        graphics.FillRectangle(surface, ClientRectangle);
    }

    /// <summary>
    /// Draw the page from boxes that were worked out beforehand, rather than accumulating a running
    /// y down the page and hoping.
    /// </summary>
    private void PaintPhase(Graphics graphics, int alpha)
    {
        TypeScale scale = Scale();
        PageContent content = new(
            current.Kicker,
            current.Headline,
            current.Detail,

            // A page carrying feature rows has its marks in the rows; a second one floating above
            // the headline would be the fourth icon on a screen that is meant to have three.
            ShowMedallion: current.FeatureRows.Count == 0,
            current.Spinner,
            primaryLabel,
            showSkip ? "I've done this before" : null,
            current.FeatureRows);

        OnboardingBoxes boxes = OnboardingLayout.Compute(
            Width / setter.DpiScale,
            Height / setter.DpiScale,
            content,
            scale,
            role => setter.Measure(graphics, scale.For(role)));

        // The buttons the hit-tester uses are the ones that were just drawn, so a press can never
        // land on a rectangle the paint pass did not agree with.
        primaryButton = boxes.Primary is { } primary ? Rectangle.Round(setter.Rect(primary)) : Rectangle.Empty;
        secondaryButton = boxes.Secondary is { } secondary ? Rectangle.Round(setter.Rect(secondary)) : Rectangle.Empty;

        // The layout places everything against the left margin. Centring is a drawing decision, so
        // the mark and the ring beside it are moved as one group rather than each to the middle,
        // which would stack them on top of each other.
        double markShift = CentreOffset(
            boxes.Title.X,
            boxes.Title.Width,
            boxes.Medallion?.X ?? boxes.Spinner?.X ?? 0,
            boxes.Spinner?.Right ?? boxes.Medallion?.Right ?? 0);

        if (boxes.Medallion is { } medallion)
        {
            PaintMark(
                graphics,
                Rectangle.Round(setter.Rect(medallion with { X = medallion.X + markShift })),
                Mark(current.Glyph),
                Color.FromArgb(alpha, TextPrimary));
        }

        // The router decides whether anything turns: a state that has stopped trying must not keep
        // spinning, and one that is still trying must not sit there looking frozen.
        if (boxes.Spinner is { } spinner)
        {
            RectangleF ring = setter.Rect(spinner with { X = spinner.X + markShift });
            PaintSpinner(graphics, new PointF(ring.X + (ring.Width / 2), ring.Y + (ring.Height / 2)), ring.Width / 2, alpha);
        }

        if (boxes.Kicker is { } kickerBox)
        {
            DrawTracked(graphics, boxes.KickerText, scale.For(TypeRole.Kicker), kickerBox, KickerInk, alpha, centred: true);
        }

        // Headline and body centred, feature rows left-aligned. That asymmetry looks like an
        // oversight written down and is not one: it is what Apple's welcome sheets do, and the
        // reason is that centring is right for one or two lines being announced and wrong for a
        // column of rows, where a ragged left edge destroys the scan down the marks.
        DrawLines(graphics, boxes.TitleText, scale.For(TypeRole.Title), boxes.Title, Resolve(current.Accent), alpha, centred: true);
        DrawLines(graphics, boxes.BodyText, scale.For(TypeRole.Body), boxes.Body, TextSecondary, alpha, centred: true);
        PaintRows(graphics, boxes.Rows, scale, alpha);

        if (boxes.Primary is not null)
        {
            PaintButtons(graphics, primaryLabel, showSkip ? "I've done this before" : null, alpha);
        }
    }

    /// <summary>
    /// Draw each line at the top the layout gave it.
    /// </summary>
    /// <remarks>
    /// One call per line into a box of its own, so nothing can bleed past the block it belongs to.
    /// The single call this replaces was given <c>NoClip</c> and word trimming, which between them
    /// let the last partial line overhang the buttons while the rest of the text disappeared with
    /// no mark at all.
    /// </remarks>
    private void DrawLines(
        Graphics graphics,
        TextLayout text,
        TypeStyle style,
        Box block,
        Color colour,
        int alpha,
        bool centred = false)
    {
        if (text.Lines.Count == 0)
        {
            return;
        }

        Font font = setter.Font(style);
        using SolidBrush brush = new(Color.FromArgb(alpha, colour));
        float left = setter.Px(block.X);
        float width = setter.Px(block.Width);

        foreach (TextLine line in text.Lines)
        {
            if (line.Text.Length == 0)
            {
                continue;
            }

            // Centred line by line rather than by handing the whole block to a centring
            // StringFormat, because the block is already broken into lines here and letting GDI+
            // re-flow it would break it a second time, at a different width.
            float x = left;
            if (centred)
            {
                x += (width - Advance(graphics, font, line.Text)) / 2;
            }

            graphics.DrawString(
                line.Text,
                font,
                brush,
                new PointF(x, setter.Px(block.Y + line.TopDip)));
        }
    }

    /// <summary>
    /// Draw the feature rows: a tinted mark, a bold line, and a quieter line beside it.
    /// </summary>
    /// <remarks>
    /// The marks are drawn in the accent, which is the one place on this surface colour is used for
    /// anything but the primary action. That is deliberate and is how the pattern reads on Apple's
    /// own welcome sheets: the eye goes down the column of coloured marks first and picks up the
    /// text from whichever one it stopped at.
    /// </remarks>
    private void PaintRows(Graphics graphics, IReadOnlyList<RowBoxes> rows, TypeScale scale, int alpha)
    {
        if (rows.Count == 0)
        {
            return;
        }

        TypeStyle titleStyle = scale.For(TypeRole.RowTitle);
        TypeStyle detailStyle = scale.For(TypeRole.RowDetail);

        foreach (RowBoxes row in rows)
        {
            PaintMark(
                graphics,
                Rectangle.Round(setter.Rect(row.Icon)),
                (Glyph)row.Glyph,
                Color.FromArgb(alpha, Palette.Accent),
                weight: 0.085f);
            DrawLines(graphics, row.TitleText, titleStyle, row.Title, TextPrimary, alpha);
            DrawLines(graphics, row.DetailText, detailStyle, row.Detail, TextSecondary, alpha);
        }
    }

    /// <summary>
    /// Draw a kicker with real tracking, one glyph at a time.
    /// </summary>
    /// <remarks>
    /// <para>The previous version faked letter-spacing by joining the characters with spaces, which
    /// gives a full space glyph between every letter and three of them at a word boundary — uneven
    /// by construction and not fixable by tuning. Advancing by a fraction of the em is what tracking
    /// actually is.</para>
    /// <para><b>The space is measured indirectly, and has to be.</b> <c>GenericTypographic</c>
    /// discards trailing whitespace, so measuring <c>" "</c> through it returns zero and a tracked
    /// word boundary collapses to nothing but the tracking. That was invisible while tracking was
    /// wide — the gap was wrong but there was a gap — and turned "SETTING UP" into "SETTINGUP" the
    /// moment it was narrowed. Measuring a space between two glyphs and subtracting them is the way
    /// to get its real advance out of GDI+.</para>
    /// </remarks>
    private void DrawTracked(
        Graphics graphics,
        TextLayout text,
        TypeStyle style,
        Box block,
        Color colour,
        int alpha,
        bool centred = false)
    {
        if (text.Lines.Count == 0)
        {
            return;
        }

        Font font = setter.Font(style);
        using SolidBrush brush = new(Color.FromArgb(alpha, colour));
        float x = setter.Px(block.X);
        float y = setter.Px(block.Y);
        float tracking = setter.Px(style.TrackingDip);
        float space = Advance(graphics, font, "i i") - Advance(graphics, font, "ii");

        if (centred)
        {
            // Measured from the same advances the draw loop uses, and with the same trailing
            // tracking removed, or a centred tracked word sits half a letter-space off.
            float total = -tracking;
            foreach (char letter in text.Lines[0].Text)
            {
                total += (letter == ' ' ? space : Advance(graphics, font, letter.ToString())) + tracking;
            }

            x += (setter.Px(block.Width) - total) / 2;
        }

        foreach (char letter in text.Lines[0].Text)
        {
            if (letter == ' ')
            {
                x += space + tracking;
                continue;
            }

            string glyph = letter.ToString();
            graphics.DrawString(glyph, font, brush, new PointF(x, y));
            x += Advance(graphics, font, glyph) + tracking;
        }
    }

    /// <summary>The type scale for this surface, measured in both directions.</summary>
    /// <remarks>
    /// One place, because three call sites each building their own is three chances for a label to
    /// be set at a different scale from the box the layout gave it.
    /// </remarks>
    private TypeScale Scale() => new(Height / setter.DpiScale, Width / setter.DpiScale);

    /// <summary>How far right a left-aligned group moves to sit centred in its column.</summary>
    private static double CentreOffset(double columnLeft, double columnWidth, double groupLeft, double groupRight) =>
        groupRight > groupLeft
            ? columnLeft + ((columnWidth - (groupRight - groupLeft)) / 2) - groupLeft
            : 0;

    /// <summary>How far the pen moves for a string, with no layout padding either side.</summary>
    private static float Advance(Graphics graphics, Font font, string text) =>
        graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    /// <summary>Resolve a named colour against this surface's palette.</summary>
    /// <remarks>
    /// Warning is finally reachable. The view carried this amber and a public Tone field for exactly
    /// this purpose, and the field was written from fifteen call sites and read from none — so a
    /// retry, a failure and ordinary progress were all the same white.
    /// </remarks>
    private static Color Resolve(ColorRole role) => role switch
    {
        ColorRole.TextSecondary => TextSecondary,
        ColorRole.Warning => Warning,
        ColorRole.Failure => Failure,
        _ => TextPrimary,
    };

    /// <summary>Pick the drawn mark for a routed glyph.</summary>
    private static Glyph Mark(GlyphRole role) => role switch
    {
        GlyphRole.Link => Glyph.Link,
        GlyphRole.Working => Glyph.Toggle,
        GlyphRole.Ready => Glyph.Unlocked,
        GlyphRole.Blocked => Glyph.Unlocked,
        _ => Glyph.Display,
    };

    /// <summary>A ring that turns while Orchard is working.</summary>
    /// <remarks>
    /// Whether it is drawn at all is the router's decision, not this method's \u2014 it used to take a
    /// <c>stopped</c> flag whose only call site passed the literal <c>false</c>.
    /// </remarks>
    private void PaintSpinner(Graphics graphics, PointF centre, float radius, int alpha)
    {
        RectangleF bounds = new(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);
        float weight = Math.Max(1.4f, radius * 0.24f);
        using (Pen track = new(Color.FromArgb(alpha / 6, TextPrimary), weight))
        {
            graphics.DrawEllipse(track, bounds);
        }

        using Pen arc = new(Color.FromArgb(alpha, TextPrimary), weight)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(arc, bounds, (float)(clock * 240 % 360), 92);
    }

    /// <summary>
    /// The mark above each page's title.
    /// </summary>
    /// <remarks>
    /// It used to sit inside a 72-pixel circle of glass, with a specular highlight and a rim. The
    /// circle carried no information — it was there to give the mark somewhere to live — and at that
    /// size it was the first thing on the page the eye landed on, ahead of the headline. The mark is
    /// now drawn on its own at roughly half the size, so the first thing read is the sentence saying
    /// what is happening.
    /// </remarks>
    private static void PaintMark(Graphics graphics, Rectangle bounds, Glyph symbol, Color colour, float weight = 0.0625f)
    {
        RectangleF mark = bounds;
        using Pen stroke = new(colour, Math.Max(1.5f, bounds.Width * weight))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        switch (symbol)
        {
            case Glyph.Display:
                graphics.DrawRectangle(stroke, mark.X, mark.Y, mark.Width, mark.Height);
                graphics.DrawLine(
                    stroke,
                    mark.X + (mark.Width * 0.32f),
                    mark.Bottom - (mark.Height * 0.14f),
                    mark.X + (mark.Width * 0.68f),
                    mark.Bottom - (mark.Height * 0.14f));
                break;
            case Glyph.Toggle:
                Rectangle track = Rectangle.Round(
                    new RectangleF(mark.X, mark.Y + (mark.Height * 0.24f), mark.Width, mark.Height * 0.52f));
                using (GraphicsPath capsule = Rounded(track, Math.Max(1, track.Height / 2)))
                {
                    graphics.DrawPath(stroke, capsule);
                }

                using (SolidBrush knob = new(colour))
                {
                    float size = mark.Height * 0.30f;
                    graphics.FillEllipse(
                        knob,
                        mark.Right - size - (mark.Height * 0.12f),
                        mark.Y + (mark.Height * 0.35f),
                        size,
                        size);
                }

                break;
            case Glyph.Link:
                // A connector and its cable. This was two overlapping arcs meant to read as links in
                // a chain; at row size, with a stroke a tenth of the mark, they merged into one blob
                // that read as nothing at all. Two shapes that do not touch survive being small.
                Rectangle plug = Rectangle.Round(new RectangleF(
                    mark.X,
                    mark.Y + (mark.Height * 0.28f),
                    mark.Width * 0.54f,
                    mark.Height * 0.44f));
                using (GraphicsPath body = Rounded(plug, Math.Max(1, plug.Height / 2)))
                {
                    graphics.DrawPath(stroke, body);
                }

                graphics.DrawLine(
                    stroke,
                    plug.Right + (mark.Width * 0.08f),
                    mark.Y + (mark.Height * 0.5f),
                    mark.Right,
                    mark.Y + (mark.Height * 0.5f));
                break;
            case Glyph.Unlocked:
                graphics.DrawRectangle(stroke, mark.X, mark.Y + (mark.Height * 0.46f), mark.Width, mark.Height * 0.54f);
                graphics.DrawArc(stroke, mark.X + (mark.Width * 0.22f), mark.Y, mark.Width * 0.76f, mark.Height * 0.64f, 180, 145);
                break;
            case Glyph.Check:
                // A tick inside a ring. Drawn rather than taken from a symbol font: the marks that
                // would otherwise be reached for here belong to Apple, and CLEAN_ROOM_AND_LEGAL.md
                // rules out carrying their icons or fonts in this repository.
                graphics.DrawEllipse(stroke, mark.X, mark.Y, mark.Width, mark.Height);
                graphics.DrawLines(
                    stroke,
                    [
                        new PointF(mark.X + (mark.Width * 0.28f), mark.Y + (mark.Height * 0.52f)),
                        new PointF(mark.X + (mark.Width * 0.44f), mark.Y + (mark.Height * 0.68f)),
                        new PointF(mark.X + (mark.Width * 0.73f), mark.Y + (mark.Height * 0.34f)),
                    ]);
                break;
            default:
                break;
        }
    }


    /// <summary>
    /// Draw the primary action as a solid shape, and the secondary as a real control.
    /// </summary>
    /// <remarks>
    /// <para>The primary used to be a lens: no fill at all, the backdrop drawn through it in four
    /// bands of increasing magnification, with two bright arcs standing in for a specular highlight.
    /// It was the most carefully built thing on the surface and the least usable — a white label on
    /// whatever colour happened to have drifted underneath it, which at its worst was around two to
    /// one. The only job a primary action has is to be obviously the primary action.</para>
    /// <para>The secondary was 50%-opacity text with no box, no rule and no hover state, which is
    /// indistinguishable from a caption. It now takes a wash under the pointer, so it reads as
    /// something that can be pressed before it is pressed.</para>
    /// </remarks>
    private void PaintButtons(Graphics graphics, string primaryText, string? secondaryText, int alpha)
    {
        PaintPrimary(graphics, primaryButton, primaryText, alpha);

        if (secondaryText is null || secondaryButton.IsEmpty)
        {
            return;
        }

        if (secondaryHot > 0.01)
        {
            using GraphicsPath wash = Rounded(secondaryButton, CornerRadius(secondaryButton));
            using SolidBrush brush = new(Color.FromArgb(
                (int)(alpha * secondaryHot * (Palette.HoverWash.A / 255.0)),
                Palette.HoverWash));
            graphics.FillPath(brush, wash);
        }

        // Body weight, not the button's bold. Set in the same weight as the primary it sits above,
        // a secondary action argues with it for the eye however much its alpha is turned down.
        Font secondaryFont = setter.Font(Scale().For(TypeRole.Body));
        Color secondaryInk = Blend(TextSecondary, TextPrimary, secondaryHot);
        using (SolidBrush ink = new(Color.FromArgb(alpha, secondaryInk)))
        using (StringFormat format = Centered())
        {
            graphics.DrawString(secondaryText, secondaryFont, ink, secondaryButton, format);
        }
    }

    /// <summary>
    /// Draw the primary action with the material a platform button actually has.
    /// </summary>
    /// <remarks>
    /// <para>One flat rectangle of saturated colour is not what a button looks like on any desktop.
    /// It is what an <i>unstyled</i> one looks like, and that was the first version of this: correct
    /// contrast, correct shape, no material at all.</para>
    /// <para>Every shipping toolkit gives its accent button the same three things, and every one of
    /// them is nearly invisible taken by itself. A slight vertical gradient, so the face is not one
    /// dead value. A lighter hairline just inside the top edge, where a light source above would
    /// catch. A darker line around the outside, which seats the shape against the surface instead of
    /// letting it float like a sticker. Held down, the gradient reverses and the label moves one
    /// pixel with it, which is what makes a press read as the surface going in rather than merely
    /// changing colour.</para>
    /// <para>This is not the glass that was removed. That was a fill-less lens magnifying the
    /// backdrop through four bands with specular crescents at its corners, and its problem was that
    /// it obscured its own label. A gradient of eighteen values across a solid fill is the ordinary
    /// way a button is drawn.</para>
    /// </remarks>
    private void PaintPrimary(Graphics graphics, Rectangle bounds, string label, int alpha)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Color face = primaryDown
            ? Palette.AccentDown
            : Blend(Palette.Accent, Palette.AccentHot, primaryHot);

        // Lit from above, except while held, when it is lit from below.
        Color top = Shade(face, primaryDown ? -0.07 : 0.11);
        Color bottom = Shade(face, primaryDown ? 0.05 : -0.09);

        int radius = CornerRadius(bounds);
        using GraphicsPath outline = Rounded(bounds, radius);

        // A pixel of bleed at each end, or the gradient wraps and puts the bottom colour on the
        // first row. The same reason PaintSurface pads its brush.
        using (LinearGradientBrush fill = new(
            new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2),
            Color.FromArgb(alpha, top),
            Color.FromArgb(alpha, bottom),
            LinearGradientMode.Vertical))
        {
            graphics.FillPath(fill, outline);
        }

        using (Pen seat = new(Color.FromArgb(alpha * 110 / 255, Shade(face, -0.42)), 1f))
        {
            graphics.DrawPath(seat, outline);
        }

        // Between the corner arcs only. Carried around the corners it becomes a rim, and a rim on a
        // filled shape is the thing that reads as glass.
        if (!primaryDown && bounds.Width > radius * 2)
        {
            using Pen lit = new(Color.FromArgb(alpha * 52 / 255, 255, 255, 255), 1f);
            graphics.DrawLine(
                lit,
                bounds.Left + radius,
                bounds.Top + 1,
                bounds.Right - radius,
                bounds.Top + 1);
        }

        Rectangle labelBox = bounds;
        if (primaryDown)
        {
            labelBox.Offset(0, 1);
        }

        Font labelFont = setter.Font(Scale().For(TypeRole.Button));
        using SolidBrush ink = new(Color.FromArgb(alpha, Color.White));
        using StringFormat format = Centered();
        graphics.DrawString(label, labelFont, ink, labelBox, format);
    }

    /// <summary>Lighten a colour towards white, or with a negative amount darken it towards black.</summary>
    private static Color Shade(Color colour, double amount) => amount >= 0
        ? Blend(colour, Color.White, amount)
        : Blend(colour, Color.Black, -amount);

    /// <summary>Mix two colours, for the hover transition.</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(from.R + ((to.R - from.R) * t)),
            (int)(from.G + ((to.G - from.G) * t)),
            (int)(from.B + ((to.B - from.B) * t)));
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static double EaseInCubic(double t) => Math.Pow(Math.Clamp(t, 0, 1), 3);

    /// <summary>
    /// A button's corner radius: soft, but nothing like a capsule.
    /// </summary>
    /// <remarks>
    /// Apple's welcome sheet uses a 15-point radius on a button around 50 points tall. Three tenths
    /// of the height reproduces that ratio at whatever size this window happens to be, which matters
    /// because this one is resizable and theirs is not.
    /// </remarks>
    private static int CornerRadius(Rectangle bounds) => Math.Max(2, (int)Math.Round(bounds.Height * 0.3));

    /// <summary>
    /// A rounded rectangle whose corners are continuous rather than circular.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape Apple means by <c>RoundedRectangle(cornerRadius:style:.continuous)</c>,
    /// and it is most of why their controls look softer than the same radius drawn with arcs. A
    /// circular corner meets the straight edge with its curvature jumping from zero to 1/r in one
    /// step; the eye reads that discontinuity as a slight pinch at the four points where the
    /// straight run begins, even when it cannot name what it is seeing.</para>
    /// <para>The curve here starts further along each edge — a little over one and a half radii —
    /// and uses a cubic whose control points sit nearer the corner, so curvature arrives gradually.
    /// It is an approximation of a superellipse rather than the real thing, which would need a
    /// higher-order curve for a difference nobody can see at these sizes.</para>
    /// </remarks>
    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        // Both must hold or the corners meet in the middle of an edge and the path self-intersects.
        float r = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
        float reach = Math.Min(r * 1.53f, Math.Min(bounds.Width, bounds.Height) / 2f);
        float pull = reach * 0.34f;

        float left = bounds.Left;
        float top = bounds.Top;
        float right = bounds.Right;
        float bottom = bounds.Bottom;

        GraphicsPath path = new();
        path.AddLine(left + reach, top, right - reach, top);
        path.AddBezier(right - reach, top, right - pull, top, right, top + pull, right, top + reach);
        path.AddLine(right, top + reach, right, bottom - reach);
        path.AddBezier(right, bottom - reach, right, bottom - pull, right - pull, bottom, right - reach, bottom);
        path.AddLine(right - reach, bottom, left + reach, bottom);
        path.AddBezier(left + reach, bottom, left + pull, bottom, left, bottom - pull, left, bottom - reach);
        path.AddLine(left, bottom - reach, left, top + reach);
        path.AddBezier(left, top + reach, left, top + pull, left + pull, top, left + reach, top);
        path.CloseFigure();
        return path;
    }

    private static StringFormat Centered() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

}
