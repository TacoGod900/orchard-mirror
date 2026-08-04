using System.Text.Json;

namespace Orchard.Core;

public static class OrchardApplicationReader
{
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;
    public const int MaximumNodes = 5_000;
    public const int MaximumDepth = 128;
    public const int MaximumStateItems = 10_000;
    public const int MaximumTextLength = 65_536;

    public static OrchardApplication ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Orchard application document '{path}' does not exist.", path);
        }

        try
        {
            var json = BoundedUtf8File.ReadAllText(
                path,
                MaximumDocumentBytes,
                "Orchard application document");
            var application = OrchardJson.Deserialize<OrchardApplication>(json);
            Validate(application);
            return application;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Invalid Orchard application JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}: {exception.Message}",
                exception);
        }
    }

    public static void Validate(OrchardApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!string.Equals(application.SchemaVersion, OrchardSchema.CurrentVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported Orchard schema '{application.SchemaVersion}'. Expected '{OrchardSchema.CurrentVersion}'.");
        }

        ValidateText(application.ApplicationId, nameof(application.ApplicationId), 255, allowEmpty: false);
        ValidateText(application.DisplayName, nameof(application.DisplayName), 128, allowEmpty: false);
        ValidateText(application.SourceFile, nameof(application.SourceFile), 1_024, allowEmpty: false);

        if (application.State is null || application.State.Count > MaximumStateItems)
        {
            throw new InvalidDataException($"State must contain at most {MaximumStateItems:N0} items.");
        }

        var stateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in application.State)
        {
            if (state is null)
            {
                throw new InvalidDataException("State may not contain null items.");
            }

            ValidateSymbol(state.Name, "state name");
            if (!Enum.IsDefined(state.Kind))
            {
                throw new InvalidDataException($"State '{state.Name}' has an invalid value kind.");
            }

            ValidateText(
                state.InitialValue,
                $"state '{state.Name}' initial value",
                MaximumTextLength,
                allowEmpty: true,
                allowContentWhitespace: true);
            ValidateLocation(state.Location, $"state '{state.Name}' location");
            if (!stateNames.Add(state.Name))
            {
                throw new InvalidDataException($"State name '{state.Name}' is duplicated.");
            }
        }

        if (application.RootView is null)
        {
            throw new InvalidDataException("The Orchard application has no root view.");
        }

        ValidateTree(application.RootView);
        if (application.Compatibility is null)
        {
            throw new InvalidDataException("The compatibility report is missing or invalid.");
        }

        ValidateCompatibility(application.Compatibility);
    }

    private static void ValidateTree(ViewNode root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(ViewNode Node, int Depth)>();
        stack.Push((root, 1));
        var count = 0;

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (node is null)
            {
                throw new InvalidDataException("The view tree contains a null node.");
            }

            count++;
            if (count > MaximumNodes)
            {
                throw new InvalidDataException($"The view tree exceeds {MaximumNodes:N0} nodes.");
            }

            if (depth > MaximumDepth)
            {
                throw new InvalidDataException($"The view tree exceeds a depth of {MaximumDepth:N0}.");
            }

            ValidateSymbol(node.Id, "view ID", permitHyphen: true);
            ValidateSymbol(node.Type, "view type", permitDot: true);
            if (!ids.Add(node.Id))
            {
                throw new InvalidDataException($"View ID '{node.Id}' is duplicated.");
            }

            if (node.Arguments is null || node.Modifiers is null || node.Events is null || node.Children is null)
            {
                throw new InvalidDataException($"View '{node.Id}' has a null collection.");
            }

            foreach (var argument in node.Arguments)
            {
                ValidateText(argument.Key, $"argument key on '{node.Id}'", 128, allowEmpty: false);
                ValidateText(
                    argument.Value,
                    $"argument '{argument.Key}' on '{node.Id}'",
                    MaximumTextLength,
                    allowEmpty: true,
                    allowContentWhitespace: true);
            }

            foreach (var modifier in node.Modifiers)
            {
                if (modifier is null || modifier.Arguments is null)
                {
                    throw new InvalidDataException($"View '{node.Id}' contains an invalid modifier.");
                }

                ValidateSymbol(modifier.Name, "modifier name");
                foreach (var argument in modifier.Arguments)
                {
                    ValidateText(argument.Key, "modifier argument key", 128, allowEmpty: false);
                    ValidateText(
                        argument.Value,
                        "modifier argument value",
                        MaximumTextLength,
                        allowEmpty: true,
                        allowContentWhitespace: true);
                }

                ValidateLocation(modifier.Location, $"modifier '{modifier.Name}' location");
            }

            foreach (var viewEvent in node.Events)
            {
                if (viewEvent is null)
                {
                    throw new InvalidDataException($"View '{node.Id}' contains a null event.");
                }

                ValidateSymbol(viewEvent.Name, "event name");
                ValidateText(
                    viewEvent.Body,
                    $"event '{viewEvent.Name}' body",
                    MaximumTextLength,
                    allowEmpty: true,
                    allowContentWhitespace: true);
                ValidateLocation(viewEvent.Location, $"event '{viewEvent.Name}' location");
            }

            ValidateLocation(node.Location, $"view '{node.Id}' location");

            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                stack.Push((node.Children[index], depth + 1));
            }
        }
    }

    private static void ValidateCompatibility(CompatibilityReport report)
    {
        if (report.ApiUsages is null || report.RequiredRemoteCapabilities is null ||
            report.UnsupportedSymbols is null || !double.IsFinite(report.LocalCompatibilityPercent) ||
            report.LocalCompatibilityPercent is < 0 or > 100)
        {
            throw new InvalidDataException("The compatibility report is missing or invalid.");
        }

        foreach (var usage in report.ApiUsages)
        {
            if (usage is null || !Enum.IsDefined(usage.Status))
            {
                throw new InvalidDataException("The compatibility report contains an invalid API usage.");
            }

            ValidateText(usage.Symbol, "compatibility symbol", 512, allowEmpty: false);
            ValidateText(usage.Category, "compatibility category", 128, allowEmpty: false);
            if (usage.Notes is not null)
            {
                ValidateText(
                    usage.Notes,
                    $"compatibility notes for '{usage.Symbol}'",
                    MaximumTextLength,
                    allowEmpty: true,
                    allowContentWhitespace: true);
            }

            ValidateLocation(usage.Location, $"compatibility location for '{usage.Symbol}'");
        }

        ValidateUniqueTextList(report.RequiredRemoteCapabilities, "remote capability");
        ValidateUniqueTextList(report.UnsupportedSymbols, "unsupported symbol");
    }

    private static void ValidateUniqueTextList(IReadOnlyList<string> values, string field)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ValidateText(value, field, 512, allowEmpty: false);
            if (!unique.Add(value))
            {
                throw new InvalidDataException($"The {field} '{value}' is duplicated.");
            }
        }
    }

    private static void ValidateLocation(SourceLocation? location, string field)
    {
        if (location is null)
        {
            return;
        }

        ValidateText(location.File, $"{field} file", 1_024, allowEmpty: false);
        if (location.Line < 1 || location.Column < 1 || location.Length < 0)
        {
            throw new InvalidDataException($"The {field} has an invalid line, column, or length.");
        }
    }

    private static void ValidateSymbol(string value, string field, bool permitHyphen = false, bool permitDot = false)
    {
        ValidateText(value, field, 256, allowEmpty: false);
        if (!value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_' ||
                permitHyphen && character == '-' ||
                permitDot && character == '.'))
        {
            throw new InvalidDataException($"The {field} '{value}' contains unsupported characters.");
        }
    }

    private static void ValidateText(
        string value,
        string field,
        int maximumLength,
        bool allowEmpty,
        bool allowContentWhitespace = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength ||
            HasUnpairedSurrogate(value) ||
            value.Any(character => IsUnsafeControl(character, allowContentWhitespace)))
        {
            throw new InvalidDataException($"The {field} is empty, too long, or contains unsafe control characters.");
        }
    }

    private static bool IsUnsafeControl(char character, bool allowContentWhitespace) =>
        char.IsControl(character) && (!allowContentWhitespace || character is not '\n' and not '\r' and not '\t') ||
        character is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }
}
