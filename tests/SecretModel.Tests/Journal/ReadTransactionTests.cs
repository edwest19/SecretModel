// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for JournalFile.ReadTransaction, which reads the body of a score request (README §3.2):
// the rules of a journal line (docs/formats/journal-v1.md) with seven properties and no seq,
// including the card-number scan (README SM-PRIV-001, SM-PRIV-002). The journal's own tests cover
// each rule in depth; these show that the same rules apply here and that seq is gone. All tokens
// are made up. 4111111111111111 is the published Visa test number, used only as an input that
// must be refused.

using System.Text;
using SecretModel.Core.Journal;
using Reason = SecretModel.Core.Journal.JournalQuarantineReason;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class ReadTransactionTests
{
    private const string Txn = "tok_txn_feedfacefeedfacefeed0001";
    private const string Cus = "tok_cus_c0ffeec0ffeec0ffeec0ffee";
    private const string Card = "tok_card_cafecafecafecafecafecafe";

    // A well-formed token whose hex part starts with 13 decimal digits: exempt from the scan.
    private const string CardWithDigits = "tok_card_1234567890123abcdefabcde";

    private static readonly (string Name, string Json)[] Valid =
    [
        ("transaction_token", $"\"{Txn}\""),
        ("customer_token", $"\"{Cus}\""),
        ("card_token", $"\"{Card}\""),
        ("amount_bin", "3"),
        ("merchant_category", "\"grocery\""),
        ("hour", "14"),
        ("is_online", "false"),
    ];

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    public void ReadTransaction_AValidTransaction_GivesItsValues()
    {
        TransactionReadResult result = Read(Body());

        Assert.AreEqual(new TransactionRequest(Txn, Cus, Card, 3, "grocery", 14, false), result.Transaction);
        Assert.IsNull(result.Reason);
        Assert.IsFalse(result.HasLongDigitRun);
    }

    [TestMethod]
    public void ReadTransaction_PropertiesInAnyOrderOverSeveralLines_AreRead()
    {
        string body = "{\n  \"is_online\": true,\r\n  \"hour\": 0,\n  \"merchant_category\": \"atm\",\n  \"amount_bin\": 7,\n" +
            $"  \"card_token\": \"{CardWithDigits}\",\n  \"customer_token\": \"{Cus}\",\n  \"transaction_token\": \"{Txn}\"\n}}\n";

        Assert.AreEqual(new TransactionRequest(Txn, Cus, CardWithDigits, 7, "atm", 0, true), Read(body).Transaction);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [DataRow("seq", "1")]
    [DataRow("client_id", "\"client-b\"")]
    [DataRow("country_code", "\"US\"")]
    public void ReadTransaction_AnyOtherProperty_IsUnknown(string name, string json) =>
        AssertReason(Reason.UnknownProperty, Body(extra: (name, json)));

    [TestMethod]
    [DataRow("transaction_token")]
    [DataRow("customer_token")]
    [DataRow("card_token")]
    [DataRow("amount_bin")]
    [DataRow("merchant_category")]
    [DataRow("hour")]
    [DataRow("is_online")]
    public void ReadTransaction_AMissingProperty_IsMissing(string name) =>
        AssertReason(Reason.MissingProperty, Body(without: name));

    [TestMethod]
    [TestCategory("SM-PRIV-001")]
    [DataRow("transaction_token", "\"tok_cus_c0ffeec0ffeec0ffeec0ffee\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"tok_txn_feedfacefeedfacefeed0001\\n\"", Reason.InvalidTransactionToken)]
    [DataRow("customer_token", "\"tok_cus_C0FFEEC0FFEEC0FFEEC0FFEE\"", Reason.InvalidCustomerToken)]
    [DataRow("card_token", "\"tok_card_cafe\"", Reason.InvalidCardToken)]
    [DataRow("amount_bin", "8", Reason.InvalidAmountBin)]
    [DataRow("amount_bin", "3.0", Reason.InvalidAmountBin)]
    [DataRow("merchant_category", "\"Grocery\"", Reason.InvalidMerchantCategory)]
    [DataRow("hour", "24", Reason.InvalidHour)]
    [DataRow("is_online", "\"false\"", Reason.InvalidIsOnline)]
    public void ReadTransaction_APropertyThatBreaksItsRule_GivesItsReason(string name, string json, Reason expected) =>
        AssertReason(expected, Body(replace: (name, json)));

    [TestMethod]
    public void ReadTransaction_TheFirstBrokenRule_IsTheReason()
    {
        string body = Body(replace: ("hour", "24")).Replace("\"grocery\"", "\"Grocery\"", StringComparison.Ordinal);

        AssertReason(Reason.InvalidMerchantCategory, body);
    }

    [TestMethod]
    [DataRow("", Reason.BlankLine)]
    [DataRow(" \t ", Reason.BlankLine)]
    [DataRow("{", Reason.NotJson)]
    [DataRow("{} {}", Reason.NotJson)]
    [DataRow("[]", Reason.NotAnObject)]
    [DataRow("{\"hour\":1,\"hour\":2}", Reason.DuplicateProperty)]
    [DataRow("{\"hour\":\"\\ud800\"}", Reason.InvalidText)]
    public void ReadTransaction_ABodyThatIsNotATransaction_GivesTheJournalsReason(string body, Reason expected) =>
        AssertReason(expected, body);

    [TestMethod]
    public void ReadTransaction_InvalidUtf8_IsInvalidText() =>
        AssertReason(Reason.InvalidText, [.. Encoding.UTF8.GetBytes("{\"hour\":\""), 0xFF, .. Encoding.UTF8.GetBytes("\"}")]);

    [TestMethod]
    public void ReadTransaction_AByteOrderMark_IsNotJson() =>
        AssertReason(Reason.NotJson, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Body())]);

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [DataRow("card_token", "\"tok_4111111111111111\"")]
    [DataRow("card_token", "\"tok_card_4111111111111111\"")]
    [DataRow("transaction_token", "\"4111111111111111\"")]
    [DataRow("transaction_token", "\"4111 1111 1111 1111\"")]
    [DataRow("customer_token", "\"4111-1111-1111-1111\"")]
    [DataRow("hour", "4111111111111111")]
    [DataRow("amount_bin", "[4111,1111,1111,1111]")]
    [DataRow("merchant_category", "\"\\u0034111111111111111\"")]
    public void ReadTransaction_APossibleCardNumberInAProperty_IsRefusedBeforeAnyRule(string name, string json)
    {
        TransactionReadResult result = Read(Body(replace: (name, json)));

        Assert.IsTrue(result.HasLongDigitRun);
        Assert.IsNull(result.Transaction);
        Assert.IsNull(result.Reason);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [DataRow("seq", "4111111111111111")]
    [DataRow("pan", "\"4111111111111111\"")]
    [DataRow("4111111111111111", "1")]
    public void ReadTransaction_APossibleCardNumberInAnExtraProperty_IsRefused(string name, string json) =>
        Assert.IsTrue(Read(Body(extra: (name, json))).HasLongDigitRun);

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [DataRow("{\"card_token\":\"4111111111111111\"")]
    [DataRow("4111111111111111")]
    [DataRow("{\"card_token\":\"4111111\\n111111111\" oops")]
    public void ReadTransaction_APossibleCardNumberInABodyThatIsNotJson_IsRefused(string body) =>
        Assert.IsTrue(Read(body).HasLongDigitRun);

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    public void ReadTransaction_TwelveDigitsInARow_AreNotACardNumber() =>
        AssertReason(Reason.InvalidHour, Body(replace: ("hour", "411111111111")));

    private static TransactionReadResult Read(string body) => JournalFile.ReadTransaction(Encoding.UTF8.GetBytes(body));

    private static void AssertReason(Reason expected, string body) => AssertReason(expected, Encoding.UTF8.GetBytes(body));

    private static void AssertReason(Reason expected, byte[] body)
    {
        TransactionReadResult result = JournalFile.ReadTransaction(body);

        Assert.AreEqual(expected, result.Reason);
        Assert.IsNull(result.Transaction);
        Assert.IsFalse(result.HasLongDigitRun);
    }

    private static string Body(string? without = null, (string Name, string Json)? replace = null, (string Name, string Json)? extra = null)
    {
        IEnumerable<string> parts = Valid
            .Where(p => p.Name != without)
            .Select(p => $"\"{p.Name}\":{(replace is { } r && r.Name == p.Name ? r.Json : p.Json)}");
        if (extra is { } e)
        {
            parts = parts.Append($"\"{e.Name}\":{e.Json}");
        }

        return "{" + string.Join(",", parts) + "}";
    }
}
