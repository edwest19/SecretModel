// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 5). AI-generated.
//
// Writes a set of test certificates for the Home-mode server (README §3.1) into a folder outside any
// git repository:
//
//   dotnet run -c Release --project tools/SecretModel.TestCerts -- <folder> [client id ...]
//
// The password for the .pfx files comes from the environment variable SECRETMODEL_CERT_PASSWORD; the
// server reads the same variable. It writes ca.cer (the test authority's public certificate; its
// private key is thrown away, so making more certificates means a new set), server.pfx, and one
// <client id>.pfx and <client id>.cer per client (client-a and client-b when none are given), then
// prints the SHA-256 thumbprints for the server's allowlist. Certificates are valid for a year.

using System.Security.Cryptography.X509Certificates;
using SecretModel.Core.Journal;
using SecretModel.TestCerts;

const string PasswordVariable = "SECRETMODEL_CERT_PASSWORD";

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: <folder outside the repository> [client id ...]");
    return 2;
}

string folder = Path.GetFullPath(args[0]);
string[] clients = args.Length > 1 ? args[1..] : ["client-a", "client-b"];
string? password = Environment.GetEnvironmentVariable(PasswordVariable);

string? problem = CertificateSet.Problem(folder, clients, password);
if (problem is not null)
{
    Console.Error.WriteLine(problem);
    return 2;
}

foreach (string line in CertificateSet.Write(folder, clients, password!, DateTimeOffset.UtcNow))
{
    Console.WriteLine(line);
}

return 0;

/// <summary>Writes the set; kept apart from the console so that tests can run it.</summary>
public static class CertificateSet
{
    /// <summary>Why the set can't be written, or null.</summary>
    public static string? Problem(string folder, IReadOnlyList<string> clients, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Set SECRETMODEL_CERT_PASSWORD first: it protects the .pfx files.";
        }

        if (clients.Any(c => !JournalNames.IsValidClientId(c)))
        {
            // Never repeated: a value that breaks the rule may be a card number (README SM-PRIV-004).
            return "A client id given breaks the client-id rule (README §8.2).";
        }

        for (DirectoryInfo? d = new(folder); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
            {
                return "Choose a folder outside any git repository: certificate files must never be committed.";
            }
        }

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return "Choose an empty or new folder: nothing is overwritten.";
        }

        return null;
    }

    /// <summary>Writes the files and returns the lines to show.</summary>
    public static IReadOnlyList<string> Write(string folder, IReadOnlyList<string> clients, string password, DateTimeOffset now)
    {
        Directory.CreateDirectory(folder);
        DateTimeOffset notBefore = now.AddMinutes(-5);
        DateTimeOffset notAfter = now.AddYears(1);
        var lines = new List<string>();

        using X509Certificate2 authority = TestCertificates.CreateAuthority(notBefore, notAfter);
        File.WriteAllBytes(Path.Combine(folder, "ca.cer"), authority.Export(X509ContentType.Cert));
        lines.Add($"ca.cer          {TestCertificates.AuthorityName}");

        using X509Certificate2 server = TestCertificates.CreateServer(authority, notBefore, notAfter, Environment.MachineName.ToLowerInvariant());
        File.WriteAllBytes(Path.Combine(folder, "server.pfx"), server.Export(X509ContentType.Pkcs12, password));
        lines.Add($"server.pfx      CN=localhost  SHA-256 {TestCertificates.Sha256Thumbprint(server)}");

        foreach (string client in clients)
        {
            using X509Certificate2 certificate = TestCertificates.CreateClient(authority, client, notBefore, notAfter);
            File.WriteAllBytes(Path.Combine(folder, client + ".pfx"), certificate.Export(X509ContentType.Pkcs12, password));
            File.WriteAllBytes(Path.Combine(folder, client + ".cer"), certificate.Export(X509ContentType.Cert));
            lines.Add($"{client}.pfx  CN={client}  SHA-256 {TestCertificates.Sha256Thumbprint(certificate)}");
        }

        lines.Add($"Valid until {notAfter:yyyy-MM-dd}. Put the client thumbprints in AllowedClientThumbprints (README §3.1).");
        return lines;
    }
}
