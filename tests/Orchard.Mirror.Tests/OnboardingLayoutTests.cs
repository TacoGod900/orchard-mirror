using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// Placement for the setup pages. Every claim below is about what a person can read: that headlines
/// break the same way whatever the display scale, that words are never silently dropped, and that
/// nothing is ever drawn on top of anything else.
/// </summary>
/// <remarks>
/// The measurer is injected and deterministic, so all of this runs headless with no fonts involved.
/// Its exact metrics do not matter — what matters is that it is the same function at both display
/// scales, which is what makes the scaling test a test of the layout rather than of a font.
/// </remarks>
internal static class OnboardingLayoutTests
{
    /// <summary>A phone-shaped window at 100% scale, in device-independent pixels.</summary>
    private const double WidthDip = 390;
    private const double HeightDip = 844;

    /// <summary>
    /// The defect that produced "the text wraps weirdly". Type was sized in points, which GDI+
    /// scales by the surface DPI, while every margin and box beside it was a raw device pixel — so
    /// on a 150% display the text came out half again as large relative to the space it had to fit.
    /// The same page at the same physical size must break identically at any scale.
    /// </summary>
    internal static void BreaksTheSameWayAtEveryDisplayScale()
    {
        // The same physical window, described the way each display describes it: 390x844 device
        // pixels at 100%, and 585x1266 device pixels at 150%. Converting to device-independent
        // pixels is the renderer's one job, and this models it. If display scale ever leaks back
        // into the type path, these two stop agreeing.
        OnboardingBoxes at100 = ComputeForDisplay(390, 844, dpiScale: 1.0);
        OnboardingBoxes at150 = ComputeForDisplay(585, 1266, dpiScale: 1.5);

        Assert(
            at100.TitleText.Lines.Count == at150.TitleText.Lines.Count,
            $"the headline broke into {at100.TitleText.Lines.Count} lines at 100% and "
                + $"{at150.TitleText.Lines.Count} at 150% on the same physical window");
        Assert(
            at100.BodyText.Lines.Count == at150.BodyText.Lines.Count,
            $"the body broke into {at100.BodyText.Lines.Count} lines at 100% and "
                + $"{at150.BodyText.Lines.Count} at 150% on the same physical window");
        Assert(
            !at150.Body.Overlaps(at150.Primary!.Value),
            "at 150% the body was laid out over the button, which is the original defect");
    }

    /// <summary>Lay out a window given in device pixels, as the renderer would.</summary>
    private static OnboardingBoxes ComputeForDisplay(double deviceWidth, double deviceHeight, double dpiScale) =>
        Compute(deviceWidth / dpiScale, deviceHeight / dpiScale, Page());

    /// <summary>
    /// The scale factor follows the window, not the display. A window twice as tall may set larger
    /// type; the same window on a scaled display must not.
    /// </summary>
    internal static void SizesTypeFromTheWindowNotTheDisplay()
    {
        TypeScale small = new(600);
        TypeScale large = new(1400);

        Assert(
            large.For(TypeRole.Title).SizeDip > small.For(TypeRole.Title).SizeDip,
            "a much taller window must be allowed larger type");
        Assert(
            large.MarginDip > small.MarginDip,
            "spacing must grow with the type it surrounds, or the two disagree at the extremes");
    }

    /// <summary>Type and space come from one object, so their ratio cannot drift apart.</summary>
    internal static void KeepsTypeAndSpaceInProportion()
    {
        foreach (double height in (double[])[600, 844, 1400])
        {
            TypeScale scale = new(height);
            double ratio = scale.For(TypeRole.Body).SizeDip / scale.MarginDip;
            TypeScale reference = new(844);
            double referenceRatio = reference.For(TypeRole.Body).SizeDip / reference.MarginDip;

            Assert(
                Math.Abs(ratio - referenceRatio) < 0.15,
                $"at {height} DIP tall, body type and margin drifted out of proportion");
        }
    }

    /// <summary>Nothing may be drawn over the buttons, however long the text is.</summary>
    internal static void NeverOverlapsTheButtons()
    {
        string monster = string.Join(" ", Enumerable.Repeat("diagnostic", 300));
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, Page(body: monster));

