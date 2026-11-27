// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Azure-mode journal source, run against Azurite (README §8.3): which blobs are
// waiting (only *.jsonl directly in a client folder; never archive/, quarantine/ or a directory
// entry), reading and moving exactly the listed version, never overwriting (SM-JRNL-006), and
// keeping a file the client replaced. The last four tests run the ingestor with the Azure source and
// the three Azure stores: archive and audit (SM-JRNL-006, SM-JRNL-008), quarantine (SM-JRNL-005),
// retry, and clients kept apart (SM-AUTH-003, SM-AUTH-006).
//
// Azurite has no hierarchical namespace, so directory entries are imitated here with the metadata a
// hierarchical namespace gives them; that, and listing order there, are checked on real Azure in
// Task 8c step 6. Azurite must be listening on 127.0.0.1:10000; if it isn't, every test here fails
// and says how to start it, and none is skipped. Each test uses its own two new containers and
// deletes them afterwards. All tokens are made up; 4111111111111111 is the published Visa test
// number, used only as an input that must be refused.

using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Api.Storage;
using SecretModel.Core.Journal;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Tests.Journal;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class BlobJournalSourceTests
{
    private const string Azurite = BlobModelStoreTests.Azurite;

    private BlobContainerClient journal = null!;
    private BlobContainerClient models = null!;

    [ClassInitialize]
    public static Task RequireAzurite(TestContext context) => BlobModelStoreTests.RequireAzurite(context);

    [TestInitialize]
    public async Task CreateContainers()
    {
        var service = new BlobServiceClient(Azurite);
        journal = service.GetBlobContainerClient("j" + Guid.NewGuid().ToString("N"));
        models = service.GetBlobContainerClient("m" + Guid.NewGuid().ToString("N"));
        await journal.CreateAsync();
        await models.CreateAsync();
    }

    [TestCleanup]
    public async Task DeleteContainers()
    {
        await journal.DeleteIfExistsAsync();
        await models.DeleteIfExistsAsync();
    }

    // A second server instance: its own client and connection, the same container.
    private BlobJournalSource Source() => new(new BlobServiceClient(Azurite).GetBlobContainerClient(journal.Name));

    private async Task<string> Put(string name, string content, IDictionary<string, string>? metadata = null)
    {
        var options = new BlobUploadOptions { Metadata = metadata };
        return (await journal.GetBlobClient(name).UploadAsync(BinaryData.FromString(content), options)).Value.ETag.ToString();
    }

    private static async Task<string?> Text(BlobContainerClient container, string name)
    {
        try
        {
            return (await container.GetBlobClient(name).DownloadContentAsync()).Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    private static string[] Names(BlobContainerClient container) => [.. container.GetBlobs().Select(b => b.Name)];

    private static string Sha256Hex(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // A container whose blobs run `race` just before each delete, as a client replacing or removing
    // the file between the server's copy and its delete would.
    private sealed class RacingContainer(string name, Func<Task> race) : BlobContainerClient(Azurite, name)
    {
        public override BlobClient GetBlobClient(string blobName) => new RacingBlob(Name, blobName, race);
    }

    private sealed class RacingBlob(string containerName, string blobName, Func<Task> race) : BlobClient(Azurite, containerName, blobName)
    {
        public override async Task<Response> DeleteAsync(
            DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            await race();
            return await base.DeleteAsync(snapshotsOption, conditions, cancellationToken);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A history store that fails to save one card.
    private sealed class FailingHistoryStore(IHistoryStore inner, string failingCard) : IHistoryStore
    {
        public Task<StoredHistory?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

        public Task<string> PutAsync(ProfileKey key, CardHistory history, string? expectedVersion, CancellationToken cancellationToken = default) =>
            key.CardToken == failingCard ? throw new IOException("The disk is full.") : inner.PutAsync(key, history, expectedVersion, cancellationToken);
    }

    private JournalIngestor Ingestor(IHistoryStore? histories = null) => new(
        Source(),
        histories ?? new BlobHistoryStore(models),
        new BlobModelStore(models),
        new BlobAuditLog(models),
        new ScoringOptions(),
        new FixedTime(new DateTimeOffset(2026, 11, 26, 20, 15, 3, TimeSpan.Zero)));

    // ---- The source ------------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-AUTH-003")]
    public async Task List_GivesOnlyJsonlBlobsDirectlyInEachClientFolder_InOrdinalOrder_WithTheirETags()
    {
        string z = await Put("b-client/z.jsonl", "z");
        string a = await Put("b-client/a.jsonl", "a");
        string upperB = await Put("b-client/B.jsonl", "B");
        await Put("b-client/x.jsonl.part", "x");         // still being uploaded under another name
        await Put("b-client/Y.JSONL", "Y");              // the extension must match exactly
        await Put("b-client/archive/old.jsonl", "o");
        await Put("b-client/quarantine/bad.jsonl", "q");
        await Put("b-client/other/deep.jsonl", "d");
        string m = await Put("a-client/m.jsonl", "m");
        await Put("root.jsonl", "r");                    // not in a client folder

        IReadOnlyList<JournalFileRef> files = await Source().ListAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                new JournalFileRef("a-client", "m.jsonl", m),
                new JournalFileRef("b-client", "B.jsonl", upperB),
                new JournalFileRef("b-client", "a.jsonl", a),
                new JournalFileRef("b-client", "z.jsonl", z),
            },
            files.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow("true")]
    [DataRow("True")]
    public async Task List_SkipsDirectoryEntries_ButNotEmptyFiles(string isFolder)
    {
        // With a hierarchical namespace a directory is listed as a zero-length blob marked hdi_isfolder.
        await Put("client-a/x.jsonl", "", new Dictionary<string, string> { ["hdi_isfolder"] = isFolder });
        string empty = await Put("client-a/empty.jsonl", "");

        CollectionAssert.AreEqual(new[] { new JournalFileRef("client-a", "empty.jsonl", empty) }, (await Source().ListAsync()).ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task Read_GivesTheListedVersion()
    {
        await Put("client-a/day1.jsonl", "line one\r\nline two\n");

        JournalFileRef file = (await Source().ListAsync()).Single();

        Assert.AreEqual("line one\r\nline two\n", Encoding.UTF8.GetString(await Source().ReadAsync(file)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-PRIV-004")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Read_OfAFileChangedOrRemovedSinceItWasListed_FailsWithoutNamingIt(bool removed)
    {
        await Put("client-a/4111111111111111.jsonl", "first");
        JournalFileRef file = (await Source().ListAsync()).Single();
        if (removed)
        {
            await journal.GetBlobClient("client-a/4111111111111111.jsonl").DeleteAsync();
        }
        else
        {
            await Put("client-a/4111111111111111.jsonl", "second");
        }

        IOException e = await Assert.ThrowsExactlyAsync<IOException>(() => Source().ReadAsync(file));

        Assert.AreEqual("A journal file changed or was removed after it was listed; it is read again on the next run.", e.Message);
        Assert.IsNull(e.InnerException, "The Blob error, which carries the blob's address, is not passed on.");
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow(JournalFolder.Archive, "archive")]
    [DataRow(JournalFolder.Quarantine, "quarantine")]
    public async Task Move_CopiesTheFileUnchanged_AndDeletesTheOriginal(JournalFolder folder, string folderName)
    {
        await Put("client-a/day1.jsonl", "line one\r\nline two\n");
        JournalFileRef file = (await Source().ListAsync()).Single();

        string name = await Source().MoveAsync(file, folder, "renamed.jsonl");

        Assert.AreEqual("renamed.jsonl", name);
        CollectionAssert.AreEqual(new[] { $"client-a/{folderName}/renamed.jsonl" }, Names(journal));
        Assert.AreEqual("line one\r\nline two\n", await Text(journal, $"client-a/{folderName}/renamed.jsonl"));
        Assert.IsEmpty(await Source().ListAsync(), "A moved file is no longer waiting.");
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow(JournalFolder.Archive, "archive")]
    [DataRow(JournalFolder.Quarantine, "quarantine")]
    public async Task Move_ToATakenName_UsesTheLowestFreeNumber_AndOverwritesNothing(JournalFolder folder, string folderName)
    {
        await Put($"client-a/{folderName}/day1.jsonl", "first");
        await Put($"client-a/{folderName}/day1.2.jsonl", "third");

        await Put("client-a/day1.jsonl", "second");
        Assert.AreEqual("day1.1.jsonl", await Source().MoveAsync((await Source().ListAsync()).Single(), folder, "day1.jsonl"));
        await Put("client-a/day1.jsonl", "fourth");
        Assert.AreEqual("day1.3.jsonl", await Source().MoveAsync((await Source().ListAsync()).Single(), folder, "day1.jsonl"));

        string[] contents = [.. await Task.WhenAll(new[] { "day1.jsonl", "day1.1.jsonl", "day1.2.jsonl", "day1.3.jsonl" }
            .Select(async n => (await Text(journal, $"client-a/{folderName}/{n}"))!))];
        CollectionAssert.AreEqual(new[] { "first", "second", "third", "fourth" }, contents);
        Assert.HasCount(4, Names(journal));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task Move_OfAFileChangedSinceItWasListed_MovesNothing()
    {
        await Put("client-a/day1.jsonl", "first");
        JournalFileRef file = (await Source().ListAsync()).Single();
        await Put("client-a/day1.jsonl", "second");

        await Assert.ThrowsExactlyAsync<IOException>(() => Source().MoveAsync(file, JournalFolder.Archive, "day1.jsonl"));

        CollectionAssert.AreEqual(new[] { "client-a/day1.jsonl" }, Names(journal));
        Assert.AreEqual("second", await Text(journal, "client-a/day1.jsonl"));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Move_WhenTheClientReplacesOrRemovesTheFileBeforeTheDelete_KeepsTheCopyAndAnyNewFile(bool removed)
    {
        await Put("client-a/day1.jsonl", "first");
        JournalFileRef file = (await Source().ListAsync()).Single();
        var racing = new BlobJournalSource(new RacingContainer(journal.Name, async () =>
        {
            if (removed)
            {
                await journal.GetBlobClient("client-a/day1.jsonl").DeleteAsync();
            }
            else
            {
                await Put("client-a/day1.jsonl", "second");
            }
        }));

        Assert.AreEqual("day1.jsonl", await racing.MoveAsync(file, JournalFolder.Archive, "day1.jsonl"));

        Assert.AreEqual("first", await Text(journal, "client-a/archive/day1.jsonl"), "The copy is of what was read.");
        Assert.AreEqual(removed ? null : "second", await Text(journal, "client-a/day1.jsonl"), "A replacement stays for the next run.");
    }

    // ---- The ingestor in Azure mode --------------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-JRNL-008")]
    public async Task Ingestor_ArchivesTheDocumentedJournal_WithTheDocumentedAuditLine()
    {
        await Put("client-a/day1.jsonl", JournalFileTests.DocumentedExample);

        IReadOnlyList<IngestedFile> handled = await Ingestor().RunAsync();

        Assert.AreEqual(AuditOutcome.Archived, handled.Single().Entry.Outcome);
        CollectionAssert.AreEqual(new[] { "client-a/archive/day1.jsonl" }, Names(journal));
        Assert.AreEqual(JournalFileTests.DocumentedExample, await Text(journal, "client-a/archive/day1.jsonl"), "Archived unchanged.");
        Assert.AreEqual(AuditFileTests.DocumentedLines[0], await Text(models, BlobAuditLog.BlobNameFor("client-a")));
        var key = new ProfileKey("client-a", "tok_cus_c0ffeec0ffeec0ffeec0ffee", "tok_card_beefbeefbeefbeefbeefbeef");
        CollectionAssert.AreEqual(new long[] { 2, 999_999_999_999 }, (await new BlobHistoryStore(models).GetAsync(key))!.History.Seq.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-JRNL-008")]
    public async Task Ingestor_QuarantinesARejectedFileUnchanged_AndAFileWithABadNameAsItsSha256()
    {
        string rejected = "{\"note\":\"4111 1111 1111 1111\"}\n";
        string good = string.Join("\n", JournalFileTests.DocumentedExample.Split('\n').Take(2)) + "\n";
        await Put("client-a/day1.jsonl", rejected);
        await Put("client-a/4111111111111111.jsonl", good);

        await Ingestor().RunAsync();

        CollectionAssert.AreEquivalent(
            new[] { "client-a/quarantine/day1.jsonl", $"client-a/quarantine/{Sha256Hex(good)}.jsonl" }, Names(journal));
        Assert.AreEqual(rejected, await Text(journal, "client-a/quarantine/day1.jsonl"));
        Assert.AreEqual(good, await Text(journal, $"client-a/quarantine/{Sha256Hex(good)}.jsonl"));
        string audit = (await Text(models, BlobAuditLog.BlobNameFor("client-a")))!;
        Assert.AreEqual(
            $"{{\"time\":\"2026-11-26T20:15:03Z\",\"file\":null,\"sha256\":\"{Sha256Hex(good)}\",\"outcome\":\"bad_name\"}}\n" +
            $"{{\"time\":\"2026-11-26T20:15:03Z\",\"file\":\"day1.jsonl\",\"sha256\":\"{Sha256Hex(rejected)}\",\"outcome\":\"rejected\",\"line\":1}}\n",
            audit);
        CollectionAssert.AreEqual(new[] { BlobAuditLog.BlobNameFor("client-a") }, Names(models), "Nothing but the audit log was stored.");
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task Ingestor_KeepsAFileForRetry_WhenACardCannotBeSaved_AndArchivesItOnTheNextRun()
    {
        string etag = await Put("client-a/day1.jsonl", JournalFileTests.DocumentedExample);
        const string beef = "tok_card_beefbeefbeefbeefbeefbeef";

        await Ingestor(new FailingHistoryStore(new BlobHistoryStore(models), beef)).RunAsync();

        CollectionAssert.AreEqual(new[] { "client-a/day1.jsonl" }, Names(journal));
        Assert.AreEqual(etag, (await journal.GetBlobClient("client-a/day1.jsonl").GetPropertiesAsync()).Value.ETag.ToString(), "Left unchanged.");
        StringAssert.Contains(await Text(models, BlobAuditLog.BlobNameFor("client-a")), "\"outcome\":\"retry\",\"records\":3,\"new\":1,");

        await Ingestor().RunAsync();

        CollectionAssert.AreEqual(new[] { "client-a/archive/day1.jsonl" }, Names(journal));
        StringAssert.EndsWith(await Text(models, BlobAuditLog.BlobNameFor("client-a")), "\"outcome\":\"archived\",\"records\":3,\"new\":2,"
            + "\"quarantined\":[[3,\"BlankLine\"],[4,\"InvalidMerchantCategory\"],[5,\"InvalidHour\"],[6,\"UnknownProperty\"]],\"cards_trained\":0}\n");
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-AUTH-006")]
    public async Task Ingestor_TakesTheClientFromTheFolder_AndKeepsClientsApart()
    {
        await Put("client-a/day1.jsonl", JournalFileTests.DocumentedExample);
        await Put("client-b/day1.jsonl", JournalFileTests.DocumentedExample);
        await Put("4111111111111111/day1.jsonl", JournalFileTests.DocumentedExample);   // not a client id

        IReadOnlyList<IngestedFile> handled = await Ingestor().RunAsync();

        CollectionAssert.AreEqual(new[] { "client-a", "client-b" }, handled.Select(h => h.ClientId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "4111111111111111/day1.jsonl", "client-a/archive/day1.jsonl", "client-b/archive/day1.jsonl" }, Names(journal));
        var store = new BlobHistoryStore(models);
        foreach (string client in new[] { "client-a", "client-b" })
        {
            var key = new ProfileKey(client, "tok_cus_c0ffeec0ffeec0ffeec0ffee", "tok_card_cafecafecafecafecafecafe");
            Assert.AreEqual(1, (await store.GetAsync(key))!.History.Count);
            Assert.AreEqual(AuditFileTests.DocumentedLines[0], await Text(models, BlobAuditLog.BlobNameFor(client)));
        }

        Assert.HasCount(2, Names(models).Select(n => n.Split('/')[0]).Distinct().ToArray(), "One folder per client.");
    }
}
