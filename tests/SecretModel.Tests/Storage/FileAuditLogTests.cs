// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Home-mode audit log: README SM-JRNL-008 (one entry per file), SM-STOR-001 (the path
// comes from the client hash only) and SM-STOR-006 (each client has its own log). Each test uses its
// own new folder under the system temp folder and deletes it afterwards.

using System.Text;
using SecretModel.Core.Journal;
using SecretModel.Core.Storage;
using SecretModel.Tests.Journal;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class FileAuditLogTests
{
    private string root = "";

    [TestInitialize]
    public void CreateRoot()
    {
        root = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void DeleteRoot() => Directory.Delete(root, recursive: true);

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-STOR-001")]
    public async Task Append_AddsOneLinePerEntry_AtThePathFromTheClientHashOnly()
    {
        var log = new FileAuditLog(root);
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();

        foreach (AuditEntry entry in entries)
        {
            await log.AppendAsync("abc", entry);
        }

        // "ba78…" is the SHA-256 of "abc", the published FIPS 180-2 test vector.
        string expectedPath = Path.Combine(root, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "audit.jsonl");
        Assert.AreEqual(expectedPath, log.PathFor("abc"));
        Assert.AreEqual(string.Concat(AuditFileTests.DocumentedLines), Encoding.UTF8.GetString(File.ReadAllBytes(expectedPath)));
        CollectionAssert.AreEqual(new[] { expectedPath }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_HaveSeparateLogs()
    {
        var log = new FileAuditLog(root);
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();

        await log.AppendAsync("client-a", entries[0]);
        await log.AppendAsync("client-b", entries[1]);
        await log.AppendAsync("client-a", entries[2]);

        Assert.AreNotEqual(log.PathFor("client-a"), log.PathFor("client-b"));
        Assert.AreEqual(AuditFileTests.DocumentedLines[0] + AuditFileTests.DocumentedLines[2], File.ReadAllText(log.PathFor("client-a")));
        Assert.AreEqual(AuditFileTests.DocumentedLines[1], File.ReadAllText(log.PathFor("client-b")));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [DataRow("")]
    [DataRow("   ")]
    public async Task AnEmptyClientId_IsRejected(string clientId)
    {
        var log = new FileAuditLog(root);

        ArgumentException e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => log.AppendAsync(clientId, AuditFileTests.DocumentedEntries()[0]));

        Assert.AreEqual("clientId", e.ParamName);
        Assert.IsEmpty(Directory.GetFileSystemEntries(root));
    }
}
