using System.Globalization;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>
/// An append-only record of what Orchard Mirror did and what the phone said back.
/// </summary>
/// <remarks>
/// <para>Until this existed the app kept no record of anything. The agent's own log lines were read
/// into a sixteen-line ring and then shown only if the agent process exited, so a connection that
/// hung rather than failed — the one that retries forever — discarded every word of explanation it
/// had been given. "Connection timed out", over and over, with the reason thrown away, is not a
/// diagnosable state, which is why this is a fix and not a nicety.</para>
/// <para>It lives here rather than in Orchard.Mirror.Shell because that project is deliberately
/// pure functions over plain data, with its target framework as the guard; a file writer would be
/// the first thing in it that touches the world. Agent.Windows is the lowest project both the agent
/// supervisor and the window already depend on.</para>
/// <para>Best-effort by design: logging must never be the reason something breaks, so every failure
/// to write is swallowed. It is not a diagnostic tool if it can throw.</para>
/// </remarks>
public static class MirrorLog
{
    /// <summary>Small enough to attach to a bug report, large enough to hold a whole session.</summary>
    private const long MaximumBytes = 2 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? path;

    /// <summary>Where the log is being written, once anything has been written to it.</summary>
    public static string? Path => path;

    /// <summary>Record one line. Never throws.</summary>
    public static void Write(string category, string message)
    {
        try
        {
            lock (Gate)
            {
                path ??= Prepare();
                if (path is null)
                {
                    return;
                }

                File.AppendAllText(
                    path,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {category,-9} {message}{Environment.NewLine}"));
            }
        }
        catch (Exception)
        {
            // A log that can break the app is worse than no log.
        }
    }

    private static string? Prepare()
    {
        try
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orchard",
                "Mirror");
            _ = Directory.CreateDirectory(directory);
            string file = System.IO.Path.Combine(directory, "mirror.log");

            // One roll rather than a numbered set. The previous session is worth keeping, because
            // this failure is usually reported after the app was restarted to get around it.
            if (File.Exists(file) && new FileInfo(file).Length > MaximumBytes)
            {
                File.Move(file, file + ".previous", overwrite: true);
            }

            return file;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
