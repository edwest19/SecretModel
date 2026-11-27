// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for Home-mode journal ingestion (README §8.2): SM-JRNL-003 (idempotent; SeqConflict),
// SM-JRNL-004 (grouped by client, customer and card, ordered by seq, retrained on the full history),
// SM-JRNL-005 (a rejected file is quarantined unchanged and nothing from it is used), SM-JRNL-006
// (archive on success, kept in place for retry after a partial failure), SM-JRNL-007 (no training
// below the minimum) and SM-JRNL-008 (one audit entry per file, with no transaction content), plus
// tenant isolation (SM-AUTH-006, SM-STOR-006) and the name rules.
//
// Each test uses its own new folders under the system temp folder and deletes them afterwards. All
// tokens are made up. 4111111111111111 is the published Visa test number, used only as an input that
// must be rejected. Expected profiles are built with CardProfile.Build, which has its own tests: here
// it checks which history, in which order, ingestion trained on.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecretModel.Core.Features;
using SecretModel.Core.Journal;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

namespace SecretModel.Tests.Journal;

[TestClass]
public sealed class JournalIngestorTests
{
    private const string Client = "client-a";
    private const string CusX = "tok_cus_aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CusY = "tok_cus_bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CardA = "tok_card_cccccccccccccccccccccccc";
    private const string CardB = "tok_card_dddddddddddddddddddddddd";

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 20, 15, 3, TimeSpan.Zero);

    private string top = "";
    private string journalRoot = "";
    private string modelRoot = "";

    [TestInitialize]
    public void CreateRoots()
    {
        top = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        journalRoot = Path.Combine(top, "journal");
        modelRoot = Path.Combine(top, "models");
        Directory.CreateDirectory(journalRoot);
        Directory.CreateDirectory(modelRoot);
    }

    [TestCleanup]
    public void DeleteRoots() => Directory.Delete(top, recursive: true);

    // ---- Test data -------------------------------------------------------------------------------

    // The features change with seq, so a card's symbols differ from one transaction to the next.
    private static (int AmountBin, string Category, int Hour, bool Online) Features(long seq) =>
        ((int)(seq % 3), Quantizer.CategoryNames[(int)(seq % 4)], (int)(seq * 5 % 24), seq % 5 == 0);

    private static int SymbolOf(long seq)
    {
        var f = Features(seq);
        return Quantizer.ToSymbol(f.AmountBin, f.Category, f.Hour, f.Online);
    }

    // One journal line for the transaction with this seq. A different amount bin gives the same seq
    // another symbol.
    private static string Line(long seq, string card = CardA, string customer = CusX, int? amountBin = null)
    {
        var f = Features(seq);
        return $"{{\"seq\":{seq},\"transaction_token\":\"tok_txn_{seq:x24}\",\"customer_token\":\"{customer}\",\"card_token\":\"{card}\"," +
               $"\"amount_bin\":{amountBin ?? f.AmountBin},\"merchant_category\":\"{f.Category}\",\"hour\":{f.Hour},\"is_online\":{(f.Online ? "true" : "false")}}}";
    }

    private static string[] Lines(IEnumerable<int> seqs, string card = CardA, string customer = CusX) =>
        [.. seqs.Select(s => Line(s, card, customer))];

    private static string JournalText(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    private static string Sha256Hex(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static int[] Symbols(IEnumerable<int> seqs) => [.. seqs.Select(s => SymbolOf(s))];

    private static byte[] ProfileBytesFor(int[] symbols) => CardProfileFile.Write(CardProfile.Build(symbols, new ScoringOptions()));

    // ---- Helpers ---------------------------------------------------------------------------------

    private string Drop(string name, string content, string client = Client)
    {
        string folder = Path.Combine(journalRoot, client);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private string Landed(string name, string client = Client) => Path.Combine(journalRoot, client, name);

    private string Archived(string name, string client = Client) => Path.Combine(journalRoot, client, "archive", name);

    private string Quarantined(string name, string client = Client) => Path.Combine(journalRoot, client, "quarantine", name);

    private JournalIngestor Ingestor(IHistoryStore? histories = null, IModelStore? profiles = null) => new(
        new FolderJournalSource(journalRoot),
        histories ?? new FileHistoryStore(modelRoot),
        profiles ?? new FileModelStore(modelRoot),
        new FileAuditLog(modelRoot),
        new ScoringOptions(),
        new FixedTime(Now));

    private static ProfileKey Key(string card = CardA, string customer = CusX, string client = Client) => new(client, customer, card);

    private async Task<CardHistory?> HistoryOf(ProfileKey key) => (await new FileHistoryStore(modelRoot).GetAsync(key))?.History;

    private async Task<CardProfile?> ProfileOf(ProfileKey key) => (await new FileModelStore(modelRoot).GetAsync(key))?.Profile;

    private byte[] HistoryBytes(ProfileKey key) => File.ReadAllBytes(new FileHistoryStore(modelRoot).PathFor(key));

    private byte[] ProfileBytes(ProfileKey key) => File.ReadAllBytes(new FileModelStore(modelRoot).PathFor(key));

    private string AuditText(string client = Client) => File.ReadAllText(new FileAuditLog(modelRoot).PathFor(client));

    // Each audit line as its property names, in order, and their values: strings decoded, anything
    // else as written.
    private List<(string Names, Dictionary<string, string> Values)> AuditLines(string client = Client)
    {
        var lines = new List<(string, Dictionary<string, string>)>();
        foreach (string line in AuditText(client).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            var values = new Dictionary<string, string>();
            foreach (JsonProperty p in document.RootElement.EnumerateObject())
            {
                values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }

            lines.Add((string.Join(",", values.Keys), values));
        }

        return lines;
    }

    private const string ArchivedNames = "time,file,sha256,outcome,records,new,quarantined,cards_trained";
    private const string RetryNames = "time,file,sha256,outcome,records,new,quarantined,cards_trained,cards_failed";

    private static void AssertCounts(Dictionary<string, string> line, string outcome, int records, int @new, string quarantined, int cardsTrained, int? cardsFailed = null)
    {
        Assert.AreEqual(outcome, line["outcome"]);
        Assert.AreEqual(records.ToString(), line["records"], "records");
        Assert.AreEqual(@new.ToString(), line["new"], "new");
        Assert.AreEqual(quarantined, line["quarantined"], "quarantined");
        Assert.AreEqual(cardsTrained.ToString(), line["cards_trained"], "cards_trained");
        if (cardsFailed is { } failed)
        {
            Assert.AreEqual(failed.ToString(), line["cards_failed"], "cards_failed");
        }
    }

    private static Exception Failure(string kind) => kind switch
    {
        "io" => new IOException("The disk is full."),
        "access" => new UnauthorizedAccessException("Access is denied."),
        "conflict" => new ConcurrencyConflictException("The stored history was changed by another writer."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A history store that fails to save one card.
    private sealed class FailingHistoryStore(IHistoryStore inner, string failingCard, Exception error) : IHistoryStore
    {
        public Task<StoredHistory?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

        public Task<string> PutAsync(ProfileKey key, CardHistory history, string? expectedVersion, CancellationToken cancellationToken = default) =>
            key.CardToken == failingCard ? throw error : inner.PutAsync(key, history, expectedVersion, cancellationToken);
    }

    // A profile store whose first save fails.
    private sealed class FailOnceModelStore(IModelStore inner, Exception error) : IModelStore
    {
        private int puts;

        public Task<StoredProfile?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

        public Task<string> PutAsync(ProfileKey key, CardProfile profile, string? expectedVersion, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref puts) == 1 ? throw error : inner.PutAsync(key, profile, expectedVersion, cancellationToken);
    }

    // ---- SM-JRNL-008, SM-JRNL-006, SM-JRNL-002: the documented examples together -----------------

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-JRNL-002")]
    public async Task TheDocumentedJournal_IsArchived_WithTheDocumentedAuditLine()
    {
        string landed = Drop("day1.jsonl", JournalFileTests.DocumentedExample);

        IReadOnlyList<IngestedFile> handled = await Ingestor().RunAsync();

        // Line 1 of docs/formats/audit-v1.md, whose hash is the journal example's.
        Assert.AreEqual(AuditFileTests.DocumentedLines[0], AuditText());
        Assert.HasCount(1, handled);
        Assert.AreEqual(Client, handled[0].ClientId);
        Assert.AreEqual(AuditOutcome.Archived, handled[0].Entry.Outcome);
        Assert.IsFalse(File.Exists(landed));
        Assert.AreEqual(JournalFileTests.DocumentedExample, File.ReadAllText(Archived("day1.jsonl")), "Archived unchanged.");

        // The three records went to the two cards' histories; neither has enough to train.
        const string cus = "tok_cus_c0ffeec0ffeec0ffeec0ffee";
        const string cafe = "tok_card_cafecafecafecafecafecafe";
        const string beef = "tok_card_beefbeefbeefbeefbeefbeef";
        CardHistory cafeHistory = (await HistoryOf(Key(cafe, cus)))!;
        CollectionAssert.AreEqual(new long[] { 1 }, cafeHistory.Seq.ToArray());
        CollectionAssert.AreEqual(new[] { Quantizer.ToSymbol(3, "grocery", 14, false) }, cafeHistory.Symbols.ToArray());
        CardHistory beefHistory = (await HistoryOf(Key(beef, cus)))!;
        CollectionAssert.AreEqual(new long[] { 2, 999_999_999_999 }, beefHistory.Seq.ToArray());
        CollectionAssert.AreEqual(
            new[] { Quantizer.ToSymbol(7, "online_market", 23, true), Quantizer.ToSymbol(0, "atm", 0, false) }, beefHistory.Symbols.ToArray());
        Assert.IsNull(await ProfileOf(Key(cafe, cus)));
        Assert.IsNull(await ProfileOf(Key(beef, cus)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    public async Task TheAuditLog_HoldsCountsAndReasons_AndNoTransactionContent()
    {
        string[] lines =
        [
            .. Lines(Enumerable.Range(0, 14)),                                   // lines 1-14
            Line(3, amountBin: 7),                                               // 15: seq 3 again, another symbol
            Line(0, CardB, CusY),                                                // 16
            Line(1, CardB, CusY).Replace("}", ",\"country_code\":\"US\"}"),       // 17: UnknownProperty
            Line(2, CardB, CusY).Replace($"\"{Features(2).Category}\"", "\"Grocery\""),              // 18: InvalidMerchantCategory
        ];
        string content = JournalText(lines);
        Drop("day1.jsonl", content);

        await Ingestor().RunAsync();

        // Lines 4 and 15 disagree about seq 3, so neither is used. Card A keeps 13 transactions and is
        // not trained; card B gets one.
        string expected =
            $"{{\"time\":\"2026-11-26T20:15:03Z\",\"file\":\"day1.jsonl\",\"sha256\":\"{Sha256Hex(content)}\",\"outcome\":\"archived\"," +
            "\"records\":14,\"new\":14,\"quarantined\":[[4,\"SeqConflict\"],[15,\"SeqConflict\"],[17,\"UnknownProperty\"],[18,\"InvalidMerchantCategory\"]],\"cards_trained\":0}\n";
        string audit = AuditText();
        Assert.AreEqual(expected, audit);

        string[] forbidden = [CusX, CusY, CardA, CardB, "tok_txn_", "US", "country_code", "Grocery", .. Quantizer.CategoryNames];
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, audit);
        }
    }

    // ---- SM-JRNL-004: grouping and order ---------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-004")]
    public async Task Records_AreGroupedByCustomerAndCard_AndOrderedBySeq()
    {
        string[] ax = Lines(Enumerable.Range(0, 16).Reverse());          // card A of customer X, newest first
        string[] bx = [Line(2, CardB), Line(0, CardB), Line(1, CardB)];  // card B of customer X
        string[] ay = [Line(101, CardA, CusY), Line(100, CardA, CusY)];  // the same card token under customer Y
        string[] mixed = [ax[0], bx[0], ax[1], ay[0], ax[2], bx[1], ax[3], ay[1], ax[4], bx[2], .. ax[5..]];
        Drop("day1.jsonl", JournalText(mixed));

        await Ingestor().RunAsync();

        CardHistory a = (await HistoryOf(Key(CardA, CusX)))!;
        CollectionAssert.AreEqual(Enumerable.Range(0, 16).Select(s => (long)s).ToArray(), a.Seq.ToArray());
        CollectionAssert.AreEqual(Symbols(Enumerable.Range(0, 16)), a.Symbols.ToArray());
        CardHistory b = (await HistoryOf(Key(CardB, CusX)))!;
        CollectionAssert.AreEqual(new long[] { 0, 1, 2 }, b.Seq.ToArray());
        CollectionAssert.AreEqual(Symbols([0, 1, 2]), b.Symbols.ToArray());
        CardHistory y = (await HistoryOf(Key(CardA, CusY)))!;
        CollectionAssert.AreEqual(new long[] { 100, 101 }, y.Seq.ToArray());
        CollectionAssert.AreEqual(Symbols([100, 101]), y.Symbols.ToArray());

        // Trained on the symbols in seq order, not in the file's order.
        byte[] inSeqOrder = ProfileBytesFor(Symbols(Enumerable.Range(0, 16)));
        byte[] inFileOrder = ProfileBytesFor(Symbols(Enumerable.Range(0, 16).Reverse()));
        CollectionAssert.AreNotEqual(inFileOrder, inSeqOrder, "The test needs an order that changes the profile.");
        CollectionAssert.AreEqual(inSeqOrder, ProfileBytes(Key(CardA, CusX)));
        Assert.IsNull(await ProfileOf(Key(CardB, CusX)));
        Assert.IsNull(await ProfileOf(Key(CardA, CusY)));

        var line = AuditLines().Single();
        Assert.AreEqual(ArchivedNames, line.Names);
        AssertCounts(line.Values, "archived", records: 21, @new: 21, "[]", cardsTrained: 1);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-004")]
    public async Task ALaterFile_IsMergedInSeqOrder_AndTheCardIsRetrainedOnItsFullHistory()
    {
        int[] even = [.. Enumerable.Range(0, 14).Select(i => i * 2)];   // 0, 2, ..., 26
        int[] odd = [.. Enumerable.Range(0, 7).Select(i => i * 2 + 1)];  // 1, 3, ..., 13
        Drop("day1.jsonl", JournalText(Lines(even)));
        Drop("day2.jsonl", JournalText(Lines(odd)));

        IReadOnlyList<IngestedFile> handled = await Ingestor().RunAsync();

        // Files are handled in name order: day1 trains the card at 14, day2 retrains it at 21.
        CollectionAssert.AreEqual(new[] { "day1.jsonl", "day2.jsonl" }, handled.Select(h => h.Entry.File).ToArray());
        int[] all = [.. even.Concat(odd).Order()];
        CardHistory history = (await HistoryOf(Key()))!;
        CollectionAssert.AreEqual(all.Select(s => (long)s).ToArray(), history.Seq.ToArray());
        CollectionAssert.AreEqual(ProfileBytesFor(Symbols(all)), ProfileBytes(Key()));
        Assert.AreEqual(21, (await ProfileOf(Key()))!.HistoryLength);

        var lines = AuditLines();
        AssertCounts(lines[0].Values, "archived", records: 14, @new: 14, "[]", cardsTrained: 1);
        AssertCounts(lines[1].Values, "archived", records: 7, @new: 7, "[]", cardsTrained: 1);
    }

    // ---- SM-JRNL-003: idempotency and SeqConflict ------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-003")]
    public async Task ReprocessingAFile_ChangesNoModelState()
    {
        string content = JournalText(Lines(Enumerable.Range(0, 16)));
        Drop("day1.jsonl", content);
        await Ingestor().RunAsync();
        byte[] history = HistoryBytes(Key());
        byte[] profile = ProfileBytes(Key());

        // The same file sent again under the same name, then a file repeating part of it.
        Drop("day1.jsonl", content);
        await Ingestor().RunAsync();
        Drop("day2.jsonl", JournalText(Lines([3, 4, 5])));
        await Ingestor().RunAsync();

        CollectionAssert.AreEqual(history, HistoryBytes(Key()));
        CollectionAssert.AreEqual(profile, ProfileBytes(Key()));
        var lines = AuditLines();
        Assert.HasCount(3, lines);
        AssertCounts(lines[0].Values, "archived", records: 16, @new: 16, "[]", cardsTrained: 1);
        AssertCounts(lines[1].Values, "archived", records: 16, @new: 0, "[]", cardsTrained: 0);
        AssertCounts(lines[2].Values, "archived", records: 3, @new: 0, "[]", cardsTrained: 0);
        Assert.AreEqual(lines[0].Values["sha256"], lines[1].Values["sha256"]);
        Assert.AreEqual(content, File.ReadAllText(Archived("day1.jsonl")));
        Assert.AreEqual(content, File.ReadAllText(Archived("day1.1.jsonl")));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-003")]
    public async Task TheSameSeqWithAnotherSymbol_IsASeqConflict_AndWhatIsStoredStays()
    {
        Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, 16))));
        await Ingestor().RunAsync();

        Drop("day2.jsonl", JournalText(Line(5, amountBin: 7), "", Line(5), Line(20)));
        await Ingestor().RunAsync();

        CardHistory history = (await HistoryOf(Key()))!;
        Assert.AreEqual(17, history.Count);
        Assert.AreEqual(SymbolOf(5), history.Symbols[history.Seq.ToList().IndexOf(5)], "The stored symbol stays.");
        Assert.AreEqual(20, history.Seq[^1]);
        var line = AuditLines()[1];
        AssertCounts(line.Values, "archived", records: 2, @new: 1, "[[1,\"SeqConflict\"],[2,\"BlankLine\"]]", cardsTrained: 1);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-003")]
    public async Task LinesThatDisagreeAboutASeq_AreAllSeqConflicts_AndARepeatCountsOnce()
    {
        // Card B's only lines disagree with each other, so it gets no history at all.
        Drop("day1.jsonl", JournalText(Line(7), Line(7, amountBin: 7), Line(7), Line(8), Line(8), Line(9, CardB), Line(9, CardB, amountBin: 7)));

        await Ingestor().RunAsync();

        CardHistory history = (await HistoryOf(Key()))!;
        CollectionAssert.AreEqual(new long[] { 8 }, history.Seq.ToArray());
        CollectionAssert.AreEqual(new[] { SymbolOf(8) }, history.Symbols.ToArray());
        Assert.IsNull(await HistoryOf(Key(CardB)));
        AssertCounts(
            AuditLines().Single().Values, "archived", records: 2, @new: 1,
            "[[1,\"SeqConflict\"],[2,\"SeqConflict\"],[3,\"SeqConflict\"],[6,\"SeqConflict\"],[7,\"SeqConflict\"]]", cardsTrained: 0);
    }

    // ---- SM-JRNL-007: the minimum ----------------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-007")]
    [DataRow(1)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    public async Task ACard_IsTrainedOnlyOnceItHas14Transactions(int count)
    {
        // With the defaults (h = 0.33), 14 transactions hold out floor(14 × 0.33) = 4 and train on 10,
        // the least training allows; 13 would hold out 4 and train on only 9.
        JournalIngestor ingestor = Ingestor();
        Assert.AreEqual(14, ingestor.MinimumHistory);
        Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, count))));

        await ingestor.RunAsync();

        Assert.AreEqual(count, (await HistoryOf(Key()))!.Count, "The history is kept either way.");
        CardProfile? profile = await ProfileOf(Key());
        if (count < 14)
        {
            Assert.IsNull(profile, "The card stays cold-start.");
        }
        else
        {
            Assert.AreEqual(count, profile!.HistoryLength);
        }

        AssertCounts(AuditLines().Single().Values, "archived", count, count, "[]", cardsTrained: count < 14 ? 0 : 1);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-007")]
    public async Task AColdCard_IsTrainedWhenALaterFileBringsItTo14()
    {
        Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, 13))));
        await Ingestor().RunAsync();
        Assert.IsNull(await ProfileOf(Key()));

        Drop("day2.jsonl", JournalText(Line(13)));
        await Ingestor().RunAsync();

        CollectionAssert.AreEqual(ProfileBytesFor(Symbols(Enumerable.Range(0, 14))), ProfileBytes(Key()));
        var lines = AuditLines();
        AssertCounts(lines[0].Values, "archived", 13, 13, "[]", cardsTrained: 0);
        AssertCounts(lines[1].Values, "archived", 1, 1, "[]", cardsTrained: 1);
    }

    // ---- SM-JRNL-006: archive and retry ----------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow("io")]
    [DataRow("access")]
    [DataRow("conflict")]
    public async Task WhenACardCannotBeSaved_TheFileStaysForRetry_AndTheRetrySkipsWhatWasSaved(string failure)
    {
        // Card B's lines 16 and 18 disagree about seq 1: they are SeqConflicts on both runs.
        string content = JournalText([.. Lines(Enumerable.Range(0, 14)), .. Lines([0, 1, 2], CardB), Line(1, CardB, amountBin: 7)]);
        const string conflicts = "[[16,\"SeqConflict\"],[18,\"SeqConflict\"]]";
        string landed = Drop("day1.jsonl", content);
        var failing = new FailingHistoryStore(new FileHistoryStore(modelRoot), CardB, Failure(failure));

        await Ingestor(histories: failing).RunAsync();

        var first = AuditLines().Single();
        Assert.AreEqual(RetryNames, first.Names);
        AssertCounts(first.Values, "retry", records: 16, @new: 14, conflicts, cardsTrained: 1, cardsFailed: 1);
        Assert.AreEqual(content, File.ReadAllText(landed), "The file stays where it is, unchanged.");
        Assert.IsFalse(Directory.Exists(Path.Combine(journalRoot, Client, "archive")));
        Assert.IsNull(await HistoryOf(Key(CardB)));
        byte[] historyA = HistoryBytes(Key(CardA));
        byte[] profileA = ProfileBytes(Key(CardA));

        await Ingestor().RunAsync();

        var second = AuditLines()[1];
        Assert.AreEqual(ArchivedNames, second.Names);
        AssertCounts(second.Values, "archived", records: 16, @new: 2, conflicts, cardsTrained: 0);
        Assert.IsFalse(File.Exists(landed));
        Assert.AreEqual(content, File.ReadAllText(Archived("day1.jsonl")));
        CollectionAssert.AreEqual(new long[] { 0, 2 }, (await HistoryOf(Key(CardB)))!.Seq.ToArray());
        CollectionAssert.AreEqual(historyA, HistoryBytes(Key(CardA)), "What was saved the first time is skipped.");
        CollectionAssert.AreEqual(profileA, ProfileBytes(Key(CardA)));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [DataRow("io")]
    [DataRow("access")]
    [DataRow("conflict")]
    public async Task WhenOnlyTheProfileCannotBeSaved_TheNextRunBuildsIt(string failure)
    {
        string landed = Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, 14))));
        JournalIngestor ingestor = Ingestor(profiles: new FailOnceModelStore(new FileModelStore(modelRoot), Failure(failure)));

        await ingestor.RunAsync();

        AssertCounts(AuditLines().Single().Values, "retry", records: 14, @new: 14, "[]", cardsTrained: 0, cardsFailed: 1);
        Assert.AreEqual(14, (await HistoryOf(Key()))!.Count, "The history was saved.");
        Assert.IsNull(await ProfileOf(Key()));
        Assert.IsTrue(File.Exists(landed));

        await ingestor.RunAsync();

        AssertCounts(AuditLines()[1].Values, "archived", records: 14, @new: 0, "[]", cardsTrained: 1);
        CollectionAssert.AreEqual(ProfileBytesFor(Symbols(Enumerable.Range(0, 14))), ProfileBytes(Key()));
        Assert.IsFalse(File.Exists(landed));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-STOR-008")]
    public async Task ADamagedStoredHistory_IsReported_AndTheFileStaysForRetry()
    {
        string path = new FileHistoryStore(modelRoot).PathFor(Key());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"format\":\"secretmodel-history\"");
        string landed = Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, 14))));

        await Ingestor().RunAsync();

        AssertCounts(AuditLines().Single().Values, "retry", records: 14, @new: 0, "[]", cardsTrained: 0, cardsFailed: 1);
        Assert.AreEqual("{\"format\":\"secretmodel-history\"", File.ReadAllText(path), "Never treated as missing or overwritten.");
        Assert.IsTrue(File.Exists(landed));
        Assert.IsNull(await ProfileOf(Key()));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-STOR-007")]
    public async Task ADamagedStoredProfile_IsReported_AndTheFileStaysForRetry()
    {
        Drop("day1.jsonl", JournalText(Lines(Enumerable.Range(0, 14))));
        await Ingestor().RunAsync();
        string path = new FileModelStore(modelRoot).PathFor(Key());
        File.WriteAllText(path, "{\"format\":\"secretmodel-profile\"");
        string landed = Drop("day2.jsonl", JournalText(Line(14)));

        await Ingestor().RunAsync();

        AssertCounts(AuditLines()[1].Values, "retry", records: 1, @new: 1, "[]", cardsTrained: 0, cardsFailed: 1);
        Assert.AreEqual("{\"format\":\"secretmodel-profile\"", File.ReadAllText(path));
        Assert.IsTrue(File.Exists(landed));
    }

    // ---- SM-JRNL-005 and the name rules: quarantine ----------------------------------------------

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-JRNL-008")]
    public async Task ARejectedFile_MovesToQuarantineUnchanged_AndNothingFromItIsUsed()
    {
        string content = JournalText(Line(0), "{\"note\":\"4111 1111 1111 1111\"}", Line(1));
        string landed = Drop("day1.jsonl", content);

        await Ingestor().RunAsync();

        Assert.AreEqual(
            $"{{\"time\":\"2026-11-26T20:15:03Z\",\"file\":\"day1.jsonl\",\"sha256\":\"{Sha256Hex(content)}\",\"outcome\":\"rejected\",\"line\":2}}\n",
            AuditText());
        Assert.IsFalse(File.Exists(landed));
        Assert.AreEqual(content, File.ReadAllText(Quarantined("day1.jsonl")));
        Assert.IsNull(await HistoryOf(Key()), "No partial ingestion.");
        string clientFolder = Path.GetDirectoryName(new FileAuditLog(modelRoot).PathFor(Client))!;
        CollectionAssert.AreEqual(
            new[] { "audit.jsonl" }, Directory.GetFileSystemEntries(clientFolder).Select(Path.GetFileName).ToArray(), "Only the audit log was written.");

        // The quarantine is never read again.
        Assert.IsEmpty(await Ingestor().RunAsync());
        Assert.HasCount(1, AuditLines());
        Assert.AreEqual(content, File.ReadAllText(Quarantined("day1.jsonl")));
    }

    [TestMethod]
    [TestCategory("SM-JRNL-008")]
    [TestCategory("SM-PRIV-002")]
    [DataRow("4111111111111111.jsonl")]
    [DataRow("4111.1111.1111.1111.jsonl")]
    [DataRow(".hidden.jsonl")]
    [DataRow("day 1.jsonl")]
    [DataRow("café.jsonl")]
    public async Task AFileWithABadName_MovesToQuarantineAsItsSha256_AndTheAuditRecordsNoName(string name)
    {
        string content = JournalText(Line(0));
        string landed = Drop(name, content);
        string sha = Sha256Hex(content);

        await Ingestor().RunAsync();

        string audit = AuditText();
        Assert.AreEqual($"{{\"time\":\"2026-11-26T20:15:03Z\",\"file\":null,\"sha256\":\"{sha}\",\"outcome\":\"bad_name\"}}\n", audit);
        Assert.DoesNotContain(Path.GetFileNameWithoutExtension(name), audit);
        Assert.IsFalse(File.Exists(landed));
        Assert.AreEqual(content, File.ReadAllText(Quarantined(sha + ".jsonl")));
        Assert.IsNull(await HistoryOf(Key()), "Its content was not read.");
    }

    // ---- Clients ---------------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-AUTH-006")]
    [TestCategory("SM-STOR-006")]
    public async Task TwoClients_WithTheSameFile_KeepSeparateHistoriesProfilesAndAuditLogs()
    {
        string content = JournalText(Lines(Enumerable.Range(0, 14)));
        Drop("day1.jsonl", content, "client-a");
        Drop("day1.jsonl", content, "client-b");

        IReadOnlyList<IngestedFile> handled = await Ingestor().RunAsync();

        CollectionAssert.AreEqual(new[] { "client-a", "client-b" }, handled.Select(h => h.ClientId).ToArray());
        var store = new FileHistoryStore(modelRoot);
        Assert.AreNotEqual(store.PathFor(Key(client: "client-a")), store.PathFor(Key(client: "client-b")));
        foreach (string client in new[] { "client-a", "client-b" })
        {
            Assert.AreEqual(14, (await HistoryOf(Key(client: client)))!.Count);
            Assert.AreEqual(14, (await ProfileOf(Key(client: client)))!.HistoryLength);
            AssertCounts(AuditLines(client).Single().Values, "archived", 14, 14, "[]", cardsTrained: 1);
        }

        Assert.IsNull(await HistoryOf(Key(client: "client-c")));
        Assert.HasCount(2, Directory.GetDirectories(modelRoot), "One folder per client.");
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [DataRow(".x")]
    [DataRow("_x")]
    [DataRow("bad name")]
    [DataRow("café")]
    [DataRow("4111111111111111")]
    public async Task AFolderWhoseNameIsNotAClientId_IsLeftAlone(string folder)
    {
        string landed = Drop("day1.jsonl", JournalText(Line(0)), folder);

        Assert.IsEmpty(await Ingestor().RunAsync());

        Assert.IsTrue(File.Exists(landed));
        Assert.IsEmpty(Directory.GetFileSystemEntries(modelRoot));
        Assert.HasCount(1, Directory.GetFileSystemEntries(Path.Combine(journalRoot, folder)));
    }

    [TestMethod]
    [TestCategory("SM-AUTH-006")]
    public async Task ALinkToAnotherClientsFile_IsLeftAlone()
    {
        string content = JournalText(Lines(Enumerable.Range(0, 14)));
        Drop("day1.jsonl", content, "client-b");
        await Ingestor().RunAsync();
        string link = Path.Combine(journalRoot, "client-a", "day1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, Archived("day1.jsonl", "client-b"));

        Assert.IsEmpty(await Ingestor().RunAsync());

        Assert.IsNull(await HistoryOf(Key(client: "client-a")), "Client A must not train on client B's transactions.");
        Assert.IsFalse(File.Exists(new FileAuditLog(modelRoot).PathFor("client-a")));
        Assert.IsNotNull(new FileInfo(link).LinkTarget, "The link is left where it is.");
        Assert.AreEqual(content, File.ReadAllText(Archived("day1.jsonl", "client-b")));
    }

    [TestMethod]
    [TestCategory("SM-AUTH-006")]
    [DataRow("archive", "day1.jsonl", false)]        // a good file is archived
    [DataRow("quarantine", "day1.jsonl", true)]      // a rejected file is quarantined
    [DataRow("quarantine", "day 1.jsonl", false)]    // so is a file with a bad name
    public async Task AnArchiveOrQuarantineThatIsALink_StopsTheRun_AndNothingReachesTheOtherClient(string folderName, string name, bool rejected)
    {
        string content = rejected ? JournalText("{\"note\":\"4111 1111 1111 1111\"}") : JournalText(Line(0));
        string landed = Drop(name, content);
        string theirs = Path.Combine(journalRoot, "client-b");
        Directory.CreateDirectory(theirs);
        Directory.CreateSymbolicLink(Path.Combine(journalRoot, Client, folderName), theirs);

        await Assert.ThrowsExactlyAsync<IOException>(() => Ingestor().RunAsync());

        Assert.IsEmpty(Directory.GetFileSystemEntries(theirs), "Nothing reached client B's folder.");
        Assert.AreEqual(content, File.ReadAllText(landed), "The file stays where it is.");
    }
}
