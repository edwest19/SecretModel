// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for the Home-mode model store: README SM-STOR-001 (path), SM-STOR-002 (optimistic
// concurrency), SM-STOR-003 (no in-process state; model and history in one file), SM-STOR-005
// (restart doesn't change scores) and SM-STOR-006 (tenant isolation at the storage layer).
// Each test uses its own new folder under the system temp folder and deletes it afterwards.

using System.Diagnostics;
using System.Security.Cryptography;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Tests.Hmm;
using SecretModel.Tests.Scoring;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class FileModelStoreTests
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

    private static ProfileKey Key(string clientId = "abc") => new(clientId, ProfileKeyTests.Customer, ProfileKeyTests.Card);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task Get_WhenNothingIsStored_ReturnsNull()
    {
        Assert.IsNull(await new FileModelStore(root).GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    public async Task Put_WritesTheProfileFile_AtThePathFromTheKeyOnly()
    {
        var store = new FileModelStore(root);
        CardProfile profile = CardProfileFileTests.DocumentedProfile();

        string version = await store.PutAsync(Key(), profile, expectedVersion: null);

        string expectedPath = Path.Combine(
            root, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ProfileKeyTests.Customer, ProfileKeyTests.Card + ".profile.json");
        Assert.AreEqual(expectedPath, store.PathFor(Key()));
        byte[] onDisk = File.ReadAllBytes(expectedPath);
        CollectionAssert.AreEqual(CardProfileFile.Write(profile), onDisk);
        Assert.AreEqual(Sha256Hex(onDisk), version, "The version is the SHA-256 of the file.");

        // Nothing else was written anywhere under the root: the profile and its lock file only.
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal)];
        string relative = Path.GetRelativePath(root, expectedPath);
        CollectionAssert.AreEqual(new[] { relative, relative + ".lock" }, files);
        Assert.AreEqual(0, new FileInfo(expectedPath + ".lock").Length, "The lock file is empty.");
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_ToCreate_FailsWhenAProfileIsAlreadyStored_AndLeavesItUnchanged()
    {
        var store = new FileModelStore(root);
        string v1 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), null));

        Assert.AreEqual("A profile is already stored for this card.", e.Message);
        Assert.AreEqual(v1, (await store.GetAsync(Key()))!.Version);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAStaleVersion_Fails_AndLeavesTheNewerProfileUnchanged()
    {
        var store = new FileModelStore(root);
        string v1 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);
        string v2 = await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), v1);
        Assert.AreNotEqual(v1, v2);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-3.5), v1));

        Assert.AreEqual("The stored profile was changed by another writer.", e.Message);
        StoredProfile stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v2, stored.Version);
        Assert.AreEqual(-2.5, stored.Profile.Baseline[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAVersion_FailsWhenNothingIsStored()
    {
        var store = new FileModelStore(root);
        string version = Sha256Hex(CardProfileFile.Write(CardProfileFileTests.DocumentedProfile()));

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), version));

        Assert.AreEqual("No profile is stored for this card any more.", e.Message);
        Assert.IsNull(await store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public void ConcurrentWriters_FromTheSameVersion_ExactlyOneWins_EveryRound()
    {
        const int writers = 8;
        const int rounds = 10;
        ProfileKey key = Key();
        string version = new FileModelStore(root).PutAsync(key, CardProfileFileTests.DocumentedProfile(-100), null).GetAwaiter().GetResult();

        for (int round = 0; round < rounds; round++)
        {
            // Separate store objects, as separate server processes would have, all starting together.
            CardProfile[] candidates = [.. Enumerable.Range(0, writers).Select(i => CardProfileFileTests.DocumentedProfile(-(round * writers) - i - 1.5))];
            using var start = new Barrier(writers);
            string expected = version;
            Task<string?>[] tasks = [.. Enumerable.Range(0, writers).Select(i => Task.Factory.StartNew(
                () =>
                {
                    var store = new FileModelStore(root);
                    start.SignalAndWait();
                    try
                    {
                        return store.PutAsync(key, candidates[i], expected).GetAwaiter().GetResult();
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
            StoredProfile stored = new FileModelStore(root).GetAsync(key).GetAwaiter().GetResult()!;
            Assert.AreEqual(results[winner], stored.Version, $"Round {round}");
            CollectionAssert.AreEqual(CardProfileFile.Write(candidates[winner]), File.ReadAllBytes(new FileModelStore(root).PathFor(key)), $"Round {round}");
            version = stored.Version;
        }

        string folder = Path.GetDirectoryName(new FileModelStore(root).PathFor(key))!;
        Assert.HasCount(2, Directory.GetFiles(folder), "No temporary files are left behind: only the profile and its lock.");
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Readers_DuringWrites_AlwaysSeeAWholeProfile()
    {
        // Two profiles of different sizes, written in turn while a reader keeps reading. The writer
        // starts after the reader's first read and keeps going until the reader has read at least
        // 200 times and seen both profiles, so reads and writes really do overlap.
        CardProfile a = CardProfileFileTests.RealProfile.Value;
        CardProfile b = CardProfileFileTests.DocumentedProfile();
        string versionA = Sha256Hex(CardProfileFile.Write(a));
        string versionB = Sha256Hex(CardProfileFile.Write(b));
        var writerStore = new FileModelStore(root);
        var readerStore = new FileModelStore(root);
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
                    if (stored.Version == versionA)
                    {
                        Volatile.Write(ref sawA, 1);
                    }
                    else if (stored.Version == versionB)
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
                Assert.IsLessThan(30.0, clock.Elapsed.TotalSeconds, $"Too slow: {writes} writes, {Volatile.Read(ref reads)} reads.");
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
    public async Task NoInProcessState_EachStoreSeesTheOthersWrites()
    {
        var first = new FileModelStore(root);
        var second = new FileModelStore(root);

        string v1 = await first.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-1.5), null);
        Assert.AreEqual(v1, (await first.GetAsync(Key()))!.Version);
        StoredProfile seenBySecond = (await second.GetAsync(Key()))!;
        Assert.AreEqual(v1, seenBySecond.Version);

        string v2 = await second.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), v1);
        StoredProfile seenByFirst = (await first.GetAsync(Key()))!;
        Assert.AreEqual(v2, seenByFirst.Version);
        Assert.AreEqual(-2.5, seenByFirst.Profile.Baseline[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-003")]
    public async Task TheModelAndItsLikelihoodHistory_AreInTheOneFile()
    {
        var store = new FileModelStore(root);
        CardProfile profile = CardProfileFileTests.RealProfile.Value;
        await store.PutAsync(Key(), profile, null);

        CardProfile fromTheFile = CardProfileFile.Read(File.ReadAllBytes(store.PathFor(Key())));

        HiddenMarkovModelTests.AssertBitIdentical(profile.Model, fromTheFile.Model);
        CollectionAssert.AreEqual(profile.Baseline.ToArray(), fromTheFile.Baseline.ToArray());
        CollectionAssert.AreEqual(profile.StateBeforeWindow.ToArray(), fromTheFile.StateBeforeWindow.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-005")]
    public async Task ANewStore_AsAfterARestart_ScoresEverySymbolIdentically()
    {
        CardProfile profile = CardProfileFileTests.RealProfile.Value;
        await new FileModelStore(root).PutAsync(Key(), profile, null);

        StoredProfile afterRestart = (await new FileModelStore(root).GetAsync(Key()))!;

        CardProfileFileTests.AssertScoresIdentical(profile, afterRestart.Profile, new ScoringOptions());
    }

    [TestMethod]
    [TestCategory("SM-STOR-005")]
    public async Task ADamagedProfileFile_IsReported_NotTreatedAsMissing()
    {
        var store = new FileModelStore(root);
        await store.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null);
        File.WriteAllText(store.PathFor(Key()), "{\"format\":\"secretmodel-profile\"");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_WithTheSameTokens_NeverSeeOrChangeEachOthersProfile()
    {
        var store = new FileModelStore(root);
        ProfileKey clientA = Key("client-a");
        ProfileKey clientB = Key("client-b");

        string versionA = await store.PutAsync(clientA, CardProfileFileTests.DocumentedProfile(-1.5), null);
        Assert.IsNull(await store.GetAsync(clientB), "Client B must not see client A's profile.");

        // Client B creating its own profile is not a conflict with client A's, and changes nothing of A's.
        await store.PutAsync(clientB, CardProfileFileTests.DocumentedProfile(-9.5), null);
        StoredProfile a = (await store.GetAsync(clientA))!;
        StoredProfile b = (await store.GetAsync(clientB))!;
        Assert.AreEqual(versionA, a.Version);
        Assert.AreEqual(-1.5, a.Profile.Baseline[0]);
        Assert.AreEqual(-9.5, b.Profile.Baseline[0]);
        Assert.AreNotEqual(store.PathFor(clientA), store.PathFor(clientB));
        Assert.HasCount(2, Directory.GetDirectories(root), "One folder per client.");
    }
}
