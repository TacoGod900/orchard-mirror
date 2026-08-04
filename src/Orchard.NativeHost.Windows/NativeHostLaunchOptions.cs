using Orchard.Protocol;

namespace Orchard.NativeHost.Windows;

/// <summary>Validated launch, protocol, and process-resource limits for a native Swift child.</summary>
public sealed record NativeHostLaunchOptions
{
    private const long MinimumMemoryLimitBytes = 32L * 1024 * 1024;
    private const long MaximumMemoryLimitBytes = 256L * 1024 * 1024 * 1024;

    public required string ExecutablePath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Trusted absolute directories containing runtime DLLs required by the child.</summary>
    public IReadOnlyList<string> RuntimeSearchPaths { get; init; } = [];

    /// <summary>
    /// Variables deliberately added to Orchard's minimal child environment. The parent environment
    /// is never copied, and base variables controlled by the launcher cannot be overridden.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The isolation boundary required for this launch.</summary>
    public NativeHostSandboxPolicy SandboxPolicy { get; init; } = NativeHostSandboxPolicy.DevelopmentJobObject;

    /// <summary>Maximum number of simultaneously active processes in the owned Job Object.</summary>
    public int ActiveProcessLimit { get; init; } = 1;

    /// <summary>Per-process committed-memory ceiling applied by the Job Object.</summary>
    public long ProcessMemoryLimitBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Aggregate committed-memory ceiling for every process in the Job Object.</summary>
    public long JobMemoryLimitBytes { get; init; } = 768L * 1024 * 1024;

    public string? WorkingDirectory { get; init; }

    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaximumCapturedOutputBytes { get; init; } = 64 * 1024;

    public ConfigurePayload Configuration { get; init; } = new()
    {
        DeviceProfileId = "iphone-15-pro",
        LogicalWidth = 393,
        LogicalHeight = 852,
        DisplayScale = 3,
        Appearance = SimulatorAppearance.Light,
        Locale = "en-AU",
        AccessibilityEnabled = false
    };

    internal ValidatedNativeHostLaunchOptions Validate()
    {
        if (!Enum.IsDefined(SandboxPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(SandboxPolicy), "The sandbox policy is not recognized.");
        }
        if (SandboxPolicy == NativeHostSandboxPolicy.RequireProductionAppContainer)
        {
            throw new NativeSandboxUnavailableException();
        }

        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            throw new ArgumentException("A native child executable is required.", nameof(ExecutablePath));
        }

        var fullExecutablePath = Path.GetFullPath(ExecutablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException("The native child executable does not exist.", fullExecutablePath);
        }

        var workingDirectory = WorkingDirectory is null
            ? Path.GetDirectoryName(fullExecutablePath)!
            : Path.GetFullPath(WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("The native child working directory does not exist.");
        }

        ValidateTimeout(ConnectionTimeout, nameof(ConnectionTimeout));
        ValidateTimeout(HandshakeTimeout, nameof(HandshakeTimeout));
        ValidateTimeout(OperationTimeout, nameof(OperationTimeout));
        ValidateTimeout(ShutdownTimeout, nameof(ShutdownTimeout));
        if (MaximumCapturedOutputBytes is < 1 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumCapturedOutputBytes),
                "Captured output must be bounded between 1 byte and 1 MiB.");
        }

        if (ActiveProcessLimit is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ActiveProcessLimit),
                "The active-process limit must be between 1 and 64.");
        }
        ValidateMemoryLimit(ProcessMemoryLimitBytes, nameof(ProcessMemoryLimitBytes));
        ValidateMemoryLimit(JobMemoryLimitBytes, nameof(JobMemoryLimitBytes));
        if (JobMemoryLimitBytes < ProcessMemoryLimitBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JobMemoryLimitBytes),
                "The aggregate job-memory limit cannot be smaller than the per-process limit.");
        }

        var arguments = Arguments?.ToArray() ?? throw new ArgumentNullException(nameof(Arguments));
        if (arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException("Native child arguments cannot be null or contain NUL.", nameof(Arguments));
        }

        var configuredRuntimeSearchPaths = RuntimeSearchPaths?.ToArray()
            ?? throw new ArgumentNullException(nameof(RuntimeSearchPaths));
        if (configuredRuntimeSearchPaths.Any(path => !Path.IsPathFullyQualified(path)))
        {
            throw new DirectoryNotFoundException("Every native runtime search path must be an existing absolute directory.");
        }
        var runtimeSearchPaths = configuredRuntimeSearchPaths.Select(Path.GetFullPath).ToArray();
        if (runtimeSearchPaths.Any(path => !Directory.Exists(path)))
        {
            throw new DirectoryNotFoundException("Every native runtime search path must be an existing absolute directory.");
        }


        var environmentVariables = ValidateEnvironment(EnvironmentVariables);

        ArgumentNullException.ThrowIfNull(Configuration);
        ProtocolValidator.Validate(ProtocolEnvelope.Create("options-validation", 0, Configuration));
        return new ValidatedNativeHostLaunchOptions(
            fullExecutablePath,
            arguments,
            runtimeSearchPaths,
            environmentVariables,
            workingDirectory,
            ConnectionTimeout,
            HandshakeTimeout,
            OperationTimeout,
            ShutdownTimeout,
            MaximumCapturedOutputBytes,
            SandboxPolicy,
            ActiveProcessLimit,
            ProcessMemoryLimitBytes,
            JobMemoryLimitBytes,
            Configuration);
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(name, "The timeout must be finite, positive, and at most five minutes.");
        }
    }

    private static void ValidateMemoryLimit(long bytes, string name)
    {
        if (bytes is < MinimumMemoryLimitBytes or > MaximumMemoryLimitBytes)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"The memory limit must be between {MinimumMemoryLimitBytes} and {MaximumMemoryLimitBytes} bytes.");
        }
    }

    private static IReadOnlyDictionary<string, string> ValidateEnvironment(
        IReadOnlyDictionary<string, string>? configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (configured.Count > 128)
        {
            throw new ArgumentException("At most 128 explicit child environment variables are permitted.",
                nameof(EnvironmentVariables));
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in configured)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0'))
            {
                throw new ArgumentException(
                    "Child environment names cannot be empty or contain '=' or NUL.",
                    nameof(EnvironmentVariables));
            }
            if (pair.Value is null || pair.Value.Contains('\0'))
            {
                throw new ArgumentException(
                    "Child environment values cannot be null or contain NUL.",
                    nameof(EnvironmentVariables));
            }
            if (WindowsNativeProcessLauncher.IsLauncherControlledEnvironmentVariable(pair.Key))
            {
                throw new ArgumentException(
                    $"The child environment variable '{pair.Key}' is controlled by the launcher.",
                    nameof(EnvironmentVariables));
            }
            if (!result.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Child environment names must be unique ignoring case.",
                    nameof(EnvironmentVariables));
            }
        }

        return result;
    }
}

internal sealed record ValidatedNativeHostLaunchOptions(
    string ExecutablePath,
    string[] Arguments,
    string[] RuntimeSearchPaths,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string WorkingDirectory,
    TimeSpan ConnectionTimeout,
    TimeSpan HandshakeTimeout,
    TimeSpan OperationTimeout,
    TimeSpan ShutdownTimeout,
    int MaximumCapturedOutputBytes,
    NativeHostSandboxPolicy SandboxPolicy,
    int ActiveProcessLimit,
    long ProcessMemoryLimitBytes,
    long JobMemoryLimitBytes,
    ConfigurePayload Configuration);
