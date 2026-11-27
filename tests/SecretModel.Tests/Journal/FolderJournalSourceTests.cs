// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Home-mode journal source (README §8.2): which files are waiting (only *.jsonl,
// directly in a client folder, never archive/ or quarantine/), and moving a handled file to
// archive/ or quarantine/ unchanged, without overwriting anything (README SM-JRNL-006). Each test
// uses its own new folder under the system temp folder and deletes it afterwards.

using SecretModel.Core.Journal;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class FolderJournalSourceTests
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

    private string Put(string relativePath, string content = "x")
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task List_GivesOnlyJsonlFilesDirectlyInEachClientFolder_InOrdinalOrder()
    {
        Put("b-client/z.jsonl");
        Put("b-client/a.jsonl");
        Put("b-client/B.jsonl");
        Put("b-client/x.jsonl.part");      // still being uploaded under another name
        Put("b-client/y.tmp");
        Put("b-client/Y.JSONL");           // the extension must match exactly
        Put("b-client/archive/old.jsonl");
        Put("b-client/quarantine/bad.jsonl");
        Put("b-client/other/deep.jsonl");
        Put("a-client/m.jsonl");
        Put("root.jsonl");                 // not in a client folder
        Directory.CreateDirectory(Path.Combine(root, "c-client"));

        IReadOnlyList<JournalFileRef> files = await new FolderJournalSource(root).ListAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                new JournalFileRef("a-client", "m.jsonl"),
                new JournalFileRef("b-client", "B.jsonl"),
                new JournalFileRef("b-client", "a.jsonl"),
                new JournalFileRef("b-client", "z.jsonl"),
            },
            files.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-006")]
    public async Task List_NeverGivesALink_EvenOneToAnotherClientsFile()
    {
        string theirs = Put("client-b/secret.jsonl");
        Put("client-a/real.jsonl");
        File.CreateSymbolicLink(Path.Combine(root, "client-a", "day1.jsonl"), theirs);
        File.CreateSymbolicLink(Path.Combine(root, "client-a", "day2.jsonl"), Path.Combine("..", "client-b", "secret.jsonl"));

        IReadOnlyList<JournalFileRef> files = await new FolderJournalSource(root).ListAsync();

        CollectionAssert.AreEqual(
            new[] { new JournalFileRef("client-a", "real.jsonl"), new JournalFileRef("client-b", "secret.jsonl") },
            files.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task Move_CreatesTheFolder_AndMovesTheFileUnchanged()
    {
        string original = Put("client-a/day1.jsonl", "line one\r\nline two\n");
        byte[] bytes = File.ReadAllBytes(original);
        var source = new FolderJournalSource(root);
        var file = new JournalFileRef("client-a", "day1.jsonl");
        CollectionAssert.AreEqual(bytes, await source.ReadAsync(file));

        string name = await source.MoveAsync(file, JournalFolder.Archive, "day1.jsonl");

        Assert.AreEqual("day1.jsonl", name);
        Assert.IsFalse(File.Exists(original));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, "client-a", "archive", "day1.jsonl")));
        Assert.IsEmpty(await source.ListAsync(), "An archived file is no longer waiting.");
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task Move_ToAQuarantine_UnderAnotherName()
    {
        string original = Put("client-a/bad name.jsonl", "content");
        var source = new FolderJournalSource(root);

        string name = await source.MoveAsync(new JournalFileRef("client-a", "bad name.jsonl"), JournalFolder.Quarantine, "abc.jsonl");

        Assert.AreEqual("abc.jsonl", name);
        Assert.IsFalse(File.Exists(original));
        Assert.AreEqual("content", File.ReadAllText(Path.Combine(root, "client-a", "quarantine", "abc.jsonl")));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow(JournalFolder.Archive, "archive")]
    [DataRow(JournalFolder.Quarantine, "quarantine")]
    public async Task Move_ToATakenName_UsesTheLowestFreeNumber_AndOverwritesNothing(JournalFolder folder, string folderName)
    {
        Put($"client-a/{folderName}/day1.jsonl", "first");
        Put($"client-a/{folderName}/day1.2.jsonl", "third");
        var source = new FolderJournalSource(root);
        var file = new JournalFileRef("client-a", "day1.jsonl");

        Put("client-a/day1.jsonl", "second");
        Assert.AreEqual("day1.1.jsonl", await source.MoveAsync(file, folder, "day1.jsonl"));
        Put("client-a/day1.jsonl", "fourth");
        Assert.AreEqual("day1.3.jsonl", await source.MoveAsync(file, folder, "day1.jsonl"));

        string target = Path.Combine(root, "client-a", folderName);
        string[] contents = [.. new[] { "day1.jsonl", "day1.1.jsonl", "day1.2.jsonl", "day1.3.jsonl" }.Select(n => File.ReadAllText(Path.Combine(target, n)))];
        CollectionAssert.AreEqual(new[] { "first", "second", "third", "fourth" }, contents);
        Assert.HasCount(4, Directory.GetFiles(target));
    }
}
