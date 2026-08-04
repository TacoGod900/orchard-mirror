using System.Security.Cryptography;
using System.Text;

namespace Orchard.Mirror.Shell;

/// <summary>What a license entitles the holder to.</summary>
public enum LicenseTier
{
    /// <summary>No valid key. The app runs in whatever unlicensed state the host decides.</summary>
    None,

    /// <summary>A free or paid beta key. Unlocks everything while the beta runs.</summary>
    Beta,

    /// <summary>A monthly subscription.</summary>
    Monthly,

    /// <summary>A yearly subscription.</summary>
    Yearly,

    /// <summary>A one-time lifetime purchase.</summary>
    Lifetime,
}

/// <summary>A parsed, well-formed license key.</summary>
/// <param name="Tier">What it unlocks.</param>
/// <param name="Data">The key's unique payload, minus the human-facing formatting.</param>
public readonly record struct LicenseKey(LicenseTier Tier, string Data);

/// <summary>
/// Parses and issues Orchard Mirror license keys, and decides what a key grants.
/// </summary>
/// <remarks>
/// <para>This is the seam the whole paid story hangs off, built now so it can be wired to a real
/// backend later without touching the app. A key looks like <c>ORCH-BETA-7K3M9QZ2-1A2B</c>: a fixed
/// prefix, a tier, an eight-character payload, and a four-character check derived from the rest.</para>
/// <para><b>What the check does and does not do.</b> The check makes a random or mistyped string
/// fail instantly and offline — no server round-trip to tell someone they fat-fingered a character —
/// and it lets the app recognise the tier a key was issued for. It is <em>not</em> a security
/// boundary: the algorithm is in this file, so a determined person could compute a valid check
/// themselves. Real anti-piracy is server validation, and this class is deliberately shaped so that
/// swapping the offline check for a signed server response later is a one-method change behind
/// <see cref="ILicenseGateway"/>. For a beta whose keys are handed out deliberately, an offline
/// check that stops typos and garbage is the right amount of machinery.</para>
/// <para>Pure functions over strings: no files, no network, no clock. Which is why the format,
/// the tiers, and every rejection can be a unit test rather than something only a live server
/// could exercise.</para>
/// </remarks>
public static class Licensing
{
    private const string Prefix = "ORCH";
    private const int DataLength = 8;
    private const int CheckLength = 4;

    private static readonly (LicenseTier Tier, string Code)[] TierCodes =
    [
        (LicenseTier.Beta, "BETA"),
        (LicenseTier.Monthly, "MON"),
        (LicenseTier.Yearly, "YEAR"),
        (LicenseTier.Lifetime, "LIFE"),
    ];

