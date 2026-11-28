// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 6). AI-generated.

using SecretModel.Core.Journal;

namespace SecretModel.Api.Hosting;

/// <summary>
/// Runs journal ingestion inside the server (README §3.3): once the server has started, then again
/// each time <see cref="Interval"/> has passed since the last run ended. "Started" means the server
/// is listening, so a server that can't take its address never touches the folders (measured: the
/// host starts this service before the web server binds). Each run is awaited before
/// the next wait begins, so only one run ever goes at a time (README §8.2). Each file handled gets
/// one log line; a run that stops with an error is logged by the error's type only, and the next run
/// tries again.
/// </summary>
public sealed class IngestionLoop(
    Func<CancellationToken, Task<IReadOnlyList<IngestedFile>>> run, TimeSpan interval, IHostApplicationLifetime lifetime, ILogger<IngestionLoop> logger)
    : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    public TimeSpan Interval => interval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never hold up the server's start, and never run before it has started.
        await Task.Yield();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (IngestedFile file in await run(stoppingToken))
                {
                    Log(file);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The log guard keeps only the exception's type.
                logger.LogError(e, "A journal ingestion run stopped; the next run will try again.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // A file name or client id that follows the rules can't hold 13 digits in a row (README §8.2),
    // and a bad name is never logged.
    private void Log(IngestedFile file)
    {
        AuditEntry entry = file.Entry;
        switch (entry.Outcome)
        {
            case AuditOutcome.Archived:
                logger.LogInformation(
                    "Journal file {File} for {Client}: archived. {Records} records, {New} new, {Quarantined} lines quarantined, {Trained} cards trained.",
                    entry.File, file.ClientId, entry.Records, entry.New, entry.Quarantined.Count, entry.CardsTrained);
                break;
            case AuditOutcome.Retry:
                logger.LogWarning(
                    "Journal file {File} for {Client}: kept for the next run, because {Failed} cards could not be saved. {Records} records, {New} new, {Quarantined} lines quarantined, {Trained} cards trained.",
                    entry.File, file.ClientId, entry.CardsFailed, entry.Records, entry.New, entry.Quarantined.Count, entry.CardsTrained);
                break;
            case AuditOutcome.Rejected:
                logger.LogWarning(
                    "Journal file {File} for {Client}: rejected and moved to quarantine, because line {Line} may hold a card number. Delete it by hand.",
                    entry.File, file.ClientId, entry.RejectedLine);
                break;
            default:
                logger.LogWarning(
                    "A journal file for {Client} has a name that breaks the rules: moved to quarantine under its SHA-256, unread.",
                    file.ClientId);
                break;
        }
    }
}
