// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. ClientHashOf
// split out 2026-11-27 so the audit log shares it.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Privacy;

namespace SecretModel.Core.Storage;

/// <summary>
/// Identifies one card's profile in a store (README §9). The storage path comes only from a SHA-256
/// hash of the client id and the two tokens (SM-STOR-001), and always includes the client, so one
/// client can never address another's profile (SM-STOR-006). Tokens must be well-formed and in the
/// right domain, which also rules out path tricks such as "..".
/// </summary>
public sealed record ProfileKey
{
    public ProfileKey(string clientId, string customerToken, string cardToken)
    {
        string clientHash = ClientHashOf(clientId);

        // Messages never repeat a rejected value: it could be a card number (SM-PRIV-004).
        if (!TokenValidator.IsValid(customerToken) || !customerToken.StartsWith("tok_cus_", StringComparison.Ordinal))
        {
            throw new ArgumentException("The customer token must be a valid tok_cus_ token.", nameof(customerToken));
        }

        if (!TokenValidator.IsValid(cardToken) || !cardToken.StartsWith("tok_card_", StringComparison.Ordinal))
        {
            throw new ArgumentException("The card token must be a valid tok_card_ token.", nameof(cardToken));
        }

        ClientId = clientId;
        CustomerToken = customerToken;
        CardToken = cardToken;
        ClientHash = clientHash;
    }

    /// <summary>
    /// SHA-256 of a client id's UTF-8 bytes, 64 lowercase hex characters: the client's folder in every
    /// store (SM-STOR-001, SM-STOR-006). The client id must not be empty.
    /// </summary>
    public static string ClientHashOf(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("The client id must not be empty.", nameof(clientId));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)));
    }

    public string ClientId { get; }

    public string CustomerToken { get; }

    public string CardToken { get; }

    /// <summary>SHA-256 of the client id's UTF-8 bytes, 64 lowercase hex characters.</summary>
    public string ClientHash { get; }

    /// <summary>"{client hash}/{customer token}/{card token}.profile.json", with forward slashes.</summary>
    public string RelativePath => $"{ClientHash}/{CustomerToken}/{CardToken}.profile.json";
}
