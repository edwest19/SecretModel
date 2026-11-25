// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Text;

namespace SecretModel.Core.Text;

/// <summary>
/// Text rules shared by the tokenizer and quantizer, written to match the Python reference
/// (reference/python), which lowercases with <c>str.lower()</c>.
/// </summary>
internal static class ReferenceText
{
    private const int CapitalSigma = 0x03A3;
    private const int SmallSigma = 0x03C3;
    private const int FinalSigma = 0x03C2;

    /// <summary>
    /// Lowercases text exactly as Python 3.13's <c>str.lower()</c> does (README SM-TOK-008), using
    /// data measured from Python (<see cref="PythonLowercaseData"/>) rather than the operating
    /// system's Unicode tables, so the result is the same on every OS. Lone surrogates are left
    /// unchanged, as Python leaves them.
    /// </summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int[] codePoints = ToCodePoints(text);
        var result = new StringBuilder(text.Length);

        for (int i = 0; i < codePoints.Length; i++)
        {
            int cp = codePoints[i];
            if (cp == CapitalSigma)
            {
                AppendCodePoint(result, IsFinalSigma(codePoints, i) ? FinalSigma : SmallSigma);
                continue;
            }

            int index = Array.BinarySearch(PythonLowercaseData.LowerFrom, cp);
            if (index >= 0)
            {
                result.Append(PythonLowercaseData.LowerTo[index]);
            }
            else
            {
                AppendCodePoint(result, cp);
            }
        }

        return result.ToString();
    }

    // A capital sigma becomes final when a cased letter comes before it and none follows it,
    // skipping case-ignorable characters in both directions.
    private static bool IsFinalSigma(int[] codePoints, int i)
    {
        int j = i - 1;
        while (j >= 0 && IsCaseIgnorable(codePoints[j]))
        {
            j--;
        }

        bool final = j >= 0 && IsCased(codePoints[j]);
        if (final && i + 1 < codePoints.Length)
        {
            j = i + 1;
            while (j < codePoints.Length && IsCaseIgnorable(codePoints[j]))
            {
                j++;
            }

            final = j == codePoints.Length || !IsCased(codePoints[j]);
        }

        return final;
    }

    private static bool IsCaseIgnorable(int cp) =>
        InRanges(PythonLowercaseData.CaseIgnorableStarts, PythonLowercaseData.CaseIgnorableEnds, cp);

    private static bool IsCased(int cp) =>
        InRanges(PythonLowercaseData.CasedStarts, PythonLowercaseData.CasedEnds, cp);

    private static bool InRanges(int[] starts, int[] ends, int cp)
    {
        int index = Array.BinarySearch(starts, cp);
        if (index >= 0)
        {
            return true;
        }

        int candidate = ~index - 1; // the last range starting below cp
        return candidate >= 0 && cp <= ends[candidate];
    }

    // Splits UTF-16 into code points. A surrogate without its partner stays as its own value.
    private static int[] ToCodePoints(string text)
    {
        var codePoints = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoints.Add(char.ConvertToUtf32(c, text[i + 1]));
                i++;
            }
            else
            {
                codePoints.Add(c);
            }
        }

        return [.. codePoints];
    }

    private static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp > 0xFFFF)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
        else
        {
            sb.Append((char)cp); // includes lone surrogates, appended unchanged
        }
    }
}
