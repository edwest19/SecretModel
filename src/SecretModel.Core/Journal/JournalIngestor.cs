// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the ingestion rules
// approved by Edwin West on 2026-11-26 (design E). AI-generated.

using System.Security.Cryptography;
using SecretModel.Core.Features;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

namespace SecretModel.Core.Journal;

/// <summary>One journal file handled by a run, and the audit entry recorded for it.</summary>
public sealed record IngestedFile(string ClientId, AuditEntry Entry);

/// <summary>
/// Ingests journal files (README §8): reads each waiting file, merges its transactions into each
/// card's symbol history in <c>seq</c> order, retrains every card that has enough history, moves the
/// file to <c>archive/</c> or <c>quarantine/</c>, and records one audit entry per file
/// (SM-JRNL-003 to SM-JRNL-008). Holds no state between runs: everything comes from the stores.
/// Only one run may go at a time.
/// </summary>
public sealed class JournalIngestor
{
    private readonly IJournalSource source;
    private readonly IHistoryStore histories;
    private readonly IModelStore profiles;
    private readonly IAuditLog audit;
    private readonly ScoringOptions options;
    private readonly TimeProvider time;
    private readonly int minimumHistory;

    public JournalIngestor(
        IJournalSource source, IHistoryStore histories, IModelStore profiles, IAuditLog audit, ScoringOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.source = source;
        this.histories = histories;
        this.profiles = profiles;
        this.audit = audit;
        this.options = options;
        this.time = time ?? TimeProvider.System;
        minimumHistory = CardProfile.MinimumHistoryFor(options);
    }

    /// <summary>
    /// The fewest transactions a card needs before it is trained: 14 with the default options. Below
    /// that its history is kept and it stays cold-start (SM-JRNL-007).
    /// </summary>
    public int MinimumHistory => minimumHistory;

    /// <summary>
    /// Handles every file the source lists once, in its order. A folder whose name is not a client id
    /// (<see cref="JournalNames.IsValidClientId"/>) is not a client, and its files are left alone.
    /// </summary>
    public async Task<IReadOnlyList<IngestedFile>> RunAsync(CancellationToken cancellationToken = default)
    {
        var handled = new List<IngestedFile>();
        foreach (JournalFileRef file in await source.ListAsync(cancellationToken))
        {
            if (JournalNames.IsValidClientId(file.ClientId))
            {
                AuditEntry entry = await HandleAsync(file, cancellationToken);
                await audit.AppendAsync(file.ClientId, entry, cancellationToken);
                handled.Add(new IngestedFile(file.ClientId, entry));
            }
        }

        return handled;
    }

    private async Task<AuditEntry> HandleAsync(JournalFileRef file, CancellationToken cancellationToken)
    {
        byte[] bytes = await source.ReadAsync(file, cancellationToken);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // A bad name may itself be a card number, so it is replaced by the hash and never recorded,
        // and the content is not read.
        if (!JournalNames.IsValidFileName(file.Name))
        {
            await source.MoveAsync(file, JournalFolder.Quarantine, sha256 + JournalNames.Extension, cancellationToken);
            return AuditEntry.BadName(time.GetUtcNow(), sha256);
        }

        JournalReadResult read = JournalFile.Read(bytes);
        if (read.IsRejected)
        {
            // Moved unchanged (SM-JRNL-005). It may hold a card number, so it is never read again.
            await source.MoveAsync(file, JournalFolder.Quarantine, file.Name, cancellationToken);
            return AuditEntry.Rejected(time.GetUtcNow(), file.Name, sha256, read.RejectedLineNumber);
        }

        var quarantined = new List<QuarantinedLine>(read.Quarantined);
        int added = 0, trained = 0, failed = 0;

        // SM-JRNL-004: one group per (client, customer, card); the client is the folder's.
        var cards = read.Records
            .GroupBy(r => (r.CustomerToken, r.CardToken))
            .OrderBy(g => g.Key.CustomerToken, StringComparer.Ordinal)
            .ThenBy(g => g.Key.CardToken, StringComparer.Ordinal);
        foreach (var card in cards)
        {
            var key = new ProfileKey(file.ClientId, card.Key.CustomerToken, card.Key.CardToken);
            CardOutcome outcome = await IngestCardAsync(key, [.. card], cancellationToken);
            quarantined.AddRange(outcome.Conflicts.Select(line => new QuarantinedLine(line, JournalQuarantineReason.SeqConflict)));
            added += outcome.Added;
            trained += outcome.Trained ? 1 : 0;
            failed += outcome.Failed ? 1 : 0;
        }

        int records = read.Records.Count - (quarantined.Count - read.Quarantined.Count);
        if (failed > 0)
        {
            // SM-JRNL-006: the file stays where it is. The next run reads it again; what was saved
            // this time is then already stored and is skipped.
            return AuditEntry.Retry(time.GetUtcNow(), file.Name, sha256, records, added, quarantined, trained, failed);
        }

        await source.MoveAsync(file, JournalFolder.Archive, file.Name, cancellationToken);
        return AuditEntry.Archived(time.GetUtcNow(), file.Name, sha256, records, added, quarantined, trained);
    }

