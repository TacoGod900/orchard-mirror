using System.Globalization;
using System.Text;

namespace Orchard.Mirror.Shell;

/// <summary>The result of reducing clipboard text to something the phone can be typed.</summary>
/// <param name="Text">What to send, already limited to typeable characters.</param>
/// <param name="Dropped">
/// How many characters were removed because the phone's keyboard has no key for them. Reported so
/// the confirmation can be honest — "pasted, two characters couldn't be typed" — rather than
/// silently mangling the text.
/// </param>
/// <param name="Truncated">Whether the text was longer than one command can carry and was cut.</param>
public readonly record struct ClipboardPaste(string Text, int Dropped, bool Truncated)
{
    /// <summary>Whether there is anything left to send.</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>
/// Reduces arbitrary clipboard text to the characters the phone's virtual keyboard can actually
/// type.
/// </summary>
/// <remarks>
/// <para>The phone is driven through a US-ASCII HID keyboard: the agent maps each character to a key
/// and <b>throws on the first one it can't</b>, after having already typed everything before it. So
/// pasting "café" or a line with a smart quote wouldn't produce a tidy failure — it would type half
/// the text and then error. A clipboard that behaves that way is worse than none, because the user
/// can't see what landed.</para>
/// <para>So the cleaning happens here, before a single keystroke is sent, and it is deliberately
/// generous rather than strict: curly quotes become straight ones, dashes and the ellipsis become
/// their ASCII shapes, exotic spaces become ordinary ones, and accented letters are decomposed to
/// their base form (café → cafe) rather than dropped. Only what survives all of that with no ASCII
/// equivalent — emoji, other scripts — is removed, and counted so the result can say so.</para>
/// <para>Pure text in, pure text out: no device, no keyboard, no I/O, so every rule here is a unit
/// test rather than something only the phone can check.</para>
/// </remarks>
public static class ClipboardText
{
    /// <summary>
    /// The most characters one paste may carry, matching the agent's per-command type limit.
    /// </summary>
    /// <remarks>
    /// A paste is typed key by key at roughly one character every 30 ms, so a cap is a kindness as
    /// much as a protocol limit: pasting a whole document would lock the phone into a minute of
    /// autonomous typing with no way to see it coming. The cap is applied after cleaning, because
    /// decomposition can lengthen the text — an ellipsis becomes three characters.
    /// </remarks>
    public const int MaxLength = 4096;

    /// <summary>Turn clipboard text into a typeable paste.</summary>
    public static ClipboardPaste Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new ClipboardPaste(string.Empty, Dropped: 0, Truncated: false);
        }

        // Two passes rather than one. The first folds the punctuation that has an obvious ASCII
        // shape but no Unicode decomposition to it -- quotes, dashes, the ellipsis -- because
        // normalization alone would drop those as unmappable. The second decomposes what remains,
        // which is what turns accented letters into their base form instead of losing them.
        string folded = FoldPunctuation(raw);
        string decomposed = folded.Normalize(NormalizationForm.FormKD);

        StringBuilder builder = new(decomposed.Length);
        int dropped = 0;
        foreach (char character in decomposed)
        {
            // Combining marks are what a decomposed "é" leaves behind once the "e" is taken; they
            // are removed rather than counted, because the letter they belonged to is already in the
            // output and dropping the mark is the whole point of decomposing.
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (IsTypeable(character))
            {
                builder.Append(character);
            }
            else
            {
                dropped++;
            }
        }

        bool truncated = builder.Length > MaxLength;
        if (truncated)
        {
            builder.Length = MaxLength;
        }

        return new ClipboardPaste(builder.ToString(), dropped, truncated);
    }

    /// <summary>Whether the phone's ASCII keyboard has a key for this character.</summary>
    /// <remarks>
    /// Printable ASCII, plus the newline the agent already accepts as Return. Tabs are folded to a
    /// space in <see cref="FoldPunctuation"/> rather than allowed here, because the virtual keyboard
    /// has no reliable Tab in a text field and a literal tab would more often move focus than indent.
    /// </remarks>
    private static bool IsTypeable(char character) =>
        character == '\n' || character is >= ' ' and <= '~';

    /// <summary>
    /// Replace common typographic characters with their ASCII shapes, and normalize whitespace.
    /// </summary>
    private static string FoldPunctuation(string text)
    {
        // Line endings first, and by replacement rather than in the loop, so CRLF collapses to one
        // newline while a deliberate blank line -- two newlines in a row -- survives. Character by
        // character could not tell the LF of a CRLF apart from the second LF of an intentional gap,
        // and would have eaten every blank line.
        string newlines = text.Replace("\r\n", "\n").Replace('\r', '\n');

        StringBuilder builder = new(newlines.Length);
        foreach (char character in newlines)
        {
            switch (character)
            {
                case '\n':
                    builder.Append('\n');
                    break;

                case '‘' or '’' or '‚' or '‛' or '′':
                    builder.Append('\'');
                    break;

                case '“' or '”' or '„' or '‟' or '″':
                    builder.Append('"');
                    break;

                case '–' or '—' or '―' or '−':
                    builder.Append('-');
                    break;

                case '…':
                    builder.Append("...");
                    break;

                case '•' or '·' or '‧':
                    builder.Append('*');
                    break;

                default:
                    // Any other whitespace -- tab, non-breaking space, thin space, the ideographic
                    // space -- becomes a single ordinary space. Newline is handled above so it
                    // survives; everything else iOS would not render as a plain gap collapses to one.
                    builder.Append(character != '\n' && char.IsWhiteSpace(character) ? ' ' : character);
                    break;
            }
        }

        return builder.ToString();
    }
}
