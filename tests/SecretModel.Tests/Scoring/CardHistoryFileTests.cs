// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.
//
// Tests for the card history file, format "secretmodel-history" version 1
// (docs/formats/history-v1.md), and for CardHistory itself: README SM-STOR-008.
//
// The known answer is the example in docs/formats/history-v1.md. Its bytes, byte count (94) and
// SHA-256 were computed by Python from the documented values, not by the C# under test.

using System.Security.Cryptography;
using System.Text;
using SecretModel.Core.Scoring;

namespace SecretModel.Tests.Scoring;

[TestClass]
public sealed class CardHistoryFileTests
{
    internal const string DocumentedJson =
        "{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[0,1,5,999999999999],\"symbols\":[57,0,3,511]}";

    internal static CardHistory DocumentedHistory() => new([0, 1, 5, 999_999_999_999], [57, 0, 3, 511]);

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public void Write_MatchesTheDocumentedBytes()
    {
        byte[] bytes = CardHistoryFile.Write(DocumentedHistory());

        Assert.HasCount(94, bytes, "The document says 94 bytes.");
        Assert.AreEqual(
            "e06ef9afcf59c80c3967ec92fc4effa6ff4674845e318ae91c9adfcfe31c78fb",
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            "The document's SHA-256, computed by Python.");
        Assert.AreEqual(DocumentedJson, Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public void Read_TheDocumentedBytes_GivesTheDocumentedHistory()
    {
        CardHistory history = CardHistoryFile.Read(Encoding.UTF8.GetBytes(DocumentedJson));

        Assert.AreEqual(4, history.Count);
        CollectionAssert.AreEqual(new long[] { 0, 1, 5, 999_999_999_999 }, history.Seq.ToArray());
        CollectionAssert.AreEqual(new[] { 57, 0, 3, 511 }, history.Symbols.ToArray());
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(100)]
    [DataRow(5000)]
    public void WriteThenRead_GivesTheSameHistory_AndTheSameBytes(int count)
    {
        var random = new Random(20261126 + count);
        var seq = new long[count];
        var symbols = new int[count];
        long next = random.Next(0, 1000);
        for (int i = 0; i < count; i++)
        {
            seq[i] = next;
            symbols[i] = random.Next(0, 512);
            next += random.Next(1, 1000);
        }

        byte[] bytes = CardHistoryFile.Write(new CardHistory(seq, symbols));
        CardHistory read = CardHistoryFile.Read(bytes);

        CollectionAssert.AreEqual(seq, read.Seq.ToArray());
        CollectionAssert.AreEqual(symbols, read.Symbols.ToArray());
        CollectionAssert.AreEqual(bytes, CardHistoryFile.Write(read));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("﻿{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[2]}x")]
    [DataRow("[]")]
    [DataRow("\"secretmodel-history\"")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[2],\"x\":1}")]
    [DataRow("{\"version\":1,\"format\":\"secretmodel-history\",\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[2],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-profile\",\"version\":1,\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":5,\"version\":1,\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":2,\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1.0,\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":\"1\",\"seq\":[1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":1,\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":2}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[],\"symbols\":[]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1,2],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[2,3]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[2,1],\"symbols\":[2,3]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1,1],\"symbols\":[2,3]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[-1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[-0],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1.0],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1e1],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1000000000000],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[\"1\"],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[null],\"symbols\":[2]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[512]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[-1]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[1.5]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[\"2\"]}")]
    [DataRow("{\"format\":\"secretmodel-history\",\"version\":1,\"seq\":[1],\"symbols\":[3000000000]}")]
    public void Read_AFileThatBreaksTheFormat_IsRejected(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => CardHistoryFile.Read(Encoding.UTF8.GetBytes(json)));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    [DataRow(new long[0], new int[0])]
    [DataRow(new long[] { 1, 2 }, new[] { 0 })]
    [DataRow(new long[] { 1 }, new[] { 0, 0 })]
    [DataRow(new long[] { 2, 1 }, new[] { 0, 0 })]
    [DataRow(new long[] { 1, 1 }, new[] { 0, 0 })]
    [DataRow(new long[] { -1 }, new[] { 0 })]
    [DataRow(new long[] { 1_000_000_000_000 }, new[] { 0 })]
    [DataRow(new long[] { 1 }, new[] { -1 })]
    [DataRow(new long[] { 1 }, new[] { 512 })]
    public void ANewHistory_ThatBreaksARule_IsRefused(long[] seq, int[] symbols)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CardHistory(seq, symbols));
    }

    [TestMethod]
    [TestCategory("SM-STOR-008")]
    public void ANewHistory_KeepsItsOwnCopy()
    {
        long[] seq = [1, 2];
        int[] symbols = [3, 4];
        var history = new CardHistory(seq, symbols);

        seq[0] = 0;
        symbols[0] = 9;

        CollectionAssert.AreEqual(new long[] { 1, 2 }, history.Seq.ToArray());
        CollectionAssert.AreEqual(new[] { 3, 4 }, history.Symbols.ToArray());
    }
}
