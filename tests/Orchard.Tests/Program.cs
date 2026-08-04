using Orchard.Compiler;
using Orchard.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orchard.Tests;

internal static class Program
{
    private static readonly ProjectManifest Manifest = new()
    {
        ApplicationId = "dev.orchard.tests",
        DisplayName = "Orchard Tests"
    };

    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Compiles a basic SwiftUI view", CompilesBasicView),
            ("Captures state declarations", CapturesState),
            ("Preserves modifiers and arguments", PreservesModifiers),
            ("Captures button action source", CapturesButtonAction),
            ("Preserves Swift string interpolation", PreservesStringInterpolation),
            ("Reports unsupported views", ReportsUnsupportedView),
            ("Routes hardware frameworks to Apple validation", ReportsRemoteCapability),
            ("Reports a missing body", ReportsMissingBody),
            ("Rejects an unterminated body", RejectsUnterminatedBody),
            ("Rejects an unterminated action", RejectsUnterminatedAction),
            ("Rejects multiple root expressions", RejectsMultipleRootExpressions),
            ("Rejects ambiguous body declarations", RejectsAmbiguousBodies),
            ("Ignores framework names in comments and strings", IgnoresFrameworkFalsePositives),
            ("Round-trips application IR as JSON", RoundTripsJson),
            ("Produces deterministic application IR", ProducesDeterministicIr),
            ("Validates project manifests", ValidatesManifest),
            ("Rejects duplicate IR node identifiers", RejectsDuplicateNodeIds),
            ("Compiles the checked-in sample project", CompilesSampleProject),
            ("Finds built-in device profiles", FindsDeviceProfile),
            ("Parses CLI options in any supported order", ParsesCliOptions),
            ("Protects project inputs from CLI output collisions", ProtectsCliInputs),
            ("Returns failure for compatibility compile errors", CompatibilityFailsOnCompilerError),
            ("Rejects unknown and wrong-case manifest fields", RejectsNonCanonicalManifestJson),
            ("Rejects missing manifest fields and unsafe paths", RejectsInvalidManifestSemantics),
            ("Rejects unknown and wrong-case IR fields", RejectsNonCanonicalIrJson),
            ("Rejects duplicate JSON properties", RejectsDuplicateJsonProperties),
            ("Validates cross-language IR fixtures", ValidatesCrossLanguageIrFixtures),
            ("Matches the versioned capability profile", MatchesCapabilityProfile),
            ("Rejects missing IR schema and numeric enums", RejectsInvalidIrShape),
            ("Rejects null IR collections", RejectsNullIrCollections),
            ("Rejects unsafe IR control strings", RejectsUnsafeIrControls),
            ("Enforces source byte and token budgets", EnforcesSourceBudgets),
            ("Enforces compiler node and depth budgets", EnforcesCompilerTreeBudgets),
            ("Enforces IR node and depth budgets", EnforcesIrTreeBudgets),
            ("Enforces manifest, source, and IR file budgets", EnforcesFileBudgets),
            ("Rejects invalid UTF-8 documents", RejectsInvalidUtf8),
            ("Classifies malformed JSON as a user error", ClassifiesMalformedJson),
            ("Escapes terminal control strings", EscapesTerminalControls),
            ("Rejects entry-point reparse traversal when supported", RejectsEntryPointReparseTraversal)
        };

        var failures = new List<string>();
        var started = DateTimeOffset.UtcNow;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("PASS ");
                Console.ResetColor();
                Console.WriteLine(test.Name);
            }
            catch (Exception exception)
            {
                failures.Add(test.Name);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("FAIL ");
                Console.ResetColor();
                Console.WriteLine(test.Name);
                Console.WriteLine($"     {exception.Message}");
            }
        }

        var elapsed = DateTimeOffset.UtcNow - started;
        Console.WriteLine();
        Console.WriteLine($"Executed {tests.Length} tests in {elapsed.TotalMilliseconds:0} ms; {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void CompilesBasicView()
    {
        var result = Compile("""
            import SwiftUI
            struct ContentView: View {
                var body: some View {
                    VStack {
                        Text("Hello")
                        Button("Go") { print("go") }
                    }
                }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal("VStack", result.Application!.RootView.Type);
        Assert.Equal(2, result.Application.RootView.Children.Count);
        Assert.Equal("Text", result.Application.RootView.Children[0].Type);
        Assert.Equal("Hello", result.Application.RootView.Children[0].Arguments["_0"]);
    }

    private static void CapturesState()
    {
        var result = Compile("""
            struct ContentView: View {
                @State private var name: String = "Ada"
                @State private var enabled = true
                @State private var count = 4
                var body: some View { Text("State") }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(3, result.Application!.State.Count);
        Assert.Equal(StateValueKind.Text, result.Application.State[0].Kind);
        Assert.Equal("Ada", result.Application.State[0].InitialValue);
        Assert.Equal(StateValueKind.Flag, result.Application.State[1].Kind);
        Assert.Equal(StateValueKind.WholeNumber, result.Application.State[2].Kind);
    }

    private static void PreservesModifiers()
    {
        var result = Compile("""
            struct ContentView: View {
                var body: some View {
                    Text("Welcome")
                        .font(.largeTitle)
                        .padding(24)
                        .foregroundStyle(.green)
                }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        var root = result.Application!.RootView;
        Assert.Equal(3, root.Modifiers.Count);
        Assert.Equal(".largeTitle", root.Modifiers[0].Arguments["_0"]);
        Assert.Equal("24", root.Modifiers[1].Arguments["_0"]);
    }

    private static void CapturesButtonAction()
    {
        var result = Compile("""
            struct ContentView: View {
                @State private var name = ""
                var body: some View {
                    Button("Continue") {
                        print("pressed")
                        name = "Grace"
                    }
                }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        var action = Assert.Single(result.Application!.RootView.Events);
        Assert.Contains("print", action.Body);
        Assert.Contains("Grace", action.Body);
    }

    private static void PreservesStringInterpolation()
    {
        var result = Compile("""
            struct ContentView: View {
                @State private var name = "Ada"
                var body: some View { Text("Hello, \(name)") }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal("Hello, \\(name)", result.Application!.RootView.Arguments["_0"]);
    }

    private static void ReportsUnsupportedView()
    {
        var result = Compile("""
            struct ContentView: View {
                var body: some View { MadeUpView("test") }
            }
            """);
        Assert.True(result.Application is not null, "An unsupported view should still produce inspectable IR.");
        Assert.Contains("MadeUpView", result.Application!.Compatibility.UnsupportedSymbols);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC2001");
        Assert.Equal(0D, result.Application.Compatibility.LocalCompatibilityPercent);
    }

    private static void ReportsRemoteCapability()
    {
        var result = Compile("""
            import ARKit
            struct ContentView: View {
                var body: some View { Text("AR preview") }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains("ARKit", result.Application!.Compatibility.RequiredRemoteCapabilities);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC2002");
    }

    private static void ReportsMissingBody()
    {
        var result = Compile("struct EmptyView {}");
        Assert.False(result.Success, "Source without a body must fail.");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC1100");
    }

    private static void RejectsUnterminatedBody()
    {
        var result = Compile("struct ContentView: View { var body: some View { Text(\"x\")");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC1109");
    }

    private static void RejectsUnterminatedAction()
    {
        var result = Compile("struct ContentView: View { var body: some View { Button(\"x\") { print(\"x\")");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC1114");
    }

    private static void RejectsMultipleRootExpressions()
    {
        var result = Compile("""
            struct ContentView: View {
                var body: some View {
                    Text("first")
                    MadeUpView()
                }
            }
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC1110");
    }

    private static void RejectsAmbiguousBodies()
    {
        var result = Compile("""
            struct First: View { var body: some View { Text("first") } }
            struct Second: View { var body: some View { Text("second") } }
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ORC1111");
    }

    private static void IgnoresFrameworkFalsePositives()
    {
        var result = Compile("""
            // ARKit and StoreKit are mentioned only in a comment.
            struct ContentView: View {
                let NotARKitMock = "StoreKit"
                var body: some View { Text("ARKit") }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(0, result.Application!.Compatibility.RequiredRemoteCapabilities.Count);
    }

    private static void RoundTripsJson()
    {
        var result = Compile("""
            struct ContentView: View {
                var body: some View { Text("JSON") }
            }
            """);
        var json = OrchardJson.Serialize(result.Application);
        var restored = OrchardJson.Deserialize<OrchardApplication>(json);
        Assert.Equal(result.Application!.ApplicationId, restored.ApplicationId);
        Assert.Equal("Text", restored.RootView.Type);
        Assert.Equal("JSON", restored.RootView.Arguments["_0"]);
        OrchardApplicationReader.Validate(restored);
    }

    private static void ProducesDeterministicIr()
    {
        const string source = "struct ContentView: View { var body: some View { Text(\"same\") } }";
        var first = Compile(source);
        var second = Compile(source);
        Assert.Equal(OrchardJson.Serialize(first.Application), OrchardJson.Serialize(second.Application));
        Assert.Equal(DateTimeOffset.UnixEpoch, first.Application!.CompiledAtUtc);
    }

    private static void ValidatesManifest()
    {
        var manifest = new ProjectManifest
        {
            ApplicationId = "not a reverse dns id",
            DisplayName = "Invalid",
            DefaultDevice = "typo-device"
        };
        Assert.Throws<InvalidDataException>(manifest.Validate);
    }

    private static void RejectsDuplicateNodeIds()
    {
        var result = Compile("""
            struct ContentView: View {
                var body: some View { VStack { Text("one") } }
            }
            """);
        result.Application!.RootView.Children.Add(new ViewNode
        {
            Id = result.Application.RootView.Id,
            Type = "Text",
            Arguments = new Dictionary<string, string> { ["_0"] = "duplicate" }
        });
        Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.Validate(result.Application));
    }

    private static void CompilesSampleProject()
    {
        var root = FindRepositoryRoot();
        var result = new OrchardCompiler().CompileProject(Path.Combine(root, "samples", "HelloOrchard"));
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal("NavigationStack", result.Application!.RootView.Type);
        Assert.True(result.Application.Compatibility.LocalCompatibilityPercent >= 60,
            "The sample should remain predominantly locally compatible.");
    }

    private static void FindsDeviceProfile()
    {
        var profile = DeviceProfile.Find("iphone-se-3");
        Assert.Equal(375, profile.LogicalWidth);
        Assert.Throws<ArgumentException>(() => DeviceProfile.Find("not-real"));
    }

    private static void ParsesCliOptions()
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, "artifacts", "test-output", $"{Guid.NewGuid():N}.orchard.json");
        var result = RunCli("build", "--output", output, Path.Combine(root, "samples", "HelloOrchard"));
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(output), result.StandardError);
        File.Delete(output);

        var unknown = RunCli("compatibility", "--bogus", Path.Combine(root, "samples", "HelloOrchard"));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("Unknown option", unknown.StandardError);
    }

    private static void ProtectsCliInputs()
    {
        var root = FindRepositoryRoot();
        var sample = Path.Combine(root, "samples", "HelloOrchard");
        var manifestPath = Path.Combine(sample, "orchard.json");
        var original = File.ReadAllText(manifestPath);
        var result = RunCli("build", sample, "--output", manifestPath);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(original, File.ReadAllText(manifestPath));
        Assert.Contains("may not overwrite", result.StandardError);
    }

    private static void CompatibilityFailsOnCompilerError()
    {
        var root = FindRepositoryRoot();
        var testRoot = Path.Combine(root, "artifacts", "test-projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(Path.Combine(testRoot, "orchard.json"), OrchardJson.Serialize(Manifest));
            File.WriteAllText(Path.Combine(testRoot, "ContentView.swift"),
                "struct ContentView: View { var body: some View { Text(\"x\"). } }");
            var result = RunCli("compatibility", testRoot);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ORC1106", result.StandardError);
        }
        finally
        {
            var expectedPrefix = Path.GetFullPath(Path.Combine(root, "artifacts", "test-projects")) +
                                 Path.DirectorySeparatorChar;
            if (Path.GetFullPath(testRoot).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void RejectsNonCanonicalManifestJson()
    {
        WithTemporaryDirectory(directory =>
        {
            var manifestPath = Path.Combine(directory, "orchard.json");
            File.WriteAllText(manifestPath, """
                {
                  "applicationId": "dev.orchard.strict",
                  "displayName": "Strict",
                  "unexpected": true
                }
                """);
            Assert.Throws<InvalidDataException>(() => ProjectManifest.Load(manifestPath));

            File.WriteAllText(manifestPath, """
                {
                  "ApplicationId": "dev.orchard.strict",
                  "displayName": "Strict"
                }
                """);
            Assert.Throws<InvalidDataException>(() => ProjectManifest.Load(manifestPath));
        });
    }

    private static void RejectsInvalidManifestSemantics()
    {
        WithTemporaryDirectory(directory =>
        {
            var manifestPath = Path.Combine(directory, "orchard.json");
            File.WriteAllText(manifestPath, """
                {
                  "displayName": "Missing application ID"
                }
                """);
            Assert.Throws<InvalidDataException>(() => ProjectManifest.Load(manifestPath));
        });

        Assert.Throws<InvalidDataException>(() => new ProjectManifest
        {
            ApplicationId = "dev.orchard.controls",
            DisplayName = "Controls",
            EntryPoint = "spoof\n.swift"
        }.Validate());
    }

    private static void RejectsNonCanonicalIrJson()
    {
        AssertIrRejected(json => json["unexpected"] = true);
        AssertIrRejected(json =>
        {
            var schema = json["schemaVersion"]?.DeepClone();
            json.Remove("schemaVersion");
            json["SchemaVersion"] = schema;
        });
    }

    private static void RejectsDuplicateJsonProperties()
    {
        Assert.Throws<JsonException>(() => OrchardJson.Deserialize<ProjectManifest>("""
            {
              "applicationId": "dev.orchard.first",
              "applicationId": "dev.orchard.second",
              "displayName": "Duplicate"
            }
            """));

        var result = Compile("""
            struct ContentView: View {
                var body: some View {
                    Text("Duplicate")
                }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        var json = OrchardJson.Serialize(result.Application!);
        var duplicateSchema = json.Replace(
            "\"schemaVersion\": \"0.1.0\",",
            "\"schemaVersion\": \"0.1.0\",\n  \"schemaVersion\": \"0.1.0\",",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => OrchardJson.Deserialize<OrchardApplication>(duplicateSchema));
    }

    private static void ValidatesCrossLanguageIrFixtures()
    {
        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "ir-v0.1");
        var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "contract.json")))?.AsObject()
            ?? throw new InvalidDataException("The IR fixture contract is missing.");
        Assert.Equal(OrchardSchema.CurrentVersion, contract["contractVersion"]?.GetValue<string>());

        var accepted = contract["accepted"]?.AsArray()
            ?? throw new InvalidDataException("The IR fixture contract has no accepted list.");
        foreach (var relativePath in accepted.Select(node => node?.GetValue<string>()))
        {
            Assert.True(!string.IsNullOrWhiteSpace(relativePath), "An accepted fixture path was empty.");
            var application = OrchardApplicationReader.ReadFile(Path.Combine(fixtureRoot, relativePath!));
            Assert.Equal(OrchardSchema.CurrentVersion, application.SchemaVersion);
        }

        var canonicalPath = contract["canonicalTransport"]?.GetValue<string>()
            ?? throw new InvalidDataException("The IR fixture contract has no canonical transport fixture.");
        OrchardApplicationReader.ReadFile(Path.Combine(fixtureRoot, canonicalPath));

        var rejected = contract["rejected"]?.AsArray()
            ?? throw new InvalidDataException("The IR fixture contract has no rejected list.");
        foreach (var item in rejected)
        {
            var relativePath = item?["path"]?.GetValue<string>()
                ?? throw new InvalidDataException("A rejected fixture has no path.");
            try
            {
                OrchardApplicationReader.ReadFile(Path.Combine(fixtureRoot, relativePath));
            }
            catch (InvalidDataException)
            {
                continue;
            }

            throw new InvalidOperationException($"Rejected fixture '{relativePath}' was accepted.");
        }
    }

    private static void MatchesCapabilityProfile()
    {
        var profilePath = Path.Combine(FindRepositoryRoot(), "schemas", "orchard-capabilities-v1.json");
        var profile = JsonNode.Parse(File.ReadAllText(profilePath))?.AsObject()
            ?? throw new InvalidDataException("The capability profile is missing.");
        Assert.Equal(CompatibilityCatalog.ProfileVersion, profile["profileVersion"]?.GetValue<string>());
        Assert.Equal(OrchardSchema.CurrentVersion, profile["irSchemaVersion"]?.GetValue<string>());

        var weights = profile["statusWeights"]?.AsObject()
            ?? throw new InvalidDataException("The capability profile has no status weights.");
        foreach (var status in Enum.GetValues<CompatibilityStatus>())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
            Assert.Equal(CompatibilityCatalog.GetScoreWeight(status), weights[name]?.GetValue<double>());
        }

        var capabilities = profile["capabilities"]?.AsObject()
            ?? throw new InvalidDataException("The capability profile has no capabilities.");
        var catalog = new CompatibilityCatalog();
        AssertCapabilityCategory(capabilities["views"]?.AsObject(), catalog.Views);
        AssertCapabilityCategory(capabilities["modifiers"]?.AsObject(), catalog.Modifiers);
        AssertCapabilityCategory(capabilities["sourceSymbols"]?.AsObject(), catalog.SourceSymbols);
        Assert.Equal(CompatibilityStatus.Unsupported, catalog.GetSourceSymbol("UnknownCapability").Status);
    }

    private static void AssertCapabilityCategory(
        JsonObject? profile,
        IReadOnlyDictionary<string, CompatibilityEntry> catalog)
    {
        if (profile is null)
        {
            throw new InvalidDataException("A capability profile category is missing.");
        }

        Assert.Equal(catalog.Count, profile.Count);
        foreach (var pair in catalog)
        {
            var entry = profile[pair.Key]?.AsObject()
                ?? throw new InvalidDataException($"Capability '{pair.Key}' is missing from the profile.");
            var expectedStatus = JsonNamingPolicy.CamelCase.ConvertName(pair.Value.Status.ToString());
            Assert.Equal(expectedStatus, entry["status"]?.GetValue<string>());
            Assert.Equal(pair.Value.Notes, entry["notes"]?.GetValue<string>());
            Assert.Equal(pair.Value.RemoteCapability, entry["remoteCapability"]?.GetValue<string>());
        }
    }

    private static void RejectsInvalidIrShape()
    {
        AssertIrRejected(json => json.Remove("schemaVersion"));
        AssertIrRejected(json =>
        {
            var state = json["state"]?.AsArray()
                ?? throw new InvalidOperationException("Expected state array.");
            state[0]!["kind"] = 0;
        });
    }

    private static void RejectsNullIrCollections()
    {
        AssertIrRejected(json => json["state"] = null);
        AssertIrRejected(json => json["rootView"]!["children"] = null);
        AssertIrRejected(json => json["compatibility"]!["apiUsages"] = null);
    }

    private static void RejectsUnsafeIrControls()
    {
        AssertIrRejected(json => json["displayName"] = "unsafe\u001B[31m");
        AssertIrRejected(json => json["displayName"] = "right-to-left\u202Espoof");
        AssertIrRejected(json => json["sourceFile"] = "line\nbreak.swift");

        var emojiManifest = new ProjectManifest
        {
            ApplicationId = "dev.orchard.emoji",
            DisplayName = "Orchard 🌳"
        };
        var emojiResult = new OrchardCompiler().Compile(
            "struct ContentView: View { var body: some View { Text(\"Hello 🌳\") } }",
            "Emoji.swift",
            emojiManifest);
        Assert.True(emojiResult.Success, FormatDiagnostics(emojiResult));
        OrchardApplicationReader.Validate(emojiResult.Application!);
    }

    private static void EnforcesSourceBudgets()
    {
        var oversized = new string('é', OrchardCompiler.MaximumSourceBytes / 2 + 1);
        var byteResult = Compile(oversized);
        Assert.False(byteResult.Success);
        Assert.Contains(byteResult.Diagnostics, diagnostic => diagnostic.Code == "ORC1003");

        var tokens = new StringBuilder(OrchardCompiler.MaximumTokens * 2 + 100);
        tokens.Append("struct ContentView: View { var body: some View { VStack { ");
        for (var index = 0; index <= OrchardCompiler.MaximumTokens; index++)
        {
            tokens.Append("x ");
        }

        tokens.Append("} } }");
        var tokenResult = Compile(tokens.ToString());
        Assert.False(tokenResult.Success);
        Assert.Contains(tokenResult.Diagnostics, diagnostic => diagnostic.Code == "ORC1004");
    }

    private static void EnforcesCompilerTreeBudgets()
    {
        var nodes = new StringBuilder();
        nodes.Append("struct ContentView: View { var body: some View { VStack { ");
        for (var index = 0; index < OrchardCompiler.MaximumViewNodes; index++)
        {
            nodes.Append("Text() ");
        }

        nodes.Append("} } }");
        var nodeResult = Compile(nodes.ToString());
        Assert.False(nodeResult.Success);
        Assert.Contains(nodeResult.Diagnostics, diagnostic => diagnostic.Code == "ORC1112");

        var depth = new StringBuilder("struct ContentView: View { var body: some View { ");
        for (var index = 0; index <= OrchardCompiler.MaximumViewDepth; index++)
        {
            depth.Append("VStack { ");
        }

        depth.Append("Text() ");
        for (var index = 0; index <= OrchardCompiler.MaximumViewDepth; index++)
        {
            depth.Append("} ");
        }

        depth.Append("} }");
        var depthResult = Compile(depth.ToString());
        Assert.False(depthResult.Success);
        Assert.Contains(depthResult.Diagnostics, diagnostic => diagnostic.Code == "ORC1113");
    }

    private static void EnforcesIrTreeBudgets()
    {
        var nodeApplication = CompileValidApplication();
        nodeApplication.RootView.Children.Clear();
        for (var index = 0; index < OrchardApplicationReader.MaximumNodes; index++)
        {
            nodeApplication.RootView.Children.Add(NewViewNode($"budget-{index}", "Text"));
        }

        Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.Validate(nodeApplication));

        var depthApplication = CompileValidApplication();
        var current = depthApplication.RootView;
        current.Children.Clear();
        for (var index = 1; index <= OrchardApplicationReader.MaximumDepth; index++)
        {
            var child = NewViewNode($"depth-{index}", "VStack");
            current.Children.Add(child);
            current = child;
        }

        Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.Validate(depthApplication));
    }

    private static void EnforcesFileBudgets()
    {
        WithTemporaryDirectory(directory =>
        {
            var manifestPath = Path.Combine(directory, "oversized-manifest.json");
            SetFileLength(manifestPath, ProjectManifest.MaximumManifestBytes + 1L);
            Assert.Throws<InvalidDataException>(() => ProjectManifest.Load(manifestPath));

            var applicationPath = Path.Combine(directory, "oversized.orchard.json");
            SetFileLength(applicationPath, OrchardApplicationReader.MaximumDocumentBytes + 1L);
            Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.ReadFile(applicationPath));

            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "orchard.json"), OrchardJson.Serialize(Manifest));
            SetFileLength(
                Path.Combine(projectDirectory, Manifest.EntryPoint),
                OrchardCompiler.MaximumSourceBytes + 1L);
            Assert.Throws<InvalidDataException>(() => new OrchardCompiler().CompileProject(projectDirectory));
        });
    }

    private static void RejectsInvalidUtf8()
    {
        WithTemporaryDirectory(directory =>
        {
            var manifestPath = Path.Combine(directory, "orchard.json");
            File.WriteAllBytes(manifestPath, [0x7B, 0x22, 0xFF, 0x22, 0x7D]);
            Assert.Throws<InvalidDataException>(() => ProjectManifest.Load(manifestPath));

            var applicationPath = Path.Combine(directory, "invalid.orchard.json");
            File.WriteAllBytes(applicationPath, [0x7B, 0x22, 0xFF, 0x22, 0x7D]);
            Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.ReadFile(applicationPath));
        });
    }

    private static void ClassifiesMalformedJson()
    {
        WithTemporaryDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "orchard.json"), "{");
            var result = RunCli("build", directory);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ORC0001", result.StandardError);
            Assert.DoesNotContain("ORC0002", result.StandardError);
        });
    }

    private static void EscapesTerminalControls()
    {
        var result = RunCli("owned\u001B[31m\u0007");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("\\u001B", result.StandardError);
        Assert.Contains("\\u0007", result.StandardError);
        Assert.DoesNotContain("\u001B", result.StandardError);
        Assert.DoesNotContain("\u0007", result.StandardError);
    }

    private static void RejectsEntryPointReparseTraversal()
    {
        WithTemporaryDirectory(directory =>
        {
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(projectDirectory);
            var externalSource = Path.Combine(directory, "external.swift");
            File.WriteAllText(
                externalSource,
                "struct ContentView: View { var body: some View { Text(\"outside\") } }");
            var linkedSource = Path.Combine(projectDirectory, "ContentView.swift");
            try
            {
                File.CreateSymbolicLink(linkedSource, externalSource);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                              PlatformNotSupportedException or NotSupportedException)
            {
                Console.WriteLine($"     Reparse test skipped: {exception.GetType().Name}");
                return;
            }

            File.WriteAllText(Path.Combine(projectDirectory, "orchard.json"), OrchardJson.Serialize(Manifest));
            Assert.Throws<InvalidDataException>(() => new OrchardCompiler().CompileProject(projectDirectory));
        });
    }

    private static OrchardApplication CompileValidApplication()
    {
        var result = Compile("""
            struct ContentView: View {
                @State private var name = "Ada"
                var body: some View { VStack { Text("Valid") } }
            }
            """);
        Assert.True(result.Success, FormatDiagnostics(result));
        return result.Application!;
    }

    private static JsonObject ValidApplicationJson() =>
        JsonNode.Parse(OrchardJson.Serialize(CompileValidApplication()))?.AsObject()
        ?? throw new InvalidOperationException("Could not construct valid application JSON.");

    private static void AssertIrRejected(Action<JsonObject> mutation)
    {
        WithTemporaryDirectory(directory =>
        {
            var json = ValidApplicationJson();
            mutation(json);
            var path = Path.Combine(directory, "application.orchard.json");
            File.WriteAllText(path, json.ToJsonString(OrchardJson.Options));
            Assert.Throws<InvalidDataException>(() => OrchardApplicationReader.ReadFile(path));
        });
    }

    private static ViewNode NewViewNode(string id, string type) => new()
    {
        Id = id,
        Type = type
    };

    private static void SetFileLength(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var root = FindRepositoryRoot();
        var parent = Path.GetFullPath(Path.Combine(root, "artifacts", "security-tests"));
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            var expectedPrefix = parent + Path.DirectorySeparatorChar;
            var fullDirectory = Path.GetFullPath(directory);
            if (fullDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullDirectory))
            {
                Directory.Delete(fullDirectory, recursive: true);
            }
        }
    }

    private static CompilationResult Compile(string source) =>
        new OrchardCompiler().Compile(source, "Test.swift", Manifest);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orchard.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the Orchard repository root.");
    }

    private static CliResult RunCli(params string[] arguments)
    {
        var root = FindRepositoryRoot();
        var configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var cli = Path.Combine(root, "artifacts", "bin", "Orchard.Cli", configuration, "orchard.dll");
        if (!File.Exists(cli))
        {
            throw new FileNotFoundException("Build Orchard.Cli before running process-level tests.", cli);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cli);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Orchard.Cli.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Orchard.Cli process test timed out.");
        }

        Task.WaitAll(standardOutput, standardError);
        return new CliResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}

internal static class Assert
{
    public static void True(bool condition, string message = "Expected true.")
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message = "Expected false.") => True(!condition, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
        }
    }

    public static T Single<T>(IReadOnlyList<T> items)
    {
        if (items.Count != 1)
        {
            throw new InvalidOperationException($"Expected one item, received {items.Count}.");
        }

        return items[0];
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static void DoesNotContain(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' not to contain '{unexpected}'.");
        }
    }

    public static void Contains<T>(T expected, IEnumerable<T> items)
    {
        if (!items.Contains(expected))
        {
            throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
        }
    }

    public static void Contains<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (!items.Any(predicate))
        {
            throw new InvalidOperationException("Expected collection to contain a matching item.");
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
