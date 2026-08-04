namespace Orchard.Mirror.Shell;

/// <summary>
/// One feature row: a mark, a short bold line, and a quieter line under it.
/// </summary>
/// <remarks>
/// This is the shape Apple's welcome sheets use, and it exists here because it is a better answer
/// than the paragraph it replaces. Three numbered sentences in a block of body copy are read as
/// prose, which means they are read once or not at all; the same three facts as rows with their own
/// marks are scannable, and the mark tells you which one you are on without reading it.
/// </remarks>
/// <param name="Glyph">Which mark to draw, resolved to a shape by whoever is painting.</param>
/// <param name="Title">The bold line. One short phrase.</param>
/// <param name="Detail">The quieter line under it.</param>
public readonly record struct FeatureRow(int Glyph, string Title, string Detail);

/// <summary>What a setup page has to show.</summary>
/// <param name="Kicker">The small tracked word above the headline.</param>
/// <param name="Title">The headline.</param>
/// <param name="Body">Supporting text. Blank lines are paragraph breaks.</param>
/// <param name="ShowMedallion">Whether the mark above the headline is drawn.</param>
/// <param name="ShowSpinner">Whether a turning ring sits beside the mark.</param>
/// <param name="PrimaryLabel">The main button's label, or empty for no button.</param>
/// <param name="SecondaryLabel">The secondary button's label, or null for none.</param>
/// <param name="Rows">Feature rows under the body, or empty for a page that has none.</param>
public readonly record struct PageContent(
    string Kicker,
    string Title,
    string Body,
    bool ShowMedallion,
    bool ShowSpinner,
    string PrimaryLabel,
    string? SecondaryLabel,
    IReadOnlyList<FeatureRow>? Rows = null);

/// <summary>Where one placed feature row goes, and what its text broke into.</summary>
/// <param name="Icon">The square the mark is drawn in.</param>
/// <param name="Title">The bold line's box.</param>
/// <param name="Detail">The quiet line's box.</param>
/// <param name="Glyph">Which mark to draw.</param>
/// <param name="TitleText">The bold line, elided to one line.</param>
/// <param name="DetailText">The quiet line's lines.</param>
public readonly record struct RowBoxes(
    Box Icon,
    Box Title,
    Box Detail,
    int Glyph,
    TextLayout TitleText,
    TextLayout DetailText);

/// <summary>Where every piece of a setup page goes, in device-independent pixels.</summary>
/// <param name="Medallion">The glass mark, or null when there was no room for it.</param>
/// <param name="Spinner">The turning ring, or null.</param>
/// <param name="Kicker">The kicker's box, or null when there was no room for it.</param>
/// <param name="Title">The headline's box.</param>
/// <param name="Body">The body's box.</param>
/// <param name="Primary">The main button, or null.</param>
/// <param name="Secondary">The secondary button, or null.</param>
/// <param name="KickerText">The kicker, elided to one line.</param>
/// <param name="TitleText">The headline's lines.</param>
/// <param name="BodyText">The body's lines.</param>
/// <param name="Rows">The placed feature rows, empty when the page has none or there was no room.</param>
public readonly record struct OnboardingBoxes(
    Box? Medallion,
    Box? Spinner,
    Box? Kicker,
    Box Title,
    Box Body,
    Box? Primary,
    Box? Secondary,
    TextLayout KickerText,
    TextLayout TitleText,
    TextLayout BodyText,
    IReadOnlyList<RowBoxes> Rows);

/// <summary>
/// Places a setup page, and decides what gives when it does not fit.
/// </summary>
/// <remarks>
/// <para>The old layout accumulated a running <c>y</c> down the page and never compared it against
/// anything. When the headline grew — which it did as soon as the window was scaled — the body's
/// rectangle was clamped to a floor of twenty pixels, one line, and the rest of the text was
/// dropped without a mark; at the same time the draw call was told not to clip, so the part that
/// did render bled over the buttons underneath.</para>
/// <para>What gives is now decided in a fixed order, and every element is inside its own box or
/// absent: the headline clamps and elides first, then the body takes whatever is left above the
/// buttons and elides, then the medallion is dropped, and last the kicker. Dropping ornament to
/// keep words is the right order; the old code dropped words to keep ornament.</para>
/// </remarks>
public static class OnboardingLayout
{
    /// <summary>The most lines a headline may take before it starts to elide.</summary>
    private const int TitleLineLimit = 3;

