// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the name rules approved by
// Edwin West on 2026-11-26 (design D). AI-generated.

using SecretModel.Core.Privacy;

namespace SecretModel.Core.Journal;

/// <summary>
/// The name rules for journal ingestion (README §8.2), the same in Home and Azure mode. A client id
/// is the name of the client's journal folder; a journal file's name is checked before anything else
/// about it.
/// </summary>
public static class JournalNames
{
    /// <summary>Only files whose names end in this are read; anything else in a client folder is left alone.</summary>
    public const string Extension = ".jsonl";

    public const int MaxClientIdLength = 64;

    /// <summary>The longest file name before <see cref="Extension"/>.</summary>
    public const int MaxFileStemLength = 100;

    /// <summary>
    /// A client id: 1 to 64 ASCII letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, starting with a
    /// letter or digit.
    /// </summary>
    public static bool IsValidClientId(string? value) =>
        value is { Length: >= 1 and <= MaxClientIdLength } && IsPlainName(value);

    /// <summary>
    /// A journal file name: 1 to 100 characters by the client-id rule, then <c>.jsonl</c>, with no run
    /// of 13 digits anywhere in it (README SM-PRIV-002, which ignores the dots, dashes and underscores
    /// between digits).
    /// </summary>
    public static bool IsValidFileName(string? value)
    {
        if (value is null || !value.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        string stem = value[..^Extension.Length];
        return stem.Length is >= 1 and <= MaxFileStemLength && IsPlainName(stem) && !TokenValidator.HasLongDigitRun(value);
    }

    private static bool IsPlainName(string value) =>
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