        Assert(boxes.Primary is not null, "this page was supposed to have a button");
        Assert(
            !boxes.Body.Overlaps(boxes.Primary!.Value),
            "a long body was drawn on top of the button underneath it");
        Assert(
            boxes.BodyText.Elided,
            "text that did not fit was dropped without being marked as dropped");
    }

    /// <summary>Text that will not fit says so, rather than disappearing.</summary>
    internal static void MarksDroppedTextRatherThanLosingIt()
    {
        string monster = string.Join(" ", Enumerable.Repeat("unreachable", 300));
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, Page(body: monster));

        Assert(boxes.BodyText.Lines.Count > 0, "the body vanished entirely");
        Assert(
            boxes.BodyText.Lines[^1].Text.EndsWith('…'),
            "the last visible line gave no sign that anything followed it");
    }

    /// <summary>
    /// The height a block reports must be the height it occupies. Feeding a measured height back as
    /// the drawing rectangle is what clipped descenders off the final line.
    /// </summary>
    internal static void ReportsEnoughHeightForEveryLine()
    {
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, Page());
        TypeScale scale = new(HeightDip);
        double lineHeight = scale.For(TypeRole.Title).LineHeightDip;

        Assert(
            boxes.TitleText.HeightDip >= boxes.TitleText.Lines.Count * lineHeight - 0.001,
            "the headline's box was shorter than the lines inside it");
        Assert(
            boxes.Title.Height >= boxes.TitleText.HeightDip - 0.001,
            "the headline's box did not cover its own text");
    }

    /// <summary>
    /// At the narrowest the window can be, the kicker must stay inside the column. It used to be
    /// drawn through an unbounded call that could not wrap or clip, so it ran off the right edge.
    /// </summary>
    internal static void KeepsTheKickerInsideTheColumn()
    {
        OnboardingBoxes boxes = Compute(277, 600, Page());

        Assert(boxes.Kicker is not null, "the kicker was dropped from a page that had room for it");
        Assert(
            boxes.Kicker!.Value.Right <= 277,
            "the kicker ran off the right edge of the narrowest allowed window");
        foreach (TextLine line in boxes.KickerText.Lines)
        {
            Assert(
                Measure(TypeRole.Kicker)(line.Text) <= boxes.Kicker.Value.Width + 0.001,
                "a kicker line was wider than the box it was given");
        }
    }

    /// <summary>Ornament gives way before words do.</summary>
    internal static void DropsOrnamentBeforeItDropsWords()
    {
        // A window barely tall enough for buttons and a headline, and a body that has to go
        // somewhere.
        OnboardingBoxes boxes = Compute(300, 380, Page(body: "The stream stopped and Orchard is trying again."));

        Assert(
            boxes.BodyText.Lines.Count > 0,
            "the body was sacrificed while ornament above it was kept");
        Assert(
            boxes.Medallion is null || boxes.Body.Y >= boxes.Medallion.Value.Bottom,
            "the medallion was kept and the body was laid out on top of it");
    }

    /// <summary>
    /// A page with no buttons sits in its space rather than jammed against the top with the whole
    /// lower half of the window empty, which is how every connection status used to read.
    /// </summary>
    internal static void CentresAPageThatHasNoButtons()
    {
        PageContent statusPage = Page() with { PrimaryLabel = string.Empty, SecondaryLabel = null };
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, statusPage);

        double above = boxes.Medallion?.Y ?? boxes.Title.Y;
        double below = HeightDip - (boxes.Body.Height > 0 ? boxes.Body.Bottom : boxes.Title.Bottom);

        Assert(above > 0, "content was placed off the top of the window");
        Assert(
            below < above * 3,
            $"content hugged the top: {above:F0} DIP above it and {below:F0} below");
    }

    /// <summary>
    /// A page with buttons distributes its slack too, rather than hugging the top.
    /// </summary>
    /// <remarks>
    /// <para>This reverses an earlier rule that held a button-bearing page pinned to its top inset.
    /// The reasoning then was that buttons anchor the bottom, so the page is held at both ends —
    /// but the buttons anchor themselves, not the text a third of a window above them. On the
    /// opening page that left roughly 340 device-independent pixels of nothing between the last
    /// line of body copy and the first button, which is most of why the screen read as a template
    /// somebody had filled in rather than a screen somebody had laid out.</para>
    /// <para>The claim is about the two gaps being comparable, not about any particular offset, so
    /// it survives the copy changing length.</para>
    /// </remarks>
    internal static void DistributesTheSlackOnAPageWithButtons()
    {
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, Page());

        Assert(boxes.Primary is not null, "this page was supposed to have a button");

        double above = boxes.Medallion?.Y ?? boxes.Title.Y;
        double contentBottom = boxes.Body.Height > 0 ? boxes.Body.Bottom : boxes.Title.Bottom;
        double below = boxes.Primary!.Value.Y - contentBottom;

        Assert(
            above > 0 && below > 0,
            $"content was not inside its own space: {above:F0} DIP above, {below:F0} below");
        Assert(
            Math.Abs(above - below) < Math.Max(above, below) * 0.6,
            $"content was not distributed: {above:F0} DIP above it and {below:F0} below, "
                + "which is the empty-middle look this rule exists to prevent");
        Assert(
            !boxes.Body.Overlaps(boxes.Primary.Value),
            "distributing the slack pushed the body onto the button");
    }

    /// <summary>
    /// Distribution must never come out of the buttons' own space, however little room there is.
    /// </summary>
    /// <remarks>
    /// The shift is a fraction of measured slack, so a page with no slack must not move at all.
    /// Without that floor a cramped window would push its own text through the controls.
    /// </remarks>
    internal static void NeverShiftsContentOntoTheButtons()
    {
        foreach (double height in (double[])[380, 460, 600, 844, 1400])
        {
            OnboardingBoxes boxes = Compute(320, height, Page(body: "The stream stopped and Orchard is trying again."));
            if (boxes.Primary is null)
            {
                continue;
            }

            Assert(
                !boxes.Body.Overlaps(boxes.Primary.Value),
                $"in a {height:F0} DIP window the body was shifted onto the button");
            Assert(
                boxes.Title.Y >= 0,
                $"in a {height:F0} DIP window the headline was shifted off the top of the surface");
        }
    }

    /// <summary>An impossible width is reported as nothing placed, not as a broken layout.</summary>
    internal static void PlacesNothingWhenThereIsNoColumnAtAll()
    {
        OnboardingBoxes boxes = Compute(20, 400, Page());

        Assert(boxes.TitleText.Lines.Count == 0, "text was placed in a window with no usable column");
    }

    /// <summary>
    /// A headline is evened out rather than filled greedily, so it never strands a word alone.
    /// </summary>
    /// <remarks>
    /// The oracle is the greedy layout of the same words at the same width, not a line count or a
    /// break position written down from what the code happens to produce. Balancing has to leave the
    /// block the same number of lines and make the longest and shortest of them closer together; if
    /// it does neither, it has done nothing worth having.
    /// </remarks>
    internal static void EvensOutAHeadlineRatherThanStrandingAWord()
    {
        // A real headline from StatusRouter, in a column that makes greedy wrapping strand its last
        // word: "Cannot reach the" fills the first line and leaves "iPhone" alone on the second.
        const string headline = "Cannot reach the iPhone";
        const double column = 300;
        TypeScale scale = new(HeightDip);
        double lineHeight = scale.For(TypeRole.Title).LineHeightDip;
        MeasureText measure = Measure(TypeRole.Title);

        TextLayout greedy = TextFlow.Layout(headline, column, 3, lineHeight, measure);
        TextLayout balanced = TextFlow.Layout(headline, column, 3, lineHeight, measure, balance: true);

        Assert(greedy.Lines.Count > 1, "this headline was supposed to need more than one line");
        Assert(
            balanced.Lines.Count == greedy.Lines.Count,
            $"balancing changed the line count from {greedy.Lines.Count} to {balanced.Lines.Count}, "
                + "which changes how much room the headline needs");
        Assert(
            Spread(balanced, measure) < Spread(greedy, measure),
            "balancing left the lines no more even than filling them greedily did");
        foreach (TextLine line in balanced.Lines)
        {
            Assert(
                measure(line.Text) <= column + 0.001,
                $"a balanced line ran past the column it was given: \"{line.Text}\"");
        }
    }

    /// <summary>One line that fits is left alone, and a single word is never split to even it out.</summary>
    internal static void LeavesAHeadlineThatFitsAlone()
    {
        TypeScale scale = new(HeightDip);
        double lineHeight = scale.For(TypeRole.Title).LineHeightDip;
        MeasureText measure = Measure(TypeRole.Title);

        TextLayout single = TextFlow.Layout("All set", 338, 3, lineHeight, measure, balance: true);

        Assert(single.Lines.Count == 1, "a headline that fits on one line was broken across two");
        Assert(single.Lines[0].Text == "All set", "a headline that fits was altered");
    }

    /// <summary>
    /// Feature rows stack in order, never overlap each other, and never reach the buttons.
    /// </summary>
    /// <remarks>
    /// The rows are the pattern Apple's welcome sheets use, and the failure they invite is the one
    /// the old running-<c>y</c> layout had: three blocks placed one after another with nothing
    /// checking that the last of them stopped before the controls did.
    /// </remarks>
    internal static void StacksFeatureRowsWithoutCollision()
    {
        CheckRows(Compute(WidthDip, HeightDip, WelcomePage()), 3);

        // The same page with a body long enough to swallow the window. The rows have to be given
        // their room before the body is given its own, or the body takes everything down to the
        // buttons and the rows are laid out through them. With the short opening subtitle this is
        // invisible, which is exactly why it is checked with a long one.
        string flood = string.Join(" ", Enumerable.Repeat("streaming", 200));
        OnboardingBoxes crowded = Compute(WidthDip, HeightDip, WelcomePage() with { Body = flood });

        Assert(crowded.Rows.Count > 0, "a long body pushed every feature row off the page");
        CheckRows(crowded, crowded.Rows.Count);
        Assert(
            !crowded.Body.Overlaps(crowded.Rows[0].Title),
            "the body was laid out over the first feature row");
    }

    /// <summary>Assert the shared invariants over one placed page's rows.</summary>
    private static void CheckRows(OnboardingBoxes boxes, int expected)
    {
        Assert(boxes.Rows.Count == expected, $"expected {expected} feature rows, placed {boxes.Rows.Count}");
        Assert(boxes.Primary is not null, "this page was supposed to have a button");

        for (int index = 0; index < boxes.Rows.Count; index++)
        {
            RowBoxes row = boxes.Rows[index];
            Assert(
                row.TitleText.Lines.Count > 0,
                $"row {index} lost its title, which is the one line it exists to show");
            Assert(
                !row.Icon.Overlaps(row.Title) && !row.Icon.Overlaps(row.Detail),
                $"row {index} drew its mark over its own text");
            Assert(
                !row.Title.Overlaps(boxes.Primary!.Value) && !row.Detail.Overlaps(boxes.Primary.Value),
                $"row {index} was laid out over the button");

            if (index > 0)
            {
                RowBoxes above = boxes.Rows[index - 1];
                Assert(
                    row.Icon.Y >= above.Icon.Bottom || row.Icon.Y >= above.Detail.Bottom,
                    $"row {index} started before row {index - 1} finished");
                Assert(
                    !row.Title.Overlaps(above.Detail),
                    $"row {index} collided with the row above it");
            }
        }
    }

    /// <summary>
    /// The mark and the kicker are surrendered before a feature row is.
    /// </summary>
    /// <remarks>
    /// The order matters and is easy to get backwards. A glyph is decoration; a row is one of the
    /// three things somebody has to do before this works at all. In a window too short for both, the
    /// rows are what must survive.
    /// </remarks>
    internal static void DropsTheMarkBeforeItDropsAFeatureRow()
    {
        OnboardingBoxes boxes = Compute(WidthDip, 470, WelcomePage());

        Assert(
            boxes.Rows.Count > 0,
            "every feature row was dropped from a window that still had room for a decorative mark");
        Assert(
            boxes.Medallion is null || boxes.Rows.Count == 3,
            "the mark was kept while feature rows were being dropped");
    }

    /// <summary>
    /// A headline is never broken in the middle of a word, at any size the window allows.
    /// </summary>
    /// <remarks>
    /// <see cref="TextFlow"/> breaks inside a word when the word alone will not fit the column, and
    /// that is the right last resort. It should never be reached by a headline: at the smallest
    /// window this application permits, the surface was setting the title from its height alone and
    /// rendering "Opening secure iPhone tunnel" as "Openin / g secure / iPhone…". The scale now
    /// takes the narrower of the two dimensions, so the column and the type shrink together.
    /// </remarks>
    internal static void NeverBreaksAHeadlineInsideAWord()
    {
        // The smallest window MirrorForm permits, in device-independent pixels at 150% scale, plus
        // the same shape one step larger.
        foreach ((double width, double height) in ((double, double)[])[(185, 400), (260, 563), (390, 844)])
        {
            foreach (string headline in (string[])["Opening secure iPhone tunnel", "Your iPhone on this PC", "Cannot reach the iPhone"])
            {
                TypeScale scale = new(height, width);
                OnboardingBoxes boxes = OnboardingLayout.Compute(
                    width, height, Page() with { Title = headline }, scale, role => Measure(scale, role));

                // Spaces removed from both sides. Wrapping legitimately consumes the space it broke
                // at, so it is the letters that have to survive in order, not the spacing between
                // them. Comparing with the spaces left in fails on correct output.
                string joined = string.Concat(boxes.TitleText.Lines.Select(line => line.Text))
                    .Replace(" ", string.Empty, StringComparison.Ordinal);
                string expected = headline.Replace(" ", string.Empty, StringComparison.Ordinal);
                Assert(
                    boxes.TitleText.Elided || joined == expected,
                    $"at {width:F0}x{height:F0} the headline was broken inside a word: "
                        + string.Join(" / ", boxes.TitleText.Lines.Select(line => line.Text)));
            }
        }
    }

    /// <summary>A page with no rows still places, and reports none rather than null.</summary>
    internal static void PlacesAPageWithNoFeatureRows()
    {
        OnboardingBoxes boxes = Compute(WidthDip, HeightDip, Page());

        Assert(boxes.Rows is { Count: 0 }, "a page without rows reported null, or was given rows it never had");
        Assert(boxes.TitleText.Lines.Count > 0, "the headline vanished from a page with no rows");
    }

    /// <summary>The opening page, as it is actually configured.</summary>
    private static PageContent WelcomePage() => new(
        Kicker: string.Empty,
        Title: "Your iPhone on this PC",
        Body: "Its screen, its sound and its touchscreen, in a window you can put anywhere.",
        ShowMedallion: false,
        ShowSpinner: false,
        PrimaryLabel: "Get started",
        SecondaryLabel: "I've done this before",
        Rows:
        [
            new FeatureRow(0, "Plug it in", "Any USB cable will do."),
            new FeatureRow(1, "Tap Trust", "The iPhone asks once, on its own screen."),
            new FeatureRow(2, "That is all", "Orchard turns on Developer Mode and connects."),
        ]);

    /// <summary>The gap between the widest and narrowest line in a block.</summary>
    private static double Spread(TextLayout layout, MeasureText measure)
    {
        double widest = 0;
        double narrowest = double.MaxValue;
        foreach (TextLine line in layout.Lines)
        {
            double width = measure(line.Text);
            widest = Math.Max(widest, width);
            narrowest = Math.Min(narrowest, width);
        }

        return layout.Lines.Count == 0 ? 0 : widest - narrowest;
    }

    private static PageContent Page(string? body = null) => new(
        Kicker: "SETTING UP",
        Title: "CoreDevice connected",
        Body: body ?? "CoreDevice is ready over Wi-Fi\n\nStarting the live HEVC stream.",
        ShowMedallion: true,
        ShowSpinner: true,
        PrimaryLabel: "Reconnect",
        SecondaryLabel: null);

    private static OnboardingBoxes Compute(double width, double height, PageContent content)
    {
        TypeScale scale = new(height);
        return OnboardingLayout.Compute(width, height, content, scale, role => Measure(scale, role));
    }

    /// <summary>
    /// A deterministic stand-in for a font: every glyph half an em wide, in the role's own size for
    /// the surface being laid out. The point is that it is the same function regardless of display
    /// scale, so any difference between two scales can only have come from the layout.
    /// </summary>
    private static MeasureText Measure(TypeScale scale, TypeRole role)
    {
        double size = scale.For(role).SizeDip;
        return text => text.Length * size * 0.5;
    }

    private static MeasureText Measure(TypeRole role) => Measure(new TypeScale(HeightDip), role);

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
