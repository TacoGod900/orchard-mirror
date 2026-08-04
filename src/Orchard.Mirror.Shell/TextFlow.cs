namespace Orchard.Mirror.Shell;

/// <summary>One laid-out line and where its top sits.</summary>
/// <param name="Text">The line's text. Empty for the space between paragraphs.</param>
/// <param name="TopDip">The line's top edge, relative to the block's own top.</param>
public readonly record struct TextLine(string Text, double TopDip);

/// <summary>Text broken to fit a width, and how much room it needs.</summary>
/// <param name="Lines">The lines, in order.</param>
/// <param name="Elided">Whether anything was dropped to make it fit.</param>
/// <param name="HeightDip">
/// The height the block genuinely occupies, computed from line heights rather than measured back
/// out of the text engine.
/// </param>
public readonly record struct TextLayout(IReadOnlyList<TextLine> Lines, bool Elided, double HeightDip);

/// <summary>Measures how wide a string is, in device-independent pixels, in the current style.</summary>
/// <param name="text">The string to measure.</param>
/// <returns>Its width.</returns>
public delegate double MeasureText(string text);

/// <summary>
/// Breaks text to a width and reports honestly how much room it needs.
/// </summary>
/// <remarks>
/// <para>Three defects live here, and all three were invisible until the window was scaled.</para>
/// <para>The height used to come from <c>MeasureString</c> and be fed straight back as the drawing
/// rectangle's height, with no headroom — the classic GDI+ case where the final line's descenders
/// are clipped — and then truncated to an int, so everything below crept upward into it. Height is
/// now derived from the line height and the number of lines, which is a fact about the layout
/// rather than a second opinion from the text engine.</para>
/// <para>Overflowing text was trimmed at a word boundary with no ellipsis and no report, so it
/// simply vanished; at the same time <c>NoClip</c> let the last partial line bleed over the buttons
/// underneath. Text that does not fit is now elided with a visible mark and says so, and nothing is
/// ever drawn outside its box.</para>
/// <para>Line breaks were also written into the copy as <c>\n</c>, so a title broke in the same
/// place whether the window was 277 or 900 wide, and then wrapped again on top of that. Breaks are
/// decided here, from the width actually available.</para>
/// </remarks>
public static class TextFlow
{
    private const string Ellipsis = "…";

    /// <summary>Break <paramref name="text"/> to fit, eliding rather than overflowing.</summary>
    /// <param name="text">The text. Blank lines in it are paragraph breaks.</param>
    /// <param name="maxWidthDip">The width available.</param>
    /// <param name="maxLines">The most lines that may be produced. Must be at least one.</param>
    /// <param name="lineHeightDip">The distance between successive line tops.</param>
    /// <param name="measure">Measures a candidate string in the intended style.</param>
    /// <param name="balance">
    /// Whether to even out the lines rather than filling each one greedily. For headlines only; see
    /// <see cref="BalancedWidth"/>.
    /// </param>
    public static TextLayout Layout(
        string text,
        double maxWidthDip,
        int maxLines,
        double lineHeightDip,
        MeasureText measure,
        bool balance = false)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        List<TextLine> lines = [];
        bool elided = false;
        double paragraphGap = lineHeightDip * 0.45;
        double top = 0;

        if (string.IsNullOrWhiteSpace(text) || maxWidthDip <= 0)
        {
            return new TextLayout(lines, false, 0);
        }

