// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 5). AI-generated.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SecretModel.Core.Journal;

namespace SecretModel.Api.Hosting;

/// <summary>Why a client certificate was not accepted. Logged by the server; never told to the client.</summary>
public enum CertificateRejection
{
    /// <summary>Its SHA-256 thumbprint is not on the allowlist.</summary>
    NotOnAllowlist,

    /// <summary>It does not chain to the configured certificate authority.</summary>
    NotIssuedByTheAuthority,

    /// <summary>It is not yet valid, or no longer valid.</summary>
    OutsideValidity,

    /// <summary>Its subject has no single CN, or the CN breaks the client-id rule (README §8.2).</summary>
    NotAClientId,
}

/// <summary>The outcome: a client id, or why not.</summary>
public sealed record CertificateCheck(string? ClientId, CertificateRejection? Rejection)
{
    public bool Accepted => Rejection is null;
}

/// <summary>The client that presented an accepted certificate. Its id comes from the certificate's CN only (SM-AUTH-003).</summary>
public sealed record ClientIdentity(string ClientId);

/// <summary>
/// The SM-AUTH-002 checks on a client certificate from the TLS handshake (README §3.1): its SHA-256
/// thumbprint is on the allowlist; it chains to the configured certificate authority, and only to
/// it (Windows' trusted roots play no part; no revocation check, because a test authority has no
/// revocation list); it is valid now; and its subject has exactly one CN, which is a client id. The
/// TLS layer has already parsed it as X.509: a certificate that doesn't parse ends the handshake.
/// </summary>
public sealed class ClientCertificateValidator
{
    private const string CommonNameOid = "2.5.4.3";

    private readonly X509Certificate2 authority;
    private readonly IReadOnlySet<string> allowedThumbprints;
    private readonly TimeProvider time;

    public ClientCertificateValidator(X509Certificate2 authority, IReadOnlySet<string> allowedThumbprints, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(allowedThumbprints);
        ArgumentNullException.ThrowIfNull(time);
        this.authority = authority;
        this.allowedThumbprints = new HashSet<string>(allowedThumbprints, StringComparer.OrdinalIgnoreCase);
        this.time = time;
    }

    public CertificateCheck Check(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!allowedThumbprints.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)))
        {
            return Rejected(CertificateRejection.NotOnAllowlist);
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        if (!IssuedByTheAuthority(certificate, now))
        {
            return Rejected(CertificateRejection.NotIssuedByTheAuthority);
        }

        if (!ValidAt(certificate, now) || !ValidAt(authority, now))
        {
            return Rejected(CertificateRejection.OutsideValidity);
        }

        X500RelativeDistinguishedName[] names = [.. certificate.SubjectName.EnumerateRelativeDistinguishedNames()];
        if (names.Any(n => n.HasMultipleElements))
        {
            return Rejected(CertificateRejection.NotAClientId);
        }

        string[] commonNames = [.. names
            .Where(n => n.GetSingleElementType().Value == CommonNameOid)
            .Select(n => n.GetSingleElementValue() ?? "")];
        if (commonNames.Length != 1 || !JournalNames.IsValidClientId(commonNames[0]))
        {
            return Rejected(CertificateRejection.NotAClientId);
        }

        return new CertificateCheck(commonNames[0], null);
    }

    private bool IssuedByTheAuthority(X509Certificate2 certificate, DateTime now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // The server makes no network calls to build a chain.
        chain.ChainPolicy.DisableCertificateDownloads = true;
        // Validity is checked on its own, so that it has a reason of its own.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.VerificationTime = now;
        try
        {
            // With custom root trust the authority is the only root, so a chain that builds ends
            // there. Two elements: the authority issued this certificate, and it isn't the authority.
            return chain.Build(certificate) && chain.ChainElements.Count == 2;
        }
        finally
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }

    private static bool ValidAt(X509Certificate2 certificate, DateTime now) =>
        now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime();

    private static CertificateCheck Rejected(CertificateRejection reason) => new(null, reason);
}
