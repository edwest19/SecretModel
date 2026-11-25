// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Tests for README SM-PRIV-006. The first test scans the real server source; the others check the
// scanner itself, so a scanner that silently found nothing would fail here.

namespace SecretModel.Tests.Privacy;

[TestClass]
public sealed class ForbiddenIdentifierTests
{
    [TestMethod]
    [TestCategory("SM-PRIV-006")]
    public void ServerSource_HasNoForbiddenIdentifiers()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        string[] files = [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)];
        Assert.IsGreaterThanOrEqualTo(5, files.Length, $"Expected the server source files under {src}.");

        var hits = new List<string>();
        foreach (string file in files)
        {
            foreach (ForbiddenIdentifierScanner.Hit hit in ForbiddenIdentifierScanner.Scan(File.ReadAllText(file)))
            {
                hits.Add($"{Path.GetRelativePath(src, file)}:{hit.Line} {hit.Identifier} ({hit.Reason})");
            }
        }

        Assert.IsEmpty(hits, string.Join(Environment.NewLine, hits));
    }

    [TestMethod]
    [TestCategory("SM-PRIV-006")]
    [DataRow("int pan = 0;", "pan")]
    [DataRow("string PanToken;", "PanToken")]
    [DataRow("var rawPAN = x;", "rawPAN")]
    [DataRow("int _pan;", "_pan")]
    [DataRow("int @pan;", "pan")]
    [DataRow("string CardNumber;", "CardNumber")]
    [DataRow("string card_number;", "card_number")]
    [DataRow("string cardnumber;", "cardnumber")]
    [DataRow("string CardHolderName;", "CardHolderName")]
    [DataRow("string cardholder;", "cardholder")]
    [DataRow("int cvv2;", "cvv2")]
    [DataRow("int CVVCode;", "CVVCode")]
    [DataRow("DateTime Expiry;", "Expiry")]
    [DataRow("char q = '\"'; int pan;", "pan")]           // a quote in a char literal must not open a string
    [DataRow("string s = @\"a\"\"b\"; int pan;", "pan")]  // verbatim string with an escaped quote
    [DataRow("string s = \"a\\\"b\"; int pan;", "pan")]   // regular string with an escaped quote
    [DataRow("string s = $\"{(a ? \"}\" : \"x\")}\"; int pan;", "pan")] // nested string inside a hole
    [DataRow("/* x */ int pan;", "pan")]
    public void ForbiddenIdentifiers_AreFound(string code, string expectedIdentifier)
    {
        IReadOnlyList<ForbiddenIdentifierScanner.Hit> hits = ForbiddenIdentifierScanner.Scan(code);
        Assert.HasCount(1, hits, $"Expected one hit in: {code}");
        Assert.AreEqual(expectedIdentifier, hits[0].Identifier);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-006")]
    [DataRow("Span<byte> buffer; int expand; Company company; Pane pane; panel x;")]
    [DataRow("// the cardholder's pan and cvv")]
    [DataRow("/* pan\n cvv */")]
    [DataRow("/// <summary>Never stores a pan.</summary>")]
    [DataRow("string s = \"pan cvv expiry\";")]
    [DataRow("string s = @\"card number\";")]
    [DataRow("string s = $\"value {x} pan\";")]
    [DataRow("char c = 'p';")]
    [DataRow("#region pan helpers")]
    [DataRow("int x = 0x3A3;")]
    [DataRow("string CardToken; int Number; string Holder;")]
    public void OtherWords_CommentsAndStrings_AreNotHits(string code)
    {
        Assert.IsEmpty(ForbiddenIdentifierScanner.Scan(code), $"Unexpected hit in: {code}");
    }

    [TestMethod]
    [TestCategory("SM-PRIV-006")]
    public void Hits_ReportTheirLineNumber()
    {
        IReadOnlyList<ForbiddenIdentifierScanner.Hit> hits =
            ForbiddenIdentifierScanner.Scan("// one\nstring s = @\"two\nnot code\";\nint pan;"); // a verbatim string may span lines
        Assert.HasCount(1, hits);
        Assert.AreEqual(4, hits[0].Line);
    }

    [TestMethod]
    [TestCategory("SM-PRIV-006")]
    public void UnsupportedRawStringLiteral_Throws()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => ForbiddenIdentifierScanner.Scan("string s = \"\"\"raw\"\"\";"));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SecretModel.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("SecretModel.slnx was not found above the test output folder.");
    }
}
