using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// License keys are the seam the paid product hangs off, so their format has to be exact: a key the
/// server issues must parse in the app, a random string must be refused, and a mistyped one must
/// fail offline rather than after a server round-trip. All pure-string, so all provable here.
/// </summary>
internal static class LicensingTests
{
    /// <summary>Every tier round-trips: a key built for a tier parses back as that tier.</summary>
    internal static void EveryTierRoundTrips()
    {
        foreach (LicenseTier tier in (LicenseTier[])[LicenseTier.Beta, LicenseTier.Monthly, LicenseTier.Yearly, LicenseTier.Lifetime])
        {
            string key = Licensing.CreateKey(tier, "ABCDEF23");
            Assert(Licensing.TryParse(key, out LicenseKey parsed), $"a freshly built {tier} key did not parse");
            Assert(parsed.Tier == tier, $"a {tier} key parsed as {parsed.Tier}");
        }
    }

    /// <summary>A gibberish string is refused, and doesn't throw doing it.</summary>
    internal static void GarbageIsRefused()
    {
        foreach (string junk in (string[])["", "   ", "hello", "ORCH", "ORCH-BETA", "not-a-key-at-all", "1234567890"])
        {
            Assert(!Licensing.TryParse(junk, out _), $"garbage \"{junk}\" was accepted as a key");
        }
    }

    /// <summary>
    /// A single altered character fails the check — the point of having one.
    /// </summary>
    /// <remarks>
    /// This is what turns "your key is wrong" into an instant, offline answer instead of a confusing
    /// server rejection. Flip one payload character and the check no longer matches.
    /// </remarks>
    internal static void OneWrongCharacterFailsTheCheck()
    {
        string good = Licensing.CreateKey(LicenseTier.Yearly, "ABCDEF23");
        // Corrupt a payload character (index inside the ORCH-YEAR-........-CCCC data section).
        char[] chars = good.ToCharArray();
        int dataStart = "ORCH-YEAR-".Length;
        chars[dataStart] = chars[dataStart] == 'A' ? 'B' : 'A';
        string tampered = new(chars);

        Assert(!Licensing.TryParse(tampered, out _), "a key with a flipped character was still accepted");
    }

    /// <summary>Case and stray formatting are forgiven; the key still parses.</summary>
    /// <remarks>
    /// A key pasted out of an email arrives lowercased, wrapped, or with the dashes eaten. Rejecting
    /// it for that would be a support ticket per customer, so the parser normalises first.
    /// </remarks>
    internal static void CaseAndFormattingAreForgiven()
    {
        string canonical = Licensing.CreateKey(LicenseTier.Monthly, "PQRSTU24");

        Assert(Licensing.TryParse(canonical.ToLowerInvariant(), out LicenseKey lower), "a lowercased key was refused");
        Assert(lower.Tier == LicenseTier.Monthly, "a lowercased key parsed to the wrong tier");

        Assert(Licensing.TryParse(canonical.Replace("-", " "), out _), "a key with spaces for dashes was refused");
        Assert(Licensing.TryParse(canonical.Replace("-", ""), out _), "a key with no separators was refused");
        Assert(Licensing.TryParse($"  {canonical}  ", out _), "a key with surrounding whitespace was refused");
    }

    /// <summary>The payload alphabet excludes characters that get misread when typed.</summary>
    internal static void KeyPayloadRejectsAmbiguousCharacters()
    {
        // '0', 'O', '1', 'I' are not in the alphabet, so a key built with them cannot exist; asking
        // for one is a programming error, and a key string containing them fails to parse.
        Assert(!Licensing.TryParse("ORCH-BETA-OOOOOOOO-0000", out _), "a key full of ambiguous characters parsed");

        bool threw = false;
        try
        {
            Licensing.CreateKey(LicenseTier.Beta, "OOOO1111");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert(threw, "building a key from ambiguous characters was allowed");
    }

    /// <summary>The offline gateway accepts a real key and explains a bad one.</summary>
    /// <remarks>
    /// The gateway is what the app actually calls, and it is the class that gets swapped for a server
    /// check later. Its contract: a good key is accepted with a tier, a bad one is refused with words
    /// a person can act on, and an empty one asks rather than scolds.
    /// </remarks>
    internal static void TheOfflineGatewayAcceptsRealKeysAndExplainsBadOnes()
    {
        OfflineBetaGateway gateway = new();

        LicenseCheck empty = gateway.Evaluate("");
        Assert(!empty.Accepted && empty.Message.Length > 0, "an empty key was not handled gracefully");

        LicenseCheck bad = gateway.Evaluate("ORCH-BETA-BADKEY99-9999");
        Assert(!bad.Accepted && bad.Message.Length > 0, "a bad key was accepted or gave no reason");

        LicenseCheck good = gateway.Evaluate(Licensing.CreateKey(LicenseTier.Lifetime, "GH7K9MNP"));
        Assert(good.Accepted, "a valid lifetime key was refused by the gateway");
        Assert(good.Tier == LicenseTier.Lifetime, "the gateway reported the wrong tier");
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
