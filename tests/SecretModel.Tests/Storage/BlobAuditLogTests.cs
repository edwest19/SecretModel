// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Azure-mode audit log, run against Azurite (README §9.2): SM-JRNL-008 (one entry per
// file, the same bytes as the Home-mode log), SM-STOR-001 (the blob name comes from the client hash
// only), SM-STOR-002 (an append that lost a race fails loudly and drops no other writer's line) and
// SM-STOR-006 (each client has its own log). The last test runs the ingestor on the three Azure
// stores and on the three Home stores and compares every byte they store. Azurite must be listening
// on 127.0.0.1:10000; if it isn't, every test here fails and says how to start it, and none is
// skipped. Each test uses its own new container and deletes it afterwards.

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
public sealed class BlobAuditLogTests
{
    private const string Azurite = BlobModelStoreTests.Azurite;

    private BlobContainerClient container = null!;

    [ClassInitialize]
    public static Task RequireAzurite(TestContext context) => BlobModelStoreTests.RequireAzurite(context);

    [TestInitialize]
    public async Task CreateContainer()
    {
        container = new BlobServiceClient(Azurite).GetBlobContainerClient("t" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
    }

    [TestCleanup]
    public async Task DeleteContainer() => await container.DeleteIfExistsAsync();

    private BlobAuditLog NewInstance() => new(new BlobServiceClient(Azurite).GetBlobContainerClient(container.Name));

    private async Task<string> Text(string blobName) => (await container.GetBlobClient(blobName).DownloadContentAsync()).Value.Content.ToString();

    // A container whose blobs run `race` just before each upload, as another writer would between
    // this writer's read and its write.
    private sealed class RacingContainer(string name, Func<Task> race) : BlobContainerClient(Azurite, name)
    {
        public override BlobClient GetBlobClient(string blobName) => new RacingBlob(Name, blobName, race);
    }

    private sealed class RacingBlob(string containerName, string blobName, Func<Task> race) : BlobClient(Azurite, containerName, blobName)
    {
        public override async Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            await race();
            return await base.UploadAsync(content, options, cancellationToken);
        }
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-STOR-001")]
    public async Task Append_AddsOneLinePerEntry_InOneBlobNamedFromTheClientHashOnly()
    {
        BlobAuditLog log = NewInstance();

        foreach (AuditEntry entry in AuditFileTests.DocumentedEntries())
        {
            await log.AppendAsync("abc", entry);
        }

        // "ba78…" is the SHA-256 of "abc", the published FIPS 180-2 test vector.
        const string expectedName = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad/audit.jsonl";
        Assert.AreEqual(expectedName, BlobAuditLog.BlobNameFor("abc"));
        CollectionAssert.AreEqual(new[] { expectedName }, container.GetBlobs().Select(b => b.Name).ToArray());
        Assert.AreEqual(string.Concat(AuditFileTests.DocumentedLines), await Text(expectedName));
        Assert.AreEqual("application/x-ndjson", (await container.GetBlobClient(expectedName).GetPropertiesAsync()).Value.ContentType);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_HaveSeparateLogs()
    {
        BlobAuditLog log = NewInstance();
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();

        await log.AppendAsync("client-a", entries[0]);
        await NewInstance().AppendAsync("client-b", entries[1]);
        await log.AppendAsync("client-a", entries[2]);

        Assert.AreNotEqual(BlobAuditLog.BlobNameFor("client-a"), BlobAuditLog.BlobNameFor("client-b"));
        Assert.AreEqual(AuditFileTests.DocumentedLines[0] + AuditFileTests.DocumentedLines[2], await Text(BlobAuditLog.BlobNameFor("client-a")));
        Assert.AreEqual(AuditFileTests.DocumentedLines[1], await Text(BlobAuditLog.BlobNameFor("client-b")));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [DataRow("")]
    [DataRow("   ")]
    public async Task AnEmptyClientId_IsRejected_AndNothingIsWritten(string clientId)
    {
        ArgumentException e = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => NewInstance().AppendAsync(clientId, AuditFileTests.DocumentedEntries()[0]));

        Assert.AreEqual("clientId", e.ParamName);
        Assert.IsEmpty(container.GetBlobs().ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public async Task AMissingContainer_IsAnError()
    {
        var log = new BlobAuditLog(new BlobServiceClient(Azurite).GetBlobContainerClient("missing" + Guid.NewGuid().ToString("N")));

        RequestFailedException e = await Assert.ThrowsExactlyAsync<RequestFailedException>(
            () => log.AppendAsync("abc", AuditFileTests.DocumentedEntries()[0]));

        Assert.AreEqual(BlobErrorCode.ContainerNotFound.ToString(), e.ErrorCode);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task AnAppendThatLostTheRaceToStartTheLog_FailsLoudly_AndLeavesTheWinnersLine()
    {
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();
        var racing = new BlobAuditLog(new RacingContainer(container.Name, () => NewInstance().AppendAsync("abc", entries[1])));

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => racing.AppendAsync("abc", entries[0]));

        Assert.AreEqual("Another writer started this client's audit log at the same time.", e.Message);
        Assert.AreEqual(AuditFileTests.DocumentedLines[1], await Text(BlobAuditLog.BlobNameFor("abc")));
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task AnAppendThatLostARace_FailsLoudly_AndDropsNoOtherWritersLine()
    {
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();
        await NewInstance().AppendAsync("abc", entries[0]);
        var racing = new BlobAuditLog(new RacingContainer(container.Name, () => NewInstance().AppendAsync("abc", entries[1])));

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => racing.AppendAsync("abc", entries[2]));

        Assert.AreEqual("This client's audit log was changed or removed by another writer.", e.Message);
        Assert.AreEqual(AuditFileTests.DocumentedLines[0] + AuditFileTests.DocumentedLines[1], await Text(BlobAuditLog.BlobNameFor("abc")));
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task AnAppendAfterTheLogWasRemoved_FailsLoudly_AndRecreatesNothing()
    {
        AuditEntry[] entries = AuditFileTests.DocumentedEntries();
        await NewInstance().AppendAsync("abc", entries[0]);
        var racing = new BlobAuditLog(new RacingContainer(
            container.Name, () => container.GetBlobClient(BlobAuditLog.BlobNameFor("abc")).DeleteAsync()));

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => racing.AppendAsync("abc", entries[1]));

        Assert.AreEqual("This client's audit log was changed or removed by another writer.", e.Message);
        Assert.IsEmpty(container.GetBlobs().ToArray());
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-STOR-008")]
    [TestCategory("SM-STOR-004")]
    public async Task TheIngestor_StoresTheSameBytes_OnTheAzureStores_AsOnTheHomeStores()
    {
        // The same journal, ingested once into the Home-mode folders and once into the blob container.
        string top = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string customer = "tok_cus_aaaaaaaaaaaaaaaaaaaaaaaa";
            const string card = "tok_card_cccccccccccccccccccccccc";
            string[] lines =
            [
                .. Enumerable.Range(0, 16).Select(seq =>
                    $"{{\"seq\":{seq},\"transaction_token\":\"tok_txn_{seq:x24}\",\"customer_token\":\"{customer}\",\"card_token\":\"{card}\"," +
                    $"\"amount_bin\":{seq % 3},\"merchant_category\":\"{Core.Features.Quantizer.CategoryNames[seq % 4]}\",\"hour\":{seq * 5 % 24},\"is_online\":false}}"),
                "",
            ];
            byte[] journal = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            var time = new FixedTime(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

            async Task Ingest(string journalRoot, IHistoryStore histories, IModelStore profiles, IAuditLog audit)
            {
                Directory.CreateDirectory(Path.Combine(journalRoot, "client-a"));
                await File.WriteAllBytesAsync(Path.Combine(journalRoot, "client-a", "day1.jsonl"), journal);
                await new JournalIngestor(new FolderJournalSource(journalRoot), histories, profiles, audit, new ScoringOptions(), time).RunAsync();
            }

            string modelRoot = Path.Combine(top, "models");
            await Ingest(Path.Combine(top, "journal-home"), new FileHistoryStore(modelRoot), new FileModelStore(modelRoot), new FileAuditLog(modelRoot));
            await Ingest(Path.Combine(top, "journal-azure"), new BlobHistoryStore(container), new BlobModelStore(container), new BlobAuditLog(container));

            var key = new ProfileKey("client-a", customer, card);
            CollectionAssert.AreEqual(new[] { key.RelativePath, BlobHistoryStore.BlobNameFor(key), BlobAuditLog.BlobNameFor("client-a") }
                .Order(StringComparer.Ordinal).ToArray(), container.GetBlobs().Select(b => b.Name).ToArray());
            CollectionAssert.AreEqual(File.ReadAllBytes(new FileModelStore(modelRoot).PathFor(key)), (await container.GetBlobClient(key.RelativePath).DownloadContentAsync()).Value.Content.ToArray());
            CollectionAssert.AreEqual(File.ReadAllBytes(new FileHistoryStore(modelRoot).PathFor(key)), (await container.GetBlobClient(BlobHistoryStore.BlobNameFor(key)).DownloadContentAsync()).Value.Content.ToArray());
            string homeAudit = File.ReadAllText(new FileAuditLog(modelRoot).PathFor("client-a"));
            Assert.AreEqual(homeAudit, await Text(BlobAuditLog.BlobNameFor("client-a")));
            StringAssert.Contains(homeAudit, "\"outcome\":\"archived\",\"records\":16,\"new\":16,\"quarantined\":[[17,\"BlankLine\"]],\"cards_trained\":1}");
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
