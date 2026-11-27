// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the audit log line writer (docs/formats/audit-v1.md): README SM-JRNL-008 (a per-file
// audit entry with the file name, counts and time, and no transaction content).
//
// The known answer is the example in docs/formats/audit-v1.md. Its byte count and SHA-256 were
// computed by Python (json.dumps with separators=(",", ":")), not by the C# under test. Line 1's
// hash is the journal format's documented example (docs/formats/journal-v1.md); the other hashes
// are made up.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Journal;
using Reason = SecretModel.Core.Journal.JournalQuarantineReason;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class AuditFileTests
{
    internal const string JournalExampleSha256 = "1771857b18f0e12adf90d603a0e1bca04eb3eb3ef1191432c8306d42563a1852";

    // The four documented lines, each with its LF.
    internal static readonly string[] DocumentedLines =
    [
        "{\"time\":\"2026-11-26T20:15:03Z\",\"file\":\"day1.jsonl\",\"sha256\":\"1771857b18f0e12adf90d603a0e1bca04eb3eb3ef1191432c8306d42563a1852\",\"outcome\":\"archived\",\"records\":3,\"new\":3,\"quarantined\":[[3,\"BlankLine\"],[4,\"InvalidMerchantCategory\"],[5,\"InvalidHour\"],[6,\"UnknownProperty\"]],\"cards_trained\":0}\n",
        "{\"time\":\"2026-11-26T20:16:00Z\",\"file\":\"day2.jsonl\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"outcome\":\"retry\",\"records\":20,\"new\":6,\"quarantined\":[[7,\"SeqConflict\"]],\"cards_trained\":1,\"cards_failed\":1}\n",
        "{\"time\":\"2026-11-26T20:17:30Z\",\"file\":\"day3.jsonl\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"outcome\":\"rejected\",\"line\":2}\n",
        "{\"time\":\"2026-11-26T20:18:45Z\",\"file\":null,\"sha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"outcome\":\"bad_name\"}\n",
    ];

    // Computed by Python from the same lines.
    private const int DocumentedLength = 824;
    private const string DocumentedSha256 = "751bb63f1a3a3164ba9e40e7d7b834a610b2fba7b22d579528a849e9950db692";

    private static DateTimeOffset Utc(int hour, int minute, int second) => new(2026, 11, 26, hour, minute, second, TimeSpan.Zero);

    internal static AuditEntry[] DocumentedEntries() =>
    [
        AuditEntry.Archived(
            Utc(20, 15, 3), "day1.jsonl", JournalExampleSha256, records: 3, @new: 3,
            [new(3, Reason.BlankLine), new(4, Reason.InvalidMerchantCategory), new(5, Reason.InvalidHour), new(6, Reason.UnknownProperty)],
            cardsTrained: 0),
        AuditEntry.Retry(Utc(20, 16, 0), "day2.jsonl", new string('a', 64), records: 20, @new: 6, [new(7, Reason.SeqConflict)], cardsTrained: 1, cardsFailed: 1),
        AuditEntry.Rejected(Utc(20, 17, 30), "day3.jsonl", new string('b', 64), line: 2),
        AuditEntry.BadName(Utc(20, 18, 45), new string('c', 64)),
    ];

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public void Write_MatchesTheDocumentedBytes()
    {
        byte[] written = [.. DocumentedEntries().SelectMany(AuditFile.WriteLine)];

        Assert.AreEqual(string.Concat(DocumentedLines), Encoding.UTF8.GetString(written));
        Assert.HasCount(DocumentedLength, written);
        Assert.AreEqual(DocumentedSha256, Convert.ToHexStringLower(SHA256.HashData(written)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public void Time_IsWrittenInUtc_ToTheSecond()
    {
        // 15:18:45.999 in New York in November (UTC-5) is 20:18:45.999 UTC; the fraction is dropped.
        var time = new DateTimeOffset(2026, 11, 26, 15, 18, 45, 999, TimeSpan.FromHours(-5));

        string line = Encoding.UTF8.GetString(AuditFile.WriteLine(AuditEntry.BadName(time, new string('c', 64))));

        Assert.AreEqual(DocumentedLines[3], line);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public void QuarantinedLines_AreWrittenInLineOrder()
    {
        AuditEntry entry = AuditEntry.Archived(
            Utc(20, 15, 3), "day1.jsonl", JournalExampleSha256, 3, 3,
            [new(6, Reason.UnknownProperty), new(3, Reason.BlankLine), new(5, Reason.InvalidHour), new(4, Reason.InvalidMerchantCategory)],
            0);

        Assert.AreEqual(DocumentedLines[0], Encoding.UTF8.GetString(AuditFile.WriteLine(entry)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-PRIV-004")]
    [DataRow("4111111111111111.jsonl")]       // a published test card number
    [DataRow("4111.1111.1111.1111.jsonl")]
    [DataRow("day 1.jsonl")]
    [DataRow("day1.txt")]
    [DataRow("")]
    public void AnEntry_RefusesAFileNameOutsideTheRules_WithoutRepeatingIt(string file)
    {
        string sha = new('a', 64);
        Action[] makers =
        [
            () => AuditEntry.Archived(Utc(0, 0, 0), file, sha, 0, 0, [], 0),
            () => AuditEntry.Retry(Utc(0, 0, 0), file, sha, 0, 0, [], 0, 1),
            () => AuditEntry.Rejected(Utc(0, 0, 0), file, sha, 1),
        ];

        foreach (Action make in makers)
        {
            ArgumentException e = Assert.ThrowsExactly<ArgumentException>(make);
            Assert.AreEqual("file", e.ParamName);
            if (file.Length > 0)
            {
                Assert.DoesNotContain(file, e.Message);
            }
        }
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [DataRow(0, 'a')]
    [DataRow(63, 'a')]
    [DataRow(65, 'a')]
    [DataRow(64, 'A')]     // uppercase
    [DataRow(64, 'g')]     // not hex
    public void AnEntry_RefusesASha256ThatIsNot64LowercaseHex(int length, char c)
    {
        string sha = new(c, length);
        Action[] makers =
        [
            () => AuditEntry.Archived(Utc(0, 0, 0), "day1.jsonl", sha, 0, 0, [], 0),
            () => AuditEntry.Retry(Utc(0, 0, 0), "day1.jsonl", sha, 0, 0, [], 0, 1),
            () => AuditEntry.Rejected(Utc(0, 0, 0), "day1.jsonl", sha, 1),
            () => AuditEntry.BadName(Utc(0, 0, 0), sha),
        ];

        foreach (Action make in makers)
        {
            Assert.AreEqual("sha256", Assert.ThrowsExactly<ArgumentException>(make).ParamName);
        }
    }
}
