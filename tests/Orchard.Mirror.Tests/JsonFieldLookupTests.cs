using System.Text.Json;
using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The device label reads back what Orchard believes it is connected to. A wrong answer here is not
/// a crash — it is a plausible-looking string presented to the owner as fact, which is worse.
/// </summary>
/// <remarks>
/// The shapes below are the real defect: a CoreDevice reply carries a generic <c>name</c> nested
/// inside unrelated payloads, and the old single depth-first pass over every candidate key at once
/// returned whichever it met first. The strip displayed "com.apple.os.update-03…" as the phone.
/// </remarks>
internal static class JsonFieldLookupTests
{
    /// <summary>The specific key wins over a generic one, however deep the generic one is found.</summary>
    internal static void PrefersTheSpecificKeyOverAGenericOne()
    {
        using JsonDocument reply = JsonDocument.Parse(
            """
            {
              "pendingUpdate": { "name": "com.apple.os.update-03d1f", "state": "idle" },
              "deviceName": "Jimmy's iPhone"
            }
            """);

        Assert(
            JsonFieldLookup.FindString(reply.RootElement, "deviceName", "name") == "Jimmy's iPhone",
            "a nested generic key outranked the reply's own deviceName");
    }

    /// <summary>Falling back to the generic key is still allowed when the specific one is absent.</summary>
    internal static void FallsBackToTheGenericKey()
    {
        using JsonDocument reply = JsonDocument.Parse("""{ "properties": { "name": "iPhone 16e" } }""");

        Assert(
            JsonFieldLookup.FindString(reply.RootElement, "deviceName", "name") == "iPhone 16e",
            "the fallback key was not used when the preferred one was missing");
    }

    /// <summary>Between two matches on the same key, the shallower one is the reply's own.</summary>
    internal static void PrefersTheShallowerOfTwoMatches()
    {
        using JsonDocument reply = JsonDocument.Parse(
            """
            {
              "attached": { "detail": { "transport": "usb" } },
              "transport": "wifi"
            }
            """);

        Assert(
            JsonFieldLookup.FindString(reply.RootElement, "transport") == "wifi",
            "a deeply nested match outranked the one at the top of the reply");
    }

    /// <summary>A key that is not a string is not an answer.</summary>
    internal static void IgnoresANonStringMatch()
    {
        using JsonDocument reply = JsonDocument.Parse("""{ "deviceName": { "value": "x" }, "name": "iPhone" }""");

        Assert(
            JsonFieldLookup.FindString(reply.RootElement, "deviceName", "name") == "iPhone",
            "an object under the preferred key was treated as the answer");
    }

    /// <summary>Nothing found is reported as nothing, so the caller can say so rather than guess.</summary>
    internal static void ReportsNothingRatherThanGuessing()
    {
        using JsonDocument reply = JsonDocument.Parse("""{ "state": "connected" }""");

        Assert(
            JsonFieldLookup.FindString(reply.RootElement, "deviceName", "name") is null,
            "a reply with no name at all produced one anyway");
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
