using System.Security.Cryptography;

namespace Orchard.Transport.Windows;

/// <summary>
/// An unguessable local pipe name and its independent 256-bit bearer credential.
/// </summary>
public sealed class OrchardPipeEndpoint
{
    private const string PipePrefix = "orchard-v1-";
    private const int PipeRandomBytes = 16;
    private const int AuthenticationTokenBytes = 32;

    private OrchardPipeEndpoint(string pipeName, string authenticationToken)
    {
        PipeName = pipeName;
        AuthenticationToken = authenticationToken;
    }

    public string PipeName { get; }

    /// <summary>
    /// Gets the bearer credential. Treat this value as a secret and never place it on a command line or in logs.
    /// </summary>
    public string AuthenticationToken { get; }

    public static OrchardPipeEndpoint Create() => new(
        PipePrefix + SecretEncoding.Encode(RandomNumberGenerator.GetBytes(PipeRandomBytes)),
        SecretEncoding.Encode(RandomNumberGenerator.GetBytes(AuthenticationTokenBytes)));

    /// <summary>Rehydrates credentials transferred through a protected process-to-process channel.</summary>
    public static OrchardPipeEndpoint FromCredentials(string pipeName, string authenticationToken)
    {
        ValidatePipeName(pipeName);
        ValidateAuthenticationToken(authenticationToken);
        return new OrchardPipeEndpoint(pipeName, authenticationToken);
    }

    public override string ToString() =>
        "OrchardPipeEndpoint { PipeName = [REDACTED], AuthenticationToken = [REDACTED] }";

    private static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            throw InvalidEndpoint($"The pipe name must begin with '{PipePrefix}'.");
        }

        var randomSuffix = pipeName[PipePrefix.Length..];
        if (!SecretEncoding.TryDecode(randomSuffix, out var randomBytes))
        {
            throw InvalidEndpoint("The pipe name must contain a canonical base64url random suffix.");
        }

        try
        {
            if (randomBytes.Length != PipeRandomBytes ||
                !string.Equals(randomSuffix, SecretEncoding.Encode(randomBytes), StringComparison.Ordinal))
            {
                throw InvalidEndpoint("The pipe name must contain a canonical 128-bit random suffix.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
        }
    }

    private static void ValidateAuthenticationToken(string authenticationToken)
    {
        if (!SecretEncoding.TryDecode(authenticationToken, out var bytes))
        {
            throw InvalidEndpoint("The authentication token must be a canonical base64url-encoded 256-bit value.");
        }

        try
        {
            if (bytes.Length != AuthenticationTokenBytes)
            {
                throw InvalidEndpoint("The authentication token must be a canonical base64url-encoded 256-bit value.");
            }

            var canonical = SecretEncoding.Encode(bytes);
            if (!string.Equals(authenticationToken, canonical, StringComparison.Ordinal))
            {
                throw InvalidEndpoint("The authentication token must use canonical unpadded base64url encoding.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static OrchardTransportException InvalidEndpoint(string message) =>
        new(OrchardTransportErrorCodes.InvalidEndpoint, message);
}

internal static class SecretEncoding
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        var padding = (value.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => null
        };
        if (padding is null)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
