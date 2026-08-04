using System.Text;
using Orchard.Core;

namespace Orchard.Compiler;

internal sealed class SwiftUiParser(
    IReadOnlyList<SwiftToken> tokens,
    IReadOnlyList<OrchardDiagnostic> lexerDiagnostics,
    string file)
{
    private static readonly HashSet<string> ActionViews = new(StringComparer.Ordinal)
    {
        "Button"
    };

    private readonly List<OrchardDiagnostic> _diagnostics = [.. lexerDiagnostics];
    private readonly List<StateDefinition> _state = [];
    private int _current;
    private int _nodeId;
    private int _viewDepth = 1;
    private bool _abortParsing;

    public (ViewNode? Root, IReadOnlyList<StateDefinition> State, IReadOnlyList<OrchardDiagnostic> Diagnostics) Parse()
    {
        ViewNode? root = null;
        var bodyDeclarations = Enumerable.Range(0, Math.Max(0, tokens.Count - 1))
            .Where(index => tokens[index] is { Kind: SwiftTokenKind.Identifier, Text: "var" } &&
                            tokens[index + 1] is { Kind: SwiftTokenKind.Identifier, Text: "body" })
            .ToList();

        if (bodyDeclarations.Count > 1)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1111",
                DiagnosticSeverity.Error,
                $"The bootstrap compiler found {bodyDeclarations.Count} body declarations and cannot select a root type unambiguously.",
                tokens[bodyDeclarations[1]].Location,
                "Compile one root View per bootstrap source file."));
            return (null, _state, _diagnostics);
        }

        while (!IsAtEnd)
        {
            SkipTrivia();
            if (CheckSymbol("@") && CheckIdentifier("State", 1))
            {
                ParseStateDeclaration();
                continue;
            }

            if (CheckIdentifier("var") && CheckIdentifier("body", 1))
            {
                root = ParseBody();
                break;
            }

            Advance();
        }

        if (root is null && !_diagnostics.Any(diagnostic => diagnostic.Code == "ORC1111"))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1100",
                DiagnosticSeverity.Error,
                "No 'var body: some View' declaration was found.",
                new SourceLocation(file, 1, 1),
                "Add a SwiftUI View with a body property."));
        }

        return (root, _state, _diagnostics);
    }

    private void ParseStateDeclaration()
    {
        var start = Advance().Location;
        Advance(); // State
        while (!IsAtEnd && !CheckIdentifier("var") && !Check(SwiftTokenKind.NewLine))
        {
            Advance();
        }

        if (!MatchIdentifier("var") || !Check(SwiftTokenKind.Identifier))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1101",
                DiagnosticSeverity.Warning,
                "Orchard could not parse this @State declaration.",
                start));
            SkipToLineEnd();
            return;
        }

        var name = Advance();
        var declaredType = string.Empty;
        if (MatchSymbol(":"))
        {
            var typeBuilder = new StringBuilder();
            while (!IsAtEnd && !CheckSymbol("=") && !Check(SwiftTokenKind.NewLine))
            {
                typeBuilder.Append(Advance().Raw);
            }

            declaredType = typeBuilder.ToString();
        }

        if (!MatchSymbol("="))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1102",
                DiagnosticSeverity.Warning,
                $"State '{name.Text}' has no local initial value; the preview will use an empty value.",
                name.Location));
            _state.Add(new StateDefinition(name.Text, InferStateKind(declaredType, null), string.Empty, name.Location));
            SkipToLineEnd();
            return;
        }

        SkipTrivia();
        var value = IsAtEnd ? null : Advance();
        _state.Add(new StateDefinition(
            name.Text,
            InferStateKind(declaredType, value),
            value?.Text ?? string.Empty,
            name.Location));
        SkipToLineEnd();
    }

    private ViewNode? ParseBody()
    {
        Advance(); // var
        Advance(); // body
        while (!IsAtEnd && !CheckSymbol("{"))
        {
            Advance();
        }

        if (!MatchSymbol("{"))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1103",
                DiagnosticSeverity.Error,
                "The body property has no opening brace.",
                Previous.Location));
            return null;
        }

        SkipTrivia();
        var root = ParseViewExpression();
        if (root is null)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1104",
                DiagnosticSeverity.Error,
                "The body does not contain a supported view expression.",
                Peek().Location));
        }

        SkipTriviaAndSeparators();
        if (!_abortParsing && !CheckSymbol("}") && !IsAtEnd)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1110",
                DiagnosticSeverity.Error,
                "The bootstrap body contains more than one root expression or unsupported trailing syntax.",
                Peek().Location,
                "Wrap sibling views in a stack, Group, List, or other container."));
            SkipBodyRemainder();
        }

        if (!_abortParsing && !MatchSymbol("}"))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1109",
                DiagnosticSeverity.Error,
                "The body property has an unterminated closure.",
                Previous.Location,
                "Add the closing brace for 'var body'."));
        }

        return root;
    }

    private ViewNode? ParseViewExpression()
    {
        SkipTriviaAndSeparators();
        if (!Check(SwiftTokenKind.Identifier))
        {
            return null;
        }

        var name = Advance();
        if (name.Text is "if" or "else" or "for" or "switch")
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1105",
                DiagnosticSeverity.Warning,
                $"Control-flow expression '{name.Text}' is preserved only as a compatibility placeholder in this milestone.",
                name.Location,
                "Extract this branch into a dedicated View while using the proof-of-concept runtime."));
        }

        var arguments = CheckSymbol("(") ? ParseArguments() : new Dictionary<string, string>(StringComparer.Ordinal);
        if (_nodeId >= OrchardCompiler.MaximumViewNodes)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1112",
                DiagnosticSeverity.Error,
                $"The view tree exceeds the {OrchardCompiler.MaximumViewNodes:N0}-node compiler limit.",
                name.Location));
            _abortParsing = true;
            while (!IsAtEnd)
            {
                Advance();
            }

            return null;
        }

        var node = new ViewNode
        {
            Id = $"view-{++_nodeId:D4}",
            Type = name.Text,
            Arguments = arguments,
            Location = name.Location
        };

        SkipTrivia();
        if (CheckSymbol("{"))
        {
            if (ActionViews.Contains(name.Text))
            {
                node.Events.Add(new ViewEvent("action", ParseRawClosure(), Peek(-1).Location));
            }
            else
            {
                ParseChildrenClosure(node);
            }
        }

        SkipTrivia();
        while (MatchSymbol("."))
        {
            if (!Check(SwiftTokenKind.Identifier))
            {
                _diagnostics.Add(new OrchardDiagnostic(
                    "ORC1106",
                    DiagnosticSeverity.Error,
                    "Expected a modifier name after '.'.",
                    Previous.Location));
                break;
            }

            var modifierName = Advance();
            var modifierArguments = CheckSymbol("(")
                ? ParseArguments()
                : new Dictionary<string, string>(StringComparer.Ordinal);

            if (CheckSymbol("{"))
            {
                modifierArguments["closure"] = ParseRawClosure();
            }

            node.Modifiers.Add(new ViewModifier(modifierName.Text, modifierArguments, modifierName.Location));
            SkipTrivia();
        }

        return node;
    }

    private void ParseChildrenClosure(ViewNode parent)
    {
        if (_viewDepth >= OrchardCompiler.MaximumViewDepth)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1113",
                DiagnosticSeverity.Error,
                $"The view tree exceeds the maximum depth of {OrchardCompiler.MaximumViewDepth:N0}.",
                parent.Location));
            SkipBalancedClosure();
            return;
        }

        MatchSymbol("{");
        _viewDepth++;
        try
        {
            while (!_abortParsing && !IsAtEnd && !CheckSymbol("}"))
            {
                SkipTriviaAndSeparators();
                if (CheckSymbol("}"))
                {
                    break;
                }

                var before = _current;
                var child = ParseViewExpression();
                if (child is not null)
                {
                    parent.Children.Add(child);
                }

                if (_current == before)
                {
                    Advance();
                }
            }
        }
        finally
        {
            _viewDepth--;
        }

        if (!_abortParsing && !MatchSymbol("}"))
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1107",
                DiagnosticSeverity.Error,
                $"View '{parent.Type}' has an unterminated content closure.",
                parent.Location));
        }
    }

    private string ParseRawClosure()
    {
        var openingLocation = Peek().Location;
        if (!MatchSymbol("{"))
        {
            return string.Empty;
        }

        var depth = 1;
        var body = new StringBuilder();
        while (!IsAtEnd && depth > 0)
        {
            var token = Advance();
            if (token.Kind == SwiftTokenKind.Symbol && token.Text == "{")
            {
                depth++;
            }
            else if (token.Kind == SwiftTokenKind.Symbol && token.Text == "}")
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }

            AppendToken(body, token);
        }

        if (depth != 0)
        {
            _diagnostics.Add(new OrchardDiagnostic(
                "ORC1114",
                DiagnosticSeverity.Error,
                "An action or modifier closure is unterminated.",
                openingLocation,
                "Add the closing brace for this closure."));
        }

        return body.ToString().Trim();
    }

    private void SkipBalancedClosure()
    {
        if (!MatchSymbol("{"))
        {
            return;
        }

        var depth = 1;
        while (!IsAtEnd && depth > 0)
        {
            var token = Advance();
            if (token.Kind == SwiftTokenKind.Symbol && token.Text == "{")
            {
                depth++;
            }
            else if (token.Kind == SwiftTokenKind.Symbol && token.Text == "}")
            {
                depth--;
            }
        }
    }

    private void SkipBodyRemainder()
    {
        var depth = 0;
        while (!IsAtEnd)
        {
            if (CheckSymbol("}") && depth == 0)
            {
                return;
            }

            var token = Advance();
            if (token.Kind == SwiftTokenKind.Symbol && token.Text == "{")
            {
                depth++;
            }
            else if (token.Kind == SwiftTokenKind.Symbol && token.Text == "}" && depth > 0)
            {
                depth--;
            }
        }
    }

    private Dictionary<string, string> ParseArguments()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        MatchSymbol("(");
        var currentArgument = new List<SwiftToken>();
        var nestedDepth = 0;
        var ordinal = 0;

        while (!IsAtEnd)
        {
            var token = Advance();
            if (token.Kind == SwiftTokenKind.Symbol && token.Text is "(" or "[" or "{")
            {
                nestedDepth++;
                currentArgument.Add(token);
                continue;
            }

            if (token.Kind == SwiftTokenKind.Symbol && token.Text is ")" or "]" or "}")
            {
                if (token.Text == ")" && nestedDepth == 0)
                {
                    AddArgument(result, currentArgument, ordinal++);
                    return result;
                }

                nestedDepth--;
                currentArgument.Add(token);
                continue;
            }

            if (token.Kind == SwiftTokenKind.Symbol && token.Text == "," && nestedDepth == 0)
            {
                AddArgument(result, currentArgument, ordinal++);
                currentArgument.Clear();
                continue;
            }

            currentArgument.Add(token);
        }

        _diagnostics.Add(new OrchardDiagnostic(
            "ORC1108",
            DiagnosticSeverity.Error,
            "Unterminated argument list.",
            Previous.Location));
        AddArgument(result, currentArgument, ordinal);
        return result;
    }

    private static void AddArgument(Dictionary<string, string> target, List<SwiftToken> tokens, int ordinal)
    {
        var meaningful = tokens.Where(token => token.Kind != SwiftTokenKind.NewLine).ToList();
        if (meaningful.Count == 0)
        {
            return;
        }

        var colon = meaningful.FindIndex(token => token.Kind == SwiftTokenKind.Symbol && token.Text == ":");
        var key = colon == 1 && meaningful[0].Kind == SwiftTokenKind.Identifier
            ? meaningful[0].Text
            : $"_{ordinal}";
        var valueTokens = colon == 1 ? meaningful[(colon + 1)..] : meaningful;
        target[key] = FormatTokens(valueTokens);
    }

    private static string FormatTokens(List<SwiftToken> tokens)
    {
        if (tokens.Count == 1 && tokens[0].Kind == SwiftTokenKind.String)
        {
            return tokens[0].Text;
        }

        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            AppendToken(builder, token);
        }

        return builder.ToString().Trim();
    }

    private static void AppendToken(StringBuilder builder, SwiftToken token)
    {
        if (token.Kind == SwiftTokenKind.NewLine)
        {
            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            return;
        }

        var needsSpace = builder.Length > 0 &&
            builder[^1] is not '(' and not '[' and not '{' and not '.' and not '$' and not ' ' &&
            token.Raw is not ")" and not "]" and not "}" and not "," and not "." and not ":";
        if (needsSpace)
        {
            builder.Append(' ');
        }

        builder.Append(token.Raw);
    }

    private void SkipTrivia()
    {
        while (Match(SwiftTokenKind.NewLine))
        {
        }
    }

    private void SkipTriviaAndSeparators()
    {
        while (Check(SwiftTokenKind.NewLine) || CheckSymbol(",") || CheckSymbol(";"))
        {
            Advance();
        }
    }

    private void SkipToLineEnd()
    {
        while (!IsAtEnd && !Check(SwiftTokenKind.NewLine))
        {
            Advance();
        }
    }

    private static StateValueKind InferStateKind(string declaredType, SwiftToken? value)
    {
        if (declaredType.Contains("Bool", StringComparison.Ordinal) || value?.Text is "true" or "false")
        {
            return StateValueKind.Flag;
        }

        if (declaredType.Contains("Int", StringComparison.Ordinal) ||
            value is { Kind: SwiftTokenKind.Number } && !value.Text.Contains('.', StringComparison.Ordinal))
        {
            return StateValueKind.WholeNumber;
        }

        if (declaredType.Contains("Double", StringComparison.Ordinal) || declaredType.Contains("Float", StringComparison.Ordinal) ||
            value is { Kind: SwiftTokenKind.Number } && value.Text.Contains('.', StringComparison.Ordinal))
        {
            return StateValueKind.DecimalNumber;
        }

        if (declaredType.Contains("String", StringComparison.Ordinal) || value?.Kind == SwiftTokenKind.String)
        {
            return StateValueKind.Text;
        }

        return StateValueKind.Unknown;
    }

    private bool Match(SwiftTokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool MatchIdentifier(string value)
    {
        if (!CheckIdentifier(value))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool MatchSymbol(string value)
    {
        if (!CheckSymbol(value))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool Check(SwiftTokenKind kind) => Peek().Kind == kind;

    private bool CheckIdentifier(string value, int distance = 0) =>
        Peek(distance) is { Kind: SwiftTokenKind.Identifier } token && token.Text == value;

    private bool CheckSymbol(string value, int distance = 0) =>
        Peek(distance) is { Kind: SwiftTokenKind.Symbol } token && token.Text == value;

    private SwiftToken Advance()
    {
        if (!IsAtEnd)
        {
            _current++;
        }

        return Previous;
    }

    private bool IsAtEnd => Peek().Kind == SwiftTokenKind.End;

    private SwiftToken Peek(int distance = 0)
    {
        var index = Math.Clamp(_current + distance, 0, tokens.Count - 1);
        return tokens[index];
    }

    private SwiftToken Previous => tokens[Math.Max(0, _current - 1)];
}
