// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the server's ingestion loop (README §3.3): no run before the server has started, a run
// once it has, then one each time the
// interval has passed since the last run ended; never two runs at once (README §8.2); a run that
// stops with an error is logged and the next one tries again; stopping the server stops the loop at
// once, waiting or running; and one log line per file handled, never a name that breaks the rules.
// The runs here are stand-ins: what a real run does is in ServerIngestionTests.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecretModel.Api.Hosting;
using SecretModel.Core.Journal;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class IngestionLoopTests
{
    private const string Sha256 = "0f0fecc3c5c858de4837dac0f4e33e18f8c30674e622a7fde6087f8327af60d3";
    private static readonly DateTimeOffset Time = new(2026, 11, 27, 23, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ARun_StartsAsTheLoopStarts()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(_ => { first.TrySetResult(); return Task.FromResult(None); }, TimeSpan.FromHours(1), out _);

        await loop.StartAsync(CancellationToken.None);

        Assert.AreSame(first.Task, await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(10))), "No run started.");
        await loop.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task NoRun_StartsBeforeTheServerHasStarted()
    {
        using var serverStarted = new CancellationTokenSource();
        int runs = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(_ =>
        {
            Interlocked.Increment(ref runs);
            first.TrySetResult();
            return Task.FromResult(None);
        }, TimeSpan.FromHours(1), out _, serverStarted.Token);

        await loop.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.AreEqual(0, Volatile.Read(ref runs), "A run started before the server did.");
        await serverStarted.CancelAsync();

        Assert.AreSame(first.Task, await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(10))), "No run after the server started.");
        await loop.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Stopping_BeforeTheServerHasStarted_EndsAtOnce_WithNoRun()
    {
        using var serverStarted = new CancellationTokenSource();
        int runs = 0;
        using IngestionLoop loop = Loop(_ => { Interlocked.Increment(ref runs); return Task.FromResult(None); }, TimeSpan.FromHours(1), out CapturedLogs logs, serverStarted.Token);
        await loop.StartAsync(CancellationToken.None);

        long start = Stopwatch.GetTimestamp();
        await loop.StopAsync(CancellationToken.None);

        // Ended, either way. Measured on .NET 10.0.15: a service stopped right after it starts is
        // usually canceled before ExecuteAsync is ever entered (196 times in 200).
        Assert.IsLessThan(5.0, Stopwatch.GetElapsedTime(start).TotalSeconds);
        Assert.IsTrue(loop.ExecuteTask!.IsCompleted);
        Assert.AreEqual(0, Volatile.Read(ref runs));
        Assert.IsEmpty(logs.Entries);
    }

    [TestMethod]
    public async Task Runs_AreOneAtATime_WithTheIntervalBetweenTheEndOfOneAndTheStartOfTheNext()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(100);
        var spans = new ConcurrentQueue<(long Start, long End)>();
        int running = 0, most = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(async token =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            long start = Stopwatch.GetTimestamp();

            // Longer than the interval: a loop that started runs on a timer would overlap them.
            await Task.Delay(250, token);
            Interlocked.Decrement(ref running);
            spans.Enqueue((start, Stopwatch.GetTimestamp()));
            if (spans.Count >= 3)
            {
                enough.TrySetResult();
            }

            return None;
        }, interval, out _);

        await loop.StartAsync(CancellationToken.None);
        Assert.AreSame(enough.Task, await Task.WhenAny(enough.Task, Task.Delay(TimeSpan.FromSeconds(20))), "Three runs didn't happen.");
        await loop.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, most, "Two runs went at once.");
        (long Start, long End)[] runs = [.. spans];
        for (int i = 1; i < runs.Length; i++)
        {
            TimeSpan gap = Stopwatch.GetElapsedTime(runs[i - 1].End, runs[i].Start);
            Assert.IsGreaterThanOrEqualTo(interval - TimeSpan.FromMilliseconds(15), gap, $"Run {i + 1} started {gap.TotalMilliseconds} ms after run {i} ended.");
        }
    }

    [TestMethod]
    public async Task ARunThatFails_IsLogged_AndTheNextRunTriesAgain()
    {
        int runs = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(_ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                throw new IOException("synthetic: a linked archive folder");
            }

            second.TrySetResult();
            return Task.FromResult(None);
        }, TimeSpan.FromMilliseconds(50), out CapturedLogs logs);

        await loop.StartAsync(CancellationToken.None);
        Assert.AreSame(second.Task, await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(10))), "No second run.");
        await loop.StopAsync(CancellationToken.None);

        Assert.Contains(
            "Error SecretModel.Api.Hosting.IngestionLoop: A journal ingestion run stopped; the next run will try again. System.IO.IOException",
            logs.All);
    }

    [TestMethod]
    public async Task Stopping_EndsTheWaitAtOnce()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(_ => { first.TrySetResult(); return Task.FromResult(None); }, TimeSpan.FromHours(1), out CapturedLogs logs);
        await loop.StartAsync(CancellationToken.None);
        await first.Task;

        long start = Stopwatch.GetTimestamp();
        await loop.StopAsync(CancellationToken.None);

        Assert.IsLessThan(5.0, Stopwatch.GetElapsedTime(start).TotalSeconds);
        Assert.IsTrue(loop.ExecuteTask!.IsCompletedSuccessfully);
        Assert.IsEmpty(logs.Entries);
    }

    [TestMethod]
    public async Task Stopping_EndsARunAtOnce_WithoutAnError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return None;
        }, TimeSpan.FromHours(1), out CapturedLogs logs);
        await loop.StartAsync(CancellationToken.None);
        await started.Task;

        long start = Stopwatch.GetTimestamp();
        await loop.StopAsync(CancellationToken.None);

        Assert.IsLessThan(5.0, Stopwatch.GetElapsedTime(start).TotalSeconds);
        Assert.IsTrue(loop.ExecuteTask!.IsCompletedSuccessfully);
        Assert.IsEmpty(logs.Entries, "A stop is not an error.");
    }

    [TestMethod]
    [TestCategory("SM-PRIV-005")]
    public async Task EachFileHandled_GetsOneLogLine_AndABadNameIsNeverLogged()
    {
        IngestedFile[] files =
        [
            new("client-a", AuditEntry.Archived(Time, "day1.jsonl", Sha256, 300, 290, [new QuarantinedLine(7, JournalQuarantineReason.InvalidHour)], 2)),
            new("client-a", AuditEntry.Retry(Time, "day2.jsonl", Sha256, 40, 40, [], 1, 3)),
            new("client-b", AuditEntry.Rejected(Time, "day3.jsonl", Sha256, 12)),
            new("client-b", AuditEntry.BadName(Time, Sha256)),
        ];
        int runs = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IngestionLoop loop = Loop(_ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                done.TrySetResult();
                return Task.FromResult<IReadOnlyList<IngestedFile>>(files);
            }

            return Task.FromResult(None);
        }, TimeSpan.FromHours(1), out CapturedLogs logs);

        await loop.StartAsync(CancellationToken.None);
        await done.Task;
        await loop.StopAsync(CancellationToken.None);

        string[] expected =
        [
            "Information SecretModel.Api.Hosting.IngestionLoop: Journal file day1.jsonl for client-a: archived. 300 records, 290 new, 1 lines quarantined, 2 cards trained. ",
            "Warning SecretModel.Api.Hosting.IngestionLoop: Journal file day2.jsonl for client-a: kept for the next run, because 3 cards could not be saved. 40 records, 40 new, 0 lines quarantined, 1 cards trained. ",
            "Warning SecretModel.Api.Hosting.IngestionLoop: Journal file day3.jsonl for client-b: rejected and moved to quarantine, because line 12 may hold a card number. Delete it by hand. ",
            "Warning SecretModel.Api.Hosting.IngestionLoop: A journal file for client-b has a name that breaks the rules: moved to quarantine under its SHA-256, unread. ",
        ];
        CollectionAssert.AreEqual(expected, logs.Entries.ToArray());
        Assert.DoesNotContain(Sha256[..12], logs.All);
    }

    private static IReadOnlyList<IngestedFile> None => [];

    // serverStarted: when the server has started; already, if not given.
    private static IngestionLoop Loop(
        Func<CancellationToken, Task<IReadOnlyList<IngestedFile>>> run, TimeSpan interval, out CapturedLogs logs, CancellationToken? serverStarted = null)
    {
        logs = new CapturedLogs();
        CapturedLogs copy = logs;
        ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(copy));
        return new IngestionLoop(run, interval, new Lifetime(serverStarted ?? new CancellationToken(true)), factory.CreateLogger<IngestionLoop>());
    }

    private sealed class Lifetime(CancellationToken started) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => started;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
