// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// README SM-PRIV-006: no identifier in src/ may name a card-data concept. This scanner removes
// comments, string literals, character literals and preprocessor lines from C# source, then splits
// every remaining identifier into words (CardHolder -> card, holder) and reports forbidden ones.
// It is word-based, not a substring search, so Span and expand are not hits.
//
// Interpolated strings are removed whole, including their {holes}. A hole only uses names declared
// elsewhere, and those declarations are scanned where they are declared.
// Constructs the scanner does not understand (raw string literals) make it throw, so it can never
// skip code silently.

using System.Text;
using System.Text.RegularExpressions;

namespace SecretModel.Tests.Privacy;

internal static partial class ForbiddenIdentifierScanner
{
    internal sealed record Hit(int Line, string Identifier, string Reason);

    [GeneratedRegex(@"@?[\p{L}_][\p{L}\p{Nd}_]*")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\p{Lu}+(?!\p{Ll})|\p{Lu}?\p{Ll}+|\p{Nd}+|\p{Lo}+")]
    private static partial Regex WordPattern();

    public static IReadOnlyList<Hit> Scan(string source)
    {
        string code = RemoveNonCode(source);
        var hits = new List<Hit>();
        int line = 1;
        int lineStart = 0;
        foreach (Match m in IdentifierPattern().Matches(code))
        {
            for (int i = lineStart; i < m.Index; i++)
            {
                if (code[i] == '\n')
                {
                    line++;
                }
            }

            lineStart = m.Index;
            string identifier = m.Value.TrimStart('@');
            string? reason = ForbiddenReason(Words(identifier));
            if (reason is not null)
            {
                hits.Add(new Hit(line, identifier, reason));
            }
        }

        return hits;
    }

    public static IReadOnlyList<string> Words(string identifier) =>
        [.. WordPattern().Matches(identifier).Select(w => w.Value.ToLowerInvariant())];

    private static string? ForbiddenReason(IReadOnlyList<string> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            string? next = i + 1 < words.Count ? words[i + 1] : null;
            if (w is "pan" or "cvv" or "expiry" or "cardholder" or "cardnumber")
            {
                return w;
            }

            if (w == "card" && next is "number" or "holder")
            {
                return $"card {next}";
            }
        }

        return null;
    }

    // Replaces comments, literals and directives with spaces, keeping newlines so line numbers hold.
    internal static string RemoveNonCode(string s)
    {
        var output = new StringBuilder(s.Length);
        int i = 0;
        bool atLineStart = true;
        while (i < s.Length)
        {
            char c = s[i];
            if (atLineStart && c == '#')
            {
                i = Blank(s, i, s.IndexOf('\n', i) is int nl and >= 0 ? nl : s.Length, output);
                continue;
            }

            if (c == '/' && Peek(s, i + 1) == '/')
            {
                i = Blank(s, i, s.IndexOf('\n', i) is int nl and >= 0 ? nl : s.Length, output);
                continue;
            }

            if (c == '/' && Peek(s, i + 1) == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new FormatException("Unterminated /* comment.");
                }

                i = Blank(s, i, end + 2, output);
                continue;
            }

            if (c == '\'')
            {
                i = Blank(s, i, EndOfCharLiteral(s, i), output);
                atLineStart = false;
                continue;
            }

            if (StringPrefixLength(s, i) is int prefix and > 0)
            {
                i = Blank(s, i, EndOfString(s, i, prefix), output);
                atLineStart = false;
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(s[i - 1])))
            {
                int end = i;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '_' or '.'))
                {
                    end++;
                }

                i = Blank(s, i, end, output);
                atLineStart = false;
                continue;
            }

            output.Append(c);
            atLineStart = c == '\n' || (atLineStart && c is ' ' or '\t' or '\r');
            i++;
        }

        return output.ToString();
    }

    // Length of the prefix ($, @, $@, @$ then ") if a string literal starts at i, else 0.
    private static int StringPrefixLength(string s, int i)
    {
        int j = i;
        while (j < s.Length && s[j] is '$' or '@' && j - i < 2)
        {
            j++;
        }

        if (Peek(s, j) != '"')
        {
            return 0;
        }

        // Three quotes open a raw string literal, unless the string is verbatim (@"""" is a verbatim
        // string holding one quote). Raw strings can't be verbatim.
        bool verbatim = s.AsSpan(i, j - i).Contains('@');
        if (!verbatim && Peek(s, j + 1) == '"' && Peek(s, j + 2) == '"')
        {
            throw new NotSupportedException(
                "Raw string literals (\"\"\") are not supported by ForbiddenIdentifierScanner; extend it first.");
        }

        return j - i + 1;
    }

    private static int EndOfString(string s, int start, int prefixLength)
    {
        string prefix = s.Substring(start, prefixLength - 1);
        bool verbatim = prefix.Contains('@');
        bool interpolated = prefix.Contains('$');
        int i = start + prefixLength;
        while (i < s.Length)
        {
            char c = s[i];
            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && Peek(s, i + 1) == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (interpolated && c == '{')
            {
                if (Peek(s, i + 1) == '{')
                {
                    i += 2;
                    continue;
                }

                i = EndOfHole(s, i + 1);
                continue;
            }

            if (!verbatim && c == '\n')
            {
                throw new FormatException("Newline inside a regular string literal.");
            }

            i++;
        }

        throw new FormatException("Unterminated string literal.");
    }

    // Skips an interpolation hole's code up to its closing brace, stepping over nested literals.
    private static int EndOfHole(string s, int i)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'')
            {
                i = EndOfCharLiteral(s, i);
                continue;
            }

            if (StringPrefixLength(s, i) is int prefix and > 0)
            {
                i = EndOfString(s, i, prefix);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    return i + 1;
                }

                depth--;
            }

            i++;
        }

        throw new FormatException("Unterminated interpolation hole.");
    }

    private static int EndOfCharLiteral(string s, int start)
    {
        int i = start + 1;
        if (Peek(s, i) == '\\')
        {
            i += 2;
        }
        else
        {
            i += 1;
        }

        while (i < s.Length && s[i] != '\'' && s[i] != '\n')
        {
            i++;
        }

        if (Peek(s, i) != '\'')
        {
            throw new FormatException("Unterminated character literal.");
        }

        return i + 1;
    }

    private static int Blank(string s, int from, int to, StringBuilder output)
    {
        for (int k = from; k < to; k++)
        {
            output.Append(s[k] == '\n' ? '\n' : ' ');
        }

        return to;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
