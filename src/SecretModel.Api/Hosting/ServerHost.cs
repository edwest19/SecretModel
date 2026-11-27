// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decisions 3, 4, 7 and 9). AI-generated.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace SecretModel.Api.Hosting;

/// <summary>
/// Builds the Home-mode server (README §3.1): Kestrel on HTTPS asks for a client certificate during
/// the TLS handshake and allows a connection without one (SM-AUTH-008), so <c>/health</c> needs no
/// certificate (SM-AUTH-001, SM-OPS-001). Every other request needs a handshake certificate that
/// passes <see cref="ClientCertificateValidator"/>: none is 401 (SM-AUTH-005), one that fails is 403.
/// <c>X-ARR-ClientCert</c> is never read (SM-AUTH-009). Error answers are small JSON bodies that
/// repeat nothing the client sent (SM-PRIV-004).
/// </summary>
public static class ServerHost
{
    public const string HealthPath = "/health";
    public const string JsonType = "application/json; charset=utf-8";

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
        builder.Configuration.AddConfiguration(settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

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

        WebApplication app = builder.Build();
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
                "A client certificate was not accepted ({Reason}); its SHA-256 thumbprint is {Thumbprint}.",
                check.Rejection, certificate.GetCertHashString(HashAlgorithmName.SHA256));
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "certificate_not_accepted");
            return;
        }

        context.Features.Set(new ClientIdentity(check.ClientId!));
        await next(context);
    }

    private static void Listen(KestrelServerOptions kestrel, Uri url, HttpsConnectionAdapterOptions https)
    {
        if (url.Host == "localhost")
        {
            kestrel.ListenLocalhost(url.Port, listen => listen.UseHttps(https));
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(url.Host.Trim('[', ']')), url.Port, listen => listen.UseHttps(https));
        }
    }
}
