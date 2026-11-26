// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for the card profile file (docs/formats/profile-v1.md): README SM-STOR-007 (locked format),
// SM-STOR-003 (the model and its likelihood history in one file), SM-STOR-005 (a profile read back
// scores exactly as before) and SM-PRIV-003 (only allowed values are persisted).

using System.Text;
using System.Text.Json;
using SecretModel.Core.Hmm;
using SecretModel.Core.Scoring;
using SecretModel.Synthetic;
using SecretModel.Tests.Hmm;

namespace SecretModel.Tests.Scoring;

[TestClass]
public sealed class CardProfileFileTests
{
    // The known answer from docs/formats/profile-v1.md.
    internal const string DocumentedExample =
        "{\"format\":\"secretmodel-profile\",\"version\":1,\"window\":2,\"history\":12,\"recent\":[2],\"state_before\":[0.25,0.75],\"seen\":[0,1,2],\"baseline\":[-1.5,-0.75,-0.5],\"model\":{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}}";

    private static readonly ScoringOptions Defaults = new();

    // Two real profiles from synthetic shoppers: the default W = 1, and W = 4 so that "recent" and
    // "state_before" carry real context.
    internal static readonly Lazy<CardProfile> RealProfile = new(() =>
        CardProfile.Build(new SyntheticShopper(21).History(200), Defaults));

    private static readonly Lazy<CardProfile> RealProfileW4 = new(() =>
        CardProfile.Build(new SyntheticShopper(22).History(200), Defaults with { WindowLength = 4 }));

    /// <summary>The documented example, or a variant with a different first baseline value.</summary>
    internal static CardProfile DocumentedProfile(double firstBaseline = -1.5) => CardProfile.FromParts(
        new HiddenMarkovModel([1, 0], new double[,] { { 0.75, 0.25 }, { 0, 1 } }, new double[,] { { 0.5, 0.25, 0.25 }, { 0.1, 0.2, 0.7 } }),
        windowLength: 2,
        historyLength: 12,
        recent: [2],
        stateBefore: [0.25, 0.75],
        seen: [0, 1, 2],
        baseline: [firstBaseline, -0.75, -0.5]);

