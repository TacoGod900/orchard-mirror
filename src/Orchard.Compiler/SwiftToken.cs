using Orchard.Core;

namespace Orchard.Compiler;

internal enum SwiftTokenKind
{
    Identifier,
    String,
    Number,
    Symbol,
    NewLine,
    End
}

internal sealed record SwiftToken(
    SwiftTokenKind Kind,
    string Text,
    string Raw,
    SourceLocation Location);