        string[] paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Trim().Length == 0)
            {
                // A blank source line is the gap between paragraphs. It costs real space, but less
                // than a whole line -- which is what writing "\n\n" into the copy used to buy.
                if (lines.Count > 0)
                {
                    top += paragraphGap;
                }

                continue;
            }

            double wrapWidth = balance ? BalancedWidth(paragraph, maxWidthDip, measure) : maxWidthDip;
            foreach (string line in Wrap(paragraph, wrapWidth, measure))
            {
                if (lines.Count == maxLines)
                {
                    elided = true;
                    break;
                }

                lines.Add(new TextLine(line, top));
                top += lineHeightDip;
            }

            if (elided)
            {
                break;
            }
        }

        if (elided && lines.Count > 0)
        {
            lines[^1] = new TextLine(
                Elide(lines[^1].Text, maxWidthDip, measure),
                lines[^1].TopDip);
        }

        // The last line occupies a full line height; `top` has already advanced past it.
        return new TextLayout(lines, elided, lines.Count == 0 ? 0 : top);
    }

    /// <summary>
    /// The narrowest column that still breaks this paragraph into the same number of lines.
    /// </summary>
    /// <remarks>
    /// <para>Greedy wrapping fills each line as far as it will go, which is right for body copy and
    /// wrong for a headline: it leaves whatever is left over alone on the last line. "Your iPhone on
    /// this PC" set greedily across this window came out as "Your iPhone on this" and then "PC" by
    /// itself, which is the sort of thing that makes a screen look unconsidered even to someone who
    /// could not say why.</para>
    /// <para>Narrowing the column until one more squeeze would cost an extra line, then wrapping at
    /// that width, distributes the words evenly without changing how many lines the block occupies
    /// or how much room it needs. The search is a bisection over the width, so it is deterministic
    /// and needs no cooperation from the font.</para>
    /// </remarks>
    private static double BalancedWidth(string paragraph, double maxWidthDip, MeasureText measure)
    {
        int target = Wrap(paragraph, maxWidthDip, measure).Count;
        if (target <= 1)
        {
            return maxWidthDip;
        }

        // The search may never go below the widest single word.
        //
        // Without this floor the bisection is free to pick a column narrower than a word in the
        // paragraph, and Wrap's last resort for a word that will not fit is to break it in half. So
        // balancing — whose whole purpose is to make a headline look considered — could hand back
        // "Openin / g secure / iPhone…". Starting the lower bound at the widest word makes that
        // structurally impossible: every candidate the search tries is wider than it.
        double widestWord = 0;
        foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            widestWord = Math.Max(widestWord, measure(word));
        }

        double tooNarrow = Math.Min(widestWord, maxWidthDip);
        double wideEnough = maxWidthDip;

        // Twelve halvings settle the column to well under a thousandth of its width, which is far
        // finer than the difference one glyph makes.
        for (int step = 0; step < 12; step++)
        {
            double candidate = (tooNarrow + wideEnough) / 2;
            if (Wrap(paragraph, candidate, measure).Count <= target)
            {
                wideEnough = candidate;
            }
            else
            {
                tooNarrow = candidate;
            }
        }

        return wideEnough;
    }

    /// <summary>Greedy word wrap, breaking inside a word only when the word alone will not fit.</summary>
    private static List<string> Wrap(string paragraph, double maxWidthDip, MeasureText measure)
    {
        List<string> wrapped = [];
        string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string current = string.Empty;

        foreach (string word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (measure(candidate) <= maxWidthDip || current.Length == 0)
            {
                if (measure(candidate) > maxWidthDip && current.Length == 0)
                {
                    // A single word wider than the whole column. Break it rather than let it run
                    // off the edge, which is what an unbounded draw call used to do.
                    foreach (string piece in BreakWord(word, maxWidthDip, measure))
                    {
                        wrapped.Add(piece);
                    }

                    current = wrapped.Count > 0 ? wrapped[^1] : string.Empty;
                    if (wrapped.Count > 0)
                    {
                        wrapped.RemoveAt(wrapped.Count - 1);
                    }

                    continue;
                }

                current = candidate;
                continue;
            }

            wrapped.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            wrapped.Add(current);
        }

        return wrapped;
    }

    private static List<string> BreakWord(string word, double maxWidthDip, MeasureText measure)
    {
        List<string> pieces = [];
        string current = string.Empty;
        foreach (char letter in word)
        {
            string candidate = current + letter;
            if (current.Length > 0 && measure(candidate) > maxWidthDip)
            {
                pieces.Add(current);
                current = letter.ToString();
                continue;
            }

            current = candidate;
        }

        if (current.Length > 0)
        {
            pieces.Add(current);
        }

        return pieces;
    }

    /// <summary>Trim a line at a word boundary and mark it, so dropped text is visible as dropped.</summary>
    private static string Elide(string line, double maxWidthDip, MeasureText measure)
    {
        if (measure(line + Ellipsis) <= maxWidthDip)
        {
            return line + Ellipsis;
        }

        string trimmed = line;
        while (trimmed.Length > 0)
        {
            int cut = trimmed.LastIndexOf(' ');
            trimmed = cut > 0 ? trimmed[..cut] : trimmed[..^1];
            if (measure(trimmed + Ellipsis) <= maxWidthDip)
            {
                return trimmed + Ellipsis;
            }
        }

        return Ellipsis;
    }
}
