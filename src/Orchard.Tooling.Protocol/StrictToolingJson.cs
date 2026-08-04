using System.Text.Json;

namespace Orchard.Tooling.Protocol;

/// <summary>Parses tooling JSON with duplicate-member and structural-budget checks.</summary>
public static class StrictToolingJson
{
    public static JsonDocument ParseObject(
        ReadOnlyMemory<byte> utf8Json,
        ToolingJsonOptions? options = null)
    {
        var validated = (options ?? new ToolingJsonOptions()).Validate();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = validated.MaximumDepth
                });
        }
        catch (JsonException exception)
        {
            throw new ToolingJsonException("The tooling message was not strict UTF-8 JSON.", exception);
        }

        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolingJsonException("A tooling message must be a JSON object.");
            }

            ValidateStructure(document.RootElement, validated);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void ValidateStructure(JsonElement root, ToolingJsonOptions options)
    {
        var remaining = options.MaximumElementCount;
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (--remaining < 0)
            {
                throw new ToolingJsonException(
                    $"A tooling message exceeded {options.MaximumElementCount} JSON elements.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    ValidateObject(element, pending, options);
                    break;

                case JsonValueKind.Array:
                    ValidateArray(element, pending, options);
                    break;
            }
        }
    }

    private static void ValidateObject(
        JsonElement element,
        Stack<JsonElement> pending,
        ToolingJsonOptions options)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (++count > options.MaximumMembersPerObject)
            {
                throw new ToolingJsonException(
                    $"A tooling object exceeded {options.MaximumMembersPerObject} members.");
            }

            if (!names.Add(property.Name))
            {
                throw new ToolingJsonException("A tooling object contained a duplicate member name.");
            }

            pending.Push(property.Value);
        }
    }

    private static void ValidateArray(
        JsonElement element,
        Stack<JsonElement> pending,
        ToolingJsonOptions options)
    {
        var count = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (++count > options.MaximumItemsPerArray)
            {
                throw new ToolingJsonException(
                    $"A tooling array exceeded {options.MaximumItemsPerArray} items.");
            }

            pending.Push(item);
        }
    }
}
