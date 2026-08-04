namespace Orchard.Mirror.Windows;

/// <summary>Remembers whether the first-run walkthrough has been completed on this machine.</summary>
/// <remarks>
/// A file under the user's local application data rather than the registry, so that clearing it —
/// which is the only way to see the walkthrough again — is something a person can do by deleting
/// something they can find.
/// </remarks>
internal static class OnboardingState
{
    /// <summary>
    /// Bumping this shows the walkthrough again to everyone.
    /// </summary>
    /// <remarks>
    /// Worth doing only when the setup steps themselves change. The walkthrough is instructions for
    /// putting the phone in the right state, so replaying it after an unrelated update is noise.
    /// </remarks>
    private const int CurrentVersion = 1;

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Orchard",
        "Mirror",
        "onboarding.txt");

    /// <summary>Has this machine already been walked through setup?</summary>
    internal static bool IsComplete()
    {
        try
        {
            return File.Exists(MarkerPath)
                && int.TryParse(File.ReadAllText(MarkerPath).Trim(), out int version)
                && version >= CurrentVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable is treated as not done. Showing the walkthrough twice is a mild
            // annoyance; skipping it on a machine that never saw it leaves the user stuck.
            return false;
        }
    }

    /// <summary>Record that setup was completed, best effort.</summary>
    internal static void MarkComplete()
    {
        try
        {
            string path = MarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
