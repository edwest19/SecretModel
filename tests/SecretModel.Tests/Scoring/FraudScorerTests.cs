// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README §7 (SM-SCORE). Shoppers and anomalies come from tools/SecretModel.Synthetic.

using System.Diagnostics;
using SecretModel.Core.Hmm;
using SecretModel.Core.Scoring;
using SecretModel.Synthetic;
using SecretModel.Tests.Hmm;

namespace SecretModel.Tests.Scoring;

[TestClass]
public sealed class FraudScorerTests
{
    private static readonly ScoringOptions Defaults = new();
    private static readonly Lazy<(int[] History, CardProfile Profile)> Shopper1 = new(() =>
    {
        int[] history = new SyntheticShopper(1).History(300);
        return (history, CardProfile.Build(history, Defaults));
    });

    [TestMethod]
    [TestCategory("SM-SCORE-001")]
    public void Window_IsTheLastWMinus1HistorySymbolsPlusTheNewOne()
    {
        int[] history = new SyntheticShopper(2).History(120);
        var options = Defaults with { WindowLength = 4 };
        CardProfile profile = CardProfile.Build(history, options);

        ScoreResult result = FraudScorer.Score(profile, 7, options);

        CollectionAssert.AreEqual(history[^3..], profile.RecentSymbols.ToArray());
        Assert.HasCount(4, result.ViterbiPath);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-001")]
    [TestCategory("SM-SCORE-002")]
    public void WindowLikelihood_IsConditionedOnTheWholeHistory_AndDividedByW()
    {
        int[] history = new SyntheticShopper(3).History(150);
        foreach (int w in new[] { 1, 3, 6 })
        {
            var options = Defaults with { WindowLength = w };
            CardProfile profile = CardProfile.Build(history, options);
            const int symbol = 200;

            double expected = profile.Model.StepLogLikelihoods([.. history, symbol])[^w..].Sum() / w;
            double? actual = FraudScorer.Score(profile, symbol, options).WindowLogLikelihood;

            Assert.IsNotNull(actual);
            Assert.AreEqual(expected, actual.Value, 1e-12, $"W = {w}");
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-003")]
    public void Baseline_IsEveryHeldOutWindowOfLengthW_ConditionedTheSameWay()
    {
        int[] history = new SyntheticShopper(4).History(150);
        var options = Defaults with { WindowLength = 3 };
        CardProfile profile = CardProfile.Build(history, options);

        double[] steps = profile.Model.StepLogLikelihoods(history);
        int trainLength = 150 - (int)Math.Floor(150 * options.HoldoutFraction);
        double[] expected = [.. Enumerable.Range(trainLength, 150 - trainLength)
            .Select(end => steps[(end - 2)..(end + 1)].Sum() / 3)
            .Order()];

        Assert.HasCount(expected.Length, profile.Baseline);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], profile.Baseline[i], 1e-12, $"baseline[{i}]");
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-003")]
    public void ScoringUsesTheProfilesW_WhateverTheOptionsSay()
    {
        CardProfile profile = CardProfile.Build(new SyntheticShopper(5).History(120), Defaults with { WindowLength = 3 });

        ScoreResult result = FraudScorer.Score(profile, 9, Defaults with { WindowLength = 1 });

        Assert.AreEqual(3, profile.WindowLength);
        Assert.HasCount(3, result.ViterbiPath);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-004")]
    public void Discrimination_MeanAucOver20Shoppers_IsAtLeast080()
    {
        AnomalyKind[] kinds = [AnomalyKind.Random, AnomalyKind.Takeover, AnomalyKind.Subtle];
        var aucs = new List<double>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var shopper = new SyntheticShopper(seed);
            CardProfile profile = CardProfile.Build(shopper.History(300), Defaults);
            double[] normal = [.. Enumerable.Range(0, 300).Select(_ => FraudScorer.Score(profile, shopper.Next().Symbol, Defaults).FraudScore)];
            double[] anomalous = [.. kinds.SelectMany(k => Enumerable.Range(0, 100)
                .Select(_ => FraudScorer.Score(profile, shopper.Anomaly(k).Symbol, Defaults).FraudScore))];
            aucs.Add(Auc(normal, anomalous));
        }

        string detail = string.Join(", ", aucs.Select((a, i) => $"{i + 1}: {a:F3}"));
        Assert.IsGreaterThanOrEqualTo(0.80, aucs.Average(), $"Per shopper: {detail}");
    }

    [TestMethod]
    [TestCategory("SM-SCORE-004")]
    public void SweepOfAll512Symbols_GivesMoreThanOneVerdict_ForEveryShopper()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            CardProfile profile = CardProfile.Build(new SyntheticShopper(seed).History(300), Defaults);
            int classes = Enumerable.Range(0, 512).Select(s => FraudScorer.Score(profile, s, Defaults).Verdict).Distinct().Count();
            Assert.IsGreaterThan(1, classes, $"Shopper {seed}: every symbol got the same verdict.");
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-004")]
    public void SyntheticShoppers_AreReproducible()
    {
        CollectionAssert.AreEqual(new SyntheticShopper(9).History(50), new SyntheticShopper(9).History(50));
        CollectionAssert.AreNotEqual(new SyntheticShopper(9).History(50), new SyntheticShopper(10).History(50));
    }

    [TestMethod]
    [TestCategory("SM-SCORE-005")]
    public void ColdStart_IsReview_WithZeroConfidence_AndItsFactor()
    {
        ScoreResult result = FraudScorer.Score(null, 123, Defaults);

        Assert.AreEqual(Verdict.Review, result.Verdict);
        Assert.IsLessThanOrEqualTo(0.3, result.Confidence);
        Assert.AreEqual(0.0, result.Confidence);
        CollectionAssert.AreEqual(new[] { "cold_start_no_history" }, result.AnomalyFactors.ToArray(), "The spec's exact factor name.");
        Assert.AreEqual(0.5, result.FraudScore);
        Assert.IsEmpty(result.ViterbiPath);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FraudScorer.Score(null, 512, Defaults));
    }

    [TestMethod]
    [TestCategory("SM-SCORE-006")]
    [DataRow(0.0, Verdict.Legit)]
    [DataRow(0.2999, Verdict.Legit)]
    [DataRow(0.3, Verdict.Review)]
    [DataRow(0.6999, Verdict.Review)]
    [DataRow(0.7, Verdict.FraudSuspected)]
    [DataRow(1.0, Verdict.FraudSuspected)]
    public void DefaultThresholds(double score, Verdict expected)
    {
        Assert.AreEqual(expected, FraudScorer.Classify(score, Defaults));
    }

    [TestMethod]
    [TestCategory("SM-SCORE-006")]
    public void Thresholds_AreConfigurable_AndMustBeOrdered()
    {
        var options = Defaults with { LegitBelow = 0.5, FraudAtOrAbove = 0.9 };
        Assert.AreEqual(Verdict.Legit, FraudScorer.Classify(0.4, options));
        Assert.AreEqual(Verdict.Review, FraudScorer.Classify(0.8, options));
        Assert.AreEqual(Verdict.FraudSuspected, FraudScorer.Classify(0.9, options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { LegitBelow = 0.8, FraudAtOrAbove = 0.7 }).Validate());
    }

    [TestMethod]
    [TestCategory("SM-SCORE-007")]
    public void SameInputAndProfile_GiveIdenticalResults_AndRebuildingGivesTheSameProfile()
    {
        (int[] history, CardProfile profile) = Shopper1.Value;
        CardProfile rebuilt = CardProfile.Build(history, Defaults);
        HiddenMarkovModelTests.AssertBitIdentical(profile.Model, rebuilt.Model);
        CollectionAssert.AreEqual(profile.Baseline.ToArray(), rebuilt.Baseline.ToArray());

        foreach (int symbol in new[] { 0, 77, 300, 511 })
        {
            ScoreResult first = FraudScorer.Score(profile, symbol, Defaults);
            ScoreResult second = FraudScorer.Score(rebuilt, symbol, Defaults);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(first.FraudScore), BitConverter.DoubleToInt64Bits(second.FraudScore));
            Assert.AreEqual(first.WindowLogLikelihood, second.WindowLogLikelihood);
            CollectionAssert.AreEqual(first.AnomalyFactors.ToArray(), second.AnomalyFactors.ToArray());
            CollectionAssert.AreEqual(first.ViterbiPath.ToArray(), second.ViterbiPath.ToArray());
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-008")]
    public void FraudScore_IsFiniteAndIn01_ForEverySymbol_UnderSeveralProfiles()
    {
        int[] history = new SyntheticShopper(6).History(200);
        ScoringOptions[] variants =
        [
            Defaults,
            Defaults with { WindowLength = 5 },
            Defaults with { Backoff = EmissionBackoff.Uniform, EmissionSmoothing = 0.01 },
            Defaults with { HoldoutFraction = 0, EmissionSmoothing = 0, Backoff = EmissionBackoff.Uniform }, // unseen symbols: probability 0
        ];
        foreach (ScoringOptions options in variants)
        {
            CardProfile profile = CardProfile.Build(history, options);
            for (int symbol = 0; symbol < 512; symbol++)
            {
                ScoreResult r = FraudScorer.Score(profile, symbol, options);
                Assert.IsTrue(double.IsFinite(r.FraudScore) && r.FraudScore >= 0 && r.FraudScore <= 1, $"{options} symbol {symbol}: {r.FraudScore}");
            }
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-008")]
    public void AWindowWithProbabilityZero_GetsTheHighestScoreTheProfileAllows()
    {
        int[] history = new SyntheticShopper(6).History(200);
        var options = Defaults with { HoldoutFraction = 0, EmissionSmoothing = 0, Backoff = EmissionBackoff.Uniform };
        CardProfile profile = CardProfile.Build(history, options);
        int unseen = Enumerable.Range(0, 512).First(s => !profile.HasSeen(s));

        ScoreResult r = FraudScorer.Score(profile, unseen, options);

        Assert.AreEqual(double.NegativeInfinity, r.WindowLogLikelihood);
        Assert.AreEqual(FraudScorer.Confidence(profile, options), r.FraudScore, 1e-12);
        Assert.AreEqual(1.0 / (profile.Baseline.Count + 1), r.BaselineTail!.Value, 1e-15);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-009")]
    public void Result_HasTheViterbiPath_AndThe5thTo95thPercentileRange()
    {
        CardProfile profile = Shopper1.Value.Profile;

        ScoreResult r = FraudScorer.Score(profile, 42, Defaults);

        Assert.HasCount(profile.WindowLength, r.ViterbiPath);
        Assert.IsTrue(r.ViterbiPath.All(s => s >= 0 && s < profile.Model.StateCount));
        double[] sorted = [.. profile.Baseline];
        double low = r.ExpectedLow!.Value;
        double high = r.ExpectedHigh!.Value;
        Assert.AreEqual(sorted[(int)Math.Ceiling(0.05 * sorted.Length) - 1], low);
        Assert.AreEqual(sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1], high);
        Assert.IsLessThanOrEqualTo(high, low);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-010")]
    public void P95Latency_IsUnder200ms_ForAWarmProfile()
    {
        CardProfile profile = Shopper1.Value.Profile;
        FraudScorer.Score(profile, 0, Defaults); // warm up
        var rng = new SplitMix64(10);
        var times = new double[1000];
        for (int i = 0; i < times.Length; i++)
        {
            int symbol = (int)(rng.NextUInt64() % 512);
            long start = Stopwatch.GetTimestamp();
            FraudScorer.Score(profile, symbol, Defaults);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        double p95 = times[949];
        Assert.IsLessThan(200.0, p95, $"p95 was {p95:F3} ms");
    }

    [TestMethod]
    [TestCategory("SM-SCORE-011")]
    public void FraudScore_IsOneMinusTheBaselineTailOverAlpha()
    {
        CardProfile profile = Shopper1.Value.Profile;
        foreach (int symbol in new[] { 0, 64, 130, 263, 511 })
        {
            ScoreResult r = FraudScorer.Score(profile, symbol, Defaults);
            double ll = r.WindowLogLikelihood!.Value;
            double p = (1.0 + profile.Baseline.Count(b => b <= ll)) / (profile.Baseline.Count + 1);
            Assert.AreEqual(p, r.BaselineTail!.Value, 1e-15, $"symbol {symbol}");
            Assert.AreEqual(Math.Clamp(1 - p / 0.05, 0, 1), r.FraudScore, 1e-15, $"symbol {symbol}");
            Assert.AreEqual(p <= 0.05, r.AnomalyFactors.Contains(FraudScorer.WindowInUnusualTail));
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-011")]
    public void BaselineTail_CountsTiesAsAtMostAsLikely()
    {
        double[] baseline = [-5, -3, -3, -1];
        Assert.AreEqual(1.0 / 5, FraudScorer.BaselineTail(baseline, -6));
        Assert.AreEqual(2.0 / 5, FraudScorer.BaselineTail(baseline, -5));
        Assert.AreEqual(4.0 / 5, FraudScorer.BaselineTail(baseline, -3));
        Assert.AreEqual(5.0 / 5, FraudScorer.BaselineTail(baseline, 0));
        Assert.AreEqual(1.0 / 5, FraudScorer.BaselineTail(baseline, double.NegativeInfinity));
    }

    [TestMethod]
    [TestCategory("SM-SCORE-014")]
    public void Confidence_IsTheHighestScoreTheProfileCanGive()
    {
        CardProfile profile = Shopper1.Value.Profile;
        double confidence = FraudScorer.Confidence(profile, Defaults);
        Assert.AreEqual(1 - 1 / ((profile.Baseline.Count + 1) * 0.05), confidence, 1e-15);

        double highest = Enumerable.Range(0, 512).Max(s => FraudScorer.Score(profile, s, Defaults).FraudScore);
        Assert.IsLessThanOrEqualTo(confidence + 1e-15, highest);
        Assert.AreEqual(confidence, FraudScorer.Score(profile, 7, Defaults).Confidence);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-014")]
    [DataRow(84, false, false)]
    [DataRow(85, true, false)]
    [DataRow(199, true, false)]
    [DataRow(200, true, true)]
    public void HistoryLength_DecidesWhetherReviewAndFraudArePossible(int length, bool reviewPossible, bool fraudPossible)
    {
        CardProfile profile = CardProfile.Build(new SyntheticShopper(7).History(length), Defaults);
        double confidence = FraudScorer.Confidence(profile, Defaults);

        Assert.AreEqual(reviewPossible, confidence >= Defaults.LegitBelow, $"confidence {confidence}");
        Assert.AreEqual(fraudPossible, confidence >= Defaults.FraudAtOrAbove, $"confidence {confidence}");
    }

    [TestMethod]
    [TestCategory("SM-SCORE-015")]
    public void Scoring_DoesNotChangeTheProfile()
    {
        CardProfile profile = CardProfile.Build(new SyntheticShopper(8).History(150), Defaults with { WindowLength = 3 });
        int[] recent = [.. profile.RecentSymbols];
        double[] state = [.. profile.StateBeforeWindow];
        double[] baseline = [.. profile.Baseline];
        byte[] model = HmmModelFile.Write(profile.Model);

        for (int symbol = 0; symbol < 512; symbol += 7)
        {
            FraudScorer.Score(profile, symbol, Defaults);
        }

        CollectionAssert.AreEqual(recent, profile.RecentSymbols.ToArray());
        CollectionAssert.AreEqual(state, profile.StateBeforeWindow.ToArray());
        CollectionAssert.AreEqual(baseline, profile.Baseline.ToArray());
        CollectionAssert.AreEqual(model, HmmModelFile.Write(profile.Model));
    }

    internal static double Auc(double[] normal, double[] anomalous)
    {
        double wins = 0;
        foreach (double a in anomalous)
        {
            foreach (double n in normal)
            {
                wins += a > n ? 1 : a == n ? 0.5 : 0;
            }
        }

        return wins / (anomalous.Length * (double)normal.Length);
    }
}
