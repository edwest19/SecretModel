// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

using SecretModel.Core.Features;
using SecretModel.Core.Journal;

namespace SecretModel.Core.Scoring;

/// <summary>
/// One card's symbol history (README §9.3): every transaction ingested for the card, as its sequence
/// number and its observation symbol, in ascending <c>seq</c> order. Kept so the card can be retrained
/// on its full history when more transactions arrive. Holds only sequence numbers and symbols
/// (README SM-PRIV-003). Stored as format <c>secretmodel-history</c> version 1.
/// </summary>
public sealed class CardHistory
{
    private readonly long[] seq;
    private readonly int[] symbols;

    /// <summary>
    /// A history from matching lists: <paramref name="seq"/> strictly ascending, each 0 to
    /// <see cref="JournalFile.MaxSeq"/>, and <paramref name="symbols"/> the same length, each a valid
    /// symbol (0 to <see cref="Quantizer.SymbolCount"/> − 1). At least one transaction.
    /// </summary>
    public CardHistory(IReadOnlyList<long> seq, IReadOnlyList<int> symbols)
    {
        ArgumentNullException.ThrowIfNull(seq);
        ArgumentNullException.ThrowIfNull(symbols);
        if (seq.Count == 0)
        {
            throw new ArgumentException("A history holds at least one transaction.", nameof(seq));
        }

        if (symbols.Count != seq.Count)
        {
            throw new ArgumentException("A history needs exactly one symbol for each seq.", nameof(symbols));
        }

        for (int i = 0; i < seq.Count; i++)
        {
            if (seq[i] is < 0 or > JournalFile.MaxSeq)
            {
                throw new ArgumentException($"seq item {i} must be 0 to {JournalFile.MaxSeq}.", nameof(seq));
            }

            if (i > 0 && seq[i] <= seq[i - 1])
            {
                throw new ArgumentException($"seq item {i} must be greater than the one before it.", nameof(seq));
            }

            if (symbols[i] is < 0 or >= Quantizer.SymbolCount)
            {
                throw new ArgumentException($"symbols item {i} must be 0 to {Quantizer.SymbolCount - 1}.", nameof(symbols));
            }
        }

        this.seq = [.. seq];
        this.symbols = [.. symbols];
    }

    /// <summary>How many transactions the history holds.</summary>
    public int Count => seq.Length;

    /// <summary>The sequence numbers, ascending.</summary>
    public IReadOnlyList<long> Seq => seq;

    /// <summary>The symbols, in the same order: <c>Symbols[i]</c> is the transaction <c>Seq[i]</c>.</summary>
    public IReadOnlyList<int> Symbols => symbols;
}
