// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the journal name rules (README §8.2, approved by Edwin West on 2026-11-26): a client id
// is its folder's name (README SM-AUTH-003: the client never comes from the file), and a journal
// file's name may not hold a run of 13 digits (SM-PRIV-002), so the audit log can record it
// (SM-JRNL-008). 4111111111111111 is the published Visa test number, used only as an input that must
// be refused.

using SecretModel.Core.Journal;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class JournalNamesTests
{
    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [DataRow("a")]
    [DataRow("7")]
    [DataRow("client-a")]
    [DataRow("Client_A.1")]
    [DataRow("x-")]
    public void ClientIds_ThatFollowTheRule_AreValid(string value)
    {
        Assert.IsTrue(JournalNames.IsValidClientId(value));
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow(".a")]
    [DataRow("-a")]
    [DataRow("_a")]
    [DataRow("a b")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("a:b")]
    [DataRow("café")]          // a letter outside ASCII
    [DataRow("ａ")]             // full-width a
    [DataRow("a\u0000")]
    [DataRow(" a")]
    public void ClientIds_ThatBreakTheRule_AreInvalid(string? value)
    {
        Assert.IsFalse(JournalNames.IsValidClientId(value));
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    public void ClientIds_Are1To64Characters()
    {
        Assert.IsTrue(JournalNames.IsValidClientId(new string('a', 64)));
        Assert.IsFalse(JournalNames.IsValidClientId(new string('a', 65)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [DataRow("day1.jsonl")]
    [DataRow("a.jsonl")]
    [DataRow("7.jsonl")]
    [DataRow("2026-11-27.jsonl")]
    [DataRow("a.b.jsonl")]
    [DataRow("x-1_2.jsonl")]
    [DataRow("123456789012.jsonl")]              // 12 digits in a row
    [DataRow("123456.789012.jsonl")]             // 12 once the dot is ignored
    public void FileNames_ThatFollowTheRule_AreValid(string value)
    {
        Assert.IsTrue(JournalNames.IsValidFileName(value));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(".jsonl")]
    [DataRow("day1")]
    [DataRow("day1.json")]
    [DataRow("day1.JSONL")]
    [DataRow("day1.jsonl.part")]
    [DataRow(".day1.jsonl")]
    [DataRow("-day1.jsonl")]
    [DataRow("_day1.jsonl")]
    [DataRow("day 1.jsonl")]
    [DataRow("day1 .jsonl")]
    [DataRow("a/b.jsonl")]
    [DataRow("a\\b.jsonl")]
    [DataRow("café.jsonl")]
    public void FileNames_ThatBreakTheCharacterRule_AreInvalid(string? value)
    {
        Assert.IsFalse(JournalNames.IsValidFileName(value));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("1234567890123.jsonl")]             // 13 digits in a row
    [DataRow("4111111111111111.jsonl")]
    [DataRow("4111.1111.1111.1111.jsonl")]       // dots, dashes and underscores between digits are ignored
    [DataRow("4111-1111-1111-1111.jsonl")]
    [DataRow("4111_1111_1111_1111.jsonl")]
    [DataRow("day-4111111111111111.jsonl")]
    [DataRow("123456.7890123.jsonl")]
    public void FileNames_WithARunOf13Digits_AreInvalid(string value)
    {
        Assert.IsFalse(JournalNames.IsValidFileName(value));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public void FileNames_Are1To100CharactersBeforeTheExtension()
    {
        Assert.IsTrue(JournalNames.IsValidFileName(new string('a', 100) + ".jsonl"));
        Assert.IsFalse(JournalNames.IsValidFileName(new string('a', 101) + ".jsonl"));
    }
}
