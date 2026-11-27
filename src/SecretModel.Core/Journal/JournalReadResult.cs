// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated. SeqConflict
// added 2026-11-27 for journal ingestion.

namespace SecretModel.Core.Journal;

/// <summary>
/// Why a journal line was quarantined (README SM-JRNL-002). The reader checks a line in the order
/// of this list, up to <see cref="InvalidIsOnline"/>, and reports the first reason that applies
/// (docs/formats/journal-v1.md). <see cref="SeqConflict"/> comes last because only ingestion can
/// tell it: it needs the card's stored history.
/// </summary>
public enum JournalQuarantineReason
{
    /// <summary>The line is empty or holds only spaces and tabs.</summary>
    BlankLine,

    /// <summary>The line is not valid UTF-8, or a string in it is not whole Unicode text (an escape such as <c>\ud800</c>).</summary>
    InvalidText,

    /// <summary>The line is JSON except that an object repeats a property name.</summary>
    DuplicateProperty,

    /// <summary>The line is not exactly one JSON value.</summary>
    NotJson,

    /// <summary>The line's value is not a JSON object.</summary>
    NotAnObject,

    /// <summary>The object has a property that is not one of the eight.</summary>
    UnknownProperty,

    /// <summary>One of the eight properties is missing.</summary>
    MissingProperty,

    /// <summary><c>seq</c> is not a whole number from 0 to 999,999,999,999.</summary>
    InvalidSeq,

    /// <summary><c>transaction_token</c> is not a valid <c>tok_txn_</c> token.</summary>
    InvalidTransactionToken,

    /// <summary><c>customer_token</c> is not a valid <c>tok_cus_</c> token.</summary>
    InvalidCustomerToken,

    /// <summary><c>card_token</c> is not a valid <c>tok_card_</c> token.</summary>
    InvalidCardToken,

    /// <summary><c>amount_bin</c> is not a whole number from 0 to 7.</summary>
    InvalidAmountBin,

    /// <summary><c>merchant_category</c> is not exactly one of the eight lowercase names.</summary>
    InvalidMerchantCategory,

    /// <summary><c>hour</c> is not a whole number from 0 to 23.</summary>
    InvalidHour,

    /// <summary><c>is_online</c> is not <c>true</c> or <c>false</c>.</summary>
    InvalidIsOnline,

    /// <summary>
    /// The card already has this <c>seq</c>, or other lines in the same file give it, with a different
    /// symbol. Never returned by <see cref="JournalFile.Read"/>; <see cref="JournalIngestor"/> gives it.
    /// What is stored stays.
    /// </summary>
    SeqConflict,
}

/// <summary>A quarantined line: its number and the reason, never its content (README SM-PRIV-004).</summary>
public readonly record struct QuarantinedLine(int LineNumber, JournalQuarantineReason Reason);

/// <summary>
/// What <see cref="JournalFile.Read"/> found in one file: either the file was rejected whole because
/// a line may carry a card number (README SM-JRNL-005), or every line was read as a record or
/// quarantined (SM-JRNL-002).
/// </summary>
public sealed class JournalReadResult
{
    private JournalReadResult(int rejectedLineNumber, IReadOnlyList<JournalRecord> records, IReadOnlyList<QuarantinedLine> quarantined)
    {
        RejectedLineNumber = rejectedLineNumber;
        Records = records;
        Quarantined = quarantined;
    }

    /// <summary>
    /// True when the file was rejected whole. Nothing from it is then returned: no records and no
    /// quarantined lines (no partial ingestion).
    /// </summary>
    public bool IsRejected => RejectedLineNumber > 0;

    /// <summary>The first line that may carry a card number, or 0 when the file was not rejected.</summary>
    public int RejectedLineNumber { get; }

    /// <summary>The valid records, in file order.</summary>
    public IReadOnlyList<JournalRecord> Records { get; }

    /// <summary>The quarantined lines, in file order.</summary>
    public IReadOnlyList<QuarantinedLine> Quarantined { get; }

    internal static JournalReadResult Rejected(int lineNumber) => new(lineNumber, [], []);

    internal static JournalReadResult Read(List<JournalRecord> records, List<QuarantinedLine> quarantined) =>
        new(0, records.AsReadOnly(), quarantined.AsReadOnly());
}
