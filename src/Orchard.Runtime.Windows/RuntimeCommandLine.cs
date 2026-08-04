namespace Orchard.Runtime.Windows;

public abstract record RuntimeLaunchRequest;

public sealed record WelcomeLaunchRequest : RuntimeLaunchRequest;

public sealed record StaticIrLaunchRequest(string IrPath) : RuntimeLaunchRequest;

public sealed record NativeLaunchRequest(
    string ExecutablePath,
    IReadOnlyList<string> RuntimeSearchPaths) : RuntimeLaunchRequest;

/// <summary>Strict command-line parsing for the standalone Windows runtime.</summary>
public static class RuntimeCommandLine
{
    private const int MaximumRuntimeSearchPaths = 32;

    public static RuntimeLaunchRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return new WelcomeLaunchRequest();
        }

        if (!string.Equals(arguments[0], "--native", StringComparison.Ordinal))
        {
            if (arguments.Count != 1)
            {
                throw Usage("Static IR mode accepts exactly one Orchard IR file path.");
            }

            return new StaticIrLaunchRequest(ValidateFile(arguments[0], "Orchard IR", requireAbsolute: false));
        }

        if (arguments.Count < 2)
        {
            throw Usage("--native requires an absolute native child executable path.");
        }

        var executable = ValidateFile(arguments[1], "Native child executable", requireAbsolute: true);
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw Usage("The native child executable must have an .exe extension.");
        }

        var runtimePaths = new List<string>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--runtime-path", StringComparison.Ordinal))
            {
                throw Usage($"Unknown native-mode argument '{SafeArgument(arguments[index])}'.");
            }

            if (++index >= arguments.Count)
            {
                throw Usage("--runtime-path requires an absolute existing directory.");
            }

            if (runtimePaths.Count >= MaximumRuntimeSearchPaths)
            {
                throw Usage($"At most {MaximumRuntimeSearchPaths} runtime search paths are permitted.");
            }

            var runtimePath = ValidateDirectory(arguments[index], "Runtime search path");
            if (!uniquePaths.Add(runtimePath))
            {
                throw Usage("Runtime search paths cannot be repeated.");
            }

            runtimePaths.Add(runtimePath);
        }

        return new NativeLaunchRequest(executable, runtimePaths.AsReadOnly());
    }

    public static string UsageText =>
        "Usage: Orchard.Runtime.Windows [<application.orchard.json> | " +
        "--native <absolute-child.exe> [--runtime-path <absolute-directory>] ...]";

    private static string ValidateFile(string? candidate, string description, bool requireAbsolute)
    {
        var path = ValidatePathText(candidate, description);
        if (requireAbsolute && !Path.IsPathFullyQualified(path))
        {
            throw Usage($"{description} path must be absolute.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Usage($"{description} path is invalid.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{description} does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string ValidateDirectory(string? candidate, string description)
    {
        var path = ValidatePathText(candidate, description);
        if (!Path.IsPathFullyQualified(path))
        {
            throw Usage($"{description} must be absolute.");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Usage($"{description} is invalid.");
        }

        if (fullPath.Contains(Path.PathSeparator, StringComparison.Ordinal))
        {
            throw Usage($"{description} cannot contain the PATH list separator.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{description} does not exist.");
        }

        return fullPath;
    }

    private static string ValidatePathText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Usage($"{description} path is required.");
        }

        if (value.Length > 32_767 || value.Any(character => character == '\0' || char.IsControl(character)))
        {
            throw Usage($"{description} path contains invalid characters.");
        }

        return value;
    }

    private static ArgumentException Usage(string message) =>
        new($"{message}{Environment.NewLine}{UsageText}");

    private static string SafeArgument(string? value) =>
        LiveUiText.SanitizeControlText(value ?? string.Empty, 96);
}
