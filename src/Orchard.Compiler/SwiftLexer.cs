using System.Globalization;
using System.Text;
using Orchard.Core;

namespace Orchard.Compiler;

internal sealed class SwiftLexer(string source, string file)
{
    private readonly List<OrchardDiagnostic> _diagnostics = [];
    private int _offset;
    private int _line = 1;
    private int _column = 1;

    public (IReadOnlyList<SwiftToken> Tokens, IReadOnlyList<OrchardDiagnostic> Diagnostics) Lex()
    {
        var tokens = new List<SwiftToken>();

        while (!IsAtEnd)
        {
            if (tokens.Count >= OrchardCompiler.MaximumTokens)
            {
                _diagnostics.Add(new OrchardDiagnostic(
                    "ORC1004",
                    DiagnosticSeverity.Error,
                    $"Source exceeds the {OrchardCompiler.MaximumTokens:N0}-token compiler limit.",
                    CurrentLocation()));
                while (!IsAtEnd)
                {
                    Advance();
                }

                break;
            }

            var current = Peek();
            if (current is ' ' or '\t' or '\r')
            {
                Advance();
                continue;
            }

            if (current == '\n')
            {
                var location = CurrentLocation();
                Advance();
                tokens.Add(new SwiftToken(SwiftTokenKind.NewLine, "\n", "\n", location));
                continue;
            }

            if (current == '/' && Peek(1) == '/')
            {
                SkipLineComment();
                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                SkipBlockComment();
                continue;
            }

            if (current == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            if (IsIdentifierStart(current))
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            if (char.IsDigit(current) || (current == '-' && char.IsDigit(Peek(1))))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            var symbolLocation = CurrentLocation();
            var symbol = Advance().ToString(CultureInfo.InvariantCulture);
            tokens.Add(new SwiftToken(SwiftTokenKind.Symbol, symbol, symbol, symbolLocation));
        }

        tokens.Add(new SwiftToken(SwiftTokenKind.End, string.Empty, string.Empty, CurrentLocation()));
        return (tokens, _diagnostics);
    }

    private SwiftToken ReadIdentifier()
    {
        var location = CurrentLocation();
        var start = _offset;
        Advance();
        while (!IsAtEnd && IsIdentifierPart(Peek()))
        {
            Advance();
        }

        var value = source[start.._offset];
        return new SwiftToken(SwiftTokenKind.Identifier, value, value, location with { Length = value.Length });
    }

    private SwiftToken ReadNumber()
    {
        var location = CurrentLocation();
        var start = _offset;
        if (Peek() == '-')
        {
            Advance();
        }

        while (!IsAtEnd && char.IsDigit(Peek()))
        {
            Advance();
        }

        if (!IsAtEnd && Peek() == '.' && char.IsDigit(Peek(1)))
        {
            Advance();
            while (!IsAtEnd && char.IsDigit(Peek()))
            {
                Advance();
            }
        }

        var value = source[start.._offset];
        return new SwiftToken(SwiftTokenKind.Number, value, value, location with { Length = value.Length });
    }

    private SwiftToken ReadString()
    {
        var location = CurrentLocation();
        var raw = new StringBuilder();
        var value = new StringBuilder();
        raw.Append(Advance());

        while (!IsAtEnd && Peek() != '"')
        {
            var character = Advance();
            raw.Append(character);
            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (IsAtEnd)
            {
                break;
            }

            var escaped = Advance();
            raw.Append(escaped);
            switch (escaped)
            {
                case 'n':
                    value.Append('\n');
                    break;
                case 'r':
                    value.Append('\r');
                    break;
                case 't':
                    value.Append('\t');
                    break;
                case '"':
                    value.Append('"');
                    break;
                case '\\':
                    value.Append('\\');
                    break;
                default:
                    // Preserve Swift interpolation and any escape this bootstrap does not interpret.
                    value.Append('\\');
                    value.Append(escaped);
                    break;
            }
        }

        if (IsAtEnd)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1001",
                DiagnosticSeverity.Error,
                "Unterminated string literal.",
                location,
                "Add a closing quote before the end of the file."));
        }
        else
        {
            raw.Append(Advance());
        }

        return new SwiftToken(
            SwiftTokenKind.String,
            value.ToString(),
            raw.ToString(),
            location with { Length = Math.Max(1, raw.Length) });
    }

    private void SkipLineComment()
    {
        while (!IsAtEnd && Peek() != '\n')
        {
            Advance();
        }
    }

    private void SkipBlockComment()
    {
        var location = CurrentLocation();
        Advance();
        Advance();
        var depth = 1;

        while (!IsAtEnd && depth > 0)
        {
            if (Peek() == '/' && Peek(1) == '*')
            {
                Advance();
                Advance();
                depth++;
            }
            else if (Peek() == '*' && Peek(1) == '/')
            {
                Advance();
                Advance();
                depth--;
            }
            else
            {
                Advance();
            }
        }

        if (depth != 0)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1002",
                DiagnosticSeverity.Error,
                "Unterminated block comment.",
                location));
        }
    }

    private SourceLocation CurrentLocation() => new(file, _line, _column);

    private bool IsAtEnd => _offset >= source.Length;

    private char Peek(int distance = 0) =>
        _offset + distance >= source.Length ? '\0' : source[_offset + distance];

    private char Advance()
    {
        var character = source[_offset++];
        if (character == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        return character;
    }

    private static bool IsIdentifierStart(char character) =>
        character == '_' || char.IsLetter(character);

    private static bool IsIdentifierPart(char character) =>
        character == '_' || char.IsLetterOrDigit(character);
}
