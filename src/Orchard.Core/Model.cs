using System.Text.Json.Serialization;

namespace Orchard.Core;

public static class OrchardSchema
{
    public const string CurrentVersion = "0.1.0";
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum CompatibilityStatus
{
    Supported,
    Partial,
    RemoteOnly,
    Unsupported
}

public enum StateValueKind
{
    Text,
    Flag,
    WholeNumber,
    DecimalNumber,
    Unknown
}

public sealed record SourceLocation(
    string File,
    int Line,
    int Column,
    int Length = 1);

public sealed record OrchardDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceLocation? Location = null,
    string? Suggestion = null,
    string? DocumentationUrl = null);

public sealed record StateDefinition(
    string Name,
    StateValueKind Kind,
    string InitialValue,
    SourceLocation? Location = null);

public sealed record ViewModifier(
    string Name,
    IReadOnlyDictionary<string, string> Arguments,
    SourceLocation? Location = null);

public sealed record ViewEvent(
    string Name,
    string Body,
    SourceLocation? Location = null);

public sealed class ViewNode
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public Dictionary<string, string> Arguments { get; init; } = new(StringComparer.Ordinal);

    public List<ViewModifier> Modifiers { get; init; } = [];

    public List<ViewEvent> Events { get; init; } = [];

    public List<ViewNode> Children { get; init; } = [];

    public SourceLocation? Location { get; init; }
}

public sealed record ApiUsage(
    string Symbol,
    string Category,
    CompatibilityStatus Status,
    string? Notes,
    SourceLocation? Location);

public sealed class CompatibilityReport
{
    public required double LocalCompatibilityPercent { get; init; }

    public required IReadOnlyList<ApiUsage> ApiUsages { get; init; }

    public required IReadOnlyList<string> RequiredRemoteCapabilities { get; init; }

    public required IReadOnlyList<string> UnsupportedSymbols { get; init; }
}

public sealed class OrchardApplication
{
    public required string SchemaVersion { get; init; }

    public required string ApplicationId { get; init; }

    public required string DisplayName { get; init; }

    public required string SourceFile { get; init; }

    public required ViewNode RootView { get; init; }

    public required IReadOnlyList<StateDefinition> State { get; init; }

    public required CompatibilityReport Compatibility { get; init; }

    public required DateTimeOffset CompiledAtUtc { get; init; }

    [JsonIgnore]
    public bool IsLocallyRunnable => Compatibility.UnsupportedSymbols.Count == 0;
}

public sealed record CompilationResult(
    OrchardApplication? Application,
    IReadOnlyList<OrchardDiagnostic> Diagnostics)
{
    public bool Success =>
        Application is not null &&
        Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}
