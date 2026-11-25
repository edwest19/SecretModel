// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README SM-PRIV-001 and SM-PRIV-002. Card numbers here are published test numbers.

using System.Text;
using SecretModel.Core.Privacy;
using SecretModel.Core.Tokenization;

namespace SecretModel.Tests.Privacy;

[TestClass]
public sealed class TokenValidatorTests
{
    private const string Hex24 = "0123456789abcdef01234567";

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    public void TokensFromTheTokenizer_AreValid()
    {
        var tokenizer = new ClientTokenizer(Encoding.ASCII.GetBytes("SecretModel validator-test secret, 40 by"));
        foreach (string token in new[]
                 {
                     tokenizer.TokenizeCard("4111111111111111"),
                     tokenizer.TokenizeCustomer("cust-0001"),
                     tokenizer.TokenizeMerchant("Acme Grocery"),
                 })
        {
            Assert.AreEqual(TokenVerdict.Valid, TokenValidator.Check(token), token);
        }
    }

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    [DataRow("tok_card_" + Hex24)]
    [DataRow("tok_cus_" + Hex24)]
    [DataRow("tok_txn_" + Hex24)]
    [DataRow("tok_merch_" + Hex24)]
    [DataRow("tok_card_123456789012345678901234")] // 24 digits is still a valid 24-hex token
    public void WellFormedTokens_AreValid(string value)
    {
        Assert.AreEqual(TokenVerdict.Valid, TokenValidator.Check(value));
        Assert.IsTrue(TokenValidator.IsValid(value));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    [DataRow("")]
    [DataRow("tok_")]
    [DataRow("tok_card_")]
    [DataRow("tok_card_0123456789abcdef0123456")]      // 23 hex
    [DataRow("tok_card_0123456789abcdef012345678")]    // 25 hex
    [DataRow("tok_card_0123456789ABCDEF01234567")]     // uppercase hex
    [DataRow("tok_card_0123456789abcdeg01234567")]     // 'g' is not hex
    [DataRow("tok_acct_0123456789abcdef01234567")]     // unknown domain
    [DataRow("TOK_card_0123456789abcdef01234567")]     // prefix is case-sensitive
    [DataRow(" tok_card_0123456789abcdef01234567")]    // leading space
    [DataRow("tok_card_0123456789abcdef01234567 ")]    // trailing space
    [DataRow("tok_card_0123456789abcdef01234567\n")]   // trailing newline
    [DataRow("tok_card_0123456789abcdef01234567_x")]   // extra suffix
    [DataRow("tok_card_abc")]                          // the prototype only checked "tok_" and length >= 20
    public void MalformedValues_AreRejected(string value)
    {
        Assert.AreEqual(TokenVerdict.Malformed, TokenValidator.Check(value));
        Assert.IsFalse(TokenValidator.IsValid(value));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    public void Null_IsRejected()
    {
        Assert.AreEqual(TokenVerdict.Malformed, TokenValidator.Check(null));
        Assert.IsFalse(TokenValidator.IsValid(null));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [DataRow("4111111111111111")]                 // raw
    [DataRow("4111 1111 1111 1111")]              // spaced
    [DataRow("4111-1111-1111-1111")]              // hyphenated
    [DataRow("tok_4111111111111111")]             // the bypass the prototype accepted
    [DataRow("tok_1111111111111111")]             // the prototype accepted this too
    [DataRow("tok_4111-1111-1111-1111")]
    [DataRow("tok_card_4111111111111111")]        // a domain in front of a card number
    [DataRow("4111.1111.1111.1111")]              // dots
    [DataRow("4111/1111/1111/1111")]              // slashes
    [DataRow("4111 1111 1111 1111")] // no-break spaces
    [DataRow("4111​1111​1111​1111")] // zero-width spaces
    [DataRow("４１１１１１１１１１１１１１１１")] // full-width digits
    [DataRow("4222222222222")]                    // 13 digits (shortest)
    [DataRow("6011111111111111117")]              // 19 digits
    [DataRow("41111111111111111111")]             // 20 digits still contains a 13-19 digit run
    public void ValuesThatMayCarryACardNumber_AreFlagged(string value)
    {
        Assert.AreEqual(TokenVerdict.LongDigitRun, TokenValidator.Check(value));
        Assert.IsFalse(TokenValidator.IsValid(value));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    public void DigitsOutsideTheBasicPlane_CountToo()
    {
        // MATHEMATICAL BOLD DIGIT FOUR / ONE (U+1D7D2, U+1D7CF): 16 of them, as surrogate pairs.
        // In the method body, not a [DataRow], so the surrogate pairs reach the test exactly.
        string boldDigits = "\U0001D7D2" + string.Concat(Enumerable.Repeat("\U0001D7CF", 15));
        Assert.AreEqual(TokenVerdict.LongDigitRun, TokenValidator.Check(boldDigits));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [DataRow("411111111111")]               // 12 digits
    [DataRow("4111 1111 1111")]             // 12 digits with spaces
    [DataRow("4111a1111b1111c1111")]        // letters break the run
    [DataRow("order 123456 item 7890123")]  // separate numbers, separated by letters
    public void ShorterDigitRuns_AreMalformedNotFlagged(string value)
    {
        Assert.AreEqual(TokenVerdict.Malformed, TokenValidator.Check(value));
    }
}
