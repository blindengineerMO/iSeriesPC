using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Signatures bind exact bytes, intended operation, resource identity and signing time.</summary>
public sealed class ContentTrustService(SqliteConnectionFactory factory, CertificateService certificates, TimeProvider? clock = null)
{
    public const string Algorithm = "RSA-PSS-SHA256/v1";
    public const int MaximumContentBytes = 64 * 1024 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ObjectSignature Sign(ReadOnlySpan<byte> content, CertificatePurpose purpose, string resource, string certificateId)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator, allowAdopted: false);
        Validate(content, purpose, resource);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var at = _clock.GetUtcNow();
        using var certificate = certificates.SigningCertificate(certificateId, purpose);
        using var key = certificate.GetRSAPrivateKey()!;
        var signature = key.SignData(Message(purpose, resource, hash, at), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new(Algorithm, certificateId, hash, Convert.ToBase64String(signature), at);
    }

    public void Verify(ReadOnlySpan<byte> content, CertificatePurpose purpose, string resource, ObjectSignature signature)
    {
        Validate(content, purpose, resource);
        if (signature is null) throw CertificateService.Invalid("Signature is required.");
        if (signature.Algorithm != Algorithm || signature.ContentHash is not { Length: 64 } hash ||
            !hash.All(char.IsAsciiHexDigit) || signature.Value is not { Length: > 0 and <= 4096 } ||
            signature.KeyId is not { Length: 64 } || signature.SignedAt > _clock.GetUtcNow().AddMinutes(5))
            throw CertificateService.Invalid("Invalid signature format or signing time.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(hash)))
            throw CertificateService.Invalid("Signed content has been altered.");
        certificates.RequireTrusted(signature.KeyId, purpose);
        using var certificate = certificates.PublicCertificate(signature.KeyId);
        if (signature.SignedAt.UtcDateTime < certificate.NotBefore.ToUniversalTime() ||
            signature.SignedAt.UtcDateTime >= certificate.NotAfter.ToUniversalTime())
            throw CertificateService.Invalid("Signing time is outside certificate validity.");
        using var key = certificate.GetRSAPublicKey()!;
        try
        {
            if (!key.VerifyData(Message(purpose, resource, hash, signature.SignedAt), Convert.FromBase64String(signature.Value),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw CertificateService.Invalid("Digital signature is invalid.");
        }
        catch (FormatException) { throw CertificateService.Invalid("Digital signature encoding is invalid."); }
    }

    private static byte[] Message(CertificatePurpose purpose, string resource, string hash, DateTimeOffset at) =>
        Encoding.UTF8.GetBytes("iSeriesPC-signed-content-v1\0" + purpose + "\0" + resource + "\0" + hash + "\0" + at.ToUniversalTime().ToString("o"));
    private static void Validate(ReadOnlySpan<byte> content, CertificatePurpose purpose, string resource)
    {
        if (content.Length > MaximumContentBytes) throw CertificateService.Invalid("Signed content exceeds the 64 MiB admission limit.");
        if (!Enum.IsDefined(purpose) || purpose == CertificatePurpose.TlsServer) throw CertificateService.Invalid("Invalid signed-content purpose.");
        if (resource is not { Length: > 0 and <= 256 } || resource.Any(char.IsControl)) throw CertificateService.Invalid("Invalid signed resource identity.");
    }
}