    /// <summary>The characters a key's payload may use. No ambiguous 0/O or 1/I, so keys read aloud.</summary>
    private const string DataAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Try to read a key the user typed or pasted.</summary>
    /// <remarks>
    /// Forgiving on the way in: case is ignored, and spaces or missing dashes are tolerated, because
    /// a key copied out of an email arrives in every possible shape. Strict on what it accepts:
    /// the prefix, a known tier, the payload alphabet, and a matching check must all hold.
    /// </remarks>
    public static bool TryParse(string? input, out LicenseKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // Normalise: uppercase, drop everything that is not part of the key, then re-segment by the
        // fixed lengths rather than trusting the dashes the user may have dropped or doubled.
        string cleaned = new(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        if (!cleaned.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string afterPrefix = cleaned[Prefix.Length..];

        foreach ((LicenseTier tier, string code) in TierCodes)
        {
            if (!afterPrefix.StartsWith(code, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = afterPrefix[code.Length..];
            if (rest.Length != DataLength + CheckLength)
            {
                return false;
            }

            string data = rest[..DataLength];
            string check = rest[DataLength..];

            if (!data.All(DataAlphabet.Contains))
            {
                return false;
            }

            if (!FixedTimeEquals(check, Check(code, data)))
            {
                return false;
            }

            key = new LicenseKey(tier, data);
            return true;
        }

        return false;
    }

    /// <summary>Build a well-formed key for a tier and payload. Used by tests and the future issuer.</summary>
    /// <remarks>
    /// The real key issuer will live on the server that Stripe's webhook calls; this is the same
    /// formatting logic it will use, kept here so the client and server can never disagree about
    /// what a valid key looks like.
    /// </remarks>
    public static string CreateKey(LicenseTier tier, string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        string normalised = data.ToUpperInvariant();
        if (normalised.Length != DataLength || !normalised.All(DataAlphabet.Contains))
        {
            throw new ArgumentException(
                $"Key payload must be {DataLength} characters from the key alphabet.", nameof(data));
        }

        string code = CodeFor(tier);
        return $"{Prefix}-{code}-{normalised}-{Check(code, normalised)}";
    }

    /// <summary>Whether a tier lets the app run its full feature set.</summary>
    /// <remarks>
    /// During the beta every issued tier unlocks everything; the distinction between them is about
    /// billing, not features. This is the one place that policy lives, so tightening it later — say,
    /// gating a feature to paid tiers after the beta — is a change here and nowhere else.
    /// </remarks>
    public static bool GrantsFullAccess(LicenseTier tier) => tier is not LicenseTier.None;

    private static string CodeFor(LicenseTier tier)
    {
        foreach ((LicenseTier candidate, string code) in TierCodes)
        {
            if (candidate == tier)
            {
                return code;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(tier), tier, "No key code for this tier.");
    }

    /// <summary>The four-character check for a tier code and payload.</summary>
    /// <remarks>
    /// Uppercase hex of the first two bytes of SHA-256 over the key's meaningful part. Hex rather
    /// than a denser encoding on purpose: it is byte-for-byte identical across every language a
    /// future server might be written in, so the client and issuer cannot drift.
    /// </remarks>
    private static string Check(string code, string data)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{Prefix}-{code}-{data}"));
        return Convert.ToHexString(hash)[..CheckLength];
    }

    /// <summary>Compare two checks without an early-out on the first differing character.</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

/// <summary>
/// The boundary between "is this key any good" and how the app finds out.
/// </summary>
/// <remarks>
/// Today the only implementation checks a key's format offline, so the beta works with no server at
/// all. When accounts go live, a second implementation calls the licensing server — is this key real,
/// is the subscription still paid, has it been refunded — and returns the same
/// <see cref="LicenseCheck"/>. Nothing in the app above this interface changes.
/// </remarks>
public interface ILicenseGateway
{
    /// <summary>Decide what a key grants.</summary>
    LicenseCheck Evaluate(string? key);
}

/// <summary>The answer to "what does this key give the holder right now".</summary>
/// <param name="Tier">The tier granted, or <see cref="LicenseTier.None"/> if the key is no good.</param>
/// <param name="Accepted">Whether the app should run its full feature set.</param>
/// <param name="Message">A short, user-facing reason, shown when a key is refused.</param>
public readonly record struct LicenseCheck(LicenseTier Tier, bool Accepted, string Message);

/// <summary>
/// The offline gateway used through the beta: a key is good if it is well-formed.
/// </summary>
/// <remarks>
/// This is the "wire it up later" seam made concrete. It ships working — beta keys unlock the app
/// with no backend — and it is the class you replace, not edit, when the licensing server exists.
/// </remarks>
public sealed class OfflineBetaGateway : ILicenseGateway
{
    /// <inheritdoc/>
    public LicenseCheck Evaluate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new LicenseCheck(LicenseTier.None, Accepted: false, "Enter your Orchard Mirror key to begin.");
        }

        if (!Licensing.TryParse(key, out LicenseKey parsed))
        {
            return new LicenseCheck(LicenseTier.None, Accepted: false, "That key doesn't look right. Check for typos.");
        }

        return new LicenseCheck(parsed.Tier, Licensing.GrantsFullAccess(parsed.Tier), "Key accepted. You're all set.");
    }
}
