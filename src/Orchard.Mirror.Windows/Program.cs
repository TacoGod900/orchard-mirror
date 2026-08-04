using System.Diagnostics;

using Orchard.Mirror.Agent.Windows;

namespace Orchard.Mirror.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // There was nothing of this kind before: an unhandled exception showed the raw WinForms
        // crash dialog and took the process down with a live CoreDevice session and a Python agent
        // still attached to the phone. That is the worst moment to skip a teardown, because a media
        // session the phone is never told to end is what leaves its daemon wedged.
        Application.ThreadException += (_, e) => Fail("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail("background thread", e.ExceptionObject as Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AgentProcessRegistry.KillAll();

        ApplicationConfiguration.Initialize();
        Application.Run(new MirrorForm());
    }

    /// <summary>
    /// Record what went wrong somewhere findable, and make sure no agent outlives the crash.
    /// </summary>
    /// <remarks>
    /// Deliberately best-effort and synchronous. Anything that awaits here may never resume, which
    /// is the same mistake the window's own close path used to make.
    /// </remarks>
    private static void Fail(string where, Exception? error)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orchard",
                "Mirror");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.Now:O}  {where}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        AgentProcessRegistry.KillAll();
    }
}
