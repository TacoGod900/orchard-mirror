namespace Orchard.NativeHost.Windows;

/// <summary>Security boundary required for a native application launch.</summary>
public enum NativeHostSandboxPolicy
{
    /// <summary>
    /// Apply the development Job Object and handle/environment isolation boundary. This is not a
    /// production sandbox because the application still has the launching user's ambient token.
    /// </summary>
    DevelopmentJobObject = 0,

    /// <summary>
    /// Require the production AppContainer, broker, filesystem, and network boundary. Launch fails
    /// closed until that complete boundary is available.
    /// </summary>
    RequireProductionAppContainer = 1
}

/// <summary>Stable capability information for Orchard's Windows native-child sandbox.</summary>
public static class NativeHostSandboxCapabilities
{
    public const string ProductionUnavailableCode = "ORS2001";

    public const bool IsProductionAppContainerAvailable = false;

    public const string RemainingProductionGate =
        "A production launch requires an AppContainer profile and restricted token, deny-by-default " +
        "network and filesystem policy, capability-broker handles, per-application data ACLs, and " +
        "sandbox escape qualification. The current Job Object boundary is development-only.";
}

/// <summary>A production sandbox was required, but the complete boundary is unavailable.</summary>
public sealed class NativeSandboxUnavailableException : InvalidOperationException
{
    public NativeSandboxUnavailableException()
        : base($"{NativeHostSandboxCapabilities.ProductionUnavailableCode}: " +
            NativeHostSandboxCapabilities.RemainingProductionGate)
    {
    }

    public string Code => NativeHostSandboxCapabilities.ProductionUnavailableCode;
}

/// <summary>Applied native-child resource and isolation policy.</summary>
public sealed record NativeHostSandboxStatus(
    NativeHostSandboxPolicy Policy,
    bool IsProductionSandboxed,
    bool JobObjectAssignedBeforeExecution,
    bool KillProcessTreeOnClose,
    bool ExplicitHandleAllowlist,
    bool ExplicitEnvironment,
    int ActiveProcessLimit,
    long ProcessMemoryLimitBytes,
    long JobMemoryLimitBytes);
