using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Orchard.Compiler;
using Orchard.Core;
using Orchard.Runtime.Windows;

namespace Orchard.Cli;

internal static class Program
{
    private const string Version = "0.1.0-dev";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Dispatch(args);
        }
        catch (CommandLineException exception)
        {
            WriteError("ORC0004", exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            WriteError("ORC0001", exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            WriteError("ORC0002", $"Unexpected failure: {exception.Message}");
            if (Environment.GetEnvironmentVariable("ORCHARD_TRACE") == "1")
            {
                Console.Error.WriteLine(EscapeTerminal(exception.ToString()));
            }

            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "version" or "--version" => PrintVersion(),
            "doctor" => Doctor(),
            "devices" => ListDevices(),
            "init" or "new" => Initialize(args.Skip(1).ToArray()),
            "build" => Build(args.Skip(1).ToArray()),
            "run" => Run(args.Skip(1).ToArray()),
            "compatibility" => Compatibility(args.Skip(1).ToArray()),
            "inspect" => Inspect(args.Skip(1).ToArray()),
            _ => UnknownCommand(args[0])
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"Orchard CLI {Version}");
        return 0;
    }

    private static int Doctor()
    {
        Console.WriteLine($"Project Orchard doctor {Version}");
        Console.WriteLine();
        var checks = new List<DoctorCheck>
        {
            new("Operating system", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                RuntimeInformation.OSDescription,
                "Windows 11 build 22000 or later is required for supported use."),
            new("Architecture", RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64,
                RuntimeInformation.ProcessArchitecture.ToString(),
                "Use an x64 or ARM64 Orchard distribution."),
            new(".NET runtime", Environment.Version.Major >= 10,
                Environment.Version.ToString(),
                "Install the pinned .NET SDK from global.json."),
            CheckExecutable("dotnet", "Install the pinned .NET SDK from global.json."),
            CheckExecutable("swift", "Install the pinned open-source Swift for Windows toolchain before the native Swift milestone."),
            CheckExecutable("git", "Install Git for project and package operations.")
        };

        foreach (var check in checks)
        {
            var marker = check.Passed ? "PASS" : "WARN";
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = check.Passed ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write($"[{marker}] ");
            Console.ForegroundColor = previous;
            Console.WriteLine($"{EscapeTerminal(check.Name)}: {EscapeTerminal(check.Detail)}");
            if (!check.Passed)
            {
                Console.WriteLine($"       {EscapeTerminal(check.Remediation)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("The current bootstrap compiler/runtime can build without Swift. The production native-Swift lane cannot.");
        return checks.Take(4).All(check => check.Passed) ? 0 : 1;
    }

    private static int ListDevices()
    {
        foreach (var device in DeviceProfile.BuiltIn)
        {
            Console.WriteLine($"{device.Id,-20} {device.DisplayName,-32} {device.LogicalWidth}×{device.LogicalHeight} @{device.Scale}x");
        }

        return 0;
    }

    private static int Initialize(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args, [], ["--force"]);
        var target = Path.GetFullPath(parsed.ProjectPath);
        var force = parsed.HasFlag("--force");
        Directory.CreateDirectory(target);
        var manifestPath = Path.Combine(target, "orchard.json");
        var sourcePath = Path.Combine(target, "ContentView.swift");
        if (!force && (File.Exists(manifestPath) || File.Exists(sourcePath)))
        {
            throw new IOException("The target already contains an Orchard project. Pass --force to replace the generated files.");
        }

        var directoryName = new DirectoryInfo(target).Name;
        var safeName = string.IsNullOrWhiteSpace(directoryName) ? "OrchardApp" : directoryName;
        var manifest = new ProjectManifest
        {
            ApplicationId = $"dev.orchard.{Slugify(safeName)}",
            DisplayName = safeName,
            EntryPoint = "ContentView.swift"
        };
        manifest.Validate();
        WriteAtomically(manifestPath, OrchardJson.Serialize(manifest));
        WriteAtomically(sourcePath, """
            import SwiftUI

            struct ContentView: View {
                @State private var name = ""

                var body: some View {
                    VStack(spacing: 16) {
                        Text("Welcome to Orchard")
                            .font(.largeTitle)

                        TextField("Name", text: $name)

                        Button("Continue") {
                            print("Continue pressed")
                        }
                    }
                    .padding(24)
                }
            }
            """);
        Console.WriteLine($"Created Orchard project at {EscapeTerminal(target)}");
        Console.WriteLine($"Run: orchard run \"{EscapeTerminal(target)}\"");
        return 0;
    }

    private static int Build(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args, [("--output", "output"), ("-o", "output")], []);
        var projectPath = parsed.ProjectPath;
        var output = parsed.GetOption("output");
        var context = ResolveProjectContext(projectPath);
        var result = Compile(projectPath);
        PrintDiagnostics(result.Diagnostics);
        if (!result.Success || result.Application is null)
        {
            return 1;
        }

        var outputPath = Path.GetFullPath(output ?? Path.Combine(context.ProjectDirectory, "artifacts", "app.orchard.json"));
        if (outputPath.Equals(context.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
            outputPath.Equals(context.EntryPointPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Build output may not overwrite orchard.json or the Swift entry point.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        WriteAtomically(outputPath, OrchardJson.Serialize(result.Application));
        Console.WriteLine($"Built {EscapeTerminal(result.Application.DisplayName)}");
        Console.WriteLine($"  Output: {EscapeTerminal(outputPath)}");
        Console.WriteLine($"  Local compatibility: {result.Application.Compatibility.LocalCompatibilityPercent:0.#}%");
        return 0;
    }

    private static int Run(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args, [("--device", "device")], ["--allow-unsupported"]);
        var projectPath = parsed.ProjectPath;
        var context = ResolveProjectContext(projectPath);
        var deviceId = parsed.GetOption("device") ?? context.Manifest.DefaultDevice;
        var allowUnsupported = parsed.HasFlag("--allow-unsupported");
        if (!DeviceProfile.TryFind(deviceId, out var device))
        {
            WriteError("ORC0005", $"Unknown device profile '{deviceId}'. Run 'orchard devices'.");
            return 2;
        }

        var result = Compile(projectPath);
        PrintDiagnostics(result.Diagnostics);
        if (!result.Success || result.Application is null)
        {
            return 1;
        }

        if (!allowUnsupported && result.Application.Compatibility.UnsupportedSymbols.Count > 0)
        {
            WriteError(
                "ORC2003",
                "The application uses unsupported local symbols. Pass --allow-unsupported to render diagnostic placeholders.");
            return 2;
        }

        Console.WriteLine($"Launching {EscapeTerminal(result.Application.DisplayName)} on {EscapeTerminal(device.DisplayName)}...");
        SimulatorLauncher.Run(result.Application, device);
        return 0;
    }

    private static int Compatibility(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args, [], ["--json"]);
        var result = Compile(parsed.ProjectPath);
        PrintDiagnostics(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        if (!result.Success || result.Application is null)
        {
            return 1;
        }

        var report = result.Application.Compatibility;
        if (parsed.HasFlag("--json"))
        {
            Console.WriteLine(OrchardJson.Serialize(report));
            return 0;
        }

        Console.WriteLine($"Local compatibility: {report.LocalCompatibilityPercent:0.#}%");
        Console.WriteLine();
        foreach (var usage in report.ApiUsages
                     .OrderBy(usage => usage.Status)
                     .ThenBy(usage => usage.Symbol, StringComparer.Ordinal))
        {
            Console.WriteLine($"{usage.Status,-12} {EscapeTerminal(usage.Category),-10} {EscapeTerminal(usage.Symbol)}");
            if (!string.IsNullOrWhiteSpace(usage.Notes))
            {
                Console.WriteLine($"                         {EscapeTerminal(usage.Notes)}");
            }
        }

        if (report.RequiredRemoteCapabilities.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Requires Apple validation:");
            foreach (var capability in report.RequiredRemoteCapabilities)
            {
                Console.WriteLine($"  • {EscapeTerminal(capability)}");
            }
        }

        if (report.UnsupportedSymbols.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Unsupported locally:");
            foreach (var symbol in report.UnsupportedSymbols)
            {
                Console.WriteLine($"  • {EscapeTerminal(symbol)}");
            }
        }

        return report.UnsupportedSymbols.Count == 0 ? 0 : 2;
    }

    private static int Inspect(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args, [], [], requireProjectPath: true);
        var path = parsed.ProjectPath;
        if (!File.Exists(path))
        {
            WriteError("ORC0003", "Pass a compiled .orchard.json file to inspect.");
            return 2;
        }

        var application = OrchardApplicationReader.ReadFile(path);
        Console.WriteLine($"{EscapeTerminal(application.DisplayName)} ({EscapeTerminal(application.ApplicationId)})");
        Console.WriteLine($"Schema: {application.SchemaVersion}");
        Console.WriteLine($"Compiled: {application.CompiledAtUtc:u}");
        Console.WriteLine($"Compatibility: {application.Compatibility.LocalCompatibilityPercent:0.#}%");
        PrintTree(application.RootView, string.Empty, true);
        return 0;
    }

    private static CompilationResult Compile(string projectPath) =>
        new OrchardCompiler().CompileProject(projectPath);

    private static ParsedCommandArguments ParseArguments(
        IReadOnlyList<string> args,
        IReadOnlyList<(string Alias, string Name)> valueOptions,
        IReadOnlyList<string> flagOptions,
        bool requireProjectPath = false)
    {
        var optionAliases = valueOptions.ToDictionary(option => option.Alias, option => option.Name, StringComparer.OrdinalIgnoreCase);
        var flags = flagOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var presentFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var optionsEnded = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                var equals = argument.IndexOf('=');
                var optionName = equals >= 0 ? argument[..equals] : argument;
                if (optionAliases.TryGetValue(optionName, out var canonicalName))
                {
                    var optionValue = equals >= 0
                        ? argument[(equals + 1)..]
                        : index + 1 < args.Count ? args[++index] : string.Empty;
                    if (string.IsNullOrWhiteSpace(optionValue) || optionValue.StartsWith('-'))
                    {
                        throw new CommandLineException($"Option '{optionName}' requires a value.");
                    }

                    if (!values.TryAdd(canonicalName, optionValue))
                    {
                        throw new CommandLineException($"Option '{optionName}' was provided more than once.");
                    }

                    continue;
                }

                if (flags.Contains(optionName) && equals < 0)
                {
                    if (!presentFlags.Add(optionName))
                    {
                        throw new CommandLineException($"Flag '{optionName}' was provided more than once.");
                    }

                    continue;
                }

                throw new CommandLineException($"Unknown option '{optionName}'. Run 'orchard help'.");
            }

            positionals.Add(argument);
        }

        if (positionals.Count > 1)
        {
            throw new CommandLineException("Only one project or file path may be provided.");
        }

        if (requireProjectPath && positionals.Count == 0)
        {
            throw new CommandLineException("A file path is required.");
        }

        return new ParsedCommandArguments(positionals.FirstOrDefault() ?? ".", values, presentFlags);
    }

    private static ProjectContext ResolveProjectContext(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var manifestPath = File.Exists(fullPath) ? fullPath : Path.Combine(fullPath, "orchard.json");
        var manifest = ProjectManifest.Load(manifestPath);
        var projectDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The project manifest has no parent directory.");
        var entryPointPath = Path.GetFullPath(Path.Combine(projectDirectory, manifest.EntryPoint));
        return new ProjectContext(manifestPath, projectDirectory, entryPointPath, manifest);
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("The output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PrintDiagnostics(IEnumerable<OrchardDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var location = diagnostic.Location is null
                ? string.Empty
                : $"{EscapeTerminal(diagnostic.Location.File)}({diagnostic.Location.Line},{diagnostic.Location.Column}): ";
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => ConsoleColor.Red,
                DiagnosticSeverity.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Cyan
            };
            Console.Error.WriteLine(
                $"{location}{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {EscapeTerminal(diagnostic.Message)}");
            Console.ForegroundColor = previous;
            if (!string.IsNullOrWhiteSpace(diagnostic.Suggestion))
            {
                Console.Error.WriteLine($"  {EscapeTerminal(diagnostic.Suggestion)}");
            }
        }
    }

    private static void PrintTree(ViewNode node, string prefix, bool last)
    {
        Console.WriteLine(
            $"{prefix}{(last ? "└─" : "├─")}{EscapeTerminal(node.Type)} [{EscapeTerminal(node.Id)}]");
        var childPrefix = prefix + (last ? "  " : "│ ");
        for (var index = 0; index < node.Children.Count; index++)
        {
            PrintTree(node.Children[index], childPrefix, index == node.Children.Count - 1);
        }
    }

    private static DoctorCheck CheckExecutable(string name, string remediation)
    {
        var executable = OperatingSystem.IsWindows() ? "where.exe" : "which";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            ArgumentList = { name },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return new DoctorCheck(name, false, "could not start lookup", remediation);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(2_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return new DoctorCheck(name, false, "lookup timed out", remediation);
        }

        Task.WaitAll(outputTask, errorTask);
        var path = outputTask.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new DoctorCheck(name, process.ExitCode == 0, path ?? "not found", remediation);
    }

    private static string Slugify(string value)
    {
        var characters = value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = new string(characters).Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "app";
        }

        return char.IsAsciiLetter(slug[0]) ? slug : $"app-{slug}";
    }

    private static int UnknownCommand(string command)
    {
        WriteError("ORC0004", $"Unknown command '{command}'. Run 'orchard help'.");
        return 2;
    }

    private static void WriteError(string code, string message) =>
        Console.Error.WriteLine($"error {EscapeTerminal(code)}: {EscapeTerminal(message)}");

    private static string EscapeTerminal(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(character);
                builder.Append(value[++index]);
                continue;
            }

            switch (character)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character) ||
                        character is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or
                        >= '\u2066' and <= '\u2069')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Project Orchard — Windows development runtime bootstrap

            Usage:
              orchard doctor
              orchard init [directory] [--force]
              orchard build [project] [--output <file>]
              orchard run [project] [--device <id>] [--allow-unsupported]
              orchard compatibility [project] [--json]
              orchard inspect <compiled.orchard.json>
              orchard devices
              orchard version

            This bootstrap executes an independently implemented SwiftUI source subset.
            It does not contain Apple SDKs, run IPAs, sign apps, or replace final Apple validation.
            """);
    }

    private sealed record DoctorCheck(
        string Name,
        bool Passed,
        string Detail,
        string Remediation);

    private sealed record ParsedCommandArguments(
        string ProjectPath,
        IReadOnlyDictionary<string, string> Options,
        IReadOnlySet<string> Flags)
    {
        public string? GetOption(string name) => Options.GetValueOrDefault(name);

        public bool HasFlag(string name) => Flags.Contains(name);
    }

    private sealed record ProjectContext(
        string ManifestPath,
        string ProjectDirectory,
        string EntryPointPath,
        ProjectManifest Manifest);

    private sealed class CommandLineException(string message) : Exception(message);
}