    [TestMethod]
    [TestCategory("SM-STOR-007")]
    public void Write_MatchesTheDocumentedBytes()
    {
        byte[] bytes = CardProfileFile.Write(DocumentedProfile());

        Assert.HasCount(295, bytes, "The document says 295 bytes.");
        Assert.AreEqual(DocumentedExample, Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    [TestCategory("SM-STOR-007")]
    public void Read_TheDocumentedBytes_GivesTheDocumentedProfile()
    {
        CardProfile profile = CardProfileFile.Read(Encoding.UTF8.GetBytes(DocumentedExample));

        Assert.AreEqual(2, profile.WindowLength);
        Assert.AreEqual(12, profile.HistoryLength);
        CollectionAssert.AreEqual(new[] { 2 }, profile.RecentSymbols.ToArray());
        CollectionAssert.AreEqual(new[] { 0.25, 0.75 }, profile.StateBeforeWindow.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, profile.SeenSymbols.ToArray());
        CollectionAssert.AreEqual(new[] { -1.5, -0.75, -0.5 }, profile.Baseline.ToArray());
        HiddenMarkovModelTests.AssertBitIdentical(DocumentedProfile().Model, profile.Model);
    }

    [TestMethod]
    [TestCategory("SM-STOR-005")]
    [TestCategory("SM-STOR-003")]
    [DataRow(1)]
    [DataRow(4)]
    public void RoundTrip_OfARealProfile_IsBitExact_AndScoresEverySymbolIdentically(int window)
    {
        CardProfile original = window == 1 ? RealProfile.Value : RealProfileW4.Value;
        ScoringOptions options = Defaults with { WindowLength = window };

        byte[] first = CardProfileFile.Write(original);
        CardProfile back = CardProfileFile.Read(first);

        Assert.AreEqual(original.WindowLength, back.WindowLength);
        Assert.AreEqual(original.HistoryLength, back.HistoryLength);
        CollectionAssert.AreEqual(original.RecentSymbols.ToArray(), back.RecentSymbols.ToArray());
        CollectionAssert.AreEqual(original.SeenSymbols.ToArray(), back.SeenSymbols.ToArray());
        AssertBitIdentical(original.StateBeforeWindow, back.StateBeforeWindow, "state_before");
        AssertBitIdentical(original.Baseline, back.Baseline, "baseline");
        HiddenMarkovModelTests.AssertBitIdentical(original.Model, back.Model);
        CollectionAssert.AreEqual(first, CardProfileFile.Write(back), "Writing the read profile must give the same bytes.");
        AssertScoresIdentical(original, back, options);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-003")]
    public void File_HoldsOnlySymbolsCountsAndModelParameters()
    {
        using JsonDocument document = JsonDocument.Parse(CardProfileFile.Write(RealProfileW4.Value));
        JsonElement root = document.RootElement;

        // Every property, and what it holds in SM-PRIV-003's terms.
        (string Name, string Holds)[] allowed =
        [
            ("format", "format name"), ("version", "format version"),
            ("window", "model parameter"), ("history", "model parameter"),
            ("recent", "observation symbols"), ("state_before", "derived model parameters"),
            ("seen", "observation symbols"), ("baseline", "derived model parameters"),
            ("model", "derived model parameters"),
        ];
        CollectionAssert.AreEqual(allowed.Select(a => a.Name).ToArray(), root.EnumerateObject().Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "format", "version", "states", "symbols", "pi", "a", "b" },
            root.GetProperty("model").EnumerateObject().Select(p => p.Name).ToArray());

        // The only strings anywhere are the two format names; everything else is a number.
        var strings = new List<string>();
        int numbers = 0;
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        Walk(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in e.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
                case JsonValueKind.String:
                    strings.Add(e.GetString()!);
                    break;
                case JsonValueKind.Number:
                    numbers++;
                    break;
                default:
                    Assert.Fail($"Unexpected JSON value kind {e.ValueKind}.");
                    break;
            }
        }

        Walk(root);
        CollectionAssert.AreEqual(new[] { CardProfileFile.Format, HmmModelFile.Format }, strings.ToArray());
        Assert.IsGreaterThan(2000, numbers, "A real 5-state, 512-symbol profile holds thousands of numbers.");
    }

    [TestMethod]
    [TestCategory("SM-STOR-007")]
    [DataRow("not json", "The profile file is not valid JSON.")]
    [DataRow("[1]", "The profile file must hold a JSON object.")]
    public void Read_RejectsFilesThatAreNotAProfileObject(string json, string message)
    {
        InvalidDataException e = Assert.ThrowsExactly<InvalidDataException>(() => CardProfileFile.Read(Encoding.UTF8.GetBytes(json)));
        Assert.AreEqual(message, e.Message);
    }

    // Each row changes one thing in the documented example.
    [TestMethod]
    [TestCategory("SM-STOR-007")]
    [DataRow("\"format\":\"secretmodel-profile\"", "\"format\":\"secretmodel-hmm\"", "The format must be \"secretmodel-profile\".")]
    [DataRow("\"format\":\"secretmodel-profile\"", "\"format\":1", "The format must be \"secretmodel-profile\".")]
    [DataRow("\"version\":1,\"window\"", "\"version\":2,\"window\"", "Only version 1 is supported.")]
    [DataRow("\"window\":2,\"history\":12", "\"history\":12,\"window\":2", "must have exactly these properties, in this order")]
    [DataRow("\"seen\":[0,1,2],", "", "must have exactly these properties, in this order")]
    [DataRow(",\"model\":", ",\"client\":\"x\",\"model\":", "must have exactly these properties, in this order")]
    [DataRow("\"window\":2", "\"window\":0", "\"window\" must be a whole number of at least 1.")]
    [DataRow("\"history\":12", "\"history\":1", "The window must be at least 1 and no longer than the history.")]
    [DataRow("\"recent\":[2]", "\"recent\":[]", "There must be exactly W − 1 = 1 recent symbol(s), each 0 to 2.")]
    [DataRow("\"recent\":[2]", "\"recent\":[3]", "There must be exactly W − 1 = 1 recent symbol(s), each 0 to 2.")]
    [DataRow("\"recent\":[2]", "\"recent\":[2.5]", "\"recent\" item 0 must be a whole number.")]
    [DataRow("\"recent\":[2]", "\"recent\":2", "\"recent\" must be an array of whole numbers.")]
    [DataRow("\"state_before\":[0.25,0.75]", "\"state_before\":[0.25,0.5]", "The state before the window must be a distribution over 2 states.")]
    [DataRow("\"state_before\":[0.25,0.75]", "\"state_before\":[1]", "\"state_before\" must be an array of 2 numbers.")]
    [DataRow("\"seen\":[0,1,2]", "\"seen\":[]", "There must be from 1 to (history) seen symbols.")]
    [DataRow("\"history\":12", "\"history\":2", "There must be from 1 to (history) seen symbols.")]
    [DataRow("\"seen\":[0,1,2]", "\"seen\":[0,2,1]", "The seen symbols must be ascending, without repeats, each 0 to 2.")]
    [DataRow("\"seen\":[0,1,2]", "\"seen\":[0,0,1,2]", "The seen symbols must be ascending, without repeats, each 0 to 2.")]
    [DataRow("\"seen\":[0,1,2]", "\"seen\":[0,1]", "Every recent symbol must be among the seen symbols.")]
    [DataRow("\"baseline\":[-1.5,-0.75,-0.5]", "\"baseline\":[]", "The baseline must hold 1 to (history − window + 1) finite values.")]
    [DataRow("\"baseline\":[-1.5,-0.75,-0.5]", "\"baseline\":[-12,-11,-10,-9,-8,-7,-6,-5,-4,-3,-2,-1]", "The baseline must hold 1 to (history − window + 1) finite values.")]
    [DataRow("\"baseline\":[-1.5,-0.75,-0.5]", "\"baseline\":[-0.75,-1.5,-0.5]", "The baseline must be in ascending order.")]
    [DataRow("\"baseline\":[-1.5,-0.75,-0.5]", "\"baseline\":[-1.5,-0.75,-1e999]", "\"baseline\" item 2 must be a finite number.")]
    [DataRow("\"baseline\":[-1.5,-0.75,-0.5]", "\"baseline\":\"x\"", "\"baseline\" must be an array of numbers.")]
    [DataRow("\"format\":\"secretmodel-hmm\"", "\"format\":\"other\"", "The format must be \"secretmodel-hmm\".")]
    [DataRow("\"a\":[[0.75,0.25],[0,1]]", "\"a\":[[0.75,0.15],[0,1]]", "a row 0 sums to 0.9, not 1.")]
    public void Read_RejectsAProfileThatBreaksARule(string find, string replace, string messagePart)
    {
        Assert.AreEqual(1, CountOf(DocumentedExample, find), "Each row must change exactly one place.");
        string json = DocumentedExample.Replace(find, replace, StringComparison.Ordinal);

        InvalidDataException e = Assert.ThrowsExactly<InvalidDataException>(() => CardProfileFile.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(messagePart, e.Message);
    }

    /// <summary>Every field of every score is bit-identical, for every possible symbol.</summary>
    internal static void AssertScoresIdentical(CardProfile expected, CardProfile actual, ScoringOptions options)
    {
        int symbols = expected.Model.SymbolCount;
        Assert.AreEqual(512, symbols);
        for (int s = 0; s < symbols; s++)
        {
            ScoreResult x = FraudScorer.Score(expected, s, options);
            ScoreResult y = FraudScorer.Score(actual, s, options);
            string at = $"symbol {s}";
            Assert.AreEqual(Bits(x.FraudScore), Bits(y.FraudScore), at);
            Assert.AreEqual(x.Verdict, y.Verdict, at);
            Assert.AreEqual(Bits(x.Confidence), Bits(y.Confidence), at);
            CollectionAssert.AreEqual(x.AnomalyFactors.ToArray(), y.AnomalyFactors.ToArray(), at);
            Assert.AreEqual(Bits(x.WindowLogLikelihood), Bits(y.WindowLogLikelihood), at);
            Assert.AreEqual(Bits(x.BaselineTail), Bits(y.BaselineTail), at);
            Assert.AreEqual(Bits(x.ExpectedLow), Bits(y.ExpectedLow), at);
            Assert.AreEqual(Bits(x.ExpectedHigh), Bits(y.ExpectedHigh), at);
            CollectionAssert.AreEqual(x.ViterbiPath.ToArray(), y.ViterbiPath.ToArray(), at);
        }
    }

    private static long? Bits(double? value) => value is double v ? BitConverter.DoubleToInt64Bits(v) : null;

    private static void AssertBitIdentical(IReadOnlyList<double> expected, IReadOnlyList<double> actual, string name)
    {
        Assert.HasCount(expected.Count, actual, name);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected[i]), BitConverter.DoubleToInt64Bits(actual[i]), $"{name}[{i}]");
        }
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
