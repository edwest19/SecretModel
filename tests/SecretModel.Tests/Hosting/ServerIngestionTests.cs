// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// End-to-end tests of ingestion inside the server (README §3.3): files waiting when the server starts
// are ingested by the first run, files that arrive later by a later run, and the cards they train
// are then scored warm over real TLS (SM-JRNL-006, SM-SCORE-001). A rejected file and a file whose
// name breaks the rules are moved to quarantine, and neither their content nor the bad name reaches
// the log (SM-JRNL-005, SM-PRIV-005). Journals are built from synthetic shoppers. All tokens are
// made up; 4111111111111111 is the published Visa test number, used only as an input that must be
// rejected.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecretModel.Api.Hosting;
using SecretModel.Api.Scoring;
using SecretModel.Core.Features;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Synthetic;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ServerIngestionTests
{
    private const string Txn = "tok_txn_feedfacefeedfacefeed0003";
    private const string Cus = "tok_cus_c0ffeec0ffeec0ffeec0ffee";
    private const string Card = "tok_card_cafecafecafecafecafecafe";
    private const string CardNumber = "4111111111111111";

    [TestMethod]
    [TestCategory("SM-JRNL-005")]
    [TestCategory("SM-JRNL-006")]
    [TestCategory("SM-PRIV-005")]
    public async Task FilesWaitingAtStart_AreIngested_ThenTheCardIsScoredWarm()
    {
        var shopper = new SyntheticShopper(61);
        RunningServer server = await RunningServer.StartAsync((folder, _) =>
        {
            string client = Directory.CreateDirectory(Path.Combine(folder, "journal", "client-a")).FullName;
            File.WriteAllText(Path.Combine(client, "day1.jsonl"), Journal(Purchases(shopper, 300)));
            File.WriteAllText(Path.Combine(client, "day2.jsonl"), Journal(Purchases(shopper, 1)).Replace(Card, CardNumber, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(client, CardNumber + ".jsonl"), Journal(Purchases(shopper, 1)));
        });
        string clientFolder = Path.Combine(server.JournalFolder, "client-a");
        try
        {
            await WaitForAsync(() => File.Exists(Path.Combine(clientFolder, "archive", "day1.jsonl"))
                && Directory.GetFiles(Path.Combine(clientFolder, "quarantine")).Length == 2);

            Purchase purchase = shopper.Anomaly(AnomalyKind.Takeover);
            await AssertScoredWarmAsync(server, purchase);
            Assert.HasCount(3, File.ReadAllLines(new FileAuditLog(server.ModelFolder).PathFor("client-a")));
        }
        finally
        {
            await server.DisposeAsync();
        }

        string logs = server.Logs.All;
        Assert.Contains("Journal file day1.jsonl for client-a: archived. 300 records, 300 new, 0 lines quarantined, 1 cards trained.", logs);
        Assert.Contains("Journal file day2.jsonl for client-a: rejected and moved to quarantine, because line 1 may hold a card number.", logs);
        Assert.Contains("A journal file for client-a has a name that breaks the rules", logs);
        Assert.DoesNotContain(CardNumber, logs);
    }

    [TestMethod]
    [TestCategory("SM-JRNL-006")]
    public async Task AFileThatArrivesLater_IsIngestedByALaterRun()
    {
        var shopper = new SyntheticShopper(62);
        await using RunningServer server = await RunningServer.StartAsync(
            (folder, _) =>
            {
                string first = Directory.CreateDirectory(Path.Combine(folder, "journal", "client-b")).FullName;
                File.WriteAllText(Path.Combine(first, "first.jsonl"), Journal(Purchases(new SyntheticShopper(63), 20)));
            },
            ingestionInterval: TimeSpan.FromMilliseconds(200));

        // client-b's file shows the first run is over, so client-a's file can only be found by a later one.
        await WaitForAsync(() => File.Exists(Path.Combine(server.JournalFolder, "client-b", "archive", "first.jsonl")));
        string client = Directory.CreateDirectory(Path.Combine(server.JournalFolder, "client-a")).FullName;
        File.WriteAllText(Path.Combine(client, "later.jsonl.part"), Journal(Purchases(shopper, 300)));

        // A client uploads under another name and renames the file when it is complete (README §8.2).
        File.Move(Path.Combine(client, "later.jsonl.part"), Path.Combine(client, "later.jsonl"));
        await WaitForAsync(() => File.Exists(Path.Combine(client, "archive", "later.jsonl")));

        await AssertScoredWarmAsync(server, shopper.Next());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-006")]
    public async Task AServerThatCantTakeItsAddress_NeverIngests()
    {
        await using RunningServer first = await RunningServer.StartAsync();
        (string folder, Dictionary<string, string?> settings) = RunningServer.Prepare();
        try
        {
            settings["SecretModel:Url"] = $"https://127.0.0.1:{first.Url.Port}";
            string client = Directory.CreateDirectory(Path.Combine(folder, "journal", "client-a")).FullName;
            File.WriteAllText(Path.Combine(client, "day1.jsonl"), Journal(Purchases(new SyntheticShopper(64), 20)));
            await using WebApplication second = ServerHost.Create(RunningServer.Configuration(settings), TestPki.Password);

            await Assert.ThrowsAsync<IOException>(() => second.StartAsync());
            await Task.Delay(500);

            Assert.IsTrue(File.Exists(Path.Combine(client, "day1.jsonl")), "The file was moved.");
            Assert.IsFalse(Directory.Exists(Path.Combine(client, "archive")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheServersLoop_WaitsFiveMinutesBetweenRuns()
    {
        await using RunningServer server = await RunningServer.StartAsync();

        IngestionLoop loop = server.Services.GetServices<IHostedService>().OfType<IngestionLoop>().Single();

        Assert.AreEqual(TimeSpan.FromMinutes(5), loop.Interval);
    }

    [TestMethod]
    public async Task StoppingTheServer_DoesNotWaitForTheNextRun()
    {
        RunningServer server = await RunningServer.StartAsync();

        long start = Stopwatch.GetTimestamp();
        await server.DisposeAsync();

        Assert.IsLessThan(10.0, Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    private static async Task AssertScoredWarmAsync(RunningServer server, Purchase purchase)
    {
        StoredProfile stored = (await new FileModelStore(server.ModelFolder).GetAsync(new ProfileKey("client-a", Cus, Card)))!;
        byte[] expected = ScoreResponse.Write(Txn, FraudScorer.Score(stored.Profile, purchase.Symbol, new ScoringOptions()));
        using HttpClient client = server.Client(TestPki.ClientA);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body(purchase)));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        using HttpResponseMessage response = await client.PostAsync(ServerHost.ScorePath, content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        byte[] answer = await response.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(expected, answer);
        Assert.DoesNotContain(FraudScorer.ColdStart, Encoding.UTF8.GetString(answer));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.IsLessThan(30.0, Stopwatch.GetElapsedTime(start).TotalSeconds, "Ingestion didn't happen within 30 seconds.");
            await Task.Delay(50);
        }
    }

    private static IEnumerable<Purchase> Purchases(SyntheticShopper shopper, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => shopper.Next())];

    private static string Body(Purchase purchase) =>
        $"{{\"transaction_token\":\"{Txn}\",\"customer_token\":\"{Cus}\",\"card_token\":\"{Card}\"," + Fields(purchase) + "}";

    private static string Journal(IEnumerable<Purchase> purchases)
    {
        var lines = new StringBuilder();
        long seq = 0;
        foreach (Purchase purchase in purchases)
        {
            seq++;
            lines.Append($"{{\"seq\":{seq},\"transaction_token\":\"tok_txn_{seq:x24}\",\"customer_token\":\"{Cus}\",\"card_token\":\"{Card}\",")
                .Append(Fields(purchase)).Append("}\n");
        }

        return lines.ToString();
    }

    private static string Fields(Purchase purchase) =>
        $"\"amount_bin\":{purchase.AmountBin},\"merchant_category\":\"{Quantizer.CategoryNames[purchase.MccGroup]}\"," +
        $"\"hour\":{(purchase.TimeBucket * 6) + 1},\"is_online\":{(purchase.IsOnline ? "true" : "false")}";
}
