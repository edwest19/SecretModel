// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. The Azurite
// constant and check made internal on 2026-11-27 so the other Blob test classes share them.
//
// Tests for the Azure-mode model store, run against Azurite, Microsoft's local Blob Storage
// emulator (README §9.2): SM-STOR-001 (blob name), SM-STOR-002 (optimistic concurrency with ETags),
// SM-STOR-003 (no in-process state; model and history in one blob), SM-STOR-004 (instances behave
// identically), SM-STOR-005 (restart doesn't change scores) and SM-STOR-006 (tenant isolation).
// Azurite must be listening on 127.0.0.1:10000. If it isn't, every test here fails and says how to
// start it; none is skipped. Each test uses its own new container and deletes it afterwards.

using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Api.Storage;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Tests.Hmm;
using SecretModel.Tests.Scoring;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class BlobModelStoreTests
{
    // The SDK's documented shortcut for the emulator's public development account. It holds no key.
    internal const string Azurite = "UseDevelopmentStorage=true";

    private const string HowToStartAzurite =
        "Azurite is not reachable on 127.0.0.1:10000, so the Blob store tests can't run. It needs Node.js 22 or newer. "
        + "Start it from the repository folder with: npm ci --prefix tools/azurite --ignore-scripts; then "
        + "tools/azurite/node_modules/.bin/azurite-blob --disableTelemetry --location <an empty folder> "
        + "(README section 9.2).";

    private BlobContainerClient container = null!;

    [ClassInitialize]
    public static async Task RequireAzurite(TestContext context)
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        try
        {
            await new BlobServiceClient(Azurite, options).GetPropertiesAsync(context.CancellationToken);
        }
        catch (Exception e) when (e is RequestFailedException or AggregateException or OperationCanceledException)
        {
            Assert.Fail($"{HowToStartAzurite} ({e.GetType().Name}: {e.Message})");
        }
    }

    [TestInitialize]
    public async Task CreateContainer()
    {
        container = new BlobServiceClient(Azurite).GetBlobContainerClient("t" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
    }

    [TestCleanup]
    public async Task DeleteContainer() => await container.DeleteIfExistsAsync();

    private static ProfileKey Key(string clientId = "abc") => new(clientId, ProfileKeyTests.Customer, ProfileKeyTests.Card);

    // A second server instance: its own client and connection, the same container.
    private BlobModelStore NewInstance() =>
        new(new BlobServiceClient(Azurite).GetBlobContainerClient(container.Name));

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task Get_WhenNothingIsStored_ReturnsNull()
    {
        Assert.IsNull(await NewInstance().GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task Put_WritesOneBlob_NamedFromTheKeyOnly()
    {
        CardProfile profile = CardProfileFileTests.DocumentedProfile();

        string version = await NewInstance().PutAsync(Key(), profile, expectedVersion: null);

        string[] names = [.. container.GetBlobs().Select(b => b.Name)];
        CollectionAssert.AreEqual(
            new[] { $"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad/{ProfileKeyTests.Customer}/{ProfileKeyTests.Card}.profile.json" },
            names);
        BlobClient blob = container.GetBlobClient(names[0]);
        CollectionAssert.AreEqual(CardProfileFile.Write(profile), (await blob.DownloadContentAsync()).Value.Content.ToArray());
        BlobProperties properties = (await blob.GetPropertiesAsync()).Value;
        Assert.AreEqual(properties.ETag.ToString(), version, "The version is the blob's ETag.");
        Assert.AreEqual("application/json", properties.ContentType);
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task AMissingContainer_IsAnError_NotAMissingProfile()
    {
        var store = new BlobModelStore(new BlobServiceClient(Azurite).GetBlobContainerClient("missing" + Guid.NewGuid().ToString("N")));

        RequestFailedException read = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => store.GetAsync(Key()));
        RequestFailedException write = await Assert.ThrowsExactlyAsync<RequestFailedException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null));

        Assert.AreEqual(BlobErrorCode.ContainerNotFound.ToString(), read.ErrorCode);
        Assert.AreEqual(BlobErrorCode.ContainerNotFound.ToString(), write.ErrorCode);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_ToCreate_FailsWhenAProfileIsAlreadyStored_AndLeavesItUnchanged()
    {
        BlobModelStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), null));

        Assert.AreEqual("A profile is already stored for this card.", e.Message);
        StoredProfile stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v1, stored.Version);
        Assert.AreEqual(-1.5, stored.Profile.Baseline[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAStaleVersion_Fails_AndLeavesTheNewerProfileUnchanged()
    {
        BlobModelStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);
        string v2 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), v1);
        Assert.AreNotEqual(v1, v2);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-3.5), v1));

        Assert.AreEqual("The stored profile was changed or removed by another writer.", e.Message);
        StoredProfile stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v2, stored.Version);
        Assert.AreEqual(-2.5, stored.Profile.Baseline[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAVersion_FailsWhenNothingIsStored()
    {
        BlobModelStore store = NewInstance();
        string v1 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null);
        await container.GetBlobClient(Key().RelativePath).DeleteAsync();

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), v1));

        Assert.AreEqual("The stored profile was changed or removed by another writer.", e.Message);
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
        string version = NewInstance().PutAsync(key, CardProfileFileTests.DocumentedProfile(-100), null).GetAwaiter().GetResult();

        for (int round = 0; round < rounds; round++)
        {
            // Each writer is a separate store with its own client, as separate server instances are.
            CardProfile[] candidates = [.. Enumerable.Range(0, writers).Select(i => CardProfileFileTests.DocumentedProfile(-(round * writers) - i - 1.5))];
            BlobModelStore[] instances = [.. Enumerable.Range(0, writers).Select(_ => NewInstance())];
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
            StoredProfile stored = NewInstance().GetAsync(key).GetAwaiter().GetResult()!;
            Assert.AreEqual(results[winner], stored.Version, $"Round {round}");
            CollectionAssert.AreEqual(
                CardProfileFile.Write(candidates[winner]),
                container.GetBlobClient(key.RelativePath).DownloadContent().Value.Content.ToArray(),
                $"Round {round}");
            version = stored.Version;
        }
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Readers_DuringWrites_AlwaysSeeAWholeProfile()
    {
        // As in FileModelStoreTests: the writer starts after the reader's first read and keeps going
        // until at least 200 writes, 200 reads and both profiles seen.
        CardProfile a = CardProfileFileTests.RealProfile.Value;
        CardProfile b = CardProfileFileTests.DocumentedProfile();
        byte[] bytesA = CardProfileFile.Write(a);
        byte[] bytesB = CardProfileFile.Write(b);
        BlobModelStore writerStore = NewInstance();
        BlobModelStore readerStore = NewInstance();
        string current = await writerStore.PutAsync(Key(), a, null);

        using var done = new CancellationTokenSource();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, sawA = 0, sawB = 0;
        Task reader = Task.Run(async () =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    StoredProfile? stored = await readerStore.GetAsync(Key());
                    Assert.IsNotNull(stored, "The profile must never be missing while it is replaced.");
                    byte[] seen = CardProfileFile.Write(stored.Profile);
                    if (seen.AsSpan().SequenceEqual(bytesA))
                    {
                        Volatile.Write(ref sawA, 1);
                    }
                    else if (seen.AsSpan().SequenceEqual(bytesB))
                    {
                        Volatile.Write(ref sawB, 1);
                    }
                    else
                    {
                        Assert.Fail("A read must give one whole profile.");
                    }

                    Interlocked.Increment(ref reads);
                    firstRead.TrySetResult();
                }
            }
            catch (Exception e)
            {
                firstRead.TrySetException(e);
                throw;
            }
        });

        int writes = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            await firstRead.Task;
            while (!reader.IsCompleted
                && (writes < 200 || Volatile.Read(ref reads) < 200 || Volatile.Read(ref sawA) == 0 || Volatile.Read(ref sawB) == 0))
            {
                Assert.IsLessThan(60.0, clock.Elapsed.TotalSeconds, $"Too slow: {writes} writes, {Volatile.Read(ref reads)} reads.");
                current = await writerStore.PutAsync(Key(), writes % 2 == 0 ? b : a, current);
                writes++;
            }
        }
        finally
        {
            // Stop the reader and let it finish, without hiding a writer failure.
            await done.CancelAsync();
            await Task.WhenAny(reader);
        }

        await reader; // a failed read fails the test here
        Assert.IsGreaterThanOrEqualTo(200, writes);
        Assert.IsGreaterThanOrEqualTo(200, reads);
    }

    [TestMethod]
    [TestCategory("SM-STOR-003")]
    [TestCategory("SM-STOR-004")]
    public async Task NoInProcessState_EachInstanceSeesTheOthersWrites()
    {
        BlobModelStore first = NewInstance();
        BlobModelStore second = NewInstance();

        string v1 = await first.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);
        Assert.AreEqual(v1, (await first.GetAsync(Key()))!.Version);
        Assert.AreEqual(v1, (await second.GetAsync(Key()))!.Version);

        string v2 = await second.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), v1);
        StoredProfile seenByFirst = (await first.GetAsync(Key()))!;
        Assert.AreEqual(v2, seenByFirst.Version);
        Assert.AreEqual(-2.5, seenByFirst.Profile.Baseline[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-003")]
    public async Task TheModelAndItsLikelihoodHistory_AreInTheOneBlob()
    {
        CardProfile profile = CardProfileFileTests.RealProfile.Value;
        await NewInstance().PutAsync(Key(), profile, null);

        Assert.HasCount(1, container.GetBlobs().ToArray());
        CardProfile fromTheBlob = CardProfileFile.Read((await container.GetBlobClient(Key().RelativePath).DownloadContentAsync()).Value.Content.ToArray());

        HiddenMarkovModelTests.AssertBitIdentical(profile.Model, fromTheBlob.Model);
        CollectionAssert.AreEqual(profile.Baseline.ToArray(), fromTheBlob.Baseline.ToArray());
        CollectionAssert.AreEqual(profile.StateBeforeWindow.ToArray(), fromTheBlob.StateBeforeWindow.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-005")]
    [TestCategory("SM-STOR-004")]
    public async Task ANewInstance_AsAfterARestart_ScoresEverySymbolIdentically()
    {
        CardProfile profile = CardProfileFileTests.RealProfile.Value;
        await NewInstance().PutAsync(Key(), profile, null);

        StoredProfile afterRestart = (await NewInstance().GetAsync(Key()))!;

        CardProfileFileTests.AssertScoresIdentical(profile, afterRestart.Profile, new ScoringOptions());
    }

    [TestMethod]
    [TestCategory("SM-STOR-005")]
    public async Task ADamagedProfileBlob_IsReported_NotTreatedAsMissing()
    {
        BlobModelStore store = NewInstance();
        await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null);
        await container.GetBlobClient(Key().RelativePath).UploadAsync(BinaryData.FromString("{\"format\":\"secretmodel-profile\""), overwrite: true);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_WithTheSameTokens_NeverSeeOrChangeEachOthersProfile()
    {
        BlobModelStore store = NewInstance();
        ProfileKey clientA = Key("client-a");
        ProfileKey clientB = Key("client-b");

        string versionA = await store.PutAsync(clientA, CardProfileFileTests.DocumentedProfile(-1.5), null);
        Assert.IsNull(await store.GetAsync(clientB), "Client B must not see client A's profile.");

        await store.PutAsync(clientB, CardProfileFileTests.DocumentedProfile(-9.5), null);
        StoredProfile a = (await store.GetAsync(clientA))!;
        StoredProfile b = (await store.GetAsync(clientB))!;
        Assert.AreEqual(versionA, a.Version);
        Assert.AreEqual(-1.5, a.Profile.Baseline[0]);
        Assert.AreEqual(-9.5, b.Profile.Baseline[0]);
        CollectionAssert.AreEquivalent(
            new[] { clientA.RelativePath, clientB.RelativePath },
            container.GetBlobs().Select(x => x.Name).ToArray(),
            "One blob per client, under that client's own hash.");
    }
}
