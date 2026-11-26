// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for how a card's profile is built: README SM-SCORE-012 (held-out baseline) and SM-SCORE-013
// (emission smoothing), plus option checks.

using SecretModel.Core.Features;
using SecretModel.Core.Hmm;
using SecretModel.Core.Scoring;
using SecretModel.Synthetic;
using SecretModel.Tests.Hmm;

namespace SecretModel.Tests.Scoring;

[TestClass]
public sealed class CardProfileTests
{
    private static readonly ScoringOptions Defaults = new();

    [TestMethod]
    [TestCategory("SM-SCORE-012")]
    public void Model_IsTrainedOnTheOlderPartOnly()
    {
        int[] history = new SyntheticShopper(11).History(150);
        int trainLength = 150 - (int)Math.Floor(150 * 0.33);

        CardProfile profile = CardProfile.Build(history, Defaults);

        HiddenMarkovModel expected = CardProfile.Smooth(
            HiddenMarkovModel.CreateRandom(Defaults.Seed).Train([history[..trainLength]], Defaults.MaxIterations, Defaults.Tolerance).Model,
            Defaults);
        HiddenMarkovModelTests.AssertBitIdentical(expected, profile.Model);
        Assert.AreEqual(101, trainLength);
        Assert.HasCount(150 - trainLength, profile.Baseline, "One baseline window per held-out transaction when W = 1.");
    }

    [TestMethod]
    [TestCategory("SM-SCORE-012")]
    public void WithNothingHeldOut_EveryWindowOfTheHistoryIsInTheBaseline()
    {
        int[] history = new SyntheticShopper(12).History(80);
        CardProfile profile = CardProfile.Build(history, Defaults with { HoldoutFraction = 0, WindowLength = 4 });
        Assert.HasCount(80 - 4 + 1, profile.Baseline);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-012")]
    public void TooShortAHistory_IsRejected_AtTheDocumentedMinimum()
    {
        int minimum = CardProfile.MinimumHistoryFor(Defaults);
        // 14: floor(14 × 0.33) = 4 held out, leaving 10 for training. 13: 4 held out, only 9 left.
        Assert.AreEqual(14, minimum);
        _ = CardProfile.Build(new SyntheticShopper(13).History(minimum), Defaults);
        Assert.ThrowsExactly<ArgumentException>(() => CardProfile.Build(new SyntheticShopper(13).History(minimum - 1), Defaults));
        Assert.AreEqual(10, CardProfile.MinimumHistoryFor(Defaults with { HoldoutFraction = 0 }));
        Assert.AreEqual(12, CardProfile.MinimumHistoryFor(Defaults with { HoldoutFraction = 0, WindowLength = 12 }));
    }

    [TestMethod]
    [TestCategory("SM-SCORE-013")]
    public void FeatureBackoff_IsADistribution_ForEveryState()
    {
        CardProfile profile = CardProfile.Build(new SyntheticShopper(14).History(150), Defaults);
        for (int state = 0; state < profile.Model.StateCount; state++)
        {
            double[] q = CardProfile.FeatureBackoff(profile.Model, state, 0.01);
            Assert.AreEqual(1.0, q.Sum(), 1e-12);
            Assert.IsTrue(q.All(p => p > 0));
        }

        HiddenMarkovModelTests.AssertRowsSumToOne(profile.Model);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-013")]
    public void SmoothedEmissions_AreTheDocumentedMixture()
    {
        int[] history = new SyntheticShopper(15).History(150);
        HiddenMarkovModel trained = HiddenMarkovModel.CreateRandom(Defaults.Seed)
            .Train([history[..101]], Defaults.MaxIterations, Defaults.Tolerance).Model;

        HiddenMarkovModel smoothed = CardProfile.Smooth(trained, Defaults);

        for (int state = 0; state < 5; state++)
        {
            double[] q = CardProfile.FeatureBackoff(trained, state, 0.01);
            foreach (int symbol in new[] { 0, 65, 300, 511 })
            {
                Assert.AreEqual(0.7 * trained.Emission(state, symbol) + 0.3 * q[symbol], smoothed.Emission(state, symbol), 1e-15);
            }

            Assert.AreEqual(trained.Transition(state, 0), smoothed.Transition(state, 0), "Transitions are not smoothed.");
        }
    }

    [TestMethod]
    [TestCategory("SM-SCORE-013")]
    public void UnseenCombinationOfHabitualFeatures_IsMorePlausibleThanUnusualFeatures()
    {
        // History: grocery and gas, amount bins 1-2, mornings, in person. Never "bin 2 gas" in the morning.
        var history = new List<int>();
        for (int i = 0; i < 60; i++)
        {
            history.Add(Quantizer.Pack(1, 0, 1, false));
            history.Add(Quantizer.Pack(2, 0, 1, false));
            history.Add(Quantizer.Pack(1, 1, 1, false));
        }

        CardProfile profile = CardProfile.Build([.. history], Defaults);
        int newCombination = Quantizer.Pack(2, 1, 1, false);   // habitual amount, merchant and time
        int unusual = Quantizer.Pack(7, 6, 0, true);           // large ATM withdrawal at night, online

        Assert.IsFalse(profile.HasSeen(newCombination));
        double plausible = FraudScorer.Score(profile, newCombination, Defaults).WindowLogLikelihood!.Value;
        double implausible = FraudScorer.Score(profile, unusual, Defaults).WindowLogLikelihood!.Value;
        Assert.IsGreaterThan(implausible + 3, plausible, $"new combination {plausible:F2}, unusual {implausible:F2}");
    }

    [TestMethod]
    [TestCategory("SM-SCORE-013")]
    public void Options_RejectInvalidSmoothingAndHoldout()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { EmissionSmoothing = 0 }).Validate(), "λ = 0 with data held out");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { EmissionSmoothing = 1 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { HoldoutFraction = 1 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { FeatureFloor = 0 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { SymbolCount = 256 }).Validate(), "feature backoff needs 512 symbols");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { TailFraction = 0 }).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (Defaults with { WindowLength = 0 }).Validate());
        (Defaults with { EmissionSmoothing = 0, HoldoutFraction = 0 }).Validate();
        (Defaults with { SymbolCount = 256, Backoff = EmissionBackoff.Uniform }).Validate();
    }

    [TestMethod]
    [TestCategory("SM-SCORE-013")]
    public void Defaults_AreTheMeasuredSettings()
    {
        Assert.AreEqual(1, Defaults.WindowLength);
        Assert.AreEqual(EmissionBackoff.Features, Defaults.Backoff);
        Assert.AreEqual(0.3, Defaults.EmissionSmoothing);
        Assert.AreEqual(0.01, Defaults.FeatureFloor);
        Assert.AreEqual(0.33, Defaults.HoldoutFraction);
        Assert.AreEqual(0.05, Defaults.TailFraction);
        Assert.AreEqual(0.3, Defaults.LegitBelow);
        Assert.AreEqual(0.7, Defaults.FraudAtOrAbove);
    }
}
