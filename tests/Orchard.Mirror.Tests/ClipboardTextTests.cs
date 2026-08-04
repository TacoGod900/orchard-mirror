using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The PC-to-phone half of a shared clipboard. The phone is typed through a US-ASCII HID keyboard
/// that throws on the first character it can't map — after typing everything before it — so raw
/// clipboard text with a smart quote or an accent in it would land half a line and then fail.
/// </summary>
/// <remarks>
/// These are all pure text-in, text-out, which is the point: the sanitizer is the one part of the
/// paste feature that can be proven without the phone, so it is proven here rather than discovered
/// on the device.
/// </remarks>
internal static class ClipboardTextTests
{
    /// <summary>Ordinary ASCII passes through untouched, and nothing is reported as lost.</summary>
    internal static void PlainAsciiSurvivesUnchanged()
    {
        ClipboardPaste paste = ClipboardText.Sanitize("Hello, world! 123 @#$%");
        Assert(paste.Text == "Hello, world! 123 @#$%", $"plain ASCII was altered to \"{paste.Text}\"");
        Assert(paste.Dropped == 0, $"plain ASCII reported {paste.Dropped} dropped characters");
        Assert(!paste.Truncated, "plain ASCII was reported as truncated");
    }

    /// <summary>
    /// Typographic punctuation becomes its ASCII shape rather than being dropped.
    /// </summary>
    /// <remarks>
    /// This is the difference between a paste that reads right and one full of holes: a word
    /// processor turns straight quotes curly and hyphens into em-dashes without asking, so almost
    /// any copied prose carries characters the phone has no key for. Folding them is what lets a
    /// normal sentence paste as a normal sentence.
    /// </remarks>
    internal static void SmartPunctuationBecomesAscii()
    {
        ClipboardPaste paste = ClipboardText.Sanitize("\u201CDon\u2019t\u2014wait\u2026\u201D");
        Assert(paste.Text == "\"Don't-wait...\"", $"smart punctuation folded to \"{paste.Text}\"");
        Assert(paste.Dropped == 0, $"folded punctuation still reported {paste.Dropped} dropped");
    }

    /// <summary>Accented letters decompose to their base rather than being lost.</summary>
    internal static void AccentsDecomposeToBaseLetters()
    {
        ClipboardPaste paste = ClipboardText.Sanitize("café na\u00EFve r\u00E9sum\u00E9");
        Assert(paste.Text == "cafe naive resume", $"accents produced \"{paste.Text}\"");
        Assert(paste.Dropped == 0, $"decomposed accents reported {paste.Dropped} dropped");
    }

    /// <summary>
    /// Characters with no ASCII equivalent are removed and counted, not silently swallowed.
    /// </summary>
    /// <remarks>
    /// The count is the whole reason the sanitizer returns a record rather than a string: an emoji
    /// or a Greek letter genuinely cannot be typed, and the honest thing is to paste what can be and
    /// tell the owner the rest was dropped, not to pretend the clipboard held only what survived.
    /// </remarks>
    internal static void UntypeableCharactersAreDroppedAndCounted()
    {
        ClipboardPaste paste = ClipboardText.Sanitize("hi \u03C0\u03C9");
        Assert(paste.Text == "hi ", $"untypeable text left \"{paste.Text}\"");
        Assert(paste.Dropped == 2, $"two untypeable characters were counted as {paste.Dropped}");
    }

    /// <summary>Windows line endings collapse to one newline, but deliberate blank lines survive.</summary>
    /// <remarks>
    /// The bug this guards is specific: an earlier draft coalesced newlines in the character loop and
    /// could not tell the LF of a CRLF apart from the second LF of an intentional gap, so it ate
    /// every blank line. A pasted address or code block with spacing has to keep that spacing.
    /// </remarks>
    internal static void LineEndingsNormalizeWithoutEatingBlankLines()
    {
        ClipboardPaste crlf = ClipboardText.Sanitize("a\r\nb");
        Assert(crlf.Text == "a\nb", $"CRLF produced {Escape(crlf.Text)}");

        ClipboardPaste blank = ClipboardText.Sanitize("a\r\n\r\nb");
        Assert(blank.Text == "a\n\nb", $"a deliberate blank line was lost: {Escape(blank.Text)}");

        ClipboardPaste bareCr = ClipboardText.Sanitize("a\rb");
        Assert(bareCr.Text == "a\nb", $"a bare carriage return produced {Escape(bareCr.Text)}");
    }

    /// <summary>Exotic spaces and tabs become an ordinary space; newlines are not touched.</summary>
    internal static void OddWhitespaceBecomesPlainSpace()
    {
        ClipboardPaste paste = ClipboardText.Sanitize("a\u00A0b\tc\u2003d");
        Assert(paste.Text == "a b c d", $"odd whitespace produced \"{paste.Text}\"");
        Assert(paste.Dropped == 0, $"whitespace folding reported {paste.Dropped} dropped");
    }

    /// <summary>Nothing in, nothing out — and no false claim of dropped characters.</summary>
    internal static void EmptyAndNullAreHandled()
    {
        ClipboardPaste empty = ClipboardText.Sanitize(string.Empty);
        Assert(!empty.HasText && empty.Dropped == 0, "empty input was not clean-empty");

        ClipboardPaste ofNull = ClipboardText.Sanitize(null);
        Assert(!ofNull.HasText && ofNull.Dropped == 0, "null input was not clean-empty");
    }

    /// <summary>
    /// Text past the per-command limit is cut and flagged, and the cut lands on the real limit.
    /// </summary>
    /// <remarks>
    /// The cap is applied after folding, because folding can lengthen the text — an ellipsis becomes
    /// three characters — so a string that was under the limit before cleaning can cross it after,
    /// and the length that matters is the one actually sent.
    /// </remarks>
    internal static void OverlongTextIsTruncatedAndFlagged()
    {
        ClipboardPaste paste = ClipboardText.Sanitize(new string('x', ClipboardText.MaxLength + 50));
        Assert(paste.Text.Length == ClipboardText.MaxLength, $"truncated to {paste.Text.Length}");
        Assert(paste.Truncated, "an overlong paste was not flagged as truncated");

        ClipboardPaste exact = ClipboardText.Sanitize(new string('x', ClipboardText.MaxLength));
        Assert(!exact.Truncated, "a paste exactly at the limit was flagged as truncated");
    }

    /// <summary>The whole output is typeable — the promise the phone's keyboard depends on.</summary>
    /// <remarks>
    /// The backstop for all of the above: whatever combination of scripts, marks and control
    /// characters goes in, every character that comes out is one the agent will not throw on. If this
    /// ever fails, a paste can still land half a line and error, which is the failure the sanitizer
    /// exists to remove.
    /// </remarks>
    internal static void EveryOutputCharacterIsTypeable()
    {
        string hostile = "Ω\t\u201Chi\u201D\r\n\u00E9\U0001F600\u0000\u007Fend\u2026";
        ClipboardPaste paste = ClipboardText.Sanitize(hostile);
        foreach (char character in paste.Text)
        {
            bool ok = character == '\n' || character is >= ' ' and <= '~';
            Assert(ok, $"output kept an untypeable character U+{(int)character:X4}");
        }
    }

    private static string Escape(string text) => text.Replace("\n", "\\n").Replace("\r", "\\r");

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
