// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Azure-mode symbol history store, run against Azurite (README §9.2): SM-STOR-001
// (blob name), SM-STOR-002 (optimistic concurrency with ETags), SM-STOR-003 (no in-process state),
// SM-STOR-004 (instances behave identically), SM-STOR-006 (tenant isolation) and SM-STOR-008 (the
// history file). Azurite must be listening on 127.0.0.1:10000; if it isn't, every test here fails and
// says how to start it, and none is skipped. Each test uses its own new container and deletes it.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Api.Storage;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Tests.Scoring;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class BlobHistoryStoreTests
{
    private BlobContainerClient container = null!;

    [ClassInitialize]
    public static Task RequireAzurite(TestContext context) => BlobModelStoreTests.RequireAzurite(context);

    [TestInitialize]
    public async Task CreateContainer()
    {
        container = new BlobServiceClient(BlobModelStoreTests.Azurite).GetBlobContainerClient("t" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
    }

    [TestCleanup]
    public async Task DeleteContainer() => await container.DeleteIfExistsAsync();

    private static ProfileKey Key(string clientId = "abc") => new(clientId, ProfileKeyTests.Customer, ProfileKeyTests.Card);

    private static CardHistory History(int firstSymbol) => new([0, 1, 5], [firstSymbol, 0, 3]);

    // A second server instance: its own client and connection, the same container.
    private BlobHistoryStore NewInstance() =>
        new(new BlobServiceClient(BlobModelStoreTests.Azurite).GetBlobContainerClient(container.Name));

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task Get_WhenNothingIsStored_ReturnsNull()
    {
        Assert.IsNull(await NewInstance().GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [TestCategory("SM-STOR-008")]
    public async Task Put_WritesOneBlob_NamedFromTheKeyOnly()
    {
        string version = await NewInstance().PutAsync(Key(), CardHistoryFileTests.DocumentedHistory(), expectedVersion: null);

        // "ba78…" is the SHA-256 of "abc", the published FIPS 180-2 test vector.
        string expectedName = $"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad/{ProfileKeyTests.Customer}/{ProfileKeyTests.Card}.history.json";
        Assert.AreEqual(expectedName, BlobHistoryStore.BlobNameFor(Key()));
        CollectionAssert.AreEqual(new[] { expectedName }, container.GetBlobs().Select(b => b.Name).ToArray());
        BlobClient blob = container.GetBlobClient(expectedName);
        Assert.AreEqual(CardHistoryFileTests.DocumentedJson, (await blob.DownloadContentAsync()).Value.Content.ToString());
        BlobProperties properties = (await blob.GetPropertiesAsync()).Value;
        Assert.AreEqual(properties.ETag.ToString(), version, "The version is the blob's ETag.");
        Assert.AreEqual("application/json", properties.ContentType);
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task Get_ReadsTheDocumentedHistory_WithItsETag()
    {
        BlobClient blob = container.GetBlobClient(BlobHistoryStore.BlobNameFor(Key()));
        Response<BlobContentInfo> written = await blob.UploadAsync(BinaryData.FromString(CardHistoryFileTests.DocumentedJson));

        StoredHistory stored = (await NewInstance().GetAsync(Key()))!;

        Assert.AreEqual(written.Value.ETag.ToString(), stored.Version);
        CollectionAssert.AreEqual(CardHistoryFileTests.DocumentedHistory().Seq.ToArray(), stored.History.Seq.ToArray());
        CollectionAssert.AreEqual(CardHistoryFileTests.DocumentedHistory().Symbols.ToArray(), stored.History.Symbols.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task AMissingContainer_IsAnError_NotAMissingHistory()
    {
        var store = new BlobHistoryStore(new BlobServiceClient(BlobModelStoreTests.Azurite).GetBlobContainerClient("missing" + Guid.NewGuid().ToString("N")));

        RequestFailedException read = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => store.GetAsync(Key()));
        RequestFailedException write = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => store.PutAsync(Key(), History(1), null));

        Assert.AreEqual(BlobErrorCode.ContainerNotFound.ToString(), read.ErrorCode);
        Assert.AreEqual(BlobErrorCode.ContainerNotFound.ToString(), write.ErrorCode);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_ToCreate_FailsWhenAHistoryIsAlreadyStored_AndLeavesItUnchanged()
    {
        BlobHistoryStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), History(1), null);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), History(2), null));

        Assert.AreEqual("A history is already stored for this card.", e.Message);
        StoredHistory stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v1, stored.Version);
        Assert.AreEqual(1, stored.History.Symbols[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAStaleVersion_Fails_AndLeavesTheNewerHistoryUnchanged()
    {
        BlobHistoryStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), History(1), null);
        string v2 = await store.PutAsync(Key(), History(2), v1);
        Assert.AreNotEqual(v1, v2);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), History(3), v1));

        Assert.AreEqual("The stored history was changed or removed by another writer.", e.Message);
        StoredHistory stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v2, stored.Version);
        Assert.AreEqual(2, stored.History.Symbols[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAVersion_FailsWhenNothingIsStored()
    {
        BlobHistoryStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), History(1), null);
        await container.GetBlobClient(BlobHistoryStore.BlobNameFor(Key())).DeleteAsync();

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), History(2), v1));

        Assert.AreEqual("The stored history was changed or removed by another writer.", e.Message);
        Assert.IsNull(await store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    [TestCategory("SM-STOR-004")]
    public void ConcurrentInstances_FromTheSameVersion_ExactlyOneWins_EveryRound()
    {
        const int writers = 8;
        const int rounds = 10;
        ProfileKey key = Key();
        string version = NewInstance().PutAsync(key, History(0), null).GetAwaiter().GetResult();

        for (int round = 0; round < rounds; round++)
        {
            // Each writer is a separate store with its own client, as separate server instances are.
            CardHistory[] candidates = [.. Enumerable.Range(0, writers).Select(i => History(1 + (round * writers) + i))];
            BlobHistoryStore[] instances = [.. Enumerable.Range(0, writers).Select(_ => NewInstance())];
            using var start = new Barrier(writers);
            string expected = version;
            Task<string?>[] tasks = [.. Enumerable.Range(0, writers).Select(i => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    try
                    {
                        return instances[i].PutAsync(key, candidates[i], expected).GetAwaiter().GetResult();
                    }
                    catch (ConcurrencyConflictException)
                    {
                        return null;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))];
            string?[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();

            int[] winners = [.. Enumerable.Range(0, writers).Where(i => results[i] is not null)];
            Assert.HasCount(1, winners, $"Round {round}: exactly one writer must win.");
            int winner = winners[0];
            StoredHistory stored = NewInstance().GetAsync(key).GetAwaiter().GetResult()!;
            Assert.AreEqual(results[winner], stored.Version, $"Round {round}");
            CollectionAssert.AreEqual(
                CardHistoryFile.Write(candidates[winner]),
                container.GetBlobClient(BlobHistoryStore.BlobNameFor(key)).DownloadContent().Value.Content.ToArray(),
                $"Round {round}");
            version = stored.Version;
        }
    }

    [TestMethod]
    [TestCategory("SM-STOR-003")]
    [TestCategory("SM-STOR-004")]
    public async Task NoInProcessState_EachInstanceSeesTheOthersWrites()
    {
        BlobHistoryStore first = NewInstance();
        BlobHistoryStore second = NewInstance();

        string v1 = await first.PutAsync(Key(), History(1), null);
        Assert.AreEqual(v1, (await second.GetAsync(Key()))!.Version);

        string v2 = await second.PutAsync(Key(), History(2), v1);
        StoredHistory seenByFirst = (await first.GetAsync(Key()))!;
        Assert.AreEqual(v2, seenByFirst.Version);
        Assert.AreEqual(2, seenByFirst.History.Symbols[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task ADamagedHistoryBlob_IsReported_NotTreatedAsMissing()
    {
        BlobHistoryStore store = NewInstance();
        await store.PutAsync(Key(), History(1), null);
        await container.GetBlobClient(BlobHistoryStore.BlobNameFor(Key())).UploadAsync(BinaryData.FromString("{\"format\":\"secretmodel-history\""), overwrite: true);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task TheHistory_SitsNextToTheProfile_AsASeparateBlobWithItsOwnVersion()
    {
        var profiles = new BlobModelStore(container);
        BlobHistoryStore histories = NewInstance();
        string profileVersion = await profiles.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null);
        string historyVersion = await histories.PutAsync(Key(), History(1), null);

        // Changing the history changes nothing of the profile, and the other way round.
        await histories.PutAsync(Key(), History(2), historyVersion);
        Assert.AreEqual(profileVersion, (await profiles.GetAsync(Key()))!.Version);
        await profiles.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), profileVersion);
        Assert.AreEqual(2, (await histories.GetAsync(Key()))!.History.Symbols[0]);

        string folder = $"{Key().ClientHash}/{ProfileKeyTests.Customer}/";
        CollectionAssert.AreEqual(
            new[] { folder + ProfileKeyTests.Card + ".history.json", folder + ProfileKeyTests.Card + ".profile.json" },
            container.GetBlobs().Select(b => b.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_WithTheSameTokens_NeverSeeOrChangeEachOthersHistory()
    {
        BlobHistoryStore store = NewInstance();
        ProfileKey clientA = Key("client-a");
        ProfileKey clientB = Key("client-b");

        string versionA = await store.PutAsync(clientA, History(1), null);
        Assert.IsNull(await store.GetAsync(clientB), "Client B must not see client A's history.");

        await store.PutAsync(clientB, History(9), null);
        StoredHistory a = (await store.GetAsync(clientA))!;
        StoredHistory b = (await store.GetAsync(clientB))!;
        Assert.AreEqual(versionA, a.Version);
        Assert.AreEqual(1, a.History.Symbols[0]);
        Assert.AreEqual(9, b.History.Symbols[0]);
        CollectionAssert.AreEquivalent(
            new[] { BlobHistoryStore.BlobNameFor(clientA), BlobHistoryStore.BlobNameFor(clientB) },
            container.GetBlobs().Select(x => x.Name).ToArray(),
            "One blob per client, under that client's own hash.");
    }
}