    private sealed record CardOutcome(IReadOnlyList<int> Conflicts, int Added, bool Trained, bool Failed);

    // Merges one card's records from one file into its stored history, then trains it if it has
    // enough history and its profile was not built from exactly this history. The history is saved
    // before the profile, so a profile that failed to save is rebuilt on the next run (its
    // transaction count differs from the history's).
    private async Task<CardOutcome> IngestCardAsync(ProfileKey key, List<JournalRecord> records, CancellationToken cancellationToken)
    {
        var conflicts = new List<int>();
        int added = 0;
        try
        {
            StoredHistory? stored = await histories.GetAsync(key, cancellationToken);
            var merged = new SortedDictionary<long, int>();
            if (stored is not null)
            {
                for (int i = 0; i < stored.History.Count; i++)
                {
                    merged.Add(stored.History.Seq[i], stored.History.Symbols[i]);
                }
            }

            var additions = new Dictionary<long, int>();
            foreach (var sameSeq in records.GroupBy(r => r.Seq))
            {
                if (merged.TryGetValue(sameSeq.Key, out int storedSymbol))
                {
                    // SM-JRNL-003: the same seq with the same symbol counts once; with another symbol
                    // the line is a conflict, and what is stored stays.
                    conflicts.AddRange(sameSeq.Where(r => Symbol(r) != storedSymbol).Select(r => r.LineNumber));
                }
                else if (sameSeq.Select(Symbol).Distinct().Count() > 1)
                {
                    // Lines in this file disagree and nothing says which is right: none is used.
                    conflicts.AddRange(sameSeq.Select(r => r.LineNumber));
                }
                else
                {
                    additions.Add(sameSeq.Key, Symbol(sameSeq.First()));
                }
            }

            CardHistory? history = stored?.History;
            if (additions.Count > 0)
            {
                foreach ((long seq, int symbol) in additions)
                {
                    merged.Add(seq, symbol);
                }

                history = new CardHistory([.. merged.Keys], [.. merged.Values]);
                await histories.PutAsync(key, history, stored?.Version, cancellationToken);
                added = additions.Count;
            }

            bool trained = false;
            if (history is not null && history.Count >= minimumHistory)
            {
                StoredProfile? profile = await profiles.GetAsync(key, cancellationToken);
                if (profile is null || profile.Profile.HistoryLength != history.Count)
                {
                    // Retrained on the full history, oldest seq first (SM-JRNL-004).
                    CardProfile built = CardProfile.Build([.. history.Symbols], options);
                    await profiles.PutAsync(key, built, profile?.Version, cancellationToken);
                    trained = true;
                }
            }

            return new CardOutcome(conflicts, added, trained, Failed: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ConcurrencyConflictException)
        {
            // The card could not be read or saved: a damaged stored file, a lost write race, or the
            // disk. Reported through the audit entry; the file is kept for retry (SM-JRNL-006).
            return new CardOutcome(conflicts, added, Trained: false, Failed: true);
        }
    }

    private static int Symbol(JournalRecord r) => Quantizer.ToSymbol(r.AmountBin, r.MerchantCategory, r.Hour, r.IsOnline);
}
