namespace Orchard.Mirror.Agent.Windows;

/// <summary>Resolved paths used to launch the CoreDevice agent.</summary>
public sealed record AgentLaunchOptions(string PythonExecutable, string AgentScript)
{
    /// <summary>Resolve development, packaged, and environment-overridden agent paths.</summary>
    public static AgentLaunchOptions Resolve()
    {
        string? pythonOverride = Environment.GetEnvironmentVariable("ORCHARD_MIRROR_PYTHON");
        string? agentOverride = Environment.GetEnvironmentVariable("ORCHARD_MIRROR_AGENT");
        string? repositoryRoot = FindRepositoryRoot();

        string agent = agentOverride
            ?? ExistingOrNull(Path.Combine(AppContext.BaseDirectory, "agent", "agent.py"))
            ?? (repositoryRoot is null ? null : ExistingOrNull(Path.Combine(repositoryRoot, "src", "Orchard.Mirror.Agent", "agent.py")))
            ?? throw new FileNotFoundException(
                "The Orchard Mirror CoreDevice agent was not found. Reinstall Orchard Mirror or set ORCHARD_MIRROR_AGENT.");

        string python = pythonOverride
            ?? ExistingOrNull(Path.Combine(AppContext.BaseDirectory, "python", "python.exe"))
            ?? (repositoryRoot is null ? null : ExistingOrNull(Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe")))
            ?? "python";

        return new AgentLaunchOptions(python, agent);
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orchard.Mirror.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ExistingOrNull(string path) => File.Exists(path) ? path : null;
}
