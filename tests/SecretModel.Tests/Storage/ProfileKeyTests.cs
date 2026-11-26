// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for the storage key: README SM-STOR-001 (path from a client hash and the tokens only),
// SM-STOR-006 (client in every key) and SM-PRIV-004 (rejections don't repeat the value).

using SecretModel.Core.Storage;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class ProfileKeyTests
{
    // Synthetic tokens in the right shape (SM-PRIV-001); they stand for no real customer or card.
    internal const string Customer = "tok_cus_0123456789abcdef01234567";
    internal const string Card = "tok_card_fedcba9876543210fedcba98";

    // SHA-256("abc"), the example in FIPS 180-2, Appendix B.1.
    private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public void RelativePath_IsTheClientHashThenTheCustomerThenTheCard()
    {
        var key = new ProfileKey("abc", Customer, Card);

        Assert.AreEqual(Sha256OfAbc, key.ClientHash);
        Assert.AreEqual($"{Sha256OfAbc}/{Customer}/{Card}.profile.json", key.RelativePath);
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public void RelativePath_NeverHoldsTheClientId()
    {
        var key = new ProfileKey("CN=home-client-01", Customer, Card);

        Assert.DoesNotContain("home-client-01", key.RelativePath);
        Assert.AreEqual(64, key.ClientHash.Length);
        Assert.IsTrue(key.ClientHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), key.ClientHash);
    }

    [TestMethod]
    [TestCategory("SM-STOR-006")]
    public void DifferentClients_WithTheSameTokens_GetDifferentPaths()
    {
        var a = new ProfileKey("client-a", Customer, Card);
        var b = new ProfileKey("client-b", Customer, Card);

        Assert.AreNotEqual(a.RelativePath, b.RelativePath);
        Assert.AreNotEqual(a, b);
        Assert.AreEqual(a, new ProfileKey("client-a", Customer, Card));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [DataRow("")]
    [DataRow("   ")]
    public void EmptyClientId_IsRejected(string clientId)
    {
        ArgumentException e = Assert.ThrowsExactly<ArgumentException>(() => new ProfileKey(clientId, Customer, Card));
        Assert.AreEqual("clientId", e.ParamName);
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [TestCategory("SM-PRIV-004")]
    [DataRow("4111111111111111")]                       // a published test card number
    [DataRow("tok_4111111111111111")]
    [DataRow("..")]
    [DataRow("../../tok_cus_0123456789abcdef01234567")]
    [DataRow("tok_cus_0123456789abcdef01234567/..")]
    [DataRow("tok_cus_0123456789ABCDEF01234567")]       // uppercase hex
    [DataRow("tok_card_fedcba9876543210fedcba98")]      // a valid token, but a card token
    [DataRow("")]
    public void BadCustomerToken_IsRejected_WithoutRepeatingIt(string value)
    {
        ArgumentException e = Assert.ThrowsExactly<ArgumentException>(() => new ProfileKey("abc", value, Card));
        Assert.AreEqual("The customer token must be a valid tok_cus_ token. (Parameter 'customerToken')", e.Message);
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [TestCategory("SM-PRIV-004")]
    [DataRow("4111111111111111")]
    [DataRow("tok_card_4111111111111111")]
    [DataRow("../tok_card_fedcba9876543210fedcba98")]
    [DataRow("tok_card_fedcba9876543210fedcba98\n")]
    [DataRow("tok_cus_0123456789abcdef01234567")]       // a valid token, but a customer token
    public void BadCardToken_IsRejected_WithoutRepeatingIt(string value)
    {
        ArgumentException e = Assert.ThrowsExactly<ArgumentException>(() => new ProfileKey("abc", Customer, value));
        Assert.AreEqual("The card token must be a valid tok_card_ token. (Parameter 'cardToken')", e.Message);
    }
}
