// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.
//
// Tests for the SM-AUTH-002 checks on a client certificate (README §3.1), with a fixed clock: the
// SHA-256 allowlist, the issuer by chain to the configured authority only, NotBefore and NotAfter
// (of the certificate and of the authority), and a subject whose single CN is a client id
// (SM-AUTH-003). Certificates are synthetic, made with .NET's own APIs.

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SecretModel.Api.Hosting;
using SecretModel.Core.Journal;
using SecretModel.TestCerts;

namespace SecretModel.Tests.Hosting;

[TestClass]
public sealed class ClientCertificateValidatorTests
{
    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";
    private const string OrganizationalUnitOid = "2.5.4.11";

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-003")]
    public void Check_AcceptsAnAllowlistedCertificateFromTheAuthority_AsItsCommonName()
    {
        CertificateCheck check = Validator(TestPki.ClientA).Check(TestPki.ClientA);

        Assert.IsTrue(check.Accepted);
        Assert.AreEqual("client-a", check.ClientId);
        Assert.IsNull(check.Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Check_RefusesACertificateNotOnTheAllowlist()
    {
        CertificateCheck check = Validator(TestPki.ClientA).Check(TestPki.ClientB);

        Assert.AreEqual(CertificateRejection.NotOnAllowlist, check.Rejection);
        Assert.IsNull(check.ClientId);
        Assert.IsFalse(check.Accepted);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Check_MatchesAThumbprintWrittenInLowercase()
    {
        var validator = new ClientCertificateValidator(
            TestPki.Authority, new HashSet<string> { TestCertificates.Sha256Thumbprint(TestPki.ClientA).ToLowerInvariant() }, new FixedTime(TestPki.Now));

        Assert.AreEqual("client-a", validator.Check(TestPki.ClientA).ClientId);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [DataRow("other authority")]
    [DataRow("self-signed")]
    public void Check_RefusesACertificateTheAuthorityDidNotIssue(string name)
    {
        X509Certificate2 certificate = TestPki.Named(name);

        Assert.AreEqual(CertificateRejection.NotIssuedByTheAuthority, Validator(certificate).Check(certificate).Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Check_RefusesTheAuthorityItself_EvenWhenItsNameIsAClientId()
    {
        using X509Certificate2 authority = TestCertificates.CreateAuthority(TestPki.Now.AddDays(-1), TestPki.Now.AddDays(1), "CN=client-z");
        var validator = new ClientCertificateValidator(
            authority, new HashSet<string> { TestCertificates.Sha256Thumbprint(authority) }, new FixedTime(TestPki.Now));

        Assert.AreEqual(CertificateRejection.NotIssuedByTheAuthority, validator.Check(authority).Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Check_ValidityIncludesBothEnds_ToTheSecond()
    {
        DateTimeOffset notBefore = WholeSeconds(TestPki.Now.AddDays(-2));
        DateTimeOffset notAfter = WholeSeconds(TestPki.Now.AddDays(2));
        using X509Certificate2 certificate = TestCertificates.CreateClient(TestPki.Authority, "client-a", notBefore, notAfter);

        Assert.AreEqual(CertificateRejection.OutsideValidity, Validator(certificate, notBefore.AddSeconds(-1)).Check(certificate).Rejection);
        Assert.IsTrue(Validator(certificate, notBefore).Check(certificate).Accepted);
        Assert.IsTrue(Validator(certificate, notAfter).Check(certificate).Accepted);
        Assert.AreEqual(CertificateRejection.OutsideValidity, Validator(certificate, notAfter.AddSeconds(1)).Check(certificate).Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    public void Check_RefusesACertificateOnceItsAuthorityHasExpired()
    {
        using X509Certificate2 authority = TestCertificates.CreateAuthority(TestPki.Now.AddDays(-10), TestPki.Now.AddDays(-1));
        using X509Certificate2 certificate = IssuedPastTheAuthority(authority, TestPki.Now.AddDays(-5), TestPki.Now.AddDays(5));
        var validator = new ClientCertificateValidator(
            authority, new HashSet<string> { TestCertificates.Sha256Thumbprint(certificate) }, new FixedTime(TestPki.Now));
        var whileTheAuthorityWasValid = new ClientCertificateValidator(
            authority, new HashSet<string> { TestCertificates.Sha256Thumbprint(certificate) }, new FixedTime(TestPki.Now.AddDays(-2)));

        Assert.AreEqual(CertificateRejection.OutsideValidity, validator.Check(certificate).Rejection);
        Assert.IsTrue(whileTheAuthorityWasValid.Check(certificate).Accepted);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-002")]
    [TestCategory("SM-AUTH-003")]
    [DataRow("4111111111111111")]
    [DataRow("Client A")]
    [DataRow("client/a")]
    [DataRow("-client")]
    [DataRow("client-é")]
    public void Check_RefusesACommonNameThatIsNotAClientId(string commonName)
    {
        using X509Certificate2 certificate = TestCertificates.CreateClient(TestPki.Authority, commonName, TestPki.Now.AddDays(-1), TestPki.Now.AddDays(1));

        Assert.AreEqual(CertificateRejection.NotAClientId, Validator(certificate).Check(certificate).Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    public void Check_AcceptsTheLongestClientId()
    {
        string longest = new('a', JournalNames.MaxClientIdLength);
        using X509Certificate2 certificate = TestCertificates.CreateClient(TestPki.Authority, longest, TestPki.Now.AddDays(-1), TestPki.Now.AddDays(1));
        using X509Certificate2 tooLong = TestCertificates.CreateClient(TestPki.Authority, longest + "a", TestPki.Now.AddDays(-1), TestPki.Now.AddDays(1));

        Assert.AreEqual(longest, Validator(certificate).Check(certificate).ClientId);
        Assert.AreEqual(CertificateRejection.NotAClientId, Validator(tooLong).Check(tooLong).Rejection);
    }

    [TestMethod]
    [TestCategory("SM-AUTH-003")]
    [DataRow("no common name")]
    [DataRow("two common names")]
    [DataRow("common name in a multi-valued name")]
    [DataRow("multi-valued name beside a common name")]
    public void Check_NeedsExactlyOneCommonNameOnItsOwn(string subject)
    {
        using X509Certificate2 certificate = TestCertificates.CreateClient(TestPki.Authority, Subject(subject), TestPki.Now.AddDays(-1), TestPki.Now.AddDays(1));

        Assert.AreEqual(CertificateRejection.NotAClientId, Validator(certificate).Check(certificate).Rejection);
    }

    private static ClientCertificateValidator Validator(X509Certificate2 allowed, DateTimeOffset? now = null) =>
        new(TestPki.Authority, new HashSet<string> { TestCertificates.Sha256Thumbprint(allowed) }, new FixedTime(now ?? TestPki.Now));

    private static DateTimeOffset WholeSeconds(DateTimeOffset time) =>
        new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static X500DistinguishedName Subject(string name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            switch (name)
            {
                case "no common name":
                    Name(writer, (OrganizationOid, "SecretModel"));
                    break;
                case "two common names":
                    Name(writer, (CommonNameOid, "client-a"));
                    Name(writer, (CommonNameOid, "client-b"));
                    break;
                case "common name in a multi-valued name":
                    Name(writer, (CommonNameOid, "client-a"), (OrganizationOid, "SecretModel"));
                    break;
                case "multi-valued name beside a common name":
                    Name(writer, (CommonNameOid, "client-a"));
                    Name(writer, (OrganizationOid, "SecretModel"), (OrganizationalUnitOid, "Tests"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(name));
            }
        }

        return new X500DistinguishedName(writer.Encode());
    }

    // One relative distinguished name: a SET of one or more (type, value) pairs.
    private static void Name(AsnWriter writer, params (string Oid, string Value)[] parts)
    {
        using (writer.PushSetOf())
        {
            foreach ((string oid, string value) in parts)
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(oid);
                    writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                }
            }
        }
    }

    // CertificateRequest.Create with an issuer certificate refuses a validity longer than the
    // issuer's, so this signs with the issuer's key directly, as another tool could.
    private static X509Certificate2 IssuedPastTheAuthority(X509Certificate2 authority, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(TestCertificates.CommonName("client-a"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        using RSA authorityKey = authority.GetRSAPrivateKey()!;
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return request.Create(authority.SubjectName, X509SignatureGenerator.CreateForRSA(authorityKey, RSASignaturePadding.Pkcs1), notBefore, notAfter, serial);
    }
}

/// <summary>A clock that always says the same time.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
