// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// SplitMix64 against the published reference outputs in GoldenVectors/splitmix64.json (copied from
// the rand_xoshiro crate's test, produced with the reference splitmix64.c). README SM-HMM-004.

using SecretModel.Core.Hmm;

namespace SecretModel.Tests.Hmm;

[TestClass]
public sealed class SplitMix64Tests
{
    [TestMethod]
    [TestCategory("SM-HMM-004")]
    public void Outputs_MatchThePublishedReference()
    {
        GoldenVectors.SplitMixFile golden = GoldenVectors.SplitMix64();
        Assert.HasCount(50, golden.Vectors);
        var rng = new SplitMix64(golden.Seed);
        for (int i = 0; i < golden.Vectors.Count; i++)
        {
            Assert.AreEqual(ulong.Parse(golden.Vectors[i]), rng.NextUInt64(), $"Output {i} differs.");
        }
    }

    [TestMethod]
    [TestCategory("SM-HMM-004")]
    public void UnitInterval_IsTopBitsPlusOneTimes2ToTheMinus53()
    {
        var bits = new SplitMix64(12345);
        var unit = new SplitMix64(12345);
        for (int i = 0; i < 1000; i++)
        {
            ulong x = bits.NextUInt64();
            double expected = ((x >> 11) + 1) / 9007199254740992.0;
            double actual = unit.NextUnitInterval();
            Assert.AreEqual(expected, actual);
            Assert.IsTrue(actual > 0 && actual <= 1, $"{actual} is outside (0, 1].");
        }
    }
}
