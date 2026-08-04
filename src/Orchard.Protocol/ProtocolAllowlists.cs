using System.Collections.Frozen;

namespace Orchard.Protocol;

/// <summary>Closed version 1 vocabularies. New entries require a protocol-profile change.</summary>
public static class ProtocolAllowlists
{
    public static IReadOnlySet<string> NodeKinds { get; } = CreateSet(
        "root",
        "text",
        "button",
        "textField",
        "secureField",
        "vStack",
        "hStack",
        "zStack",
        "group",
        "conditional",
        "spacer",
        "divider",
        "image",
        "scrollView",
        "list",
        "navigationStack",
        "toggle",
        "slider",
        "progressView");

    public static IReadOnlySet<string> Events { get; } = CreateSet(
        "press",
        "change",
        "submit",
        "focus",
        "blur",
        "appear",
        "disappear");

    public static IReadOnlySet<string> PropertyNames { get; } = CreateSet(
        "text",
        "value",
        "placeholder",
        "systemName",
        "resourceName",
        "axis",
        "alignment",
        "spacing",
        "padding",
        "font",
        "fontSize",
        "fontWeight",
        "foregroundColor",
        "backgroundColor",
        "width",
        "height",
        "minWidth",
        "minHeight",
        "maxWidth",
        "maxHeight",
        "enabled",
        "hidden",
        "opacity",
        "cornerRadius",
        "accessibilityLabel",
        "accessibilityHint",
        "accessibilityValue",
        "accessibilityIdentifier");

    private static FrozenSet<string> CreateSet(params string[] values) =>
        values.ToFrozenSet(StringComparer.Ordinal);
}
