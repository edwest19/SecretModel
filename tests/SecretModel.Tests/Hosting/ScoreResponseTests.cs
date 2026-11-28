// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Known-answer tests for the score response, version 1 (docs/formats/score-response-v1.md; README
// SM-SCORE-006, SM-SCORE-009). The expected texts are the document's examples, written by hand from
// the format's rules; their byte counts and SHA-256 hashes were computed by Python from the same
// texts, not by the C# under test, and the numbers' digits match Python's repr. All tokens are made up.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Api.Scoring;
using SecretModel.Core.Scoring;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ScoreResponseTests
{
    internal const string ExampleA =
        "{\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0001\",\"fraud_score\":1,\"verdict\":\"FRAUD_SUSPECTED\",\"confidence\":0.999," +
        "\"anomaly_factors\":[\"window_in_unusual_tail\",\"below_all_baseline_windows\",\"symbol_not_in_history\"]," +
        "\"window_log_likelihood\":-9.375,\"baseline_tail\":5E-05,\"expected_range\":[-6.5,-2.125],\"viterbi_path\":[3]}";

    internal const string ExampleB =
        "{\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0002\",\"fraud_score\":0,\"verdict\":\"LEGIT\",\"confidence\":0.3333333333333333," +
        "\"anomaly_factors\":[],\"window_log_likelihood\":-3.0625,\"baseline_tail\":0.6,\"expected_range\":[-5.5,-1.75],\"viterbi_path\":[0,2,2,1]}";

    internal const string ExampleC =
        "{\"transaction_token\":\"tok_txn_feedfacefeedfacefeed0003\",\"fraud_score\":0.5,\"verdict\":\"REVIEW\",\"confidence\":0," +
        "\"anomaly_factors\":[\"cold_start_no_history\"],\"window_log_likelihood\":null,\"baseline_tail\":null,\"expected_range\":null,\"viterbi_path\":[]}";

    [TestMethod]
    [TestCategory("SM-SCORE-006")]
    [TestCategory("SM-SCORE-009")]
    [DataRow("A", 320, "d05775c43ec828477766e3e884b206c49d86f17f957f15a67b467e09e43f4f93")]
    [DataRow("B", 250, "17e2472c749450527a9fd46baba7b30f015dae511f0658d63457e7cd10a7cac4")]
    [DataRow("C", 242, "0f0fecc3c5c858de4837dac0f4e33e18f8c30674e622a7fde6087f8327af60d3")]
    public void Write_TheDocumentedExamples_GiveTheDocumentedBytes(string example, int length, string sha256)
    {
        (string token, ScoreResult result, string expected) = example switch
        {
            "A" => ("tok_txn_feedfacefeedfacefeed0001", new ScoreResult(
                1, Verdict.FraudSuspected, 0.999, [FraudScorer.WindowInUnusualTail, FraudScorer.BelowAllBaseline, FraudScorer.SymbolNotInHistory],
                -9.375, 5e-05, -6.5, -2.125, [3]), ExampleA),
            "B" => ("tok_txn_feedfacefeedfacefeed0002", new ScoreResult(
                0, Verdict.Legit, 1.0 / 3, [], -3.0625, 0.6, -5.5, -1.75, [0, 2, 2, 1]), ExampleB),
            _ => ("tok_txn_feedfacefeedfacefeed0003", new ScoreResult(
                0.5, Verdict.Review, 0, [FraudScorer.ColdStart], null, null, null, null, []), ExampleC),
        };

        byte[] written = ScoreResponse.Write(token, result);

        // The expected text is the document's, checked against the document's count and hash first.
        Assert.AreEqual(length, Encoding.UTF8.GetByteCount(expected));
        Assert.AreEqual(sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))));
        Assert.AreEqual(expected, Encoding.UTF8.GetString(written));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), written);
    }

    [TestMethod]
    [TestCategory("SM-SCORE-006")]
    [DataRow(Verdict.Legit, "LEGIT")]
    [DataRow(Verdict.Review, "REVIEW")]
    [DataRow(Verdict.FraudSuspected, "FRAUD_SUSPECTED")]
    public void VerdictName_IsTheDocumentedName(Verdict verdict, string name) =>
        Assert.AreEqual(name, ScoreResponse.VerdictName(verdict));

    [TestMethod]
    [TestCategory("SM-SCORE-008")]
    [DataRow(double.NegativeInfinity)]
    [DataRow(double.NaN)]
    public void Write_ANumberThatIsNotFinite_IsRefused(double value) =>
        Assert.ThrowsExactly<ArgumentException>(() => ScoreResponse.Write(
            "tok_txn_feedfacefeedfacefeed0001", new ScoreResult(0.5, Verdict.Review, 0.5, [], value, 0.5, -3, -1, [0])));
}
