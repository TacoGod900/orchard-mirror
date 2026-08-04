using System.Globalization;
using System.Text;

namespace Orchard.Protocol;

/// <summary>An expected protocol rejection with a stable error code.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string code, string message)
        : base(ProtocolTerminalText.Escape(message))
    {
        Code = ProtocolTerminalText.Escape(code);
    }

    public ProtocolException(string code, string message, Exception innerException)
        : base(ProtocolTerminalText.Escape(message), innerException)
    {
        Code = ProtocolTerminalText.Escape(code);
    }

    public string Code { get; }

    public override string ToString() => ProtocolTerminalText.Escape(base.ToString());
}

/// <summary>A well-formed message that violates the version 1 contract.</summary>
public sealed class ProtocolValidationException : ProtocolException
{
    public ProtocolValidationException(string code, string path, string message)
        : base(code, $"{path}: {message}")
    {
        Path = ProtocolTerminalText.Escape(path);
    }

    public string Path { get; }
}

internal static class ProtocolTerminalText
{
    public static string Escape(string? value)
    {
        if (value is null)
        {
            return "[null]";
        }

        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\n')
            {
                output.Append("\\n");
                continue;
            }
            if (character == '\r')
            {
                output.Append("\\r");
                continue;
            }
            if (character == '\t')
            {
                output.Append("\\t");
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                var lowSurrogate = value[index + 1];
                var rune = new Rune(character, lowSurrogate);
                if (IsTerminalUnsafe(Rune.GetUnicodeCategory(rune)))
                {
                    AppendUnicodeEscape(output, character);
                    AppendUnicodeEscape(output, lowSurrogate);
                }
                else
                {
                    output.Append(character);
                    output.Append(lowSurrogate);
                }

                index++;
                continue;
            }

            var category = char.IsSurrogate(character)
                ? UnicodeCategory.Surrogate
                : CharUnicodeInfo.GetUnicodeCategory(character);
            if (IsTerminalUnsafe(category))
            {
                AppendUnicodeEscape(output, character);
            }
            else
            {
                output.Append(character);
            }
        }

        return output.ToString();
    }

    private static bool IsTerminalUnsafe(UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate;

    private static void AppendUnicodeEscape(StringBuilder output, char character) =>
        output.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
}
