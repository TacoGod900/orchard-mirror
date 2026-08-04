using Orchard.Core;

namespace Orchard.Compiler;

public sealed class OrchardCompiler(CompatibilityCatalog? catalog = null)
{
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    public const int MaximumTokens = 250_000;
    public const int MaximumViewNodes = OrchardApplicationReader.MaximumNodes;
    public const int MaximumViewDepth = OrchardApplicationReader.MaximumDepth;

    private readonly CompatibilityCatalog _catalog = catalog ?? new CompatibilityCatalog();

    public CompilationResult Compile(string source, string file, ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
        {
            return new CompilationResult(null,
            [
                new OrchardDiagnostic(
                    "ORC1003",
                    DiagnosticSeverity.Error,
                    $"Source exceeds the {MaximumSourceBytes:N0}-byte compiler limit.",
                    new SourceLocation(file, 1, 1))
            ]);
        }

        var lexer = new SwiftLexer(source, file);
        var (tokens, lexerDiagnostics) = lexer.Lex();
        var parser = new SwiftUiParser(tokens, lexerDiagnostics, file);
        var (root, state, parserDiagnostics) = parser.Parse();
        var diagnostics = parserDiagnostics.ToList();

        if (root is null)
        {
            return new CompilationResult(null, diagnostics);
        }

        var identifiers = tokens
            .Where(token => token.Kind == SwiftTokenKind.Identifier)
            .Select(token => token.Text);
        var (report, compatibilityDiagnostics) = AnalyseCompatibility(root, identifiers);
        diagnostics.AddRange(compatibilityDiagnostics);
        if (manifest.TreatCompatibilityWarningsAsErrors)
        {
            diagnostics = diagnostics
                .Select(diagnostic => diagnostic.Code.StartsWith("ORC2", StringComparison.Ordinal) &&
                                      diagnostic.Severity == DiagnosticSeverity.Warning
                    ? diagnostic with { Severity = DiagnosticSeverity.Error }
                    : diagnostic)
                .ToList();
        }

        var application = new OrchardApplication
        {
            SchemaVersion = OrchardSchema.CurrentVersion,
            ApplicationId = manifest.ApplicationId,
            DisplayName = manifest.DisplayName,
            SourceFile = file,
            RootView = root,
            State = state,
            Compatibility = report,
            CompiledAtUtc = ResolveBuildTimestamp()
        };

        return new CompilationResult(application, diagnostics);
    }

    public CompilationResult CompileProject(string projectPath)
    {
        var manifestPath = ResolveManifestPath(projectPath);
        var manifest = ProjectManifest.Load(manifestPath);
        var projectDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("The project manifest has no parent directory.");
        var entryPoint = Path.GetFullPath(Path.Combine(projectDirectory, manifest.EntryPoint));
        if (!IsWithinDirectory(projectDirectory, entryPoint))
        {
            throw new InvalidDataException("The entry point must be inside the Orchard project directory.");
        }

        RejectReparsePoints(projectDirectory, entryPoint);

        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException($"Entry point '{manifest.EntryPoint}' was not found.", entryPoint);
        }

        var logicalSourcePath = manifest.EntryPoint.Replace(Path.DirectorySeparatorChar, '/');
        var source = BoundedUtf8File.ReadAllText(
            entryPoint,
            MaximumSourceBytes,
            $"Entry point '{manifest.EntryPoint}'");
        return Compile(source, logicalSourcePath, manifest);
    }

    private (CompatibilityReport Report, IReadOnlyList<OrchardDiagnostic> Diagnostics) AnalyseCompatibility(
        ViewNode root,
        IEnumerable<string> identifiers)
    {
        var usages = new List<ApiUsage>();
        var diagnostics = new List<OrchardDiagnostic>();
        var remote = new SortedSet<string>(StringComparer.Ordinal);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var node in Traverse(root))
        {
            var viewEntry = _catalog.GetView(node.Type);
            usages.Add(new ApiUsage(node.Type, "view", viewEntry.Status, viewEntry.Notes, node.Location));
            RecordCompatibility(node.Type, viewEntry, node.Location);

            foreach (var modifier in node.Modifiers)
            {
                var modifierEntry = _catalog.GetModifier(modifier.Name);
                usages.Add(new ApiUsage($".{modifier.Name}", "modifier", modifierEntry.Status, modifierEntry.Notes, modifier.Location));
                RecordCompatibility($".{modifier.Name}", modifierEntry, modifier.Location);
            }
        }

        foreach (var (symbol, entry) in _catalog.FindSourceCapabilities(identifiers))
        {
            usages.Add(new ApiUsage(symbol, "framework", entry.Status, entry.Notes, null));
            RecordCompatibility(symbol, entry, null);
        }

        var score = usages.Count == 0
            ? 100
            : Math.Round(usages.Average(usage => usage.Status switch
            {
                CompatibilityStatus.Supported => 100,
                CompatibilityStatus.Partial => 65,
                CompatibilityStatus.RemoteOnly => 20,
                _ => 0
            }), 1, MidpointRounding.AwayFromZero);

        return (new CompatibilityReport
        {
            LocalCompatibilityPercent = score,
            ApiUsages = usages,
            RequiredRemoteCapabilities = remote.ToList(),
            UnsupportedSymbols = unsupported.ToList()
        }, diagnostics);

        void RecordCompatibility(string symbol, CompatibilityEntry entry, SourceLocation? location)
        {
            if (!string.IsNullOrWhiteSpace(entry.RemoteCapability))
            {
                remote.Add(entry.RemoteCapability);
            }

            if (entry.Status == CompatibilityStatus.Unsupported)
            {
                unsupported.Add(symbol);
                diagnostics.Add(new OrchardDiagnostic(
                    "ORC2001",
                    DiagnosticSeverity.Warning,
                    $"'{symbol}' is not supported by the local Orchard runtime.",
                    location,
                    "Use 'orchard compatibility' for details and validate this path on Apple hardware."));
            }
            else if (entry.Status == CompatibilityStatus.RemoteOnly)
            {
                diagnostics.Add(new OrchardDiagnostic(
                    "ORC2002",
                    DiagnosticSeverity.Warning,
                    $"'{symbol}' requires Apple validation: {entry.Notes}",
                    location));
            }
        }
    }

    private static IEnumerable<ViewNode> Traverse(ViewNode root)
    {
        var stack = new Stack<ViewNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var index = current.Children.Count - 1; index >= 0; index--)
            {
                stack.Push(current.Children[index]);
            }
        }
    }

    private static string ResolveManifestPath(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        var manifestPath = Path.Combine(fullPath, "orchard.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("No orchard.json manifest was found.", manifestPath);
        }

        return manifestPath;
    }

    private static bool IsWithinDirectory(string directory, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(candidate));
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static void RejectReparsePoints(string projectDirectory, string entryPoint)
    {
        var relative = Path.GetRelativePath(projectDirectory, entryPoint);
        var current = Path.GetFullPath(projectDirectory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Entry point '{relative}' crosses reparse point '{segment}', which is not permitted.");
                }
            }
        }
    }

    private static DateTimeOffset ResolveBuildTimestamp()
    {
        var sourceDateEpoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        return long.TryParse(sourceDateEpoch, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;
    }
}
