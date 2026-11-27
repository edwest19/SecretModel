// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 5). AI-generated.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SecretModel.TestCerts;

/// <summary>
/// Makes certificates for testing the Home-mode server (README §3.1): a test certificate authority,
/// a server certificate for <c>localhost</c>, and client certificates whose CN is a client id. RSA
/// 2048 with SHA-256, so Windows PowerShell 5.1 can use the client certificates too. For tests and a
/// home network only: the authority has no revocation list. Every certificate comes back with its
/// private key, loaded from PKCS#12 so that Windows can use it for TLS.
/// </summary>
public static class TestCertificates
{
    public const string AuthorityName = "CN=SecretModel Test CA";

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    public static X509Certificate2 CreateAuthority(DateTimeOffset notBefore, DateTimeOffset notAfter, string name = AuthorityName)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);
        return Reload(certificate);
    }

    /// <summary>A server certificate for <c>localhost</c>, 127.0.0.1 and ::1, and any other DNS names given.</summary>
    public static X509Certificate2 CreateServer(X509Certificate2 authority, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] extraDnsNames)
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (string name in extraDnsNames)
        {
            names.AddDnsName(name);
        }

        return Issue(authority, CommonName("localhost"), ServerAuthentication, notBefore, notAfter, names.Build(), X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment);
    }

    /// <summary>A client certificate whose subject is <c>CN={commonName}</c>.</summary>
    public static X509Certificate2 CreateClient(X509Certificate2 authority, string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        CreateClient(authority, CommonName(commonName), notBefore, notAfter);

    /// <summary>A client certificate with any subject, for testing the server's checks on it.</summary>
    public static X509Certificate2 CreateClient(X509Certificate2 authority, X500DistinguishedName subject, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        Issue(authority, subject, ClientAuthentication, notBefore, notAfter, null, X509KeyUsageFlags.DigitalSignature);

    /// <summary>A self-signed client certificate: no authority issued it.</summary>
    public static X509Certificate2 CreateSelfSignedClient(string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = ClientRequest(CommonName(commonName), key);
        using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);
        return Reload(certificate);
    }

    /// <summary>A subject holding one CN, built rather than parsed so that no character is special.</summary>
    public static X500DistinguishedName CommonName(string value)
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(value);
        return name.Build();
    }

    /// <summary>The certificate's SHA-256 thumbprint, 64 uppercase hex characters.</summary>
    public static string Sha256Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 Issue(
        X509Certificate2 authority, X500DistinguishedName subject, string usage, DateTimeOffset notBefore, DateTimeOffset notAfter,
        X509Extension? alternativeNames, X509KeyUsageFlags keyUsage)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        if (alternativeNames is not null)
        {
            request.CertificateExtensions.Add(alternativeNames);
        }

        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using X509Certificate2 issued = request.Create(authority, notBefore, notAfter, serial);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        return Reload(withKey);
    }

    private static CertificateRequest ClientRequest(X500DistinguishedName subject, RSA key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthentication)], critical: false));
        return request;
    }

    // A certificate made in memory holds an ephemeral key, which Windows can't use for TLS; one read
    // back from PKCS#12 can.
    private static X509Certificate2 Reload(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
}
