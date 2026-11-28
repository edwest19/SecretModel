// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decisions 1 to 4, 7 and 9, and the Task 4 answers). AI-generated.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Net.Http.Headers;
using SecretModel.Api.Scoring;
using SecretModel.Core.Features;
using SecretModel.Core.Journal;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

namespace SecretModel.Api.Hosting;

/// <summary>
/// Builds the Home-mode server (README §3.1): Kestrel on HTTPS asks for a client certificate during
/// the TLS handshake and allows a connection without one (SM-AUTH-008), so <c>/health</c> needs no
/// certificate (SM-AUTH-001, SM-OPS-001). Every other request needs a handshake certificate that
/// passes <see cref="ClientCertificateValidator"/>: none is 401 (SM-AUTH-005), one that fails is 403.
/// <c>X-ARR-ClientCert</c> is never read (SM-AUTH-009). <c>POST /v1/score</c> scores one transaction
/// against the client's own profile for the card (README §3.2). Error answers are small JSON bodies
/// that repeat nothing the client sent (SM-PRIV-004), and every log entry passes through
/// <see cref="GuardedLoggerProvider"/> (SM-PRIV-005).
/// </summary>
public static class ServerHost
{
    public const string HealthPath = "/health";
    public const string ScorePath = "/v1/score";
    public const string JsonType = "application/json; charset=utf-8";

    /// <summary>The largest request body read, in bytes (README §3.2).</summary>
    public const int MaxBodyBytes = 4096;

    /// <summary>The thumbprint characters logged for a rejected certificate: too few to hold 13 digits in a row.</summary>
    public const int LoggedThumbprintCharacters = 12;

    // The defaults the profiles were trained with (README §7). Thresholds have no setting yet.
    private static readonly ScoringOptions Options = new();

