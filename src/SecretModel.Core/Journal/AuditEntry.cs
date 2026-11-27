// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the audit log approved by
// Edwin West on 2026-11-26 (design B). AI-generated.

namespace SecretModel.Core.Journal;

/// <summary>What happened to a journal file (docs/formats/audit-v1.md).</summary>
public enum AuditOutcome
{
    /// <summary>Every card was saved and the file moved to <c>archive/</c> (README SM-JRNL-006).</summary>
    Archived,

    /// <summary>At least one card could not be saved; the file stays in place for the next run (SM-JRNL-006).</summary>
    Retry,

    /// <summary>A line may carry a card number; the file moved to <c>quarantine/</c> unchanged (SM-JRNL-005).</summary>
    Rejected,

    /// <summary>The file name breaks the rules; the file moved to <c>quarantine/</c> under its SHA-256.</summary>
    BadName,
}

/// <summary>
/// One line of the audit log (README SM-JRNL-008, docs/formats/audit-v1.md): which file, when, and
/// counts, never transaction content. Made only through the four factories, one per outcome, so an
/// entry always has exactly the properties its outcome needs. A file name must pass
/// <see cref="JournalNames.IsValidFileName"/>, so nothing that may be a card number is ever recorded.
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry(
        DateTimeOffset time, string? file, string sha256, AuditOutcome outcome,
        int records, int @new, IReadOnlyList<QuarantinedLine> quarantined, int cardsTrained, int cardsFailed, int rejectedLine)
    {
        if (file is not null && !JournalNames.IsValidFileName(file))
        {
            // The name is not repeated: it may hold a card number (README SM-PRIV-004).
            throw new ArgumentException("An audit entry records only file names that follow the name rules.", nameof(file));
        }

        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("The SHA-256 must be 64 lowercase hex characters.", nameof(sha256));
        }

        Time = time;
        File = file;
        Sha256 = sha256;
        Outcome = outcome;
        Records = records;
        New = @new;
        Quarantined = [.. quarantined.OrderBy(q => q.LineNumber)];
        CardsTrained = cardsTrained;
        CardsFailed = cardsFailed;
        RejectedLine = rejectedLine;
    }

    public DateTimeOffset Time { get; }

    /// <summary>The file's name as it arrived; null for <see cref="AuditOutcome.BadName"/>.</summary>
    public string? File { get; }

    public string Sha256 { get; }

    public AuditOutcome Outcome { get; }

    public int Records { get; }

    public int New { get; }

    /// <summary>The quarantined lines, in line order.</summary>
    public IReadOnlyList<QuarantinedLine> Quarantined { get; }

    public int CardsTrained { get; }

    public int CardsFailed { get; }

    /// <summary>For <see cref="AuditOutcome.Rejected"/>, the first line that may carry a card number; otherwise 0.</summary>
    public int RejectedLine { get; }

    public static AuditEntry Archived(
        DateTimeOffset time, string file, string sha256, int records, int @new, IReadOnlyList<QuarantinedLine> quarantined, int cardsTrained)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(quarantined);
        return new(time, file, sha256, AuditOutcome.Archived, records, @new, quarantined, cardsTrained, 0, 0);
    }

    public static AuditEntry Retry(
        DateTimeOffset time, string file, string sha256, int records, int @new, IReadOnlyList<QuarantinedLine> quarantined,
        int cardsTrained, int cardsFailed)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(quarantined);
        return new(time, file, sha256, AuditOutcome.Retry, records, @new, quarantined, cardsTrained, cardsFailed, 0);
    }

    public static AuditEntry Rejected(DateTimeOffset time, string file, string sha256, int line)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new(time, file, sha256, AuditOutcome.Rejected, 0, 0, [], 0, 0, line);
    }

    public static AuditEntry BadName(DateTimeOffset time, string sha256) =>
        new(time, null, sha256, AuditOutcome.BadName, 0, 0, [], 0, 0, 0);
}
