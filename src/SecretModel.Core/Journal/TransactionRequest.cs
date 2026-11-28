// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 1). AI-generated.

namespace SecretModel.Core.Journal;

/// <summary>
/// One transaction to score (README §3.2): a journal record without <c>seq</c>, with every rule of
/// docs/formats/journal-v1.md already checked. The client is not here: it comes from the client
/// certificate, never from the request (README SM-AUTH-003).
/// </summary>
public sealed record TransactionRequest(
    string TransactionToken,
    string CustomerToken,
    string CardToken,
    int AmountBin,
    string MerchantCategory,
    int Hour,
    bool IsOnline);

/// <summary>
/// What <see cref="JournalFile.ReadTransaction"/> found: a transaction, the first rule it breaks, or
/// that it may hold a card number (README SM-PRIV-002). Never anything from the content itself.
/// </summary>
public sealed class TransactionReadResult
{
    private TransactionReadResult(TransactionRequest? transaction, JournalQuarantineReason? reason, bool hasLongDigitRun)
    {
        Transaction = transaction;
        Reason = reason;
        HasLongDigitRun = hasLongDigitRun;
    }

    /// <summary>The transaction, or null when it was refused.</summary>
    public TransactionRequest? Transaction { get; }

    /// <summary>The first rule broken, in the order of <see cref="JournalQuarantineReason"/>; null otherwise.</summary>
    public JournalQuarantineReason? Reason { get; }

    /// <summary>True when the content may hold a card number (13 or more digits in a row, SM-PRIV-002). It is then refused before any other rule.</summary>
    public bool HasLongDigitRun { get; }

    internal static TransactionReadResult LongDigitRun { get; } = new(null, null, true);

    internal static TransactionReadResult Read(TransactionRequest transaction) => new(transaction, null, false);

    internal static TransactionReadResult Invalid(JournalQuarantineReason reason) => new(null, reason, false);
}