    /// <param name="settings">Everything the server reads; <see cref="ServerSettings.Load"/> checks it first.</param>
    /// <param name="certificatePassword">The server certificate's password, from <see cref="ServerSettings.PasswordVariable"/>.</param>
    /// <param name="time">The clock for certificate validity; the system clock if null.</param>
    /// <param name="logs">Another place to send logs to, for tests.</param>
    public static WebApplication Create(IConfiguration settings, string? certificatePassword, TimeProvider? time = null, ILoggerProvider? logs = null)
    {
        ServerSettings loaded = ServerSettings.Load(settings, certificatePassword);

        // Always Production: the template's launch profile sets Development, which would add a
        // page that shows exception details to the client.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        // Every place logs go is behind the guard, and the server decides the levels: the factories
        // behind it pass everything they are given.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(_ => new GuardedLoggerProvider(LoggerFactory.Create(console =>
            console.SetMinimumLevel(LogLevel.Trace).AddSimpleConsole(o => o.SingleLine = true))));
        if (logs is not null)
        {
            builder.Services.AddSingleton<ILoggerProvider>(_ => new GuardedLoggerProvider(LoggerFactory.Create(copy =>
                copy.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))));
        }

        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var https = new HttpsConnectionAdapterOptions
        {
            ServerCertificate = loaded.ServerCertificate,
            ClientCertificateMode = ClientCertificateMode.AllowCertificate,
            // Every certificate the handshake brings is passed on and checked below, so that a
            // rejected one gets a 403 instead of a dropped connection (measured, README §3.1).
            ClientCertificateValidation = (_, _, _) => true,
            CheckCertificateRevocation = false,
        };
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            Listen(kestrel, loaded.Url, https);
        });
        builder.Services.AddSingleton(new ClientCertificateValidator(loaded.Authority, loaded.AllowedThumbprints, time ?? TimeProvider.System));
        builder.Services.AddSingleton<IModelStore>(new FileModelStore(loaded.ModelFolder));

        WebApplication app = builder.Build();

        // An exception anywhere: 500 with no detail. The handler's log entry passes through the
        // guard, which keeps only the exception's type.
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context => WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal"),
        });
        app.UseStatusCodePages(async context =>
        {
            string? error = context.HttpContext.Response.StatusCode switch
            {
                StatusCodes.Status404NotFound => "not_found",
                StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
                _ => null,
            };
            if (error is not null)
            {
                context.HttpContext.Response.ContentType = JsonType;
                await context.HttpContext.Response.WriteAsync($"{{\"error\":\"{error}\"}}");
            }
        });
        app.Use(RequireClientCertificateAsync);
        app.MapGet(HealthPath, () => Results.Text("{\"status\":\"ok\"}", JsonType));
        app.MapPost(ScorePath, ScoreAsync);
        return app;
    }

    /// <summary>Writes an error answer: <c>{"error":"…"}</c>.</summary>
    public static Task WriteErrorAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonType;
        return context.Response.WriteAsync($"{{\"error\":\"{error}\"}}");
    }

    private static async Task RequireClientCertificateAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == HealthPath)
        {
            await next(context);
            return;
        }

        // Only the certificate from the TLS handshake counts. X-ARR-ClientCert is never read.
        X509Certificate2? certificate = context.Connection.ClientCertificate;
        if (certificate is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "certificate_required");
            return;
        }

        CertificateCheck check = context.RequestServices.GetRequiredService<ClientCertificateValidator>().Check(certificate);
        if (!check.Accepted)
        {
            // The thumbprint helps whoever runs the server add a certificate; the subject is not
            // logged, because a CN that breaks the rules may hold a card number.
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ServerHost).FullName!).LogWarning(
                "A client certificate was not accepted ({Reason}); its SHA-256 thumbprint starts {Thumbprint}.",
                check.Rejection, certificate.GetCertHashString(HashAlgorithmName.SHA256)[..LoggedThumbprintCharacters]);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "certificate_not_accepted");
            return;
        }

        context.Features.Set(new ClientIdentity(check.ClientId!));
        await next(context);
    }

    /// <summary>
    /// <c>POST /v1/score</c> (README §3.2): the body is one transaction without <c>seq</c>, read by
    /// the journal's rules (<see cref="JournalFile.ReadTransaction"/>). The profile is the one this
    /// client trained for the card; the client comes from the certificate only (SM-AUTH-003,
    /// SM-AUTH-006). Read-only (SM-SCORE-015).
    /// </summary>
    private static async Task ScoreAsync(HttpContext context)
    {
        if (!IsJson(context.Request.ContentType))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return;
        }

        byte[]? body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
            return;
        }

        TransactionReadResult read = JournalFile.ReadTransaction(body);
        if (read.Transaction is not { } transaction)
        {
            string reason = read.HasLongDigitRun ? "PossibleCardNumber" : read.Reason!.Value.ToString();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = JsonType;
            await context.Response.WriteAsync($"{{\"error\":\"invalid_request\",\"reason\":\"{reason}\"}}");
            return;
        }

        string clientId = context.Features.GetRequiredFeature<ClientIdentity>().ClientId;
        var key = new ProfileKey(clientId, transaction.CustomerToken, transaction.CardToken);
        StoredProfile? stored = await context.RequestServices.GetRequiredService<IModelStore>().GetAsync(key, context.RequestAborted);
        int symbol = Quantizer.ToSymbol(transaction.AmountBin, transaction.MerchantCategory, transaction.Hour, transaction.IsOnline);
        byte[] answer = ScoreResponse.Write(transaction.TransactionToken, FraudScorer.Score(stored?.Profile, symbol, Options));
        context.Response.ContentType = JsonType;
        await context.Response.Body.WriteAsync(answer, context.RequestAborted);
    }

    // application/json, with no parameter but an optional charset=utf-8.
    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? media)
        && media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && media.Parameters.All(p => p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
            && p.Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    // The body, or null when it is longer than MaxBodyBytes. Never more than one byte past the limit is read.
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        byte[] buffer = new byte[MaxBodyBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > MaxBodyBytes ? null : buffer[..length];
    }

    private static void Listen(KestrelServerOptions kestrel, Uri url, HttpsConnectionAdapterOptions https)
    {
        if (url.Host == "localhost")
        {
            kestrel.ListenLocalhost(url.Port, listen => listen.UseHttps(https));
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(url.Host), url.Port, listen => listen.UseHttps(https));
        }
    }
}