    /// <summary>A placeable-nothing, with empty lists rather than nulls.</summary>
    private static OnboardingBoxes Nothing => new(
        null,
        null,
        null,
        default,
        default,
        null,
        null,
        new TextLayout([], false, 0),
        new TextLayout([], false, 0),
        new TextLayout([], false, 0),
        []);

    /// <summary>Place everything, or report it cannot be placed at all.</summary>
    /// <param name="surfaceWidthDip">Surface width.</param>
    /// <param name="surfaceHeightDip">Surface height.</param>
    /// <param name="content">What the page has to say.</param>
    /// <param name="scale">The type scale and spacing for this surface.</param>
    /// <param name="measureFor">Supplies a measurer for each role.</param>
    public static OnboardingBoxes Compute(
        double surfaceWidthDip,
        double surfaceHeightDip,
        PageContent content,
        TypeScale scale,
        Func<TypeRole, MeasureText> measureFor)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(measureFor);

        double margin = scale.MarginDip;
        double contentWidth = surfaceWidthDip - (margin * 2);
        if (contentWidth <= 0)
        {
            // Nothing can be placed, but the result still has to be safe to read: `default` would
            // hand back null line lists and turn "the window is too narrow" into a crash.
            return Nothing;
        }

        (Box? primary, Box? secondary) = LayOutButtons(surfaceWidthDip, surfaceHeightDip, content, scale);
        double floor = secondary?.Y ?? primary?.Y ?? (surfaceHeightDip - scale.EdgeGapDip);
        if (primary is not null)
        {
            floor -= scale.GapAfter(TypeRole.Body);
        }

        // Each attempt gives up one more thing. The first that leaves the body a line to stand on
        // wins; the last is taken whether it fits or not, because a page with no words at all is
        // worse than a cramped one.
        //
        // Ornament goes before content, so the mark and then the kicker are surrendered before the
        // feature rows are. The rows are the substance of the opening page — three facts somebody
        // needs before they plug anything in — and they outrank a decorative glyph.
        bool wantsRows = content.Rows is { Count: > 0 };
        (bool Medallion, bool Kicker, bool Rows)[] attempts =
        [
            (content.ShowMedallion, content.Kicker.Length > 0, wantsRows),
            (false, content.Kicker.Length > 0, wantsRows),
            (false, false, wantsRows),
            (false, false, false),
        ];

        OnboardingBoxes placed = Nothing;
        foreach ((bool withMedallion, bool withKicker, bool withRows) in attempts)
        {
            placed = Place(
                content, scale, measureFor, margin, contentWidth, floor, withMedallion, withKicker, withRows, primary, secondary);
            if (placed.BodyText.Lines.Count > 0 || content.Body.Trim().Length == 0)
            {
                break;
            }
        }

