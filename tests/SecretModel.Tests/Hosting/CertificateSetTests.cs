// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for tools/SecretModel.TestCerts (README §3.1): it writes a set of test certificates the
// server accepts, only into an empty folder outside any git repository (SM-OPS-002: certificate
// files never go into the repository), only with a password, and only for valid client ids, which
// it never repeats when one is wrong (SM-PRIV-004). Each test uses its own new folder under the
// system temp folder and deletes it afterwards.

using System.Security.Cryptography.X509Certificates;
using SecretModel.Api.Hosting;
using SecretModel.TestCerts;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class CertificateSetTests
{
    private string root = "";

    [TestInitialize]
    public void CreateRoot()
    {
        root = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void DeleteRoot() => Directory.Delete(root, recursive: true);

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-003")]
    public void Write_MakesAnAuthorityAServerAndClientsTheServerAccepts()
    {
        string folder = Path.Combine(root, "certs");
        DateTimeOffset now = TestPki.Now;

        IReadOnlyList<string> lines = CertificateSet.Write(folder, ["client-a", "client-b"], TestPki.Password, now);

        CollectionAssert.AreEquivalent(
            new[] { "ca.cer", "server.pfx", "client-a.pfx", "client-a.cer", "client-b.pfx", "client-b.cer" },
            Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        using X509Certificate2 authority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(folder, "ca.cer"));
        using X509Certificate2 server = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(folder, "server.pfx"), TestPki.Password);
        Assert.AreEqual(TestCertificates.AuthorityName, authority.Subject);
        Assert.IsTrue(server.HasPrivateKey);
        Assert.AreEqual("CN=localhost", server.Subject);
        Assert.Contains($"SHA-256 {TestCertificates.Sha256Thumbprint(server)}", lines[1]);
        foreach (string client in new[] { "client-a", "client-b" })
        {
            using X509Certificate2 withKey = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(folder, client + ".pfx"), TestPki.Password);
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(folder, client + ".cer"));
            string thumbprint = TestCertificates.Sha256Thumbprint(withKey);
            var validator = new ClientCertificateValidator(authority, new HashSet<string> { thumbprint }, new FixedTime(now));

            Assert.IsTrue(withKey.HasPrivateKey);
            Assert.AreEqual(thumbprint, TestCertificates.Sha256Thumbprint(publicOnly));
            Assert.AreEqual(client, validator.Check(withKey).ClientId);
            Assert.IsLessThan(1.0, Math.Abs((now.AddMinutes(-5).UtcDateTime - withKey.NotBefore.ToUniversalTime()).TotalSeconds));
            Assert.IsLessThan(1.0, Math.Abs((now.AddYears(1).UtcDateTime - withKey.NotAfter.ToUniversalTime()).TotalSeconds));
            Assert.IsTrue(lines.Any(l => l.StartsWith(client + ".pfx", StringComparison.Ordinal) && l.EndsWith("SHA-256 " + thumbprint, StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void Problem_IsNoneForANewOrEmptyFolderOutsideARepository()
    {
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        Assert.IsNull(CertificateSet.Problem(Path.Combine(root, "new"), ["client-a"], TestPki.Password));
        Assert.IsNull(CertificateSet.Problem(Path.Combine(root, "empty"), ["client-a"], TestPki.Password));
    }

    [TestMethod]
    [TestCategory("SM-OPS-002")]
    [DataRow(null)]
    [DataRow("")]
    public void Problem_NeedsAPassword(string? password) =>
        Assert.AreEqual(
            "Set SECRETMODEL_CERT_PASSWORD first: it protects the .pfx files.", CertificateSet.Problem(Path.Combine(root, "new"), ["client-a"], password));

    [TestMethod]
    [TestCategory("SM-PRIV-004")]
    [DataRow("4111111111111111")]
    [DataRow("Client A")]
    [DataRow("../client-a")]
    [DataRow("")]
    public void Problem_RefusesABadClientId_WithoutRepeatingIt(string client)
    {
        string? problem = CertificateSet.Problem(Path.Combine(root, "new"), ["client-a", client], TestPki.Password);

        Assert.AreEqual("A client id given breaks the client-id rule (README §8.2).", problem);
    }

    [TestMethod]
    [TestCategory("SM-OPS-002")]
    [DataRow("folder")]
    [DataRow("file")]
    public void Problem_RefusesAFolderInsideAGitRepository(string gitEntry)
    {
        string repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        if (gitEntry == "folder")
        {
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
        }
        else
        {
            File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: elsewhere");
        }

        Assert.AreEqual(
            "Choose a folder outside any git repository: certificate files must never be committed.",
            CertificateSet.Problem(Path.Combine(repository, "certs", "test"), ["client-a"], TestPki.Password));
        Assert.AreEqual(
            "Choose a folder outside any git repository: certificate files must never be committed.",
            CertificateSet.Problem(repository, ["client-a"], TestPki.Password));
    }

    [TestMethod]
    public void Problem_RefusesAFolderThatIsNotEmpty()
    {
        string folder = Path.Combine(root, "used");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));

        Assert.AreEqual("Choose an empty or new folder: nothing is overwritten.", CertificateSet.Problem(folder, ["client-a"], TestPki.Password));
    }
}
