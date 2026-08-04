using System.Text.Json;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>
/// Reads the iPhone's screen resolution out of the CoreDevice device/display report.
/// </summary>
/// <remarks>
/// The encoded mirror picture is padded up to a coding-block multiple — an iPhone 16e streams a
/// 1184×2576 picture for its 1170×2532 screen — and the surplus rows and columns are black. Only
/// the reported screen size distinguishes them, because the stream signals no conformance window.
/// iOS 27 reports sizes as <c>[width, height]</c> number pairs; the object form is accepted too so
/// a future build that switches shapes does not silently reintroduce the black edges.
/// </remarks>
public static class CoreDeviceDisplaySize
{
    private const int MinimumEdge = 240;
    private const int MaximumEdge = 8192;

    /// <summary>
    /// Find the primary display's resolution. Returns false when the report omits it, so the caller
    /// keeps presenting the whole decoded frame rather than cropping to a guess.
    /// </summary>
    public static bool TryFind(JsonElement root, out int width, out int height)
    {
        width = 0;
        height = 0;
        foreach (JsonElement display in EnumerateDisplays(root))
        {
            if (TryReadDisplay(display, out width, out height))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Yield every display description, primary ones first, so a connected external screen cannot
    /// outrank the phone's own panel.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateDisplays(JsonElement root)
    {
        List<JsonElement> primary = [];
        List<JsonElement> others = [];
        Collect(root, primary, others, depth: 0);
        return primary.Concat(others);
    }

    private static void Collect(JsonElement value, List<JsonElement> primary, List<JsonElement> others, int depth)
    {
        // The device report is shallow; the bound stops a hostile or looping payload from
        // driving unbounded recursion.
        if (depth > 12)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("currentMode", out _) || value.TryGetProperty("nativeSize", out _))
            {
                bool isPrimary = value.TryGetProperty("primary", out JsonElement flag)
                    && flag.ValueKind == JsonValueKind.True;
                (isPrimary ? primary : others).Add(value);
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                Collect(property.Value, primary, others, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                Collect(item, primary, others, depth + 1);
            }
        }
    }

    private static bool TryReadDisplay(JsonElement display, out int width, out int height)
    {
        // The mode being streamed wins over the panel's native size, so a phone running a
        // non-native mode is still cropped to the picture actually being encoded.
        if (display.TryGetProperty("currentMode", out JsonElement mode)
            && mode.ValueKind == JsonValueKind.Object
            && TryReadSize(mode, "size", out width, out height))
        {
            return true;
        }

        return TryReadSize(display, "nativeSize", out width, out height)
            || TryReadSize(display, "size", out width, out height);
    }

    private static bool TryReadSize(JsonElement owner, string name, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!owner.TryGetProperty(name, out JsonElement size))
        {
            return false;
        }

        if (size.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            int[] edges = new int[2];
            foreach (JsonElement item in size.EnumerateArray())
            {
                if (index == 2 || !TryReadEdge(item, out edges[index]))
                {
                    return false;
                }

                index++;
            }

            if (index != 2)
            {
                return false;
            }

            width = edges[0];
            height = edges[1];
            return true;
        }

        return size.ValueKind == JsonValueKind.Object
            && TryReadNamedEdge(size, "width", out width)
            && TryReadNamedEdge(size, "height", out height);
    }

    private static bool TryReadNamedEdge(JsonElement owner, string name, out int edge)
    {
        edge = 0;
        return owner.TryGetProperty(name, out JsonElement value) && TryReadEdge(value, out edge);
    }

    private static bool TryReadEdge(JsonElement value, out int edge)
    {
        edge = 0;
        double number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double parsed) => parsed,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed) => parsed,
            _ => double.NaN,
        };
        if (double.IsNaN(number) || number < MinimumEdge || number > MaximumEdge)
        {
            return false;
        }

        edge = (int)Math.Round(number);
        return true;
    }
}
