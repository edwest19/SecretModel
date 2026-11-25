// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README §5 (SM-QNT).

using SecretModel.Core.Features;

namespace SecretModel.Tests.Features;

[TestClass]
public sealed class QuantizerTests
{
    [TestMethod]
    [TestCategory("SM-QNT-006")]
    public void Symbols_MatchThePythonReferenceGoldenVectors()
    {
        IReadOnlyList<GoldenVectors.SymbolVector> vectors = GoldenVectors.Quantizer().Vectors;
        Assert.HasCount(545, vectors, "The golden file should hold 545 vectors.");
        Assert.AreEqual(512, vectors.Count(v => v.Set == "all_512"), "The all_512 set should hold 512 vectors.");

        var mismatches = new List<string>();
        foreach (GoldenVectors.SymbolVector v in vectors)
        {
            int actual = Quantizer.ToSymbol(v.AmountBin, v.Category, v.Hour, v.IsOnline);
            if (actual != v.Symbol)
            {
                mismatches.Add($"({v.AmountBin}, \"{v.Category}\", {v.Hour}, {v.IsOnline}): expected {v.Symbol}, got {actual}");
            }
        }

        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    [TestMethod]
    [TestCategory("SM-QNT-001")]
    [DataRow(0, 0, 0, false, 0)]
    [DataRow(0, 0, 0, true, 1)]
    [DataRow(1, 2, 3, true, 87)]
    [DataRow(3, 5, 1, false, 234)]
    [DataRow(7, 7, 3, true, 511)]
    public void Pack_FollowsTheSpecFormula(int amountBin, int mccGroup, int timeBucket, bool isOnline, int expected)
    {
        Assert.AreEqual(expected, Quantizer.Pack(amountBin, mccGroup, timeBucket, isOnline));
    }

    [TestMethod]
    [TestCategory("SM-QNT-002")]
    public void Pack_IsABijectionOnto0To511()
    {
        var seen = new HashSet<int>();
        for (int a = 0; a < Quantizer.AmountBinCount; a++)
        for (int m = 0; m < Quantizer.MccGroupCount; m++)
        for (int t = 0; t < Quantizer.TimeBucketCount; t++)
        foreach (bool o in new[] { false, true })
        {
            int symbol = Quantizer.Pack(a, m, t, o);
            Assert.IsTrue(seen.Add(symbol), $"Symbol {symbol} was produced twice.");
        }

        Assert.HasCount(512, seen);
        Assert.AreEqual(0, seen.Min());
        Assert.AreEqual(511, seen.Max());
    }

    [TestMethod]
    [TestCategory("SM-QNT-003")]
    [DataRow(0, 0)]
    [DataRow(5, 0)]
    [DataRow(6, 1)]
    [DataRow(11, 1)]
    [DataRow(12, 2)]
    [DataRow(17, 2)]
    [DataRow(18, 3)]
    [DataRow(23, 3)]
    public void TimeBucket_Boundaries(int hour, int expected)
    {
        Assert.AreEqual(expected, Quantizer.TimeBucket(hour));
    }

    [TestMethod]
    [TestCategory("SM-QNT-003")]
    [DataRow(-1)]
    [DataRow(24)]
    [DataRow(25)]
    public void HourOutside0To23_IsRejected(int hour)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.TimeBucket(hour));
    }

    [TestMethod]
    [TestCategory("SM-QNT-004")]
    [DataRow("casino")]
    [DataRow("gas station")]
    [DataRow(" grocery")]
    [DataRow("")]
    public void UnknownCategory_MapsToGroup7(string category)
    {
        Assert.AreEqual(7, Quantizer.MccGroup(category));
    }

    [TestMethod]
    [TestCategory("SM-QNT-004")]
    [DataRow("grocery", 0)]
    [DataRow("GROCERY", 0)]
    [DataRow("gas", 1)]
    [DataRow("restaurant", 2)]
    [DataRow("retail", 3)]
    [DataRow("travel", 4)]
    [DataRow("Online_Market", 5)]
    [DataRow("atm", 6)]
    [DataRow("other", 7)]
    public void KnownCategory_MapsToItsGroup_IgnoringCase(string category, int expected)
    {
        Assert.AreEqual(expected, Quantizer.MccGroup(category));
    }

    [TestMethod]
    [TestCategory("SM-QNT-005")]
    [DataRow(-1)]
    [DataRow(8)]
    [DataRow(100)]
    public void AmountBinOutside0To7_IsRejectedNotClamped(int amountBin)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.Pack(amountBin, 0, 0, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.ToSymbol(amountBin, "grocery", 9, false));
    }

    [TestMethod]
    [TestCategory("SM-QNT-005")]
    public void OtherFeaturesOutOfRange_AreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.Pack(0, 8, 0, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.Pack(0, -1, 0, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.Pack(0, 0, 4, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Quantizer.Pack(0, 0, -1, false));
    }
}
