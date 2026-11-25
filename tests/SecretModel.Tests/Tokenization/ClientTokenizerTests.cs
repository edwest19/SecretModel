// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README §4 (SM-TOK). Card values used here are published test numbers, not real cards.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SecretModel.Core.Tokenization;

namespace SecretModel.Tests.Tokenization;

[TestClass]
public sealed class ClientTokenizerTests
{
    private static readonly byte[] SecretA = Encoding.ASCII.GetBytes("SecretModel unit-test secret A, 40 bytes");
    private static readonly byte[] SecretB = Encoding.ASCII.GetBytes("SecretModel unit-test secret B, 40 bytes");

    [TestMethod]
    [TestCategory("SM-TOK-007")]
    public void Tokens_MatchThePythonReferenceGoldenVectors()
    {
        GoldenVectors.TokenizerFile golden = GoldenVectors.Tokenizer();
        Assert.HasCount(24, golden.Vectors, "The golden file should hold 24 vectors.");
        var tokenizer = new ClientTokenizer(Convert.FromHexString(golden.SecretHex));

        var mismatches = new List<string>();
        foreach (GoldenVectors.TokenVector v in golden.Vectors)
        {
            string actual = v.Method switch
            {
                "card" => tokenizer.TokenizeCard(v.Input),
                "customer" => tokenizer.TokenizeCustomer(v.Input),
                "merchant" => tokenizer.TokenizeMerchant(v.Input),
                _ => throw new InvalidDataException($"Unknown method '{v.Method}' in tokenizer.json."),
            };
            if (actual != v.Token)
            {
                mismatches.Add($"{v.Method} \"{Escape(v.Input)}\": expected {v.Token}, got {actual}");
            }
        }

        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    [TestMethod]
    [TestCategory("SM-TOK-007")]
    public void TextThatCannotBeEncoded_IsRejected_AsInTheReference()
    {
        // Python's str.encode() raises UnicodeEncodeError on a lone surrogate; so must we.
        var tokenizer = new ClientTokenizer(SecretA);
        Assert.ThrowsExactly<EncoderFallbackException>(() => tokenizer.TokenizeCustomer("\uD800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => tokenizer.TokenizeMerchant("Acme \uD800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => tokenizer.TokenizeCard("4111\uDC00"));
    }

    [TestMethod]
    [TestCategory("SM-TOK-001")]
    public void Token_IsDomainPrefixPlusFirst24HexOfHmacSha256()
    {
        var tokenizer = new ClientTokenizer(SecretA);
        var format = new Regex("^tok_(card|cus|merch)_[0-9a-f]{24}$");

        string card = tokenizer.TokenizeCard("4111111111111111");
        string customer = tokenizer.TokenizeCustomer("cust-0001");
        string merchant = tokenizer.TokenizeMerchant("acme grocery");

        Assert.AreEqual(ExpectedToken("card", "4111111111111111"), card);
        Assert.AreEqual(ExpectedToken("cus", "cust-0001"), customer);
        Assert.AreEqual(ExpectedToken("merch", "acme grocery"), merchant);
        foreach (string token in new[] { card, customer, merchant })
        {
            Assert.IsTrue(format.IsMatch(token), $"'{token}' does not match {format}.");
        }
    }

    [TestMethod]
    [TestCategory("SM-TOK-002")]
    public void SameSecretAndInput_GiveTheSameToken_AcrossInstances()
    {
        var first = new ClientTokenizer((byte[])SecretA.Clone());
        var second = new ClientTokenizer((byte[])SecretA.Clone());

        Assert.AreEqual(first.TokenizeCard("5555555555554444"), second.TokenizeCard("5555555555554444"));
        Assert.AreEqual(first.TokenizeCustomer("cust-0042"), second.TokenizeCustomer("cust-0042"));
        Assert.AreEqual(first.TokenizeMerchant("Acme Grocery"), second.TokenizeMerchant("Acme Grocery"));
    }

    [TestMethod]
    [TestCategory("SM-TOK-003")]
    public void DifferentSecret_GivesADifferentToken()
    {
        var a = new ClientTokenizer(SecretA);
        var b = new ClientTokenizer(SecretB);

        Assert.AreNotEqual(a.TokenizeCard("4111111111111111"), b.TokenizeCard("4111111111111111"));
        Assert.AreNotEqual(a.TokenizeCustomer("cust-0001"), b.TokenizeCustomer("cust-0001"));
        Assert.AreNotEqual(a.TokenizeMerchant("acme grocery"), b.TokenizeMerchant("acme grocery"));
    }

    [TestMethod]
    [TestCategory("SM-TOK-004")]
    public void SameInputInDifferentDomains_GivesDifferentHashes()
    {
        // Compare only the 24-hex part: the prefixes always differ, so comparing whole tokens would
        // pass even if the domain were left out of the HMAC input.
        var tokenizer = new ClientTokenizer(SecretA);
        const string input = "12345";

        string card = HexPart(tokenizer.TokenizeCard(input));
        string customer = HexPart(tokenizer.TokenizeCustomer(input));
        string merchant = HexPart(tokenizer.TokenizeMerchant(input));

        Assert.AreNotEqual(card, customer);
        Assert.AreNotEqual(card, merchant);
        Assert.AreNotEqual(customer, merchant);
    }

    [TestMethod]
    [TestCategory("SM-TOK-005")]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(31)]
    public void SecretShorterThan32Bytes_IsRejected(int length)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ClientTokenizer(new byte[length]));
    }

    [TestMethod]
    [TestCategory("SM-TOK-005")]
    public void SecretOfExactly32Bytes_IsAccepted_AndNullIsRejected()
    {
        _ = new ClientTokenizer(new byte[32]);
        Assert.ThrowsExactly<ArgumentNullException>(() => new ClientTokenizer(null!));
    }

    [TestMethod]
    [TestCategory("SM-TOK-006")]
    [DataRow("4111 1111 1111 1111")]
    [DataRow("4111-1111-1111-1111")]
    [DataRow("4111 - 1111-1111 1111")]
    public void CardSpacesAndHyphens_AreRemovedBeforeHashing(string grouped)
    {
        var tokenizer = new ClientTokenizer(SecretA);
        Assert.AreEqual(tokenizer.TokenizeCard("4111111111111111"), tokenizer.TokenizeCard(grouped));
    }

    [TestMethod]
    [TestCategory("SM-TOK-006")]
    public void CustomerIds_AreNotNormalized()
    {
        // Only card values are normalized, as in the reference.
        var tokenizer = new ClientTokenizer(SecretA);
        Assert.AreNotEqual(tokenizer.TokenizeCustomer("cust0001"), tokenizer.TokenizeCustomer("cust 0001"));
        Assert.AreNotEqual(tokenizer.TokenizeCustomer("cust-0001"), tokenizer.TokenizeCustomer("CUST-0001"));
    }

    private static string ExpectedToken(string domain, string value)
    {
        byte[] mac = HMACSHA256.HashData(SecretA, Encoding.UTF8.GetBytes($"{domain}:{value}"));
        return $"tok_{domain}_{Convert.ToHexStringLower(mac)[..24]}";
    }

    private static string HexPart(string token) => token[(token.LastIndexOf('_') + 1)..];

    private static string Escape(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));
}
