// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

namespace SecretModel.Core.Journal;

/// <summary>
/// One transaction read from a journal file (docs/formats/journal-v1.md), with every rule of the
/// format already checked. <see cref="LineNumber"/> is where it came from; the other properties are
/// the file's eight, one for one. The client is not here: it comes from the folder the file was in,
/// never from the file (README SM-AUTH-003).
/// </summary>
public sealed record JournalRecord(
    int LineNumber,
    long Seq,
    string TransactionToken,
    string CustomerToken,
    string CardToken,
    int AmountBin,
    string MerchantCategory,
    int Hour,
    bool IsOnline);
