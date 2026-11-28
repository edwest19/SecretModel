// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the Home-mode server's settings (README §3.1): the run mode comes from settings only
// (SM-AUTH-010); the address, the certificate files and the allowlist are checked before the server
// starts; and no message shows the certificate password (SM-OPS-002). Each test uses its own new
// folder under the system temp folder and deletes it afterwards.

using System.Security.Cryptography.X509Certificates;
using SecretModel.Api.Hosting;
using SecretModel.TestCerts;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ServerSettingsTests
{
    private string folder = "";
    private Dictionary<string, string?> settings = [];

    [TestInitialize]
    public void Prepare() => (folder, settings) = RunningServer.Prepare();

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(folder, recursive: true);

    [TestMethod]
    [TestCategory("SM-AUTH-010")]
    [DataRow(null, RunMode.Home)]
    [DataRow("", RunMode.Home)]
    [DataRow("Home", RunMode.Home)]
    [DataRow("HOME", RunMode.Home)]
    [DataRow("Cloud", RunMode.Home)]
    [DataRow("Azure2", RunMode.Home)]
    [DataRow("Azure", RunMode.Azure)]
    [DataRow("aZuRe", RunMode.Azure)]
    [DataRow(" Azure\t", RunMode.Azure)]
    public void ParseMode_IsAzureOnlyForAzure(string? value, RunMode expected) =>
        Assert.AreEqual(expected, ServerSettings.ParseMode(value));

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Load_ReadsEverySetting()
    {
        ServerSettings loaded = Load();

        Assert.AreEqual(RunMode.Home, loaded.Mode);
        Assert.AreEqual(new Uri("https://127.0.0.1:0"), loaded.Url);
        Assert.IsTrue(loaded.ServerCertificate.HasPrivateKey);
        Assert.AreEqual(TestCertificates.Sha256Thumbprint(TestPki.Server), TestCertificates.Sha256Thumbprint(loaded.ServerCertificate));
        Assert.IsFalse(loaded.Authority.HasPrivateKey);
        Assert.AreEqual(TestCertificates.Sha256Thumbprint(TestPki.Authority), TestCertificates.Sha256Thumbprint(loaded.Authority));
        CollectionAssert.AreEquivalent(TestPki.Allowed.Select(TestCertificates.Sha256Thumbprint).ToList(), loaded.AllowedThumbprints.ToList());
        Assert.AreEqual(Path.Combine(folder, "models"), loaded.ModelFolder);
    }

    [TestMethod]
    [DataRow("https://localhost:7443")]
    [DataRow("https://127.0.0.1:7443")]
    [DataRow("https://0.0.0.0:7443")]
    [DataRow("https://192.168.1.20:7443")]
    [DataRow("https://[::1]:7443")]
    public void Load_AcceptsLocalhostOrAnIpAddress(string url)
    {
        settings["SecretModel:Url"] = url;

        Assert.AreEqual(new Uri(url), Load().Url);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("http://localhost:7443")]
    [DataRow("localhost:7443")]
    [DataRow("https://localhost:7443/v1")]
    [DataRow("https://localhost:7443/?mode=Azure")]
    [DataRow("https://user@localhost:7443")]
    [DataRow("https://example.com:7443")]
    [DataRow("not an address")]
    public void Load_RefusesAnyOtherAddress(string? url)
    {
        settings["SecretModel:Url"] = url;

        StringAssert.StartsWith(Refused().Message, "SecretModel:Url must");
    }

    [TestMethod]
    [DataRow("ServerCertificate", null)]
    [DataRow("ServerCertificate", "")]
    [DataRow("ClientCertificateAuthority", null)]
    [DataRow("ClientCertificateAuthority", "")]
    [DataRow("ModelFolder", null)]
    [DataRow("ModelFolder", "")]
    public void Load_RefusesAMissingFileSetting(string key, string? value)
    {
        settings["SecretModel:" + key] = value;

        Assert.AreEqual(
            $"SecretModel:{key} is not set. Put it in the settings file named by SECRETMODEL_SETTINGS (README §3.1).", Refused().Message);
    }

    [TestMethod]
    [DataRow("ServerCertificate")]
    [DataRow("ClientCertificateAuthority")]
    public void Load_RefusesACertificateFileThatIsNotThere(string key)
    {
        string missing = Path.Combine(folder, "missing.file");
        settings["SecretModel:" + key] = missing;

        string message = Refused().Message;

        Assert.Contains($"SecretModel:{key} was not found", message);
        StringAssert.EndsWith(message, missing);
    }

    [TestMethod]
    public void Load_RefusesAModelFolderThatIsNotThere()
    {
        string missing = Path.Combine(folder, "no-such-folder");
        settings["SecretModel:ModelFolder"] = missing;

        Assert.AreEqual($"The model folder SecretModel:ModelFolder was not found: {missing}", Refused().Message);
        Assert.IsFalse(Directory.Exists(missing), "The server never creates it.");
    }

    [TestMethod]
    public void Load_GivesTheModelFolderAsAFullPath()
    {
        settings["SecretModel:ModelFolder"] = ".";

        Assert.AreEqual(Path.GetFullPath("."), Load().ModelFolder);
    }

    [TestMethod]
    [TestCategory("SM-OPS-002")]
    [DataRow("a-wrong-password")]
    [DataRow("")]
    [DataRow(null)]
    public void Load_RefusesTheWrongPassword_WithoutShowingIt(string? password)
    {
        string message = Refused(password).Message;

        Assert.AreEqual(
            "The server certificate SecretModel:ServerCertificate could not be read with the password in SECRETMODEL_CERT_PASSWORD.", message);
    }

    [TestMethod]
    public void Load_RefusesAServerCertificateWithoutItsKey()
    {
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(TestPki.Server.RawData);
        File.WriteAllBytes(Path.Combine(folder, "server.pfx"), publicOnly.Export(X509ContentType.Pkcs12, TestPki.Password));

        Assert.AreEqual("The server certificate SecretModel:ServerCertificate holds no private key.", Refused().Message);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("pfx")]
    public void Load_RefusesAnAuthorityFileThatIsNotACertificate(string content)
    {
        byte[] bytes = content == "pfx"
            ? TestPki.Authority.Export(X509ContentType.Pkcs12, TestPki.Password)
            : "not a certificate"u8.ToArray();
        File.WriteAllBytes(Path.Combine(folder, "ca.cer"), bytes);

        Assert.AreEqual("The client certificate authority SecretModel:ClientCertificateAuthority is not a certificate.", Refused().Message);
    }

    [TestMethod]
    public void Load_RefusesAnEmptyAllowlist()
    {
        RemoveAllowlist();

        Assert.AreEqual("SecretModel:AllowedClientThumbprints is empty, so no client could connect.", Refused().Message);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [DataRow("")]
    [DataRow("0123456789abcdef0123456789abcdef0123456")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG")]
    [DataRow("01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB")]
    public void Load_RefusesAnAllowlistEntryThatIsNotASha256Thumbprint(string entry)
    {
        settings["SecretModel:AllowedClientThumbprints:99"] = entry;

        Assert.AreEqual("Each SecretModel:AllowedClientThumbprints entry must be a SHA-256 thumbprint: 64 hex characters.", Refused().Message);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Load_AcceptsAThumbprintInLowercaseWithSpaces_AndTheServerMatchesIt()
    {
        RemoveAllowlist();
        settings["SecretModel:AllowedClientThumbprints:0"] = "  " + TestCertificates.Sha256Thumbprint(TestPki.ClientA).ToLowerInvariant() + " ";

        ServerSettings loaded = Load();
        var validator = new ClientCertificateValidator(loaded.Authority, loaded.AllowedThumbprints, new FixedTime(TestPki.Now));

        Assert.AreEqual("client-a", validator.Check(TestPki.ClientA).ClientId);
        Assert.AreEqual(CertificateRejection.NotOnAllowlist, validator.Check(TestPki.ClientB).Rejection);
    }

    private ServerSettings Load(string? password = TestPki.Password) =>
        ServerSettings.Load(RunningServer.Configuration(settings), password);

    private ServerSettingsException Refused(string? password = TestPki.Password) =>
        Assert.ThrowsExactly<ServerSettingsException>(() => Load(password));

    private void RemoveAllowlist()
    {
        foreach (string key in settings.Keys.Where(k => k.StartsWith("SecretModel:AllowedClientThumbprints:", StringComparison.Ordinal)).ToList())
        {
            settings.Remove(key);
        }
    }
}
