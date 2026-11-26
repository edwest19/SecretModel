// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

namespace SecretModel.Core.Scoring;

/// <summary>The distribution trained emissions are smoothed toward (OPEN-2).</summary>
public enum EmissionBackoff
{
    /// <summary>Every symbol equally likely: 1 / M.</summary>
    Uniform,

    /// <summary>
    /// For each state, the product of its four per-feature distributions (amount bin, merchant group,
    /// time bucket, online), read off its trained emission row. A new combination of habitual
    /// features stays plausible; a symbol with unusual features does not. Needs the 512-symbol
    /// alphabet of README §5.
    /// </summary>
    Features,
}

/// <summary>
/// Settings for building card profiles and scoring transactions (README §7). The defaults for the
/// window length (OPEN-3), the smoothing (OPEN-2) and the held-out share were chosen by the measurement
/// in README §7.1 and docs/studies/scoring-study.md.
/// </summary>
public sealed record ScoringOptions
{
    /// <summary>W: transactions per scored window, the transaction under test included (SM-SCORE-001).</summary>
    public int WindowLength { get; init; } = 1;

    /// <summary>
    /// λ in b' = (1 − λ)·b + λ·q: mixes each emission row, after training, with a backoff
    /// distribution q (see <see cref="Backoff"/>), so a symbol the card never used gets a non-zero
    /// probability (OPEN-2).
    /// </summary>
    public double EmissionSmoothing { get; init; } = 0.3;

    /// <summary>
    /// h: the newest share of the history held out of training. The model is trained on the older
    /// 1 − h; the baseline is built only from windows whose newest transaction is in the held-out part,
    /// so it measures how surprising unseen transactions were, not how well the model fits its own
    /// training data. 0 builds the baseline in-sample from the whole history.
    /// </summary>
    public double HoldoutFraction { get; init; } = 0.33;

    /// <summary>What each emission row is smoothed toward (OPEN-2).</summary>
    public EmissionBackoff Backoff { get; init; } = EmissionBackoff.Features;

    /// <summary>
    /// μ, used by <see cref="EmissionBackoff.Features"/> only: each per-feature distribution is
    /// mixed with its own uniform distribution, (1 − μ)·m + μ / size, so no feature value is impossible.
    /// </summary>
    public double FeatureFloor { get; init; } = 0.01;

    /// <summary>
    /// α: the share of a card's own history treated as its unusual tail. A window that is at least as
    /// unlikely as only a fraction p ≤ α of the card's baseline windows gets fraud score 1 − p/α;
    /// any window more ordinary than that scores 0.
    /// </summary>
    public double TailFraction { get; init; } = 0.05;

    /// <summary>fraud_score below this is LEGIT (SM-SCORE-006).</summary>
    public double LegitBelow { get; init; } = 0.3;

    /// <summary>fraud_score at or above this is FRAUD_SUSPECTED; between the two is REVIEW (SM-SCORE-006).</summary>
    public double FraudAtOrAbove { get; init; } = 0.7;

    /// <summary>Hidden states and symbols of each card's model (SM-HMM-008).</summary>
    public int StateCount { get; init; } = Hmm.HiddenMarkovModel.DefaultStateCount;

    public int SymbolCount { get; init; } = Hmm.HiddenMarkovModel.DefaultSymbolCount;

    /// <summary>Seed for every card's starting model, so training is reproducible (SM-HMM-004).</summary>
    public ulong Seed { get; init; } = 20261125;

    public int MaxIterations { get; init; } = 50;

    public double Tolerance { get; init; } = 1e-6;

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowLength, 1, nameof(WindowLength));
        if (!(EmissionSmoothing >= 0 && EmissionSmoothing < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(EmissionSmoothing), EmissionSmoothing, "Must be at least 0 and below 1.");
        }

        if (!(HoldoutFraction >= 0 && HoldoutFraction < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(HoldoutFraction), HoldoutFraction, "Must be at least 0 and below 1.");
        }

        if (HoldoutFraction > 0 && EmissionSmoothing == 0)
        {
            // A held-out symbol the training part never saw would have probability zero.
            throw new ArgumentOutOfRangeException(nameof(HoldoutFraction), "Holding data out needs EmissionSmoothing above 0.");
        }

        if (!(FeatureFloor > 0 && FeatureFloor <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(FeatureFloor), FeatureFloor, "Must be above 0 and at most 1.");
        }

        if (Backoff == EmissionBackoff.Features && SymbolCount != Features.Quantizer.SymbolCount)
        {
            throw new ArgumentOutOfRangeException(nameof(Backoff), "Feature backoff needs the 512-symbol alphabet.");
        }

        if (!(TailFraction > 0 && TailFraction <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(TailFraction), TailFraction, "Must be above 0 and at most 1.");
        }

        if (!(LegitBelow >= 0 && LegitBelow <= FraudAtOrAbove && FraudAtOrAbove <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(LegitBelow), "Thresholds must satisfy 0 <= LegitBelow <= FraudAtOrAbove <= 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(StateCount, 1, nameof(StateCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(SymbolCount, 1, nameof(SymbolCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIterations, 1, nameof(MaxIterations));
        if (!double.IsFinite(Tolerance) || Tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "Must be finite and not negative.");
        }
    }
}
