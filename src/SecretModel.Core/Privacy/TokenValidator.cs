// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Text;
using System.Text.RegularExpressions;

namespace SecretModel.Core.Privacy;

/// <summary>What <see cref="TokenValidator.Check"/> found.</summary>
public enum TokenVerdict
{
    /// <summary>A well-formed token (SM-PRIV-001).</summary>
    Valid,

    /// <summary>Not a well-formed token. Rejected.</summary>
    Malformed,

    /// <summary>
    /// Not a well-formed token, and it contains a run of 13 or more digits, so it may carry a card
    /// number (SM-PRIV-002). Rejected, and handled as personal data: never logged or echoed
    /// (SM-PRIV-004, SM-PRIV-005), and a journal file containing one is rejected whole (SM-JRNL-005).
    /// </summary>
    LongDigitRun,
}

/// <summary>
/// Server-side check of every token the service receives (README §2). Returns a verdict only; it
/// never puts the value it checked into a message.
/// </summary>
public static partial class TokenValidator
{
    /// <summary>The shortest run of digits treated as a possible card number (SM-PRIV-002).</summary>
    public const int MinimumDigitRun = 13;

    // The whole value must match. \z, not $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"^tok_(cus|card|txn|merch)_[0-9a-f]{24}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenFormat();

    /// <summary>Classifies a value received where a token is expected.</summary>
    public static TokenVerdict Check(string? value)
    {
        if (value is null)
        {
            return TokenVerdict.Malformed;
        }

        if (TokenFormat().IsMatch(value))
        {
            return TokenVerdict.Valid;
        }

        return HasLongDigitRun(value) ? TokenVerdict.LongDigitRun : TokenVerdict.Malformed;
    }

    /// <summary>True only for <see cref="TokenVerdict.Valid"/>.</summary>
    public static bool IsValid(string? value) => Check(value) == TokenVerdict.Valid;

    /// <summary>
    /// True when <paramref name="value"/> holds 13 or more consecutive decimal digits, in any script,
    /// ignoring every character that is not a letter or digit (SM-PRIV-002). Used on its own by the
    /// journal reader, which scans whole values and lines rather than single tokens (SM-JRNL-005).
    /// </summary>
    // Ignores every character that is not a letter or digit (spaces, hyphens, underscores, dots,
    // slashes, no-break and zero-width spaces, ...), then looks for 13 or more consecutive decimal
    // digits in any script, so full-width or other non-ASCII digits count too.
    public static bool HasLongDigitRun(string value)
    {
        int run = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                if (++run >= MinimumDigitRun)
                {
                    return true;
                }
            }
            else if (Rune.IsLetter(rune))
            {
                run = 0;
            }
        }

        return false;
    }
}
