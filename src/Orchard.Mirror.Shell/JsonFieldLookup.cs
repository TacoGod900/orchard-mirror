using System.Text.Json;

namespace Orchard.Mirror.Shell;

/// <summary>
/// Pulls a named string out of a CoreDevice reply whose shape Orchard does not control.
/// </summary>
/// <remarks>
/// The agent forwards whatever the device answered, and those replies nest deeply and repeat
/// generic key names at several levels. Finding a field therefore has to search — but the order it
/// searches in is the whole problem, and getting it wrong is not a crash, it is a plausible wrong
/// answer displayed as fact.
/// </remarks>
public static class JsonFieldLookup
{
    /// <summary>
    /// Find the first of <paramref name="names"/> that is present, preferring earlier names and
    /// shallower matches.
    /// </summary>
    /// <remarks>
    /// Each name is searched breadth-first and to exhaustion before the next is tried, so a
    /// specific key always beats a generic one and a shallow match beats a deep one. A single
    /// depth-first pass over all the names at once returns whichever it happens to meet first,
    /// which is how the connected device's label came back reading "com.apple.os.update-03…" — a
    /// nested <c>name</c> belonging to something else entirely, found before the reply's own
    /// <c>deviceName</c>.
    /// </remarks>
    public static string? FindString(JsonElement root, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string name in names)
        {
            if (Nearest(root, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? Nearest(JsonElement root, string name)
    {
        Queue<JsonElement> pending = new();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            JsonElement current = pending.Dequeue();
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    // Every key at this depth is checked before descending, or a deeper match on
                    // the same name could win over a shallower one.
                    foreach (JsonProperty property in current.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String
                            && property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            return property.Value.GetString();
                        }
                    }

                    foreach (JsonProperty property in current.EnumerateObject())
                    {
                        pending.Enqueue(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in current.EnumerateArray())
                    {
                        pending.Enqueue(item);
                    }

                    break;

                default:
                    break;
            }
        }

        return null;
    }
}
