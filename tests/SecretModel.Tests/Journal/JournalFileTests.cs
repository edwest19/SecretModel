// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.
//
// Tests for the journal file reader (docs/formats/journal-v1.md): README SM-JRNL-001 (newline-
// delimited UTF-8 JSON), SM-JRNL-002 (a malformed record is quarantined with its line number and
// reason, and the rest carries on) and SM-JRNL-005 (a file that may carry a card number is rejected
// whole), which applies SM-PRIV-002 to whole lines and values.
//
// The known answer is the example in docs/formats/journal-v1.md. Its byte count and SHA-256 were
// computed by Python from the same lines, not by the C# under test; the expected records and reasons
// follow from the format's rules by hand. All tokens are made up. 4111111111111111 is the published
// Visa test number, used only as an input that must be rejected.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Features;
using SecretModel.Core.Journal;
using Reason = SecretModel.Core.Journal.JournalQuarantineReason;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class JournalFileTests
{
    private const string Cus = "tok_cus_c0ffeec0ffeec0ffeec0ffee";
    private const string CardA = "tok_card_cafecafecafecafecafecafe";
    private const string CardB = "tok_card_beefbeefbeefbeefbeefbeef";
    private const string Txn1 = "tok_txn_feedfacefeedfacefeed0001";
    private const string Txn2 = "tok_txn_feedfacefeedfacefeed0002";
    private const string Txn6 = "tok_txn_feedfacefeedfacefeed0006";

    // A well-formed token whose hex part starts with 13 decimal digits.
    private const string CardWithDigits = "tok_card_1234567890123abcdefabcde";

    // The known answer from docs/formats/journal-v1.md: 7 lines, each ending in LF; line 3 is empty.
    internal const string DocumentedExample =
        "{\"seq\":1,\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0001\",\"customer_token\":\"tok_cus_c0ffeec0ffeec0ffeec0ffee\",\"card_token\":\"tok_card_cafecafecafecafecafecafe\",\"amount_bin\":3,\"merchant_category\":\"grocery\",\"hour\":14,\"is_online\":false}\n" +
        "{\"card_token\":\"tok_card_beefbeefbeefbeefbeefbeef\",\"customer_token\":\"tok_cus_c0ffeec0ffeec0ffeec0ffee\",\"seq\":2,\"is_online\":true,\"hour\":23,\"merchant_category\":\"online_market\",\"amount_bin\":7,\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0002\"}\n" +
        "\n" +
        "{\"seq\":3,\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0003\",\"customer_token\":\"tok_cus_c0ffeec0ffeec0ffeec0ffee\",\"card_token\":\"tok_card_cafecafecafecafecafecafe\",\"amount_bin\":3,\"merchant_category\":\"Grocery\",\"hour\":9,\"is_online\":false}\n" +
        "{\"seq\":4,\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0004\",\"customer_token\":\"tok_cus_c0ffeec0ffeec0ffeec0ffee\",\"card_token\":\"tok_card_cafecafecafecafecafecafe\",\"amount_bin\":2,\"merchant_category\":\"gas\",\"hour\":24,\"is_online\":false}\n" +
        "{\"seq\":5,\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0005\",\"customer_token\":\"tok_cus_c0ffeec0ffeec0ffeec0ffee\",\"card_token\":\"tok_card_cafecafecafecafecafecafe\",\"amount_bin\":1,\"merchant_category\":\"restaurant\",\"hour\":19,\"is_online\":false,\"country_code\":\"US\"}\n" +
        "{\"seq\": 999999999999, \"transaction_token\": \"tok_txn_feedfacefeedfacefeed0006\", \"customer_token\": \"tok_cus_c0ffeec0ffeec0ffeec0ffee\", \"card_token\": \"tok_card_beefbeefbeefbeefbeefbeef\", \"amount_bin\": 0, \"merchant_category\": \"atm\", \"hour\": 0, \"is_online\": false}\n";

    private static readonly JournalRecord[] DocumentedRecords =
    [
        new(1, 1, Txn1, Cus, CardA, 3, "grocery", 14, false),
        new(2, 2, Txn2, Cus, CardB, 7, "online_market", 23, true),
        new(7, 999_999_999_999, Txn6, Cus, CardB, 0, "atm", 0, false),
    ];

    private static readonly QuarantinedLine[] DocumentedQuarantine =
    [
        new(3, Reason.BlankLine),
        new(4, Reason.InvalidMerchantCategory),
        new(5, Reason.InvalidHour),
        new(6, Reason.UnknownProperty),
    ];

    // A valid record's properties as raw JSON, in the format's table order.
    private static readonly (string Name, string Json)[] ValidProperties =
    [
        ("seq", "1"),
        ("transaction_token", "\"" + Txn1 + "\""),
        ("customer_token", "\"" + Cus + "\""),
        ("card_token", "\"" + CardA + "\""),
        ("amount_bin", "3"),
        ("merchant_category", "\"grocery\""),
        ("hour", "14"),
        ("is_online", "false"),
    ];

    /// <summary>
    /// A valid record line with changes: a known name replaces that property's raw JSON (null removes
    /// it); any other name adds a property, and a leading "+" adds a known name a second time.
    /// </summary>
    private static string Record(params (string Name, string? Json)[] changes)
    {
        var parts = new List<string>();
        foreach ((string name, string json) in ValidProperties)
        {
            int i = Array.FindIndex(changes, c => c.Name == name);
            string? value = i < 0 ? json : changes[i].Json;
            if (value is not null)
            {
                parts.Add($"\"{name}\":{value}");
            }
        }

        foreach ((string name, string? json) in changes)
        {
            if (!ValidProperties.Any(p => p.Name == name))
            {
                parts.Add($"\"{name.TrimStart('+')}\":{json}");
            }
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static JournalReadResult ReadText(string text) => JournalFile.Read(Encoding.UTF8.GetBytes(text));

    private static JournalReadResult ReadLines(params string[] lines) => ReadText(string.Concat(lines.Select(l => l + "\n")));

    private static JournalRecord ValidRecordAt(int lineNumber) => new(lineNumber, 1, Txn1, Cus, CardA, 3, "grocery", 14, false);

    private static void AssertRejectedAt(int lineNumber, JournalReadResult result)
    {
        Assert.IsTrue(result.IsRejected, "The file should be rejected whole.");
        Assert.AreEqual(lineNumber, result.RejectedLineNumber);
        Assert.IsEmpty(result.Records, "A rejected file gives no records.");
        Assert.IsEmpty(result.Quarantined, "A rejected file quarantines no single lines.");
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    // ----- The known answer -----

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    [TestCategory("SM-JRNL-002")]
    public void Read_TheDocumentedExample_GivesTheDocumentedResult()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DocumentedExample);
        Assert.HasCount(1479, bytes, "The document says 1,479 bytes.");
        Assert.AreEqual(
            "1771857b18f0e12adf90d603a0e1bca04eb3eb3ef1191432c8306d42563a1852",
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            "The document's SHA-256, computed by Python.");

        JournalReadResult result = JournalFile.Read(bytes);

        Assert.IsFalse(result.IsRejected);
        Assert.AreEqual(0, result.RejectedLineNumber);
        CollectionAssert.AreEqual(DocumentedRecords, result.Records.ToArray());
        CollectionAssert.AreEqual(DocumentedQuarantine, result.Quarantined.ToArray());
    }

    // ----- SM-JRNL-001: lines and encoding -----

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    public void Read_CrlfLineEndings_ReadTheSameAsLf()
    {
        JournalReadResult result = ReadText(DocumentedExample.Replace("\n", "\r\n", StringComparison.Ordinal));

        CollectionAssert.AreEqual(DocumentedRecords, result.Records.ToArray());
        CollectionAssert.AreEqual(DocumentedQuarantine, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    public void Read_ALastLineWithoutANewline_IsRead()
    {
        JournalReadResult result = ReadText(DocumentedExample[..^1]);

        CollectionAssert.AreEqual(DocumentedRecords, result.Records.ToArray());
        CollectionAssert.AreEqual(DocumentedQuarantine, result.Quarantined.ToArray());
    }

    // "R" stands for a valid record. A newline at the very end does not start another line.
    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    [DataRow("", 0, 0)]
    [DataRow("\n", 0, 1)]
    [DataRow("\n\n", 0, 2)]
    [DataRow("\r\n", 0, 1)]
    [DataRow("R", 1, 0)]
    [DataRow("R\n", 1, 0)]
    [DataRow("R\r\n", 1, 0)]
    [DataRow("R\nR", 2, 0)]
    [DataRow("R\n\n", 1, 1)]
    [DataRow("\nR", 1, 1)]
    public void Read_CountsLinesAsDocumented(string pattern, int records, int blankLines)
    {
        JournalReadResult result = ReadText(pattern.Replace("R", Record(), StringComparison.Ordinal));

        Assert.HasCount(records, result.Records);
        Assert.HasCount(blankLines, result.Quarantined);
        Assert.IsTrue(result.Quarantined.All(q => q.Reason == Reason.BlankLine));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    public void Read_AByteOrderMark_MakesLine1NotJson()
    {
        byte[] file = Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes(Record() + "\n" + Record() + "\n"));

        JournalReadResult result = JournalFile.Read(file);

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.NotJson) }, result.Quarantined.ToArray());
        CollectionAssert.AreEqual(new[] { ValidRecordAt(2) }, result.Records.ToArray());
    }

    // Bytes put in the middle of "grocery": a lone byte, a stray continuation byte, an overlong
    // encoding, an encoded surrogate, a truncated sequence, and a code point above U+10FFFF.
    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    [DataRow("FF")]
    [DataRow("80")]
    [DataRow("C0AF")]
    [DataRow("EDA080")]
    [DataRow("E282")]
    [DataRow("F4908080")]
    public void Read_InvalidUtf8_IsQuarantinedAsInvalidText(string hex)
    {
        string[] halves = Record().Split("grocery");
        byte[] line = Concat(Encoding.UTF8.GetBytes(halves[0] + "gro"), Convert.FromHexString(hex), Encoding.UTF8.GetBytes("cery" + halves[1]));
        byte[] file = Concat(Encoding.UTF8.GetBytes(Record() + "\n"), line, Encoding.UTF8.GetBytes("\n" + Record() + "\n"));

        JournalReadResult result = JournalFile.Read(file);

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(2, Reason.InvalidText) }, result.Quarantined.ToArray());
        CollectionAssert.AreEqual(new[] { ValidRecordAt(1), ValidRecordAt(3) }, result.Records.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    public void Read_InvalidUtf8OutsideAString_IsQuarantinedAsInvalidText()
    {
        // The parser would call this line NotJson; the reader checks the encoding first.
        byte[] file = Concat(Encoding.UTF8.GetBytes(Record()), [0xFF], Encoding.UTF8.GetBytes("\n" + Record() + "\n"));

        JournalReadResult result = JournalFile.Read(file);

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.InvalidText) }, result.Quarantined.ToArray());
        CollectionAssert.AreEqual(new[] { ValidRecordAt(2) }, result.Records.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-001")]
    public void Read_ValidNonAsciiUtf8_IsNotInvalidText()
    {
        // "é" and an emoji are valid UTF-8, so the line gets as far as the category rule.
        JournalReadResult result = ReadLines(Record(("merchant_category", "\"caf\u00e9 \U0001F600\"")));

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.InvalidMerchantCategory) }, result.Quarantined.ToArray());
    }

    // ----- SM-JRNL-002: each rule, with the line number, and the rest carries on -----

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("seq", "-1", Reason.InvalidSeq)]
    [DataRow("seq", "1.0", Reason.InvalidSeq)]
    [DataRow("seq", "1e1", Reason.InvalidSeq)]
    [DataRow("seq", "1E1", Reason.InvalidSeq)]
    [DataRow("seq", "-0", Reason.InvalidSeq)]
    [DataRow("seq", "12345678901.5", Reason.InvalidSeq)]
    [DataRow("seq", "\"1\"", Reason.InvalidSeq)]
    [DataRow("seq", "true", Reason.InvalidSeq)]
    [DataRow("seq", "null", Reason.InvalidSeq)]
    [DataRow("seq", "[1]", Reason.InvalidSeq)]
    [DataRow("seq", "01", Reason.NotJson)]
    [DataRow("transaction_token", "\"" + CardA + "\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"tok_txn_FEEDFACEFEEDFACEFEED0001\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"tok_txn_feedfacefeedfacefeed001\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"tok_txn_feedfacefeedfacefeed00001\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"" + Txn1 + "\\n\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\" " + Txn1 + "\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "\"tok_txn_\"", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "5", Reason.InvalidTransactionToken)]
    [DataRow("transaction_token", "null", Reason.InvalidTransactionToken)]
    [DataRow("customer_token", "\"" + Txn1 + "\"", Reason.InvalidCustomerToken)]
    [DataRow("customer_token", "\"tok_cus_C0FFEEC0FFEEC0FFEEC0FFEE\"", Reason.InvalidCustomerToken)]
    [DataRow("customer_token", "[\"" + Cus + "\"]", Reason.InvalidCustomerToken)]
    [DataRow("card_token", "\"" + Cus + "\"", Reason.InvalidCardToken)]
    [DataRow("card_token", "\"tok_merch_cafecafecafecafecafecafe\"", Reason.InvalidCardToken)]
    [DataRow("card_token", "\"tok_card_cafecafecafecafecafecafg\"", Reason.InvalidCardToken)]
    [DataRow("amount_bin", "8", Reason.InvalidAmountBin)]
    [DataRow("amount_bin", "-1", Reason.InvalidAmountBin)]
    [DataRow("amount_bin", "3.0", Reason.InvalidAmountBin)]
    [DataRow("amount_bin", "\"3\"", Reason.InvalidAmountBin)]
    [DataRow("amount_bin", "false", Reason.InvalidAmountBin)]
    [DataRow("merchant_category", "\"Grocery\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"GROCERY\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"Online_Market\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"groceries\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\" grocery\"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"grocery \"", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "5", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "null", Reason.InvalidMerchantCategory)]
    [DataRow("merchant_category", "\"\\ud83d\\ude00\"", Reason.InvalidMerchantCategory)]
    [DataRow("hour", "24", Reason.InvalidHour)]
    [DataRow("hour", "99", Reason.InvalidHour)]
    [DataRow("hour", "-1", Reason.InvalidHour)]
    [DataRow("hour", "14.0", Reason.InvalidHour)]
    [DataRow("hour", "1e1", Reason.InvalidHour)]
    [DataRow("hour", "\"14\"", Reason.InvalidHour)]
    [DataRow("is_online", "\"true\"", Reason.InvalidIsOnline)]
    [DataRow("is_online", "\"false\"", Reason.InvalidIsOnline)]
    [DataRow("is_online", "1", Reason.InvalidIsOnline)]
    [DataRow("is_online", "0", Reason.InvalidIsOnline)]
    [DataRow("is_online", "null", Reason.InvalidIsOnline)]
    [DataRow("is_online", "True", Reason.NotJson)]
    [DataRow("merchant_category", "\"\\ud800\"", Reason.InvalidText)]
    [DataRow("merchant_category", "\"\\udc00\"", Reason.InvalidText)]
    [DataRow("merchant_category", "\"a\\ud800b\"", Reason.InvalidText)]
    [DataRow("hour", "[\"\\ud800\"]", Reason.InvalidText)]
    [DataRow("hour", "{\"a\":1,\"a\":2}", Reason.DuplicateProperty)]
    [DataRow("hour", "[{\"a\":1,\"a\":2}]", Reason.DuplicateProperty)]
    public void Read_ALineThatBreaksARule_IsQuarantinedWithItsLineNumberAndReason(string property, string json, Reason expected)
    {
        JournalReadResult result = ReadLines(Record(), Record((property, json)), Record());

        Assert.IsFalse(result.IsRejected);
        CollectionAssert.AreEqual(new[] { new QuarantinedLine(2, expected) }, result.Quarantined.ToArray());
        CollectionAssert.AreEqual(new[] { ValidRecordAt(1), ValidRecordAt(3) }, result.Records.ToArray(), "The other lines carry on.");
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("seq")]
    [DataRow("transaction_token")]
    [DataRow("customer_token")]
    [DataRow("card_token")]
    [DataRow("amount_bin")]
    [DataRow("merchant_category")]
    [DataRow("hour")]
    [DataRow("is_online")]
    public void Read_AMissingProperty_IsQuarantined(string property)
    {
        JournalReadResult result = ReadLines(Record((property, null)));

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.MissingProperty) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("country_code", "\"US\"")]
    [DataRow("client_id", "\"client-a\"")]
    [DataRow("Seq", "1")]
    [DataRow("seq ", "1")]
    [DataRow("symbol", "57")]
    public void Read_AnUnknownProperty_IsQuarantined(string property, string json)
    {
        JournalReadResult result = ReadLines(Record((property, json)));

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.UnknownProperty) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("+seq", "1")]
    [DataRow("+seq", "2")]
    [DataRow("+hour", "14")]
    [DataRow("+is_online", "false")]
    [DataRow("s\\u0065q", "1")]
    public void Read_ARepeatedProperty_IsQuarantined(string property, string json)
    {
        JournalReadResult result = ReadLines(Record((property, json)));

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.DuplicateProperty) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("[1]")]
    [DataRow("[]")]
    [DataRow("\"x\"")]
    [DataRow("\"" + CardA + "\"")]
    [DataRow("5")]
    [DataRow("null")]
    [DataRow("true")]
    public void Read_ALineThatIsNotAnObject_IsQuarantined(string line)
    {
        JournalReadResult result = ReadLines(line);

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.NotAnObject) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("not json")]
    [DataRow("{")]
    [DataRow("}")]
    [DataRow("{\"seq\":1,}")]
    [DataRow("{\"seq\":1}{\"seq\":2}")]
    [DataRow("{\"seq\":1} // comment")]
    [DataRow("{seq:1}")]
    [DataRow("'x'")]
    [DataRow("NaN")]
    [DataRow("\u000b")]
    [DataRow("{}\r{}")]
    public void Read_ALineThatIsNotJson_IsQuarantined(string line)
    {
        // Row 11: a CR that is not just before an LF does not end the line, so this is two values.
        JournalReadResult result = ReadLines(line);

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, Reason.NotJson) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [DataRow(" \t ")]
    public void Read_ABlankLine_IsQuarantined(string line)
    {
        JournalReadResult result = ReadLines(Record(), line, Record());

        CollectionAssert.AreEqual(new[] { new QuarantinedLine(2, Reason.BlankLine) }, result.Quarantined.ToArray());
        Assert.HasCount(2, result.Records);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    public void Read_TheFirstBrokenRule_IsTheReason()
    {
        JournalReadResult result = ReadLines(
            Record(("+seq", "1"), ("x", "1")),
            "[" + Record(("+seq", "1")),
            Record(("merchant_category", "\"\\ud800\""), ("x", "1")),
            "[\"\\ud800\"]",
            Record(("hour", "24"), ("x", "1")),
            Record(("hour", "24"), ("is_online", null)),
            Record(("seq", "-1"), ("hour", "24")),
            Record(("transaction_token", "\"" + Cus + "\""), ("card_token", "\"" + Cus + "\"")),
            Record(("customer_token", "1"), ("card_token", "1")),
            Record(("card_token", "1"), ("amount_bin", "8")),
            Record(("amount_bin", "8"), ("merchant_category", "\"x\"")),
            Record(("merchant_category", "\"x\""), ("hour", "24")),
            Record(("hour", "24"), ("is_online", "1")));

        Reason[] expected =
        [
            Reason.DuplicateProperty,
            Reason.NotJson,
            Reason.InvalidText,
            Reason.InvalidText,
            Reason.UnknownProperty,
            Reason.MissingProperty,
            Reason.InvalidSeq,
            Reason.InvalidTransactionToken,
            Reason.InvalidCustomerToken,
            Reason.InvalidCardToken,
            Reason.InvalidAmountBin,
            Reason.InvalidMerchantCategory,
            Reason.InvalidHour,
        ];
        CollectionAssert.AreEqual(expected, result.Quarantined.Select(q => q.Reason).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, expected.Length).ToArray(), result.Quarantined.Select(q => q.LineNumber).ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    [DataRow("seq", "0")]
    [DataRow("seq", "999999999999")]
    [DataRow("amount_bin", "0")]
    [DataRow("amount_bin", "7")]
    [DataRow("hour", "0")]
    [DataRow("hour", "23")]
    [DataRow("is_online", "true")]
    [DataRow("merchant_category", "\"grocery\"")]
    [DataRow("merchant_category", "\"gas\"")]
    [DataRow("merchant_category", "\"restaurant\"")]
    [DataRow("merchant_category", "\"retail\"")]
    [DataRow("merchant_category", "\"travel\"")]
    [DataRow("merchant_category", "\"online_market\"")]
    [DataRow("merchant_category", "\"atm\"")]
    [DataRow("merchant_category", "\"other\"")]
    [DataRow("merchant_category", "\"gr\\u006fcery\"")]
    [DataRow("card_token", "\"tok_card_\\u0063afecafecafecafecafecafe\"")]
    public void Read_AValueAtTheEdgeOfItsRule_IsAccepted(string property, string json)
    {
        JournalReadResult result = ReadLines(Record((property, json)));

        Assert.IsEmpty(result.Quarantined);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    public void Read_TheRecord_HoldsTheLinesValues()
    {
        JournalReadResult result = ReadLines(
            Record(),
            Record(("seq", "999999999999"), ("transaction_token", "\"" + Txn2 + "\""), ("card_token", "\"" + CardB + "\""),
                ("amount_bin", "7"), ("merchant_category", "\"gr\\u006fcery\""), ("hour", "23"), ("is_online", "true")));

        CollectionAssert.AreEqual(
            new[] { ValidRecordAt(1), new JournalRecord(2, 999_999_999_999, Txn2, Cus, CardB, 7, "grocery", 23, true) },
            result.Records.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    public void Read_WhitespaceBetweenJsonTokens_IsAllowed()
    {
        string spaced = Record().Replace(",", " ,\t", StringComparison.Ordinal).Replace(":", " : ", StringComparison.Ordinal);

        JournalReadResult result = ReadLines("  " + spaced + "\t");

        CollectionAssert.AreEqual(new[] { ValidRecordAt(1) }, result.Records.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    public void CategoryNames_AreTheDocumentsEightNames_InTheQuantizersGroupOrder()
    {
        string[] documented = ["grocery", "gas", "restaurant", "retail", "travel", "online_market", "atm", "other"];

        CollectionAssert.AreEqual(documented, Quantizer.CategoryNames.ToArray());
        for (int group = 0; group < documented.Length; group++)
        {
            Assert.AreEqual(group, Quantizer.MccGroup(Quantizer.CategoryNames[group]));
        }
    }

    [TestMethod]
    [TestCategory("SM-JRNL-002")]
    public void TheResult_HoldsNoTextFromAQuarantinedLine()
    {
        // A quarantined line is its number and a reason, nothing else.
        PropertyInfo[] properties = typeof(QuarantinedLine).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        CollectionAssert.AreEquivalent(new[] { typeof(int), typeof(Reason) }, properties.Select(p => p.PropertyType).ToArray());

        PropertyInfo[] resultProperties = typeof(JournalReadResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        CollectionAssert.AreEquivalent(
            new[] { typeof(bool), typeof(int), typeof(IReadOnlyList<JournalRecord>), typeof(IReadOnlyList<QuarantinedLine>) },
            resultProperties.Select(p => p.PropertyType).ToArray());
    }

    // ----- SM-JRNL-005: a possible card number rejects the whole file -----

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("card_token", "\"4111111111111111\"")]
    [DataRow("card_token", "\"tok_4111111111111111\"")]
    [DataRow("card_token", "\"tok_card_4111111111111111\"")]
    [DataRow("card_token", "\"4111 1111 1111 1111\"")]
    [DataRow("card_token", "\"4111-1111-1111-1111\"")]
    [DataRow("transaction_token", "\"4111.1111.1111.1111\"")]
    [DataRow("customer_token", "\"4111\u00a01111\u00a01111\u00a01111\"")]
    [DataRow("customer_token", "\"4111\u200b1111\u200b1111\u200b1111\"")]
    [DataRow("merchant_category", "\"\uff14\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\"")]
    [DataRow("merchant_category", "\"\\u0034\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\"")]
    [DataRow("merchant_category", "\"1234567890123\"")]
    [DataRow("seq", "1000000000000")]
    [DataRow("seq", "4111111111111111")]
    [DataRow("hour", "4111111111111111")]
    [DataRow("amount_bin", "4111111111.111111")]
    [DataRow("is_online", "[4111,1111,1111,1111]")]
    [DataRow("is_online", "{\"4111\":\"111111111111\"}")]
    [DataRow("is_online", "[1234567,\"" + CardA + "\",123456]")]
    [DataRow("4111111111111111", "1")]
    [DataRow("x", "\"4111111111111111\"")]
    public void Read_APossibleCardNumberInAJsonLine_RejectsTheWholeFile(string property, string json)
    {
        JournalReadResult result = ReadLines(Record(), Record(("hour", "24")), Record((property, json)), Record());

        AssertRejectedAt(3, result);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("card 4111 1111 1111 1111")]
    [DataRow("{\"card_token\":\"4111111111111111\"")]
    [DataRow("{\"card_token\":\"" + CardWithDigits + "\"")]
    [DataRow("{\"seq\":1,\"seq\":1,\"card_token\":\"" + CardWithDigits + "\"}")]
    [DataRow("{\"a\":\"\\ud800\",\"card_token\":\"" + CardWithDigits + "\"}")]
    [DataRow("{\"card_token\":\"\\u0034\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\\u0031\"")]
    [DataRow("{\"card_token\":\"\\uFF14\\uff11\\uFF11\\uff11\\uFF11\\uff11\\uFF11\\uff11\\uFF11\\uff11\\uFF11\\uff11\\uFF11\\uff11\\uFF11\\uff11\"")]
    [DataRow("{\"seq\":1,\"seq\":1,\"x\":\"4111111\\n111111111\"}")]
    [DataRow("{\"x\":\"41111111\\f11111111\"")]
    [DataRow("{\"x\":\"41111111\\t11111111\"")]
    [DataRow("{\"x\":\"41111111\\r11111111\"")]
    [DataRow("{\"x\":\"41111111\\b11111111\"")]
    [DataRow("{\"x\":\"\\u1234567890123\"")]
    public void Read_APossibleCardNumberInALineThatIsNotValidJson_RejectsTheWholeFile(string line)
    {
        // Rows 3 to 5 hold a well-formed token, but the line is not valid JSON (cut short, a repeated
        // name, an unpaired surrogate), so nothing in it is exempt. Rows 6 to 12 hide the digits behind
        // JSON escapes (\u in either case of hex, \n, \f, \t, \r, \b), which are decoded for the scan.
        // Row 13 needs the scan as written: decoded, \u1234 is a letter and only 9 digits follow it.
        JournalReadResult result = ReadLines(Record(), Record(("hour", "24")), line, Record());

        AssertRejectedAt(3, result);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    public void Read_APossibleCardNumberSplitByInvalidUtf8_RejectsTheWholeFile()
    {
        // The invalid byte is ignored like any other separator, so the 16 digits join up.
        byte[] line = Concat(Encoding.UTF8.GetBytes("{\"card_token\":\"4111111"), [0xFF], Encoding.UTF8.GetBytes("111111111\"}"));
        byte[] file = Concat(Encoding.UTF8.GetBytes(Record() + "\n"), line, Encoding.UTF8.GetBytes("\n" + Record() + "\n"));

        AssertRejectedAt(2, JournalFile.Read(file));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("{\"x\":\"\\\\u0031\\\\u0032\\\\u0033\\\\u0034\\\\u0035\\\\u0036\\\\u0037\\\\u0038\\\\u0039\\\\u0030\\\\u0031\\\\u0032\\\\u0033\"", Reason.NotJson)]
    [DataRow("{\"x\":\"12345\\q67890123\"", Reason.NotJson)]
    [DataRow("{\"x\":\"\\u004", Reason.NotJson)]
    public void Read_ABrokenLineWhoseEscapesHoldNoLongRun_IsOnlyQuarantined(string line, Reason expected)
    {
        // Row 1: each \\ is an escaped backslash, so what follows is the letter u, not an escape, and the
        // run is broken. Row 2: \q is no escape, so the q stays and breaks the 5 + 8 digits apart.
        // Row 3: the line ends inside \u004, which is kept as written and read without error.
        JournalReadResult result = ReadLines(line);

        Assert.IsFalse(result.IsRejected);
        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, expected) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    public void Read_APossibleCardNumberOnTheLastLine_StillLeavesNothing()
    {
        // The documented example's seven lines, then a possible card number on line 8.
        AssertRejectedAt(8, ReadText(DocumentedExample + "\"4111111111111111\""));
        AssertRejectedAt(8, ReadText(DocumentedExample + "card 4111 1111 1111 1111\n"));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    public void Read_SeveralPossibleCardNumbers_ReportTheFirstLine()
    {
        JournalReadResult result = ReadLines(Record(), "4111111111111111", "4111111111111111");

        AssertRejectedAt(2, result);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("merchant_category", "\"123456789012\"", Reason.InvalidMerchantCategory)]
    [DataRow("card_token", "\"4111 1111 1111\"", Reason.InvalidCardToken)]
    [DataRow("123456789012", "1", Reason.UnknownProperty)]
    [DataRow("1234567", "123456", Reason.UnknownProperty)]
    [DataRow("is_online", "[123456789012,\"x\",1]", Reason.InvalidIsOnline)]
    public void Read_TwelveDigitsInARow_DoNotRejectTheFile(string property, string json, Reason expected)
    {
        // Row 4: a property name and its value are scanned on their own, so 7 + 6 digits don't join.
        // Row 5: a letter inside an array breaks the run.
        JournalReadResult result = ReadLines(Record((property, json)));

        Assert.IsFalse(result.IsRejected);
        CollectionAssert.AreEqual(new[] { new QuarantinedLine(1, expected) }, result.Quarantined.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-PRIV-002")]
    public void Read_WellFormedTokensInAJsonLine_AreExempt()
    {
        JournalReadResult result = ReadLines(
            Record(("transaction_token", "\"tok_txn_000000000000000000000001\""), ("customer_token", "\"tok_cus_999999999999999999999999\""), ("card_token", "\"tok_card_123456789012345678901234\"")),
            Record(("merchant_category", "\"" + CardWithDigits + "\"")),
            Record(("is_online", "[\"" + CardWithDigits + "\"]")),
            Record((CardWithDigits, "1")));

        Assert.IsFalse(result.IsRejected);
        CollectionAssert.AreEqual(
            new[] { new JournalRecord(1, 1, "tok_txn_000000000000000000000001", "tok_cus_999999999999999999999999", "tok_card_123456789012345678901234", 3, "grocery", 14, false) },
            result.Records.ToArray());
        CollectionAssert.AreEqual(
            new[] { new QuarantinedLine(2, Reason.InvalidMerchantCategory), new QuarantinedLine(3, Reason.InvalidIsOnline), new QuarantinedLine(4, Reason.UnknownProperty) },
            result.Quarantined.ToArray());
    }
}
