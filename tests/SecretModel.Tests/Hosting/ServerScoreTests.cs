// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// End-to-end tests of POST /v1/score (README §3.2) over real TLS on 127.0.0.1: a cold start gives
// the documented answer byte for byte (SM-SCORE-005; docs/formats/score-response-v1.md); a warm
// card gets exactly what the scorer gives for this client's own profile, read-only and repeatable
// (SM-SCORE-007, SM-SCORE-015); one client never reaches another's profile (SM-AUTH-006, SM-PS-004);
// the client comes from the certificate only (SM-AUTH-003); a refused request repeats nothing it
// held, in the answer or the logs (SM-PRIV-002, SM-PRIV-004, SM-PRIV-005); and p95 latency for a
// warm card is under 200 ms (SM-SCORE-010). Profiles are built from synthetic shoppers. All tokens
// are made up; 4111111111111111 is the published Visa test number, used only as an input that must
// be refused.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecretModel.Api.Hosting;
using SecretModel.Api.Scoring;
using SecretModel.Core.Features;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;
using SecretModel.Synthetic;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ServerScoreTests
{
    private const string Txn = "tok_txn_feedfacefeedfacefeed0003";
    private const string Cus = "tok_cus_c0ffeec0ffeec0ffeec0ffee";
    private const string Card = "tok_card_cafecafecafecafecafecafe";
    private const string CardNumber = "4111111111111111";

    private static readonly ScoringOptions Defaults = new();

    [TestMethod]
    [TestCategory("SM-SCORE-005")]
    [TestCategory("SM-SCORE-009")]
    public async Task ColdStart_GivesTheDocumentedAnswer_ByteForByte()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);

        using HttpResponseMessage response = await PostAsync(client, Body());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(ScoreResponseTests.ExampleC), await response.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    [TestCategory("SM-SCORE-007")]
    [TestCategory("SM-SCORE-009")]
    [TestCategory("SM-SCORE-015")]
    [DataRow(31UL, "habitual")]
    [DataRow(32UL, "random")]
    [DataRow(33UL, "takeover")]
    [DataRow(34UL, "subtle")]
    public async Task WarmCard_GetsTheScorersAnswerForItsProfile_RepeatablyAndReadOnly(ulong seed, string kind)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        var shopper = new SyntheticShopper(seed);
        CardProfile stored = await StoreProfileAsync(server, "client-a", shopper.History(300));
        Purchase purchase = kind == "habitual" ? shopper.Next() : shopper.Anomaly(Enum.Parse<AnomalyKind>(kind, ignoreCase: true));
        string path = new FileModelStore(server.ModelFolder).PathFor(new ProfileKey("client-a", Cus, Card));
        byte[] before = await File.ReadAllBytesAsync(path);
        byte[] expected = ScoreResponse.Write(Txn, FraudScorer.Score(stored, purchase.Symbol, Defaults));
        using HttpClient client = server.Client(TestPki.ClientA);

        using HttpResponseMessage first = await PostAsync(client, Body(purchase));
        using HttpResponseMessage second = await PostAsync(client, Body(purchase));

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        CollectionAssert.AreEqual(expected, await first.Content.ReadAsByteArrayAsync());
        CollectionAssert.AreEqual(expected, await second.Content.ReadAsByteArrayAsync());
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path), "Scoring never changes the profile.");
    }

    [TestMethod]
    [TestCategory("SM-AUTH-006")]
    [TestCategory("SM-PS-004")]
    public async Task AnotherClientsProfile_IsNeverUsed_ForTheSameTokens()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        var shopper = new SyntheticShopper(41);
        CardProfile profileA = await StoreProfileAsync(server, "client-a", shopper.History(300));
        Purchase purchase = shopper.Anomaly(AnomalyKind.Takeover);
        using HttpClient clientA = server.Client(TestPki.ClientA);
        using HttpClient clientB = server.Client(TestPki.ClientB);

        // Only client-a has a profile for these tokens: client-b gets a cold start.
        using HttpResponseMessage coldB = await PostAsync(clientB, Body(purchase));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(ScoreResponseTests.ExampleC), await coldB.Content.ReadAsByteArrayAsync());

        // Each client trains the same tokens on its own history and gets its own profile's answer.
        CardProfile profileB = await StoreProfileAsync(server, "client-b", new SyntheticShopper(42).History(300));
        using HttpResponseMessage a = await PostAsync(clientA, Body(purchase));
        using HttpResponseMessage b = await PostAsync(clientB, Body(purchase));
        byte[] expectedA = ScoreResponse.Write(Txn, FraudScorer.Score(profileA, purchase.Symbol, Defaults));
        byte[] expectedB = ScoreResponse.Write(Txn, FraudScorer.Score(profileB, purchase.Symbol, Defaults));

        CollectionAssert.AreNotEqual(expectedA, expectedB, "The two profiles must give different answers, or this test shows nothing.");
        CollectionAssert.AreEqual(expectedA, await a.Content.ReadAsByteArrayAsync());
        CollectionAssert.AreEqual(expectedB, await b.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-AUTH-006")]
    public async Task TheClient_ComesFromTheCertificate_NeverFromAHeaderOrTheQuery()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        await StoreProfileAsync(server, "client-b", new SyntheticShopper(43).History(300));
        using HttpClient clientA = server.Client(TestPki.ClientA);
        var request = new HttpRequestMessage(HttpMethod.Post, ServerHost.ScorePath + "?client_id=client-b")
        {
            Content = Json(Body()),
        };
        request.Headers.Add("X-Client-Id", "client-b");
        request.Headers.Add("client_id", "client-b");

        using HttpResponseMessage response = await clientA.SendAsync(request);

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(ScoreResponseTests.ExampleC), await response.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-PRIV-004")]
    [DataRow("{\"client_id\":\"client-b\"}", "UnknownProperty")]
    [DataRow("{\"seq\":1}", "UnknownProperty")]
    [DataRow("", "BlankLine")]
    [DataRow("{", "NotJson")]
    [DataRow("[1,2]", "NotAnObject")]
    [DataRow("{\"hour\":24}", "InvalidHour")]
    [DataRow("{\"merchant_category\":\"Grocery\"}", "InvalidMerchantCategory")]
    [DataRow("{\"card_token\":\"tok_card_cafe\"}", "InvalidCardToken")]
    [DataRow("{\"amount_bin\":null}", "InvalidAmountBin")]
    public async Task ABrokenRequest_Is400_WithTheFirstBrokenRule(string change, string reason)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);

        using HttpResponseMessage response = await PostAsync(client, Changed(change));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual($"{{\"error\":\"invalid_request\",\"reason\":\"{reason}\"}}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-PRIV-002")]
    [TestCategory("SM-PRIV-004")]
    [TestCategory("SM-PRIV-005")]
    [TestCategory("SM-PS-004")]
    [DataRow("card_token", CardNumber)]
    [DataRow("card_token", "tok_" + CardNumber)]
    [DataRow("customer_token", "4111 1111 1111 1111")]
    [DataRow("transaction_token", "tok_txn_" + CardNumber)]
    public async Task ACardNumber_Is400_AndNeverRepeated(string property, string value)
    {
        RunningServer server = await RunningServer.StartAsync();
        string body = Body().Replace(property switch
        {
            "card_token" => Card,
            "customer_token" => Cus,
            _ => Txn,
        }, value, StringComparison.Ordinal);
        HttpResponseMessage response;
        using (HttpClient client = server.Client(TestPki.ClientA))
        {
            response = await PostAsync(client, body);
        }

        // Stopping the server flushes every log entry.
        await server.DisposeAsync();
        using HttpResponseMessage answer = response;

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("{\"error\":\"invalid_request\",\"reason\":\"PossibleCardNumber\"}", await response.Content.ReadAsStringAsync());
        string headers = response.Headers.ToString() + response.Content.Headers;
        Assert.DoesNotContain("1111", headers);
        Assert.DoesNotContain("1111", server.Logs.All);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    public async Task EveryLogEntryOfTheServer_PassesTheGuard()
    {
        RunningServer server = await RunningServer.StartAsync();
        ILogger logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SecretModel.Tests");
        foreach (LogLevel level in Enum.GetValues<LogLevel>().Where(l => l != LogLevel.None))
        {
            logger.Log(level, "Value {Value}.", CardNumber);
            logger.Log(level, new InvalidOperationException(CardNumber), "Failed.");
        }

        await server.DisposeAsync();

        string[] ours = [.. server.Logs.Entries.Where(e => e.Contains(" SecretModel.Tests: ", StringComparison.Ordinal))];
        Assert.IsNotEmpty(ours, "Entries at the levels the server keeps reach the log.");
        Assert.DoesNotContain(CardNumber, server.Logs.All);
        Assert.IsTrue(ours.All(e => e.EndsWith($": {GuardedLoggerProvider.Withheld} ", StringComparison.Ordinal)
            || e.EndsWith(": Failed. (System.InvalidOperationException) ", StringComparison.Ordinal)), string.Join("\n", ours));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    [DoNotParallelize]
    public async Task TheConsole_GetsOnlyGuardedEntries()
    {
        // The console is process-wide, so this test runs on its own and puts it back afterwards.
        TextWriter before = Console.Out;
        using var console = new StringWriter();
        Console.SetOut(console);
        try
        {
            RunningServer server = await RunningServer.StartAsync();
            ILogger logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SecretModel.Tests");
            logger.LogWarning("Value {Value}.", CardNumber);
            logger.LogError(new InvalidOperationException(CardNumber), "Failed.");

            // Stopping the server flushes the console.
            await server.DisposeAsync();
        }
        finally
        {
            Console.SetOut(before);
        }

        string written = console.ToString();
        Assert.Contains(GuardedLoggerProvider.Withheld, written);
        Assert.Contains("Failed. (System.InvalidOperationException)", written);
        Assert.DoesNotContain(CardNumber, written);
    }

    [TestMethod]
    [DataRow(4096, HttpStatusCode.OK)]
    [DataRow(4097, HttpStatusCode.RequestEntityTooLarge)]
    public async Task ABodyOver4096Bytes_Is413(int size, HttpStatusCode expected)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);
        string body = Body() + new string(' ', size - Encoding.UTF8.GetByteCount(Body()));

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.AreEqual(expected, response.StatusCode);
        if (expected == HttpStatusCode.RequestEntityTooLarge)
        {
            Assert.AreEqual("{\"error\":\"request_too_large\"}", await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [DataRow(4096, HttpStatusCode.OK)]
    [DataRow(4097, HttpStatusCode.RequestEntityTooLarge)]
    [DataRow(100_000, HttpStatusCode.RequestEntityTooLarge)]
    public async Task ABodyOver4096Bytes_Is413_EvenWithoutALength(int size, HttpStatusCode expected)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);
        byte[] body = Encoding.UTF8.GetBytes(Body() + new string(' ', size - Encoding.UTF8.GetByteCount(Body())));
        var content = new StreamContent(new UnknownLengthStream(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        using HttpResponseMessage response = await client.PostAsync(ServerHost.ScorePath, content);

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("text/plain")]
    [DataRow("application/xml")]
    [DataRow("application/json; charset=utf-16")]
    [DataRow("application/json; version=2")]
    [DataRow("application/jsonl")]
    public async Task AContentTypeOtherThanJson_Is415(string? contentType)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body()));
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using HttpResponseMessage response = await client.PostAsync(ServerHost.ScorePath, content);

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.AreEqual("{\"error\":\"unsupported_media_type\"}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("application/json; charset=utf-8")]
    [DataRow("Application/JSON; Charset=UTF-8")]
    public async Task JsonWithOrWithoutUtf8Charset_IsAccepted(string contentType)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body()));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using HttpResponseMessage response = await client.PostAsync(ServerHost.ScorePath, content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-001")]
    [TestCategory("SM-AUTH-005")]
    public async Task Scoring_NeedsACertificate()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(null);

        using HttpResponseMessage response = await PostAsync(client, Body());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetOnTheScorePath_Is405()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);

        using HttpResponseMessage response = await client.GetAsync(ServerHost.ScorePath);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-004")]
    [TestCategory("SM-PRIV-005")]
    public async Task ADamagedProfile_Is500_WithNoDetail_AndLoggedByTypeOnly()
    {
        RunningServer server = await RunningServer.StartAsync();
        string path = new FileModelStore(server.ModelFolder).PathFor(new ProfileKey("client-a", Cus, Card));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"format\":\"not a profile\"}");
        HttpResponseMessage response;
        using (HttpClient client = server.Client(TestPki.ClientA))
        {
            response = await PostAsync(client, Body());
        }

        await server.DisposeAsync();

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("{\"error\":\"internal\"}", await response.Content.ReadAsStringAsync());
        Assert.Contains("(System.IO.InvalidDataException)", server.Logs.All);
        Assert.DoesNotContain("not a profile", server.Logs.All);
        response.Dispose();
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    public async Task AnyException_Is500_WithNoDetail_AndLoggedByTypeOnly()
    {
        RunningServer server = await RunningServer.StartAsync();
        HttpResponseMessage response;
        using (HttpClient client = server.Client(TestPki.ClientA))
        {
            response = await client.GetAsync(RunningServer.ThrowPath);
        }

        await server.DisposeAsync();

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual("{\"error\":\"internal\"}", await response.Content.ReadAsStringAsync());
        Assert.Contains("(System.InvalidOperationException)", server.Logs.All);
        Assert.DoesNotContain(RunningServer.ThrownMessage, server.Logs.All);
        response.Dispose();
    }

    [TestMethod]
    [TestCategory("SM-SCORE-010")]
    [DoNotParallelize]
    public async Task WarmCard_P95Latency_IsUnder200Milliseconds()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        var shopper = new SyntheticShopper(51);
        await StoreProfileAsync(server, "client-a", shopper.History(300));
        using HttpClient client = server.Client(TestPki.ClientA);
        string[] bodies = [.. Enumerable.Range(0, 220).Select(_ => Body(shopper.Next()))];

        // The first 20 warm the server, the connection and the file cache; the other 200 are timed.
        var times = new List<double>();
        for (int i = 0; i < bodies.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostAsync(client, bodies[i]);
            _ = await response.Content.ReadAsByteArrayAsync();
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            if (i >= 20)
            {
                times.Add(milliseconds);
            }
        }

        times.Sort();
        double p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
        Console.WriteLine($"SM-SCORE-010: {times.Count} requests, median {times[times.Count / 2]:F2} ms, p95 {p95:F2} ms, max {times[^1]:F2} ms.");
        Assert.IsLessThan(200.0, p95);
    }

    private static async Task<CardProfile> StoreProfileAsync(RunningServer server, string clientId, int[] history)
    {
        var store = new FileModelStore(server.ModelFolder);
        var key = new ProfileKey(clientId, Cus, Card);
        await store.PutAsync(key, CardProfile.Build(history, Defaults), null);

        // What scoring reads: the profile as stored.
        return (await store.GetAsync(key))!.Profile;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) => client.PostAsync(ServerHost.ScorePath, Json(body));

    private static ByteArrayContent Json(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        return content;
    }

    private static string Body() => Body(new Purchase(3, 0, 2, false));

    private static string Body(Purchase purchase) =>
        $"{{\"transaction_token\":\"{Txn}\",\"customer_token\":\"{Cus}\",\"card_token\":\"{Card}\"," +
        $"\"amount_bin\":{purchase.AmountBin},\"merchant_category\":\"{Quantizer.CategoryNames[purchase.MccGroup]}\"," +
        $"\"hour\":{(purchase.TimeBucket * 6) + 1},\"is_online\":{(purchase.IsOnline ? "true" : "false")}}}";

    // A valid body with one property changed or added: {"name":value} gives the new value.
    private static string Changed(string change)
    {
        if (change.Length == 0 || change[0] != '{' || change == "{")
        {
            return change;
        }

        string name = change[2..change.IndexOf('"', 2)];
        string value = change[(change.IndexOf(':') + 1)..^1];
        string body = Body();
        int at = body.IndexOf($"\"{name}\":", StringComparison.Ordinal);
        if (at < 0)
        {
            return body[..^1] + "," + change[1..];
        }

        int start = at + name.Length + 3;
        int end = body.IndexOfAny([',', '}'], start);
        return body[..start] + value + body[end..];
    }

    // A stream that won't say how long it is, so HttpClient sends the body in chunks.
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
