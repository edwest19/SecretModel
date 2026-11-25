// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README SM-TOK-008: ReferenceText.Lower must lowercase exactly as Python's str.lower().
// The expected hashes come from running Python (tools/golden/generate_golden_vectors.py); the tests
// rebuild the same lines from the C# and compare. CI runs them on Linux and Windows.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Text;

namespace SecretModel.Tests.Text;

[TestClass]
public sealed class ReferenceTextTests
{
    private const string Sigma = "Σ";

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    public void EveryCodePoint_LowercasesAsPythonDoes()
    {
        var lines = new StringBuilder();
        foreach (int cp in AllCodePoints())
        {
            lines.Append($"{cp:X} {Hex(ReferenceText.Lower(char.ConvertFromUtf32(cp)))}\n");
        }

        Assert.AreEqual(GoldenVectors.Lowercase().SingleCodePointSha256, Sha256(lines));
    }

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    public void FinalSigmaRule_MatchesPython_NextToEveryCodePoint()
    {
        var lines = new StringBuilder();
        foreach (int cp in AllCodePoints())
        {
            string c = char.ConvertFromUtf32(cp);
            lines.Append($"{cp:X} {Hex(ReferenceText.Lower(c + Sigma))}|{Hex(ReferenceText.Lower("A" + c + Sigma))}|")
                 .Append($"{Hex(ReferenceText.Lower("A" + Sigma + c))}|{Hex(ReferenceText.Lower("A" + Sigma + c + "b"))}\n");
        }

        Assert.AreEqual(GoldenVectors.Lowercase().SigmaContextSha256, Sha256(lines));
    }

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    public void RandomMixedStrings_LowercaseAsPythonDoes()
    {
        IReadOnlyList<GoldenVectors.LowercaseVector> vectors = GoldenVectors.Lowercase().Vectors;
        Assert.HasCount(400, vectors, "The golden file should hold 400 strings.");

        var mismatches = new List<string>();
        foreach (GoldenVectors.LowercaseVector v in vectors)
        {
            string actual = ReferenceText.Lower(v.Input);
            if (actual != v.Lower)
            {
                mismatches.Add($"[{Hex(v.Input)}]: expected [{Hex(v.Lower)}], got [{Hex(actual)}]");
            }
        }

        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    public void TableUnicodeVersion_MatchesTheGoldenFile()
    {
        string tableVersion = PythonLowercaseData.UnicodeVersion;
        Assert.AreEqual(GoldenVectors.Lowercase().UnicodeVersion, tableVersion);
    }

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    [DataRow("ΟΔΟΣ", "οδος")]   // ΟΔΟΣ -> οδος, final sigma
    [DataRow("ΣΟΣ", "σος")]               // ΣΟΣ -> σος, first sigma not final
    [DataRow("İ", "i̇")]                                      // İ -> i + combining dot
    public void KnownCases(string input, string expected)
    {
        Assert.AreEqual(expected, ReferenceText.Lower(input));
    }

    [TestMethod]
    [TestCategory("SM-TOK-008")]
    public void LoneSurrogates_AreLeftUnchanged_AsPythonLeavesThem()
    {
        // Not a [DataRow]: attribute strings are stored as UTF-8, which turns a lone surrogate into
        // U+FFFD, so a DataRow version of this test compared U+FFFD with U+FFFD and could not fail.
        Assert.AreEqual("\uD800", ReferenceText.Lower("\uD800"));
        Assert.AreEqual("a\uDC00b", ReferenceText.Lower("A\uDC00B")); // Python: 'A\udc00B'.lower() == 'a\udc00b'
    }

    private static IEnumerable<int> AllCodePoints()
    {
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is < 0xD800 or > 0xDFFF)
            {
                yield return cp;
            }
        }
    }

    private static string Hex(string s)
    {
        var parts = new List<string>();
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                parts.Add(char.ConvertToUtf32(s[i], s[i + 1]).ToString("X"));
                i++;
            }
            else
            {
                parts.Add(((int)s[i]).ToString("X"));
            }
        }

        return string.Join(' ', parts);
    }

    private static string Sha256(StringBuilder lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(lines.ToString())));
}
