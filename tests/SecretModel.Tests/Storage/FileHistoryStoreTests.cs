// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.
//
// Tests for the Home-mode history store: README SM-STOR-008 (each card's symbol history, next to its
// profile), SM-STOR-001 (path), SM-STOR-002 (optimistic concurrency), SM-STOR-003 (no in-process
// state) and SM-STOR-006 (tenant isolation at the storage layer). They mirror FileModelStoreTests.
// Each test uses its own new folder under the system temp folder and deletes it afterwards.

using System.Security.Cryptography;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Tests.Scoring;

namespace SecretModel.Tests.Storage;

[TestClass]
public sealed class FileHistoryStoreTests
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

    // Histories that differ only in their first symbol.
    private static CardHistory History(int firstSymbol) => new([1, 2, 3], [firstSymbol, 0, 511]);

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task Get_WhenNothingIsStored_ReturnsNull()
    {
        Assert.IsNull(await new FileHistoryStore(root).GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-001")]
    [TestCategory("SM-STOR-008")]
    public async Task Put_WritesTheHistoryFile_AtThePathFromTheKeyOnly()
    {
        var store = new FileHistoryStore(root);

        string version = await store.PutAsync(Key(), CardHistoryFileTests.DocumentedHistory(), expectedVersion: null);

        // "ba78…" is the SHA-256 of "abc", the published FIPS 180-2 test vector.
        string expectedPath = Path.Combine(
            root, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ProfileKeyTests.Customer, ProfileKeyTests.Card + ".history.json");
        Assert.AreEqual(expectedPath, store.PathFor(Key()));
        byte[] onDisk = File.ReadAllBytes(expectedPath);
        Assert.AreEqual(CardHistoryFileTests.DocumentedJson, System.Text.Encoding.UTF8.GetString(onDisk));
        Assert.AreEqual(Sha256Hex(onDisk), version, "The version is the SHA-256 of the file.");

        // Nothing else was written anywhere under the root: the history and its lock file only.
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal)];
        string relative = Path.GetRelativePath(root, expectedPath);
        CollectionAssert.AreEqual(new[] { relative, relative + ".lock" }, files);
        Assert.AreEqual(0, new FileInfo(expectedPath + ".lock").Length, "The lock file is empty.");
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_ToCreate_FailsWhenAHistoryIsAlreadyStored_AndLeavesItUnchanged()
    {
        var store = new FileHistoryStore(root);
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
        var store = new FileHistoryStore(root);
        string v1 = await store.PutAsync(Key(), History(1), null);
        string v2 = await store.PutAsync(Key(), History(2), v1);
        Assert.AreNotEqual(v1, v2);

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), History(3), v1));

        Assert.AreEqual("The stored history was changed by another writer.", e.Message);
        StoredHistory stored = (await store.GetAsync(Key()))!;
        Assert.AreEqual(v2, stored.Version);
        Assert.AreEqual(2, stored.History.Symbols[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public async Task Put_WithAVersion_FailsWhenNothingIsStored()
    {
        var store = new FileHistoryStore(root);
        string version = Sha256Hex(CardHistoryFile.Write(History(1)));

        ConcurrencyConflictException e = await Assert.ThrowsExactlyAsync<ConcurrencyConflictException>(
            () => store.PutAsync(Key(), History(1), version));

        Assert.AreEqual("No history is stored for this card any more.", e.Message);
        Assert.IsNull(await store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-002")]
    public void ConcurrentWriters_FromTheSameVersion_ExactlyOneWins_EveryRound()
    {
        const int writers = 8;
        const int rounds = 10;
        ProfileKey key = Key();
        string version = new FileHistoryStore(root).PutAsync(key, History(0), null).GetAwaiter().GetResult();

        for (int round = 0; round < rounds; round++)
        {
            // Separate store objects, as separate server processes would have, all starting together.
            CardHistory[] candidates = [.. Enumerable.Range(0, writers).Select(i => History((round * writers) + i + 1))];
            using var start = new Barrier(writers);
            string expected = version;
            Task<string?>[] tasks = [.. Enumerable.Range(0, writers).Select(i => Task.Factory.StartNew(
                () =>
                {
                    var store = new FileHistoryStore(root);
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
            StoredHistory stored = new FileHistoryStore(root).GetAsync(key).GetAwaiter().GetResult()!;
            Assert.AreEqual(results[winner], stored.Version, $"Round {round}");
            CollectionAssert.AreEqual(CardHistoryFile.Write(candidates[winner]), File.ReadAllBytes(new FileHistoryStore(root).PathFor(key)), $"Round {round}");
            version = stored.Version;
        }

        string folder = Path.GetDirectoryName(new FileHistoryStore(root).PathFor(key))!;
        Assert.HasCount(2, Directory.GetFiles(folder), "No temporary files are left behind: only the history and its lock.");
    }

    [TestMethod]
    [TestCategory("SM-STOR-003")]
    public async Task NoInProcessState_EachStoreSeesTheOthersWrites()
    {
        var first = new FileHistoryStore(root);
        var second = new FileHistoryStore(root);

        string v1 = await first.PutAsync(Key(), History(1), null);
        Assert.AreEqual(v1, (await second.GetAsync(Key()))!.Version);

        string v2 = await second.PutAsync(Key(), History(2), v1);
        StoredHistory seenByFirst = (await first.GetAsync(Key()))!;
        Assert.AreEqual(v2, seenByFirst.Version);
        Assert.AreEqual(2, seenByFirst.History.Symbols[0]);
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task ADamagedHistoryFile_IsReported_NotTreatedAsMissing()
    {
        var store = new FileHistoryStore(root);
        await store.PutAsync(Key(), History(1), null);
        File.WriteAllText(store.PathFor(Key()), "{\"format\":\"secretmodel-history\"");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.GetAsync(Key()));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public async Task TheHistory_SitsNextToTheProfile_AsASeparateFileWithItsOwnVersionAndLock()
    {
        var profiles = new FileModelStore(root);
        var histories = new FileHistoryStore(root);
        string profileVersion = await profiles.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(), null);
        string historyVersion = await histories.PutAsync(Key(), History(1), null);

        // Changing the history changes nothing of the profile, and the other way round.
        await histories.PutAsync(Key(), History(2), historyVersion);
        Assert.AreEqual(profileVersion, (await profiles.GetAsync(Key()))!.Version);
        string newProfileVersion = await profiles.PutAsync(Key(), CardProfileFileTests.DocumentedProfile(-2.5), profileVersion);
        Assert.AreNotEqual(profileVersion, newProfileVersion);
        Assert.AreEqual(2, (await histories.GetAsync(Key()))!.History.Symbols[0]);

        Assert.AreEqual(Path.GetDirectoryName(profiles.PathFor(Key())), Path.GetDirectoryName(histories.PathFor(Key())));
        string folder = Path.GetDirectoryName(histories.PathFor(Key()))!;
        string card = ProfileKeyTests.Card;
        CollectionAssert.AreEqual(
            new[] { card + ".history.json", card + ".history.json.lock", card + ".profile.json", card + ".profile.json.lock" },
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_WithTheSameTokens_NeverSeeOrChangeEachOthersHistory()
    {
        var store = new FileHistoryStore(root);
        ProfileKey clientA = Key("client-a");
        ProfileKey clientB = Key("client-b");

        string versionA = await store.PutAsync(clientA, History(1), null);
        Assert.IsNull(await store.GetAsync(clientB), "Client B must not see client A's history.");

        // Client B creating its own history is not a conflict with client A's, and changes nothing of A's.
        await store.PutAsync(clientB, History(9), null);
        StoredHistory a = (await store.GetAsync(clientA))!;
        StoredHistory b = (await store.GetAsync(clientB))!;
        Assert.AreEqual(versionA, a.Version);
        Assert.AreEqual(1, a.History.Symbols[0]);
        Assert.AreEqual(9, b.History.Symbols[0]);
        Assert.AreNotEqual(store.PathFor(clientA), store.PathFor(clientB));
        Assert.HasCount(2, Directory.GetDirectories(root), "One folder per client.");
    }
}
