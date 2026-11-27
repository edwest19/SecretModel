// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// End-to-end tests of the Home-mode server (README §3.1) over real TLS on 127.0.0.1: /health needs
// no certificate (SM-AUTH-001, SM-OPS-001); every other request needs a handshake certificate that
// passes the SM-AUTH-002 checks (SM-AUTH-008), else 401 (SM-AUTH-005) or 403; the client id comes
// from the certificate's CN only (SM-AUTH-003); X-ARR-ClientCert is never read (SM-AUTH-009); the run
// mode comes from settings, and only Home mode is built (SM-AUTH-010); and error answers repeat
// nothing the client sent (SM-PRIV-004). Certificates are made by tools/SecretModel.TestCerts.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using SecretModel.Api.Hosting;
using SecretModel.TestCerts;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ServerTests
{
    private const string CertificateRequired = "{\"error\":\"certificate_required\"}";
    private const string CertificateNotAccepted = "{\"error\":\"certificate_not_accepted\"}";
    private const string ForgedHeader = "X-ARR-ClientCert";

    [TestMethod]
    [TestCategory("SM-AUTH-001")]
    [TestCategory("SM-OPS-001")]
    [DataRow(null)]
    [DataRow("client-a")]
    [DataRow("unlisted")]
    [DataRow("card number name")]
    public async Task Health_AnswersWithAnyOrNoCertificate_AndSaysNothingElse(string? certificate)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(certificate is null ? null : TestPki.Named(certificate));

        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(response.Headers.Contains("Server"), "No Server header names the software.");
    }

    [TestMethod]
    [TestCategory("SM-AUTH-001")]
    [TestCategory("SM-AUTH-005")]
    [TestCategory("SM-PRIV-004")]
    [DataRow("GET", "/")]
    [DataRow("GET", RunningServer.ClientPath)]
    [DataRow("POST", "/v1/score")]
    [DataRow("PUT", "/v1/score")]
    [DataRow("DELETE", "/v1/models")]
    [DataRow("GET", "/health/more")]
    [DataRow("GET", "/v1/4111111111111111")]
    public async Task NoCertificate_Is401_WithoutRepeatingTheRequest(string method, string path)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(null);

        using HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual(CertificateRequired, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-001")]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-AUTH-008")]
    [DataRow("client-a")]
    [DataRow("client-b")]
    public async Task AcceptedCertificate_IsTheClientNamedByItsCommonName(string clientId)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.Named(clientId));

        using HttpResponseMessage response = await client.GetAsync(RunningServer.ClientPath);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(clientId, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    public async Task ClientId_NeverComesFromAHeaderOrTheQuery()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);
        var request = new HttpRequestMessage(HttpMethod.Get, RunningServer.ClientPath + "?client_id=client-b");
        request.Headers.Add("X-Client-Id", "client-b");
        request.Headers.Add("client_id", "client-b");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual("client-a", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-008")]
    [TestCategory("SM-PRIV-004")]
    [TestCategory("SM-PRIV-005")]
    [DataRow("unlisted", CertificateRejection.NotOnAllowlist)]
    [DataRow("other authority", CertificateRejection.NotIssuedByTheAuthority)]
    [DataRow("self-signed", CertificateRejection.NotIssuedByTheAuthority)]
    [DataRow("expired", CertificateRejection.OutsideValidity)]
    [DataRow("not yet valid", CertificateRejection.OutsideValidity)]
    [DataRow("card number name", CertificateRejection.NotAClientId)]
    public async Task RejectedCertificate_Is403_AndOnlyItsReasonAndThumbprintAreLogged(string name, CertificateRejection reason)
    {
        X509Certificate2 certificate = TestPki.Named(name);
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(certificate);

        using HttpResponseMessage response = await client.GetAsync(RunningServer.ClientPath);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual(CertificateNotAccepted, await response.Content.ReadAsStringAsync());
        string logs = server.Logs.All;
        Assert.Contains($"({reason})", logs);
        Assert.Contains(TestCertificates.Sha256Thumbprint(certificate), logs);
        Assert.DoesNotContain(TestPki.CommonNameOf(certificate), logs, "The subject is never logged.");
    }

    [TestMethod]
    [TestCategory("SM-AUTH-005")]
    [TestCategory("SM-AUTH-009")]
    public async Task ForgedHeaderAlone_IsAnsweredExactlyAsNoCertificate()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(null);
        var forged = new HttpRequestMessage(HttpMethod.Get, RunningServer.ClientPath);
        forged.Headers.Add(ForgedHeader, Convert.ToBase64String(TestPki.ClientA.RawData));

        using HttpResponseMessage withHeader = await client.SendAsync(forged);
        using HttpResponseMessage without = await client.GetAsync(RunningServer.ClientPath);

        Assert.AreEqual(HttpStatusCode.Unauthorized, withHeader.StatusCode);
        Assert.AreEqual(without.StatusCode, withHeader.StatusCode);
        Assert.AreEqual(without.Content.Headers.ContentType?.ToString(), withHeader.Content.Headers.ContentType?.ToString());
        Assert.AreEqual(await without.Content.ReadAsStringAsync(), await withHeader.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-009")]
    public async Task ForgedHeader_DoesNotHelpARejectedHandshakeCertificate()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.Unlisted);
        var forged = new HttpRequestMessage(HttpMethod.Get, RunningServer.ClientPath);
        forged.Headers.Add(ForgedHeader, Convert.ToBase64String(TestPki.ClientA.RawData));

        using HttpResponseMessage response = await client.SendAsync(forged);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(CertificateNotAccepted, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [TestCategory("SM-AUTH-009")]
    public async Task ForgedHeader_DoesNotChangeTheClient()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientB);
        var forged = new HttpRequestMessage(HttpMethod.Get, RunningServer.ClientPath);
        forged.Headers.Add(ForgedHeader, Convert.ToBase64String(TestPki.ClientA.RawData));

        using HttpResponseMessage response = await client.SendAsync(forged);

        Assert.AreEqual("client-b", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-010")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Home")]
    [DataRow("home")]
    [DataRow("Cloud")]
    [DataRow("Azure2")]
    public async Task AbsentOrUnrecognizedMode_StartsInHomeMode_WhichNeverReadsTheHeader(string? mode)
    {
        await using RunningServer server = await RunningServer.StartAsync((_, settings) =>
        {
            if (mode is not null)
            {
                settings["SecretModel:Mode"] = mode;
            }
        });
        using HttpClient noCertificate = server.Client(null);
        using HttpClient clientA = server.Client(TestPki.ClientA);
        var forged = new HttpRequestMessage(HttpMethod.Get, RunningServer.ClientPath);
        forged.Headers.Add(ForgedHeader, Convert.ToBase64String(TestPki.ClientA.RawData));

        using HttpResponseMessage rejected = await noCertificate.SendAsync(forged);
        using HttpResponseMessage accepted = await clientA.GetAsync(RunningServer.ClientPath);

        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.AreEqual("client-a", await accepted.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-010")]
    [DataRow("Azure")]
    [DataRow("azure")]
    [DataRow(" AZURE ")]
    public void AzureMode_RefusesToStart(string mode)
    {
        (string folder, Dictionary<string, string?> settings) = RunningServer.Prepare();
        try
        {
            settings["SecretModel:Mode"] = mode;

            ServerSettingsException e = Assert.ThrowsExactly<ServerSettingsException>(
                () => ServerHost.Create(RunningServer.Configuration(settings), TestPki.Password));

            Assert.AreEqual("Azure mode is not built yet. Set SecretModel:Mode to Home, or leave it out.", e.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("SM-PRIV-004")]
    public async Task UnknownPath_Is404_WithoutRepeatingIt()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(TestPki.ClientA);

        using HttpResponseMessage response = await client.GetAsync("/v1/4111111111111111");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("{\"error\":\"not_found\"}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-OPS-001")]
    public async Task WrongMethodOnHealth_Is405()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpClient client = server.Client(null);

        using HttpResponseMessage response = await client.PostAsync("/health", new StringContent("{}"));

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.AreEqual(ServerHost.JsonType, response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-008")]
    public async Task CertificatesFromTheTool_WorkWithTheServer()
    {
        string certificates = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            CertificateSet.Write(certificates, ["client-a", "client-b"], TestPki.Password, DateTimeOffset.UtcNow);
            using X509Certificate2 clientA = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(certificates, "client-a.pfx"), TestPki.Password);
            using X509Certificate2 clientB = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(certificates, "client-b.pfx"), TestPki.Password);
            using X509Certificate2 serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(certificates, "server.pfx"), TestPki.Password);
            await using RunningServer server = await RunningServer.StartAsync((_, settings) =>
            {
                settings["SecretModel:ServerCertificate"] = Path.Combine(certificates, "server.pfx");
                settings["SecretModel:ClientCertificateAuthority"] = Path.Combine(certificates, "ca.cer");
                foreach (string key in settings.Keys.Where(k => k.StartsWith("SecretModel:AllowedClientThumbprints:", StringComparison.Ordinal)).ToList())
                {
                    settings.Remove(key);
                }

                settings["SecretModel:AllowedClientThumbprints:0"] = TestCertificates.Sha256Thumbprint(clientA);
            });
            using HttpClient accepted = server.Client(clientA, serverCertificate);
            using HttpClient unlisted = server.Client(clientB, serverCertificate);

            using HttpResponseMessage a = await accepted.GetAsync(RunningServer.ClientPath);
            using HttpResponseMessage b = await unlisted.GetAsync(RunningServer.ClientPath);

            Assert.AreEqual("client-a", await a.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.Forbidden, b.StatusCode);
        }
        finally
        {
            Directory.Delete(certificates, recursive: true);
        }
    }
}
