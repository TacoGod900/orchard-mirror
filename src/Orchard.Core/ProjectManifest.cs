using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orchard.Core;

public sealed partial class ProjectManifest
{
    public const int MaximumManifestBytes = 256 * 1024;

    public required string ApplicationId { get; init; }

    public required string DisplayName { get; init; }

    public string EntryPoint { get; init; } = "ContentView.swift";

    public string MinimumIosVersion { get; init; } = "17.0";

    public string DefaultDevice { get; init; } = "iphone-15-pro";

    public bool TreatCompatibilityWarningsAsErrors { get; init; }

    public static ProjectManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Manifest '{path}' does not exist.", path);
        }

        try
        {
            var json = BoundedUtf8File.ReadAllText(path, MaximumManifestBytes, $"Manifest '{path}'");
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, OrchardJson.Options)
                ?? throw new InvalidDataException($"Manifest '{path}' is empty or invalid.");
            manifest.Validate();
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Manifest '{path}' contains invalid JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}: {exception.Message}",
                exception);
        }
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ApplicationId) || ApplicationId.Length > 255 ||
            !ApplicationIdExpression().IsMatch(ApplicationId))
        {
            errors.Add("applicationId must be a reverse-DNS identifier using ASCII letters, digits, hyphens, and dots.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 128 || ContainsUnsafeText(DisplayName))
        {
            errors.Add("displayName must contain 1-128 printable characters without control or bidi-override characters.");
        }

        if (string.IsNullOrWhiteSpace(EntryPoint) || EntryPoint.Length > 512 || ContainsUnsafeText(EntryPoint) ||
            Path.IsPathRooted(EntryPoint) ||
            EntryPoint.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal) ||
            !Path.GetExtension(EntryPoint).Equals(".swift", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("entryPoint must be a relative .swift path inside the project and may not contain '..'.");
        }

        if (!Version.TryParse(MinimumIosVersion, out var minimumVersion) || minimumVersion.Major <= 0)
        {
            errors.Add("minimumIosVersion must be a dotted positive version such as '17.0'.");
        }

        if (!DeviceProfile.TryFind(DefaultDevice, out _))
        {
            errors.Add($"defaultDevice '{DefaultDevice}' is unknown. Run 'orchard devices' for valid IDs.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }
    }

    private static bool ContainsUnsafeText(string value) => value.Any(character =>
        char.IsControl(character) ||
        character is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069');

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*(?:\\.[A-Za-z0-9][A-Za-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationIdExpression();
}

public sealed record DeviceProfile(
    string Id,
    string DisplayName,
    int LogicalWidth,
    int LogicalHeight,
    int Scale,
    bool HasHomeIndicator,
    int SafeAreaTop,
    int SafeAreaBottom)
{
    public static IReadOnlyList<DeviceProfile> BuiltIn { get; } =
    [
        new("iphone-se-3", "iPhone SE (3rd generation)", 375, 667, 2, false, 20, 0),
        new("iphone-15", "iPhone 15", 393, 852, 3, true, 59, 34),
        new("iphone-15-pro", "iPhone 15 Pro", 393, 852, 3, true, 59, 34),
        new("iphone-15-pro-max", "iPhone 15 Pro Max", 430, 932, 3, true, 59, 34),
        new("ipad-pro-11", "iPad Pro 11-inch", 834, 1194, 2, true, 24, 20)
    ];

    public static DeviceProfile Find(string id) =>
        TryFind(id, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown Orchard device profile '{id}'.", nameof(id));

    public static bool TryFind(string id, out DeviceProfile profile)
    {
        var match = BuiltIn.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            profile = null!;
            return false;
        }

        profile = match;
        return true;
    }
}
