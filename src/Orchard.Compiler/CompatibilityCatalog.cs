using Orchard.Core;

namespace Orchard.Compiler;

public sealed record CompatibilityEntry(
    CompatibilityStatus Status,
    string? Notes = null,
    string? RemoteCapability = null);

public sealed class CompatibilityCatalog
{
    public const string ProfileVersion = "1.0.0";

    private readonly IReadOnlyDictionary<string, CompatibilityEntry> _views;
    private readonly IReadOnlyDictionary<string, CompatibilityEntry> _modifiers;
    private readonly IReadOnlyDictionary<string, CompatibilityEntry> _sourceSymbols;

    public CompatibilityCatalog()
    {
        _views = new Dictionary<string, CompatibilityEntry>(StringComparer.Ordinal)
        {
            ["VStack"] = new(CompatibilityStatus.Supported),
            ["HStack"] = new(CompatibilityStatus.Supported),
            ["ZStack"] = new(CompatibilityStatus.Supported),
            ["Text"] = new(CompatibilityStatus.Supported),
            ["Button"] = new(CompatibilityStatus.Supported),
            ["TextField"] = new(CompatibilityStatus.Supported),
            ["SecureField"] = new(CompatibilityStatus.Supported),
            ["Image"] = new(CompatibilityStatus.Partial, "System symbols render as Orchard placeholders in this milestone."),
            ["Spacer"] = new(CompatibilityStatus.Supported),
            ["Divider"] = new(CompatibilityStatus.Supported),
            ["Toggle"] = new(CompatibilityStatus.Supported),
            ["ProgressView"] = new(CompatibilityStatus.Partial, "Indeterminate and linear styles are supported."),
            ["Label"] = new(CompatibilityStatus.Partial, "Text and system-image labels are supported."),
            ["ScrollView"] = new(CompatibilityStatus.Supported),
            ["NavigationStack"] = new(CompatibilityStatus.Partial, "Single-window navigation rendering is supported; route restoration is pending."),
            ["List"] = new(CompatibilityStatus.Partial, "Static list content is supported; collection diffing is pending."),
            ["Form"] = new(CompatibilityStatus.Partial, "Core form controls are supported."),
            ["Section"] = new(CompatibilityStatus.Partial, "Headers and static children are supported."),
            ["Group"] = new(CompatibilityStatus.Supported),
            ["NavigationLink"] = new(CompatibilityStatus.Partial, "Destination closures are parsed but not yet activated."),
            ["Picker"] = new(CompatibilityStatus.Partial, "Static options are supported."),
            ["DatePicker"] = new(CompatibilityStatus.Partial, "Date-only selection is supported.")
        };

        _modifiers = new Dictionary<string, CompatibilityEntry>(StringComparer.Ordinal)
        {
            ["padding"] = new(CompatibilityStatus.Supported),
            ["font"] = new(CompatibilityStatus.Supported),
            ["foregroundStyle"] = new(CompatibilityStatus.Partial, "Solid named colours are supported."),
            ["foregroundColor"] = new(CompatibilityStatus.Partial, "Solid named colours are supported."),
            ["background"] = new(CompatibilityStatus.Partial, "Solid named colours are supported."),
            ["cornerRadius"] = new(CompatibilityStatus.Supported),
            ["frame"] = new(CompatibilityStatus.Partial, "Fixed width and height are supported."),
            ["navigationTitle"] = new(CompatibilityStatus.Supported),
            ["disabled"] = new(CompatibilityStatus.Supported),
            ["opacity"] = new(CompatibilityStatus.Supported),
            ["lineLimit"] = new(CompatibilityStatus.Supported),
            ["multilineTextAlignment"] = new(CompatibilityStatus.Supported),
            ["accessibilityLabel"] = new(CompatibilityStatus.Supported),
            ["accessibilityHint"] = new(CompatibilityStatus.Supported),
            ["accessibilityIdentifier"] = new(CompatibilityStatus.Supported),
            ["animation"] = new(CompatibilityStatus.Partial, "Basic property animations are supported."),
            ["onAppear"] = new(CompatibilityStatus.Partial, "Synchronous local handlers are supported."),
            ["tint"] = new(CompatibilityStatus.Supported)
        };

        _sourceSymbols = new Dictionary<string, CompatibilityEntry>(StringComparer.Ordinal)
        {
            ["ARKit"] = new(CompatibilityStatus.RemoteOnly, "ARKit requires physical Apple sensors.", "ARKit"),
            ["RealityKit"] = new(CompatibilityStatus.RemoteOnly, "RealityKit rendering requires Apple validation.", "RealityKit"),
            ["PKPaymentAuthorizationController"] = new(CompatibilityStatus.RemoteOnly, "Apple Pay flows require Apple-controlled entitlements and hardware.", "Apple Pay"),
            ["HKHealthStore"] = new(CompatibilityStatus.RemoteOnly, "HealthKit data and entitlements require Apple validation.", "HealthKit"),
            ["SecureEnclave"] = new(CompatibilityStatus.RemoteOnly, "Secure Enclave operations require Apple hardware.", "Secure Enclave"),
            ["CarPlay"] = new(CompatibilityStatus.RemoteOnly, "CarPlay requires Apple validation and approved entitlements.", "CarPlay"),
            ["MetalKit"] = new(CompatibilityStatus.RemoteOnly, "Metal translation is not part of the initial local runtime.", "Metal"),
            ["StoreKit"] = new(CompatibilityStatus.RemoteOnly, "StoreKit purchase completion requires Apple services.", "StoreKit")
        };
    }

    /// <summary>
    /// Read-only catalog projections used to verify the versioned cross-language profile.
    /// Unknown symbols are intentionally absent and therefore fail closed through the
    /// category-specific lookup methods.
    /// </summary>
    public IReadOnlyDictionary<string, CompatibilityEntry> Views => _views;

    public IReadOnlyDictionary<string, CompatibilityEntry> Modifiers => _modifiers;

    public IReadOnlyDictionary<string, CompatibilityEntry> SourceSymbols => _sourceSymbols;

    public static double GetScoreWeight(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.Supported => 100,
        CompatibilityStatus.Partial => 65,
        CompatibilityStatus.RemoteOnly => 20,
        CompatibilityStatus.Unsupported => 0,
        _ => 0
    };

    public CompatibilityEntry GetView(string name) =>
        _views.GetValueOrDefault(name)
        ?? new CompatibilityEntry(CompatibilityStatus.Unsupported, $"View '{name}' is not present in the current compatibility catalog.");

    public CompatibilityEntry GetModifier(string name) =>
        _modifiers.GetValueOrDefault(name)
        ?? new CompatibilityEntry(CompatibilityStatus.Unsupported, $"Modifier '.{name}' is not present in the current compatibility catalog.");

    public CompatibilityEntry GetSourceSymbol(string name) =>
        _sourceSymbols.GetValueOrDefault(name)
        ?? new CompatibilityEntry(CompatibilityStatus.Unsupported, $"Source capability '{name}' is not present in the current compatibility catalog.");

    public IEnumerable<(string Symbol, CompatibilityEntry Entry)> FindSourceCapabilities(IEnumerable<string> identifiers)
    {
        var identifierSet = identifiers.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _sourceSymbols)
        {
            if (identifierSet.Contains(pair.Key))
            {
                yield return (pair.Key, pair.Value);
            }
        }
    }
}
