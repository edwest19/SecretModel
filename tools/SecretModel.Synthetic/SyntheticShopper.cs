// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Synthetic test shoppers for the scoring tests, the scoring study and the PowerShell harness.
// Everything here is made up: no real cards, people or merchants. Symbols are built with the real
// quantizer (README §5), so a symbol means the same thing here as in the service.

using SecretModel.Core.Features;
using SecretModel.Core.Hmm;

namespace SecretModel.Synthetic;

/// <summary>One transaction's features, before packing into a symbol.</summary>
public readonly record struct Purchase(int AmountBin, int MccGroup, int TimeBucket, bool IsOnline)
{
    public int Symbol => Quantizer.Pack(AmountBin, MccGroup, TimeBucket, IsOnline);
}

/// <summary>The kinds of injected anomaly (README §7.1).</summary>
public enum AnomalyKind
{
    /// <summary>Any of the 512 symbols, uniformly.</summary>
    Random,

    /// <summary>Account takeover: ATM, travel or online market; amount bin 6 or 7; mostly online and at night.</summary>
    Takeover,

    /// <summary>A habitual purchase with one feature changed: amount bin +3, night time, or the online flag flipped.</summary>
    Subtle,
}

/// <summary>
/// A shopper with three habit modes (routine, evening, weekend). Each mode prefers 2–3 merchant
/// groups, a band of 2–3 neighbouring amount bins, 1–2 time buckets and its own online rate; the
/// choices, and how modes follow each other, come from the seed. Habits use merchant groups 0–5 and
/// amount bins 0–5 only, so takeover-style purchases (ATM, bins 6–7) are always out of character.
/// </summary>
public sealed class SyntheticShopper
{
    private const int Modes = 3;

    private readonly int[][] mcc = new int[Modes][];
    private readonly int[][] amountBins = new int[Modes][];
    private readonly int[][] timeBuckets = new int[Modes][];
    private readonly double[] onlineRate = new double[Modes];
    private readonly double[,] modeTransitions = new double[Modes, Modes];
    private readonly SplitMix64 rng;
    private int mode;

    public SyntheticShopper(ulong seed)
    {
        rng = new SplitMix64(seed);
        for (int m = 0; m < Modes; m++)
        {
            mcc[m] = Distinct(rng, 2 + Next(2), 6);          // merchant groups 0-5
            int low = Next(4);                                 // amount band starts at 0-3
            amountBins[m] = [.. Enumerable.Range(low, 2 + Next(2)).Where(b => b <= 5)];
            timeBuckets[m] = Distinct(rng, 1 + Next(2), 4);
            onlineRate[m] = m == 2 ? 0.3 + 0.2 * rng.NextUnitInterval() : 0.05 * rng.NextUnitInterval();
            double stay = 0.5 + 0.3 * rng.NextUnitInterval();
            for (int n = 0; n < Modes; n++)
            {
                modeTransitions[m, n] = n == m ? stay : (1 - stay) / (Modes - 1);
            }
        }

        mode = Next(Modes);
    }

    /// <summary>The next habitual purchase; advances the shopper's habit mode.</summary>
    public Purchase Next()
    {
        int m = mode;
        var purchase = new Purchase(
            Pick(amountBins[m]), Pick(mcc[m]), Pick(timeBuckets[m]), rng.NextUnitInterval() < onlineRate[m]);
        double u = rng.NextUnitInterval();
        double cumulative = 0;
        for (int n = 0; n < Modes; n++)
        {
            cumulative += modeTransitions[m, n];
            if (u <= cumulative || n == Modes - 1)
            {
                mode = n;
                break;
            }
        }

        return purchase;
    }

    /// <summary>The next <paramref name="count"/> habitual symbols, oldest first.</summary>
    public int[] History(int count) => [.. Enumerable.Range(0, count).Select(_ => Next().Symbol)];

    /// <summary>An anomalous purchase of the given kind. Subtle anomalies start from a habitual purchase.</summary>
    public Purchase Anomaly(AnomalyKind kind)
    {
        switch (kind)
        {
            case AnomalyKind.Random:
                int symbol = Next(Quantizer.SymbolCount);
                return new Purchase(symbol >> 6, (symbol >> 3) & 7, (symbol >> 1) & 3, (symbol & 1) == 1);
            case AnomalyKind.Takeover:
                return new Purchase(
                    6 + Next(2),
                    Pick([4, 5, 6]),
                    rng.NextUnitInterval() < 0.7 ? 0 : Next(4),
                    rng.NextUnitInterval() < 0.8);
            default:
                Purchase p = Next();
                return Next(3) switch
                {
                    0 => p with { AmountBin = Math.Min(7, p.AmountBin + 3) },
                    1 => p with { TimeBucket = p.TimeBucket == 0 ? 3 : 0 },
                    _ => p with { IsOnline = !p.IsOnline },
                };
        }
    }

    private int Next(int count) => (int)(rng.NextUInt64() % (ulong)count);

    private int Pick(int[] values) => values[Next(values.Length)];

    private static int[] Distinct(SplitMix64 rng, int count, int range)
    {
        var chosen = new List<int>();
        while (chosen.Count < count)
        {
            int v = (int)(rng.NextUInt64() % (ulong)range);
            if (!chosen.Contains(v))
            {
                chosen.Add(v);
            }
        }

        chosen.Sort();
        return [.. chosen];
    }
}
