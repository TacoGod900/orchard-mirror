using System.Text.Json;

namespace Orchard.Mirror.Agent.Windows;

/// <summary>Interprets the nested CoreDevice lock-state response without depending on one iOS build's field casing.</summary>
public static class CoreDeviceLockState
{
    /// <summary>Return true only for an explicit lock field whose value is true or exactly <c>locked</c>.</summary>
    public static bool IsLocked(JsonElement value) => IsLocked(value, lockContext: false);

    private static bool IsLocked(JsonElement value, bool lockContext)
    {
        if (lockContext && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Equals("locked", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                bool lockField = IsCurrentLockField(property.Name);
                if (lockField && property.Value.ValueKind is JsonValueKind.True)
                {
                    return true;
                }

                if (lockField && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString()?.Equals("locked", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if (IsLocked(property.Value, lockContext || lockField))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (IsLocked(item, lockContext))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCurrentLockField(string name) =>
        name.Equals("lockState", StringComparison.OrdinalIgnoreCase)
        || name.Equals("locked", StringComparison.OrdinalIgnoreCase)
        || name.Equals("isLocked", StringComparison.OrdinalIgnoreCase)
        || name.Equals("deviceLocked", StringComparison.OrdinalIgnoreCase);
}
