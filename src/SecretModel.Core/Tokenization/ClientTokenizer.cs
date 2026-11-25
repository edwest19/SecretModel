// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Text;

namespace SecretModel.Core.Tokenization;

/// <summary>
/// Client-side tokenizer (README §4, SM-TOK). Turns identifying values into tokens of the form
/// <c>tok_{domain}_{h}</c>, where <c>h</c> is the first 24 hex characters of
/// HMAC-SHA256(secret, "{domain}:{value}"). The vault secret stays on the client.
/// Behavior matches reference/python/client/tokenizer.py; golden vectors check that (SM-TOK-007).
/// </summary>
public sealed class ClientTokenizer
{
    /// <summary>The shortest vault secret accepted, in bytes (SM-TOK-005).</summary>
    public const int MinimumSecretLength = 32;

    private const int TokenHexLength = 24;

    // Throws on text that can't be encoded (a lone surrogate), as Python's str.encode() does.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] secret;

    /// <param name="vaultSecret">At least 32 bytes. The tokenizer keeps its own copy.</param>
    public ClientTokenizer(byte[] vaultSecret)
    {
        ArgumentNullException.ThrowIfNull(vaultSecret);
        if (vaultSecret.Length < MinimumSecretLength)
        {
            throw new ArgumentException(
                $"The vault secret must be at least {MinimumSecretLength} bytes.", nameof(vaultSecret));
        }

        secret = (byte[])vaultSecret.Clone();
    }

    /// <summary>
    /// Tokenizes a card value in the <c>card</c> domain. Spaces and hyphens are removed first, so
    /// grouped and ungrouped forms give the same token (SM-TOK-006).
    /// </summary>
    public string TokenizeCard(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string compact = value.Replace(" ", "", StringComparison.Ordinal)
                              .Replace("-", "", StringComparison.Ordinal);
        return Tokenize("card", compact);
    }

    /// <summary>Tokenizes a customer ID in the <c>cus</c> domain, exactly as given.</summary>
    public string TokenizeCustomer(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Tokenize("cus", value);
    }

    /// <summary>Tokenizes a merchant name in the <c>merch</c> domain, lowercased first.</summary>
    public string TokenizeMerchant(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Tokenize("merch", ReferenceText.Lower(value));
    }

    private string Tokenize(string domain, string value)
    {
        byte[] message = StrictUtf8.GetBytes($"{domain}:{value}");
        byte[] mac = HMACSHA256.HashData(secret, message);
        return $"tok_{domain}_{Convert.ToHexStringLower(mac)[..TokenHexLength]}";
    }
}
