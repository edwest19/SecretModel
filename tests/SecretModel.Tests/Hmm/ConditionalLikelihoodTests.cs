// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// README SM-HMM-012: per-step log-likelihoods conditioned on everything before them, the filtered
// hidden state after a sequence, and continuing from it. The scorer's windows rely on these.

using SecretModel.Core.Hmm;

namespace SecretModel.Tests.Hmm;

[TestClass]
public sealed class ConditionalLikelihoodTests
{
    [TestMethod]
    [TestCategory("SM-HMM-012")]
    public void Steps_SumToTheLogLikelihood_AndMatchTheExactFixture()
    {
        GoldenVectors.HmmFile golden = GoldenVectors.Hmm();
        HiddenMarkovModel model = HiddenMarkovModelTests.Build(golden.Fixture.Pi, golden.Fixture.A, golden.Fixture.B);

        double[] steps = model.StepLogLikelihoods(golden.Forward.Observations);

        Assert.HasCount(golden.Forward.Observations.Length, steps);
        Assert.AreEqual(golden.Forward.LogLikelihood, steps.Sum(), 1e-9);
        Assert.AreEqual(model.LogLikelihood(golden.Forward.Observations), steps.Sum(), 1e-12);
    }

    [TestMethod]
    [TestCategory("SM-HMM-012")]
    public void FirstStep_IsTheMarginalProbabilityOfTheFirstSymbol()
    {
        GoldenVectors.HmmFile golden = GoldenVectors.Hmm();
        HiddenMarkovModel model = HiddenMarkovModelTests.Build(golden.Fixture.Pi, golden.Fixture.A, golden.Fixture.B);

        // P(o0 = 0) = sum_i pi_i b_i(0) = 1/2*1/2 + 1/4*1/8 + 1/4*1/4 = 0.34375, exact in binary.
        Assert.AreEqual(Math.Log(0.34375), model.StepLogLikelihoods([0])[0], 1e-15);
    }

    [TestMethod]
    [TestCategory("SM-HMM-012")]
    public void ContinuingFromTheFilteredState_GivesTheSameStepsAsTheWholeSequence()
    {
        int[] sequence = HiddenMarkovModelTests.Sample(HiddenMarkovModelTests.Teacher(), sequences: 1, length: 80, seed: 31)[0];
        HiddenMarkovModel model = HiddenMarkovModel.CreateRandom(5).Train([sequence], maxIterations: 10, tolerance: 0).Model;
        double[] whole = model.StepLogLikelihoods(sequence);

        foreach (int split in new[] { 1, 17, 50, 79 })
        {
            double[] state = model.FilterState(sequence[..split]);
            double[] rest = model.StepLogLikelihoods(sequence[split..], state);
            for (int t = 0; t < rest.Length; t++)
            {
                Assert.AreEqual(whole[split + t], rest[t], 1e-12, $"split {split}, step {t}");
            }
        }
    }

    [TestMethod]
    [TestCategory("SM-HMM-012")]
    public void FilteredState_IsADistribution_AndRejectsImpossibleSequences()
    {
        HiddenMarkovModel random = HiddenMarkovModel.CreateRandom(3);
        double[] state = random.FilterState([1, 2, 3, 4]);
        Assert.HasCount(5, state);
        Assert.AreEqual(1.0, state.Sum(), 1e-12);
        Assert.IsTrue(state.All(p => p >= 0));

        var model = new HiddenMarkovModel([1, 0], new double[,] { { 1, 0 }, { 0, 1 } }, new double[,] { { 1, 0 }, { 0, 1 } });
        Assert.ThrowsExactly<InvalidOperationException>(() => model.FilterState([0, 1]));
        double[] steps = model.StepLogLikelihoods([0, 1, 0]);
        Assert.AreEqual(0.0, steps[0]);
        Assert.AreEqual(double.NegativeInfinity, steps[1]);
        Assert.AreEqual(double.NegativeInfinity, steps[2], "Steps after an impossible one stay impossible.");
    }

    [TestMethod]
    [TestCategory("SM-HMM-012")]
    public void ViterbiFromAFilteredState_StartsFromOneTransitionOnward()
    {
        // From state 0 for sure, the next state is 1 for sure, so the path must start in state 1.
        var model = new HiddenMarkovModel(
            [0.5, 0.5],
            new double[,] { { 0, 1 }, { 1, 0 } },
            new double[,] { { 0.5, 0.5 }, { 0.5, 0.5 } });

        Assert.AreEqual(1, model.Viterbi([0, 0], stateBefore: [1, 0]).Path[0]);
        Assert.AreEqual(0, model.Viterbi([0, 0], stateBefore: [0, 1]).Path[0]);
        Assert.ThrowsExactly<ArgumentException>(() => model.Viterbi([0], stateBefore: [0.5, 0.4]));
        Assert.ThrowsExactly<ArgumentException>(() => model.StepLogLikelihoods([0], stateBefore: [1.0]));
    }
}
