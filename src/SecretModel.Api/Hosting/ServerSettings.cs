// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decisions 4, 5, 6 and 8; ModelFolder from the Task 4 answers). AI-generated.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SecretModel.Api.Hosting;

/// <summary>The server's run mode (README §1). It comes from settings only, never from a request (SM-AUTH-010).</summary>
public enum RunMode
{
    Home,
    Azure,
}

/// <summary>A setting is missing or wrong; the server does not start. The message names no secret.</summary>
public sealed class ServerSettingsException(string message) : Exception(message);

/// <summary>
/// The Home-mode server's settings (README §3.1), from the <c>SecretModel</c> section: the committed
/// <c>appsettings.json</c> holds no secrets, and the real values come from a file outside the
/// repository. The server certificate's password comes from an environment variable and is passed
/// in, never read from a file. Every check happens before the server starts.
/// </summary>
public sealed record ServerSettings(
    RunMode Mode,
    Uri Url,
    X509Certificate2 ServerCertificate,
    X509Certificate2 Authority,
    IReadOnlySet<string> AllowedThumbprints,
    string ModelFolder,
    string JournalFolder)
{
    public const string Section = "SecretModel";
    public const string PasswordVariable = "SECRETMODEL_CERT_PASSWORD";
    public const string SettingsFileVariable = "SECRETMODEL_SETTINGS";

    /// <summary>
    /// <c>Azure</c> (any case) is Azure mode; anything else, or nothing, is Home mode, which never
    /// trusts the <c>X-ARR-ClientCert</c> header (SM-AUTH-010).
    /// </summary>
    public static RunMode ParseMode(string? value) =>
        string.Equals(value?.Trim(), "Azure", StringComparison.OrdinalIgnoreCase) ? RunMode.Azure : RunMode.Home;

    public static ServerSettings Load(IConfiguration configuration, string? certificatePassword)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(Section);

        RunMode mode = ParseMode(section["Mode"]);
        if (mode == RunMode.Azure)
        {
            throw new ServerSettingsException("Azure mode is not built yet. Set SecretModel:Mode to Home, or leave it out.");
        }

        Uri url = ParseUrl(section["Url"]);
        X509Certificate2 server = LoadServerCertificate(Required(section, "ServerCertificate"), certificatePassword);
        X509Certificate2 authority = LoadAuthority(Required(section, "ClientCertificateAuthority"));
        IReadOnlySet<string> thumbprints = LoadThumbprints(section.GetSection("AllowedClientThumbprints"));

        // Neither folder is ever created here: a mistyped path would make every card cold-start, or
        // leave every journal file waiting, without a word.
        string modelFolder = ExistingFolder(section, "ModelFolder", "model folder");
        string journalFolder = ExistingFolder(section, "JournalFolder", "journal folder");
        if (Inside(modelFolder, journalFolder) || Inside(journalFolder, modelFolder))
        {
            // The model root holds a folder per client hash, which can look like a client folder, with
            // the audit log in it (README §3.3).
            throw new ServerSettingsException("SecretModel:JournalFolder and SecretModel:ModelFolder must be separate folders, neither inside the other.");
        }

        return new ServerSettings(mode, url, server, authority, thumbprints, modelFolder, journalFolder);
    }

    private static string Required(IConfigurationSection section, string key) =>
        section[key] is { Length: > 0 } value
            ? value
            : throw new ServerSettingsException(
                $"SecretModel:{key} is not set. Put it in the settings file named by {SettingsFileVariable} (README §3.1).");

    private static string ExistingFolder(IConfigurationSection section, string key, string name)
    {
        string folder = Required(section, key);
        if (!Directory.Exists(folder))
        {
            throw new ServerSettingsException($"The {name} SecretModel:{key} was not found: {folder}");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    // True when folder is outer or inside it. Case is ignored, as Windows does.
    private static bool Inside(string folder, string outer) =>
        folder.Equals(outer, StringComparison.OrdinalIgnoreCase)
        || folder.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static Uri ParseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps
            || url.AbsolutePath != "/" || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.UserInfo))
        {
            throw new ServerSettingsException("SecretModel:Url must be an https address with no path, such as https://localhost:7443.");
        }

        if (url.Host != "localhost" && !IPAddress.TryParse(url.Host, out _))
        {
            throw new ServerSettingsException("SecretModel:Url must name localhost or an IP address, such as 0.0.0.0 for every address of this computer.");
        }

        return url;
    }

    private static X509Certificate2 LoadServerCertificate(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new ServerSettingsException($"The server certificate SecretModel:ServerCertificate was not found: {path}");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (CryptographicException)
        {
            throw new ServerSettingsException(
                $"The server certificate SecretModel:ServerCertificate could not be read with the password in {PasswordVariable}.");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new ServerSettingsException("The server certificate SecretModel:ServerCertificate holds no private key.");
        }

        return certificate;
    }

    private static X509Certificate2 LoadAuthority(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServerSettingsException($"The client certificate authority SecretModel:ClientCertificateAuthority was not found: {path}");
        }

        try
        {
            return X509CertificateLoader.LoadCertificateFromFile(path);
        }
        catch (CryptographicException)
        {
            throw new ServerSettingsException("The client certificate authority SecretModel:ClientCertificateAuthority is not a certificate.");
        }
    }

    private static HashSet<string> LoadThumbprints(IConfigurationSection section)
    {
        var thumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection item in section.GetChildren())
        {
            string value = item.Value?.Trim() ?? "";
            if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            {
                throw new ServerSettingsException(
                    "Each SecretModel:AllowedClientThumbprints entry must be a SHA-256 thumbprint: 64 hex characters.");
            }

            thumbprints.Add(value);
        }

        if (thumbprints.Count == 0)
        {
            throw new ServerSettingsException("SecretModel:AllowedClientThumbprints is empty, so no client could connect.");
        }

        return thumbprints;
    }
}