        return Centre(placed, floor);
    }

    /// <summary>
    /// Sit a page optically in its space instead of jammed against the top.
    /// </summary>
    /// <remarks>
    /// <para>Shifted by slightly less than half the slack, because optical centre sits above
    /// geometric centre.</para>
    /// <para>This used to exempt any page that had buttons, on the grounds that buttons anchor the
    /// bottom and so the page is held at both ends. They anchor themselves. On the opening page the
    /// text stopped a third of the way down and the buttons began at the very bottom, leaving a
    /// wide band of nothing between them that read as an unfinished template. The slack is measured
    /// to the top of the buttons and only a fraction of it is ever taken, so distributing it cannot
    /// push anything into them.</para>
    /// </remarks>
    private static OnboardingBoxes Centre(OnboardingBoxes boxes, double floor)
    {
        if (boxes.TitleText.Lines.Count == 0)
        {
            return boxes;
        }

        double contentBottom = boxes.Rows.Count > 0
            ? boxes.Rows[^1].Icon.Bottom
            : boxes.Body.Height > 0 ? boxes.Body.Bottom : boxes.Title.Bottom;
        if (boxes.Rows.Count > 0)
        {
            RowBoxes last = boxes.Rows[^1];
            contentBottom = Math.Max(contentBottom, last.Detail.Height > 0 ? last.Detail.Bottom : last.Title.Bottom);
        }

        double slack = floor - contentBottom;
        if (slack <= 0)
        {
            return boxes;
        }

        double shift = slack * 0.42;
        List<RowBoxes> rows = new(boxes.Rows.Count);
        foreach (RowBoxes row in boxes.Rows)
        {
            rows.Add(row with
            {
                Icon = Shift(row.Icon, shift)!.Value,
                Title = Shift(row.Title, shift)!.Value,
                Detail = Shift(row.Detail, shift)!.Value,
            });
        }

        return boxes with
        {
            Medallion = Shift(boxes.Medallion, shift),
            Spinner = Shift(boxes.Spinner, shift),
            Kicker = Shift(boxes.Kicker, shift),
            Title = Shift(boxes.Title, shift)!.Value,
            Body = Shift(boxes.Body, shift)!.Value,
            Rows = rows,
        };
    }

    private static Box? Shift(Box? box, double downDip) =>
        box is { } value ? value with { Y = value.Y + downDip } : null;

    private static (Box? Primary, Box? Secondary) LayOutButtons(
        double surfaceWidthDip,
        double surfaceHeightDip,
        PageContent content,
        TypeScale scale)
    {
        if (content.PrimaryLabel.Length == 0)
        {
            return (null, null);
        }

        double margin = scale.MarginDip;
        double width = surfaceWidthDip - (margin * 2);
        double height = scale.ButtonHeightDip;
        Box primary = new(margin, surfaceHeightDip - scale.EdgeGapDip - height, width, height);
        Box? secondary = content.SecondaryLabel is null
            ? null
            : new Box(margin, primary.Y - scale.ButtonGapDip - height, width, height);
        return (primary, secondary);
    }

    private static OnboardingBoxes Place(
        PageContent content,
        TypeScale scale,
        Func<TypeRole, MeasureText> measureFor,
        double margin,
        double contentWidth,
        double floor,
        bool withMedallion,
        bool withKicker,
        bool withRows,
        Box? primary,
        Box? secondary)
    {
        double top = scale.TopInsetDip;
        Box? medallion = null;
        Box? spinner = null;

        double ring = scale.SpinnerDip;
        if (withMedallion)
        {
            double size = scale.MedallionDip;
            medallion = new Box(margin, top, size, size);
            if (content.ShowSpinner)
            {
                spinner = new Box(margin + size + (size * 0.45), top + ((size - ring) / 2), ring, ring);
            }

            top += size + scale.GapAfter(TypeRole.Body);
        }
        else if (content.ShowSpinner)
        {
            spinner = new Box(margin, top, ring, ring);
            top += ring + scale.GapAfter(TypeRole.Kicker);
        }

        Box? kickerBox = null;
        TextLayout kickerText = new([], false, 0);
        if (withKicker)
        {
            TypeStyle style = scale.For(TypeRole.Kicker);
            kickerText = TextFlow.Layout(content.Kicker, contentWidth, 1, style.LineHeightDip, measureFor(TypeRole.Kicker));
            kickerBox = new Box(margin, top, contentWidth, style.LineHeightDip);
            top += style.LineHeightDip + scale.GapAfter(TypeRole.Kicker);
        }

        TypeStyle titleStyle = scale.For(TypeRole.Title);
        TextLayout titleText = TextFlow.Layout(
            content.Title,
            contentWidth,
            TitleLineLimit,
            titleStyle.LineHeightDip,
            measureFor(TypeRole.Title),

            // Headlines only. Body copy reads better filled, and a balanced paragraph of five lines
            // draws attention to its own shape.
            balance: true);
        Box titleBox = new(margin, top, contentWidth, titleText.HeightDip);
        top += titleText.HeightDip + scale.GapAfter(TypeRole.Title);

        // The rows are measured before the body is given its room, so the body takes what is left
        // over rather than the rows taking what the body did not want. Laying them out in reading
        // order would let a long paragraph push the rows through the buttons.
        List<RowBoxes> rows = withRows
            ? MeasureRows(content.Rows!, scale, measureFor, margin, contentWidth)
            : [];
        double rowsHeight = RowsHeight(rows);
        double rowsReserve = rowsHeight > 0 ? rowsHeight + scale.GapAfter(TypeRole.Body) : 0;

        TypeStyle bodyStyle = scale.For(TypeRole.Body);
        double available = floor - top - rowsReserve;
        int bodyLines = available <= 0 ? 0 : (int)Math.Floor(available / bodyStyle.LineHeightDip);
        TextLayout bodyText = bodyLines < 1
            ? new TextLayout([], content.Body.Trim().Length > 0, 0)
            : TextFlow.Layout(content.Body, contentWidth, bodyLines, bodyStyle.LineHeightDip, measureFor(TypeRole.Body));
        Box bodyBox = new(margin, top, contentWidth, bodyText.HeightDip);

        if (rows.Count > 0)
        {
            double rowsTop = bodyBox.Height > 0
                ? bodyBox.Bottom + scale.GapAfter(TypeRole.Body)
                : top;
            rows = ShiftRows(rows, rowsTop);
        }

        return new OnboardingBoxes(
            medallion,
            spinner,
            kickerBox,
            titleBox,
            bodyBox,
            primary,
            secondary,
            kickerText,
            titleText,
            bodyText,
            rows);
    }

    /// <summary>
    /// Lay the feature rows out from a top of zero, so they can be moved as a block afterwards.
    /// </summary>
    /// <remarks>
    /// A row is as tall as the taller of its mark and its two lines of text. Using the text alone
    /// would let a one-line row be shorter than its own 37-point mark, and the marks would then sit
    /// closer together than the rows they belong to.
    /// </remarks>
    private static List<RowBoxes> MeasureRows(
        IReadOnlyList<FeatureRow> rows,
        TypeScale scale,
        Func<TypeRole, MeasureText> measureFor,
        double margin,
        double contentWidth)
    {
        double icon = scale.RowIconDip;
        double textLeft = margin + icon + scale.RowIconGapDip;
        double textWidth = contentWidth - icon - scale.RowIconGapDip;
        List<RowBoxes> placed = [];
        if (textWidth <= 0)
        {
            return placed;
        }

        TypeStyle titleStyle = scale.For(TypeRole.RowTitle);
        TypeStyle detailStyle = scale.For(TypeRole.RowDetail);
        double top = 0;

        foreach (FeatureRow row in rows)
        {
            TextLayout title = TextFlow.Layout(row.Title, textWidth, 1, titleStyle.LineHeightDip, measureFor(TypeRole.RowTitle));
            TextLayout detail = TextFlow.Layout(row.Detail, textWidth, 2, detailStyle.LineHeightDip, measureFor(TypeRole.RowDetail));

            double textHeight = title.HeightDip + detail.HeightDip;
            double height = Math.Max(icon, textHeight);

            // Text centred against the mark when the mark is the taller of the two, which is what
            // keeps a one-line row from hanging off the top of its own icon.
            double textTop = top + Math.Max(0, (height - textHeight) / 2);

            placed.Add(new RowBoxes(
                new Box(margin, top + Math.Max(0, (height - icon) / 2), icon, icon),
                new Box(textLeft, textTop, textWidth, title.HeightDip),
                new Box(textLeft, textTop + title.HeightDip, textWidth, detail.HeightDip),
                row.Glyph,
                title,
                detail));

            top += height + scale.RowGapDip;
        }

        return placed;
    }

    /// <summary>How tall the placed rows are together, without the trailing gap.</summary>
    private static double RowsHeight(List<RowBoxes> rows)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        RowBoxes last = rows[^1];
        double bottom = Math.Max(last.Icon.Bottom, last.Detail.Height > 0 ? last.Detail.Bottom : last.Title.Bottom);
        return bottom - rows[0].Icon.Y;
    }

    private static List<RowBoxes> ShiftRows(List<RowBoxes> rows, double downDip)
    {
        List<RowBoxes> shifted = new(rows.Count);
        foreach (RowBoxes row in rows)
        {
            shifted.Add(row with
            {
                Icon = row.Icon with { Y = row.Icon.Y + downDip },
                Title = row.Title with { Y = row.Title.Y + downDip },
                Detail = row.Detail with { Y = row.Detail.Y + downDip },
            });
        }

        return shifted;
    }
}
