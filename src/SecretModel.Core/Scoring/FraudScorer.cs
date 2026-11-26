// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using SecretModel.Core.Hmm;

namespace SecretModel.Core.Scoring;

public enum Verdict
{
    Legit,
    Review,
    FraudSuspected,
}

/// <summary>The outcome of scoring one transaction (README §7).</summary>
/// <param name="FraudScore">0 to 1, never NaN or infinity (SM-SCORE-008).</param>
/// <param name="Confidence">0 to 1: the highest score this profile can give (<see cref="FraudScorer.Confidence"/>). 0 at cold start.</param>
/// <param name="AnomalyFactors">Short reasons, such as <c>cold_start_no_history</c>.</param>
/// <param name="WindowLogLikelihood">Per-symbol log-likelihood of the scored window; null at cold start.</param>
/// <param name="BaselineTail">p: the share of the card's baseline windows at most this likely; null at cold start.</param>
/// <param name="ExpectedLow">5th percentile of the card's baseline (SM-SCORE-009); null at cold start.</param>
/// <param name="ExpectedHigh">95th percentile of the card's baseline (SM-SCORE-009); null at cold start.</param>
/// <param name="ViterbiPath">Most likely hidden states over the window (SM-SCORE-009); empty at cold start.</param>
public sealed record ScoreResult(
    double FraudScore,
    Verdict Verdict,
    double Confidence,
    IReadOnlyList<string> AnomalyFactors,
    double? WindowLogLikelihood,
    double? BaselineTail,
    double? ExpectedLow,
    double? ExpectedHigh,
    IReadOnlyList<int> ViterbiPath);

/// <summary>
/// Scores a transaction against its card's profile (README §7). The window is the profile's last
/// W − 1 history symbols plus the new symbol. Its log-likelihood, conditioned on the history before
/// the window, is divided by W (SM-SCORE-001, SM-SCORE-002) and compared with the card's own
/// baseline of length-W windows, conditioned the same way (SM-SCORE-003). Read-only and
/// deterministic (SM-SCORE-007).
/// </summary>
public static class FraudScorer
{
    public const string ColdStart = "cold_start_no_history";
    public const string WindowInUnusualTail = "window_in_unusual_tail";
    public const string SymbolNotInHistory = "symbol_not_in_history";
    public const string BelowAllBaseline = "below_all_baseline_windows";

    /// <summary>
    /// Confidence: the highest fraud score this profile can give, 1 − 1 / ((n + 1)·α) for n baseline
    /// windows, and never below 0. A small baseline can't put any window far into its tail, so it
    /// can't produce a high score; this says how strongly the profile is able to speak.
    /// </summary>
    public static double Confidence(CardProfile profile, ScoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        return Math.Max(0, 1 - 1 / ((profile.Baseline.Count + 1) * options.TailFraction));
    }

    /// <param name="profile">The card's profile, or null when the card has none (cold start, SM-SCORE-005).</param>
    /// <param name="symbol">The transaction's symbol, 0 to M − 1 (§5).</param>
    public static ScoreResult Score(CardProfile? profile, int symbol, ScoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (profile is null)
        {
            if (symbol < 0 || symbol >= options.SymbolCount)
            {
                throw new ArgumentOutOfRangeException(nameof(symbol), symbol, $"Symbols must be 0 to {options.SymbolCount - 1}.");
            }

            // Halfway between the thresholds: neither cleared nor accused (SM-SCORE-005).
            double neutral = (options.LegitBelow + options.FraudAtOrAbove) / 2;
            return new ScoreResult(neutral, Classify(neutral, options), 0, [ColdStart], null, null, null, null, []);
        }

        HiddenMarkovModel model = profile.Model;
        if (symbol < 0 || symbol >= model.SymbolCount)
        {
            throw new ArgumentOutOfRangeException(nameof(symbol), symbol, $"Symbols must be 0 to {model.SymbolCount - 1}.");
        }

        // The window is conditioned on the history before it, exactly as every baseline window is.
        int w = profile.WindowLength;
        int[] window = [.. profile.RecentSymbols, symbol];
        double perSymbol = model.StepLogLikelihoods(window, profile.StateBeforeWindowArray).Sum() / w;
        double tail = BaselineTail(profile.Baseline, perSymbol);
        double fraudScore = Math.Clamp(1 - tail / options.TailFraction, 0, 1);

        var factors = new List<string>();
        if (tail <= options.TailFraction)
        {
            factors.Add(WindowInUnusualTail);
        }

        if (perSymbol < profile.Baseline[0])
        {
            factors.Add(BelowAllBaseline);
        }

        if (!profile.HasSeen(symbol))
        {
            factors.Add(SymbolNotInHistory);
        }

        return new ScoreResult(
            fraudScore,
            Classify(fraudScore, options),
            Confidence(profile, options),
            factors,
            perSymbol,
            tail,
            Percentile(profile.Baseline, 0.05),
            Percentile(profile.Baseline, 0.95),
            model.Viterbi(window, profile.StateBeforeWindowArray).Path);
    }

    /// <summary>LEGIT, REVIEW or FRAUD_SUSPECTED by the configured thresholds (SM-SCORE-006).</summary>
    public static Verdict Classify(double fraudScore, ScoringOptions options) =>
        fraudScore < options.LegitBelow ? Verdict.Legit
        : fraudScore < options.FraudAtOrAbove ? Verdict.Review
        : Verdict.FraudSuspected;

    // p = (1 + number of baseline windows at most this likely) / (n + 1). Never 0, so even a window
    // below every baseline window, or one with log-likelihood −∞, gets a finite score.
    public static double BaselineTail(IReadOnlyList<double> sortedBaseline, double perSymbol)
    {
        int lo = 0;
        int hi = sortedBaseline.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sortedBaseline[mid] <= perSymbol)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return (1.0 + lo) / (sortedBaseline.Count + 1.0);
    }

    // Nearest-rank percentile of a sorted list.
    internal static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
