// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Helpers for the Home-mode server tests (README §3.1): a set of test certificates made once per
// test run with .NET's own APIs, a real Kestrel server on 127.0.0.1 with a port the system picks,
// clients that present a chosen certificate in the TLS handshake, and a log capture. All synthetic.

using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SecretModel.Api.Hosting;
using SecretModel.TestCerts;

namespace SecretModel.Tests.Hosting;

/// <summary>The test certificates. Made once; every test only reads them.</summary>
internal static class TestPki
{
    /// <summary>Protects the test .pfx files written by the tests. Not a secret.</summary>
    public const string Password = "synthetic-test-password";

    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    public static readonly X509Certificate2 Authority = TestCertificates.CreateAuthority(Now.AddDays(-10), Now.AddDays(10));
    public static readonly X509Certificate2 OtherAuthority = TestCertificates.CreateAuthority(Now.AddDays(-10), Now.AddDays(10), "CN=Another Test CA");
    public static readonly X509Certificate2 Server = TestCertificates.CreateServer(Authority, Now.AddDays(-1), Now.AddDays(5));
    public static readonly X509Certificate2 ClientA = Client("client-a");
    public static readonly X509Certificate2 ClientB = Client("client-b");

    /// <summary>Issued by the authority, but its thumbprint is not on the allowlist.</summary>
    public static readonly X509Certificate2 Unlisted = Client("client-c");

    // Each of these is on the allowlist, so that only the check under test refuses it.
    public static readonly X509Certificate2 FromOtherAuthority = TestCertificates.CreateClient(OtherAuthority, "client-a", Now.AddDays(-1), Now.AddDays(5));
    public static readonly X509Certificate2 SelfSigned = TestCertificates.CreateSelfSignedClient("client-a", Now.AddDays(-1), Now.AddDays(5));
    public static readonly X509Certificate2 Expired = TestCertificates.CreateClient(Authority, "client-a", Now.AddDays(-5), Now.AddDays(-1));
    public static readonly X509Certificate2 NotYetValid = TestCertificates.CreateClient(Authority, "client-a", Now.AddDays(1), Now.AddDays(5));

    /// <summary>A CN holding a published test card number, which breaks the client-id rule.</summary>
    public static readonly X509Certificate2 CardNumberName = Client("4111111111111111");

    public static IReadOnlyList<X509Certificate2> Allowed =>
        [ClientA, ClientB, FromOtherAuthority, SelfSigned, Expired, NotYetValid, CardNumberName];

    public static X509Certificate2 Named(string name) => name switch
    {
        "client-a" => ClientA,
        "client-b" => ClientB,
        "unlisted" => Unlisted,
        "other authority" => FromOtherAuthority,
        "self-signed" => SelfSigned,
        "expired" => Expired,
        "not yet valid" => NotYetValid,
        "card number name" => CardNumberName,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>The CN, as a test that the logs never hold it needs it.</summary>
    public static string CommonNameOf(X509Certificate2 certificate) => certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    private static X509Certificate2 Client(string commonName) =>
        TestCertificates.CreateClient(Authority, commonName, Now.AddDays(-1), Now.AddDays(5));
}

/// <summary>Every log entry the server writes, formatted, with its exception if any.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    public string All => string.Join("\n", entries);

    public IReadOnlyList<string> Entries => [.. entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A Home-mode server on 127.0.0.1 for one test, with its own folder for the certificate files.</summary>
internal sealed class RunningServer : IAsyncDisposable
{
    /// <summary>A test-only endpoint that answers with the client id the server derived.</summary>
    public const string ClientPath = "/test/client";

    private readonly WebApplication app;
    private readonly string folder;

    private RunningServer(WebApplication app, string folder, CapturedLogs logs)
    {
        this.app = app;
        this.folder = folder;
        Logs = logs;
        Url = new Uri(app.Urls.First());
    }

    public Uri Url { get; }

    public CapturedLogs Logs { get; }

    /// <summary>A new folder holding server.pfx and ca.cer, and settings that name them.</summary>
    public static (string Folder, Dictionary<string, string?> Settings) Prepare()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SecretModel.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "server.pfx"), TestPki.Server.Export(X509ContentType.Pkcs12, TestPki.Password));
        File.WriteAllBytes(Path.Combine(folder, "ca.cer"), TestPki.Authority.Export(X509ContentType.Cert));
        var settings = new Dictionary<string, string?>
        {
            ["SecretModel:Url"] = "https://127.0.0.1:0",
            ["SecretModel:ServerCertificate"] = Path.Combine(folder, "server.pfx"),
            ["SecretModel:ClientCertificateAuthority"] = Path.Combine(folder, "ca.cer"),
        };
        for (int i = 0; i < TestPki.Allowed.Count; i++)
        {
            settings[$"SecretModel:AllowedClientThumbprints:{i}"] = TestCertificates.Sha256Thumbprint(TestPki.Allowed[i]);
        }

        return (folder, settings);
    }

    public static IConfiguration Configuration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>A test-only endpoint that always throws, with a message that must never reach the client.</summary>
    public const string ThrowPath = "/test/throw";

    public const string ThrownMessage = "synthetic failure: details that stay on the server";

    public static async Task<RunningServer> StartAsync(Action<string, Dictionary<string, string?>>? change = null, string password = TestPki.Password)
    {
        (string folder, Dictionary<string, string?> settings) = Prepare();
        change?.Invoke(folder, settings);
        var logs = new CapturedLogs();
        WebApplication app = ServerHost.Create(Configuration(settings), password, logs: logs);
        app.MapGet(ClientPath, (HttpContext context) => context.Features.Get<ClientIdentity>()?.ClientId ?? "(none)");
        app.MapGet(ThrowPath, string () => throw new InvalidOperationException(ThrownMessage));
        await app.StartAsync();
        return new RunningServer(app, folder, logs);
    }

    /// <summary>The environment the server runs in.</summary>
    public string EnvironmentName => app.Environment.EnvironmentName;

    /// <summary>
    /// A client that trusts only the test server certificate, by its thumbprint, and presents
    /// <paramref name="certificate"/> in the handshake, or no certificate when it is null.
    /// </summary>
    public HttpClient Client(X509Certificate2? certificate, X509Certificate2? server = null)
    {
        string serverThumbprint = TestCertificates.Sha256Thumbprint(server ?? TestPki.Server);
        var tls = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHashString(HashAlgorithmName.SHA256) == serverThumbprint,
        };
        if (certificate is not null)
        {
            tls.ClientCertificates = [certificate];
            tls.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        return new HttpClient(new SocketsHttpHandler { SslOptions = tls }) { BaseAddress = Url };
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }
}
