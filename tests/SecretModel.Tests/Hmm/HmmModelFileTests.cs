// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for the model file (docs/formats/hmm-model-v1.md): README SM-HMM-005 and SM-HMM-011.

using System.Text;
using SecretModel.Core.Hmm;

namespace SecretModel.Tests.Hmm;

[TestClass]
public sealed class HmmModelFileTests
{
    // The known answer from docs/formats/hmm-model-v1.md.
    private const string DocumentedExample =
        "{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}";

    private static HiddenMarkovModel DocumentedModel() => new(
        [1, 0],
        new double[,] { { 0.75, 0.25 }, { 0, 1 } },
        new double[,] { { 0.5, 0.25, 0.25 }, { 0.1, 0.2, 0.7 } });

    [TestMethod]
    [TestCategory("SM-HMM-011")]
    public void Write_MatchesTheDocumentedBytes()
    {
        byte[] bytes = HmmModelFile.Write(DocumentedModel());

        Assert.HasCount(134, bytes, "The document says 134 bytes.");
        Assert.AreEqual(DocumentedExample, Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    [TestCategory("SM-HMM-011")]
    public void Read_TheDocumentedBytes_GivesTheDocumentedModel()
    {
        HiddenMarkovModel model = HmmModelFile.Read(Encoding.UTF8.GetBytes(DocumentedExample));

        HiddenMarkovModelTests.AssertBitIdentical(DocumentedModel(), model);
    }

    [TestMethod]
    [TestCategory("SM-HMM-005")]
    public void RoundTrip_IsBitExact_ForAwkwardDoubles()
    {
        double third = 1.0 / 3;
        var model = new HiddenMarkovModel(
            [double.Epsilon, 0.25, 0.75],                           // smallest subnormal
            new double[,]
            {
                { third, third, third },
                { 0.1, 0.2, 0.7 },
                { 2.2250738585072014E-308, 0.9999999999999999, 1.0 - 0.9999999999999999 }, // smallest normal
            },
            new double[,] { { 0.123456789012345678, 1 - 0.123456789012345678 }, { 0.5, 0.5 }, { 1e-300, 1 } });

        HiddenMarkovModel back = HmmModelFile.Read(HmmModelFile.Write(model));

        HiddenMarkovModelTests.AssertBitIdentical(model, back);
    }

    [TestMethod]
    [TestCategory("SM-HMM-005")]
    public void RoundTrip_IsBitExact_ForATrained5x512Model()
    {
        int[][] data = HiddenMarkovModelTests.Sample(HiddenMarkovModelTests.Teacher(), sequences: 5, length: 50, seed: 8);
        HiddenMarkovModel trained = HiddenMarkovModel.CreateRandom(4).Train(data, maxIterations: 10, tolerance: 0).Model;

        byte[] first = HmmModelFile.Write(trained);
        HiddenMarkovModel back = HmmModelFile.Read(first);

        HiddenMarkovModelTests.AssertBitIdentical(trained, back);
        CollectionAssert.AreEqual(first, HmmModelFile.Write(back), "Writing the read model must give the same bytes.");
    }

    [TestMethod]
    [TestCategory("SM-HMM-011")]
    [DataRow("not json", "The model file is not valid JSON.")]
    [DataRow("[1,2]", "The model file must hold a JSON object.")]
    [DataRow("{\"format\":\"other\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "The format must be \"secretmodel-hmm\".")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":2,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "Only version 1 is supported.")]
    [DataRow("{\"version\":1,\"format\":\"secretmodel-hmm\",\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "must have exactly these properties, in this order")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]],\"card\":1}", "must have exactly these properties, in this order")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]]}", "must have exactly these properties, in this order")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":0,\"symbols\":3,\"pi\":[],\"a\":[],\"b\":[]}", "\"states\" must be a whole number of at least 1.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2.5,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "\"states\" must be a whole number of at least 1.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "\"pi\" must be an array of 2 numbers.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2]]}", "\"b[1]\" must be an array of 3 numbers.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,\"0\"],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "\"pi\" item 1 must be a finite number.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,1e999],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "\"pi\" item 1 must be a finite number.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1.5,-0.5],\"a\":[[0.75,0.25],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "pi row 0 has a value that is negative or not finite.")]
    [DataRow("{\"format\":\"secretmodel-hmm\",\"version\":1,\"states\":2,\"symbols\":3,\"pi\":[1,0],\"a\":[[0.75,0.15],[0,1]],\"b\":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}", "a row 0 sums to 0.9, not 1.")]
    public void Read_RejectsFilesThatDontFollowTheFormat(string json, string messagePart)
    {
        InvalidDataException e = Assert.ThrowsExactly<InvalidDataException>(() => HmmModelFile.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(messagePart, e.Message);
    }
}
