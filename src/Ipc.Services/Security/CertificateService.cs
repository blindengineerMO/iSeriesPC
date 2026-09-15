using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public enum CertificatePurpose { ObjectSigning, Restore, Update, ServiceDeployment, TlsServer }
public sealed record CertificateInfo(string Id, string Label, string Subject, string Issuer, DateTimeOffset NotBefore,
    DateTimeOffset NotAfter, string State, bool HasPrivateKey, bool IsAuthority, string? ReplacedBy, int DaysRemaining);
public sealed record CertificateBinding(string Service, CertificatePurpose Purpose, string CertificateId);
public sealed record WrappingKeyInfo(string Id, bool Active, long EncryptedValues);

/// <summary>Instance-scoped certificate and private-key inventory, independent of the host trust store.</summary>
public sealed class CertificateService : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SecretProtector _protector;
    private readonly TimeProvider _clock;
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    public CertificateService(SqliteConnectionFactory factory, TimeProvider? clock = null)
    { _factory = factory; _protector = new(factory); _clock = clock ?? TimeProvider.System; }

    public IReadOnlyList<CertificateInfo> List()
    {
        Admin();
        return Rows().Select(Info).ToArray();
    }

    public IReadOnlyList<WrappingKeyInfo> WrappingKeys()
    {
        Admin();
        return _protector.Inventory().Select(key =>
        {
            using var connection = _factory.Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT count(*) FROM sys_mfa WHERE secret LIKE $prefix) +
                  (SELECT count(*) FROM sys_mfa_enrollments WHERE secret LIKE $prefix) +
                  (SELECT count(*) FROM sys_certificates WHERE private_key LIKE $prefix)
                """;
            command.Parameters.AddWithValue("$prefix", "v1." + key.Id + ".%");
            return new WrappingKeyInfo(key.Id, key.Active, Convert.ToInt64(command.ExecuteScalar()));
        }).ToArray();
    }

    public string RotateWrappingKey()
    {
        Admin();
        var id = _protector.Rotate();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var target in new[] { ("sys_mfa", "profile", "secret", "totp/"),
            ("sys_mfa_enrollments", "token_hash", "secret", "totp/"), ("sys_certificates", "id", "private_key", "certificate/") })
        {
            var (table, identifier, value, prefix) = target;
            using var select = connection.CreateCommand(); select.Transaction = transaction;
            select.CommandText = $"SELECT {identifier},{(table == "sys_mfa_enrollments" ? "profile" : identifier)},{value} FROM {table} WHERE {value} IS NOT NULL";
            var rows = new List<(string Id, string Purpose, string Envelope)>();
            using (var reader = select.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetString(0), prefix + reader.GetString(1), reader.GetString(2)));
            foreach (var row in rows)
            {
                var secret = _protector.Unprotect(row.Purpose, row.Envelope);
                string envelope;
                try { envelope = _protector.Protect(row.Purpose, secret); }
                finally { CryptographicOperations.ZeroMemory(secret); }
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET {value}=$value WHERE {identifier}=$id";
                update.Parameters.AddWithValue("$value", envelope); update.Parameters.AddWithValue("$id", row.Id); update.ExecuteNonQuery();
            }
        }
        MfaStore.Audit(connection, transaction, "security.wrapping-key.rotated", OperationIdentity.Current?.Principal ?? "*SYSTEM", true, _clock.GetUtcNow());
        transaction.Commit();
        // Old keys are retained for historical backups, even after live ciphertext is rewrapped.
        return id;
    }

    public byte[] ExportCertificate(string id)
    {
        Admin();
        return Get(id).Der.ToArray();
    }

    public CertificateInfo Import(string label, ReadOnlySpan<byte> encoded, string? password = null)
    {
        Admin(); Label(label);
        if (encoded.Length is < 1 or > 1048576) throw Invalid("Certificate import is limited to 1 MiB.");
        using var certificate = new X509Certificate2(encoded.ToArray(), password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        return Save(label, certificate);
    }

    public CertificateInfo ImportPem(string label, string certificatePem, string? privateKeyPem = null)
    {
        Admin(); Label(label);
        if (certificatePem.Length > 1048576 || privateKeyPem?.Length > 65536) throw Invalid("Certificate import is too large.");
        using var certificate = privateKeyPem is null ? X509Certificate2.CreateFromPem(certificatePem) :
            X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        return Save(label, certificate);
    }

    public CertificateInfo CreateAuthority(string label, string commonName, int days = 3650)
    {
        Admin(); Label(label); CommonName(commonName); Days(days, 3650);
        using var key = RSA.Create(3072);
        var request = Request(commonName, key);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(_clock.GetUtcNow().AddMinutes(-5), _clock.GetUtcNow().AddDays(days));
        return Save(label, certificate);
    }

    public CertificateInfo Issue(string label, string commonName, string authorityId, CertificatePurpose purpose,
        int days = 365, IReadOnlyList<string>? dnsNames = null)
    {
        Admin(); Label(label); CommonName(commonName); Days(days, 825); Purpose(purpose);
        var authority = Get(authorityId);
        if (authority.State != "Active") throw Invalid("Issuing authority is not active.");
        using var issuer = WithPrivateKey(authority);
        if (!IsAuthority(issuer)) throw Invalid("Certificate is not a certificate authority.");
        using var key = RSA.Create(3072);
        var request = Request(commonName, key);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature |
            (purpose == CertificatePurpose.TlsServer ? X509KeyUsageFlags.KeyEncipherment : 0), true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {
            new(purpose == CertificatePurpose.TlsServer ? ServerAuthOid : CodeSigningOid) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (purpose == CertificatePurpose.TlsServer)
        {
            if (dnsNames is null || dnsNames.Count is < 1 or > 32) throw Invalid("TLS certificates require 1 through 32 DNS/IP names.");
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                if (System.Net.IPAddress.TryParse(name, out var address)) san.AddIpAddress(address);
                else if (Uri.CheckHostName(name) == UriHostNameType.Dns && !name.Contains('*')) san.AddDnsName(name);
                else throw Invalid("Invalid TLS DNS/IP name.");
            }
            request.CertificateExtensions.Add(san.Build());
        }
        var now = _clock.GetUtcNow();
        var notBefore = now.AddMinutes(-5);
        if (issuer.NotBefore.ToUniversalTime() > notBefore.UtcDateTime) notBefore = new(issuer.NotBefore.ToUniversalTime());
        if (now.UtcDateTime < issuer.NotBefore.ToUniversalTime() || now.AddDays(days).UtcDateTime > issuer.NotAfter.ToUniversalTime())
            throw Invalid("Requested lifetime is outside the issuing certificate's validity.");
        var serial = RandomNumberGenerator.GetBytes(20); serial[0] &= 0x7f; serial[0] |= 1;
        using var issued = request.Create(issuer, notBefore, now.AddDays(days), serial);
        using var complete = issued.CopyWithPrivateKey(key);
        return Save(label, complete);
    }

    public void SetTrust(string id, CertificatePurpose purpose, bool trusted)
    {
        Admin(); Purpose(purpose);
        var row = Get(id);
        using var certificate = new X509Certificate2(row.Der);
        if (trusted && (row.State != "Active" || !IsAuthority(certificate) || !IsCurrent(certificate)))
            throw Invalid("Only a current active CA certificate can be a trust anchor.");
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = trusted
            ? "INSERT INTO sys_certificate_trust(certificate_id,purpose) VALUES($id,$purpose) ON CONFLICT DO NOTHING"
            : "DELETE FROM sys_certificate_trust WHERE certificate_id=$id AND purpose=$purpose";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$purpose", purpose.ToString());
        command.ExecuteNonQuery();
    }

    public void Revoke(string id)
    {
        Admin(); _ = Get(id);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_certificates SET state='Revoked' WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    public CertificateInfo Renew(string id, string authorityId, CertificatePurpose purpose, int days = 365,
        IReadOnlyList<string>? dnsNames = null)
    {
        Admin(); var old = Get(id);
        if (old.State != "Active") throw Invalid("Only active certificates can be renewed.");
        using var certificate = new X509Certificate2(old.Der);
        if (IsAuthority(certificate)) throw Invalid("Create a new CA and explicitly establish its trust and bindings.");
        var replacement = Issue(old.Label, certificate.GetNameInfo(X509NameType.SimpleName, false), authorityId, purpose, days, dnsNames);
        // Persist the replacement first. A failed validation/transaction leaves it unbound,
        // while the previous certificate and all existing bindings remain usable.
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var bindings = connection.CreateCommand();
        bindings.Transaction = transaction;
        bindings.CommandText = "SELECT DISTINCT purpose FROM sys_certificate_bindings WHERE certificate_id=$id";
        bindings.Parameters.AddWithValue("$id", id);
        var purposes = new List<CertificatePurpose>();
        using (var reader = bindings.ExecuteReader())
            while (reader.Read()) purposes.Add(Enum.Parse<CertificatePurpose>(reader.GetString(0)));
        foreach (var bound in purposes) RequireTrusted(replacement.Id, bound, signing: true);
        using var change = connection.CreateCommand();
        change.Transaction = transaction;
        change.CommandText = """
            UPDATE sys_certificates SET state='Retired',replaced_by=$next WHERE id=$id AND state='Active';
            """;
        change.Parameters.AddWithValue("$id", id); change.Parameters.AddWithValue("$next", replacement.Id);
        if (change.ExecuteNonQuery() != 1) throw Invalid("Certificate changed during renewal.");
        change.CommandText = "UPDATE sys_certificate_bindings SET certificate_id=$next,changed=$now WHERE certificate_id=$id";
        change.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("o")); change.ExecuteNonQuery();
        MfaStore.Audit(connection, transaction, "security.certificate.renewed", OperationIdentity.Current?.Principal ?? "*SYSTEM", true, _clock.GetUtcNow());
        transaction.Commit();
        return replacement;
    }

    public void Bind(string service, string id, CertificatePurpose purpose)
    {
        Admin();
        if (service.Length is < 1 or > 64 || service.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw Invalid("Service binding names contain only letters, digits, dash, underscore and dot.");
        RequireTrusted(id, purpose, signing: true);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_certificate_bindings(service,purpose,certificate_id,changed) VALUES($service,$purpose,$id,$now)
            ON CONFLICT(service) DO UPDATE SET purpose=excluded.purpose,certificate_id=excluded.certificate_id,changed=excluded.changed;
            """;
        command.Parameters.AddWithValue("$service", service); command.Parameters.AddWithValue("$purpose", purpose.ToString());
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("o"));
        command.ExecuteNonQuery();
        MfaStore.Audit(connection, transaction, "security.certificate.bound", OperationIdentity.Current?.Principal ?? "*SYSTEM", true, _clock.GetUtcNow());
        transaction.Commit();
    }

    public IReadOnlyList<CertificateBinding> Bindings()
    {
        Admin();
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT service,purpose,certificate_id FROM sys_certificate_bindings ORDER BY service";
        using var reader = command.ExecuteReader(); var result = new List<CertificateBinding>();
        while (reader.Read()) result.Add(new(reader.GetString(0), Enum.Parse<CertificatePurpose>(reader.GetString(1)), reader.GetString(2)));
        return result;
    }

    internal X509Certificate2 SigningCertificate(string id, CertificatePurpose purpose)
    { RequireTrusted(id, purpose, signing: true); return WithPrivateKey(Get(id)); }

    internal void RequireTrusted(string id, CertificatePurpose purpose, bool signing = false)
    {
        Purpose(purpose);
        var rows = Rows();
        var leaf = rows.SingleOrDefault(r => r.Id == id) ?? throw Invalid("Certificate is not in the instance inventory.");
        if (leaf.State == "Revoked" || signing && (leaf.State != "Active" || leaf.Key is null))
            throw Invalid("Certificate is revoked, retired or has no private key.");
        using var certificate = new X509Certificate2(leaf.Der);
        if (!IsCurrent(certificate) || IsAuthority(certificate)) throw Invalid("A currently valid end-entity certificate is required.");
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize is < 3072 or > 8192) throw Invalid("RSA keys must have 3072 through 8192 bits.");
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        var oid = purpose == CertificatePurpose.TlsServer ? ServerAuthOid : CodeSigningOid;
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == oid)) throw Invalid("Certificate purpose does not match its extended key usage.");
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault() is not { } usage ||
            (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0) throw Invalid("Certificate cannot make digital signatures.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = _clock.GetUtcNow().UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(oid));
        var trusted = TrustIds(purpose);
        var loaded = new List<X509Certificate2>();
        try
        {
            foreach (var row in rows.Where(r => r.State != "Revoked"))
            {
                var candidate = new X509Certificate2(row.Der); loaded.Add(candidate);
                chain.ChainPolicy.ExtraStore.Add(candidate);
                if (trusted.Contains(row.Id)) chain.ChainPolicy.CustomTrustStore.Add(candidate);
            }
            if (!chain.Build(certificate) || chain.ChainElements.Cast<X509ChainElement>().Any(element =>
                rows.Any(r => r.Id == Id(element.Certificate) && r.State == "Revoked")))
                throw Invalid("Certificate chain is untrusted, expired or revoked for this purpose.");
        }
        finally { foreach (var item in loaded) item.Dispose(); }
    }

    internal X509Certificate2 PublicCertificate(string id) => new(Get(id).Der);

    private CertificateInfo Save(string label, X509Certificate2 certificate)
    {
        using var publicKey = certificate.GetRSAPublicKey();
        if (publicKey is null || publicKey.KeySize is < 3072 or > 8192) throw Invalid("Only RSA certificates with 3072 through 8192 bits are supported.");
        if (certificate.SignatureAlgorithm.Value is not ("1.2.840.113549.1.1.11" or "1.2.840.113549.1.1.12" or "1.2.840.113549.1.1.13"))
            throw Invalid("Certificate signatures must use RSA PKCS#1 with SHA-256, SHA-384 or SHA-512.");
        var id = Id(certificate);
        string? envelope = null;
        if (certificate.HasPrivateKey)
        {
            using var key = certificate.GetRSAPrivateKey() ?? throw Invalid("The RSA private key is unavailable.");
            var bytes = key.ExportPkcs8PrivateKey();
            try { envelope = _protector.Protect("certificate/" + id, bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sys_certificates(id,label,certificate,private_key,created) VALUES($id,$label,$der,$key,$created)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$label", label.ToUpperInvariant());
        command.Parameters.AddWithValue("$der", certificate.RawData); command.Parameters.AddWithValue("$key", (object?)envelope ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToString("o"));
        if (command.ExecuteNonQuery() != 1) throw Invalid("Certificate already exists; immutable inventory entries cannot be overwritten.");
        return Info(Get(id));
    }

    private X509Certificate2 WithPrivateKey(Row row)
    {
        if (row.Key is null) throw Invalid("Certificate has no private key.");
        var bytes = _protector.Unprotect("certificate/" + row.Id, row.Key);
        try
        {
            using var key = RSA.Create(); key.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length) throw Invalid("Private key contains trailing data.");
            using var certificate = new X509Certificate2(row.Der);
            return certificate.CopyWithPrivateKey(key);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private IReadOnlyList<Row> Rows()
    {
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,label,certificate,private_key,state,replaced_by FROM sys_certificates ORDER BY label,created,id";
        using var reader = command.ExecuteReader(); var result = new List<Row>();
        while (reader.Read())
        {
            var row = new Row(reader.GetString(0), reader.GetString(1), (byte[])reader[2],
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            using var certificate = new X509Certificate2(row.Der);
            if (Id(certificate) != row.Id) throw Invalid("Certificate inventory fingerprint does not match its content.");
            result.Add(row);
        }
        return result;
    }
    private HashSet<string> TrustIds(CertificatePurpose purpose)
    {
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT certificate_id FROM sys_certificate_trust WHERE purpose=$purpose";
        command.Parameters.AddWithValue("$purpose", purpose.ToString()); using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    private Row Get(string id) => Rows().SingleOrDefault(r => r.Id == id) ?? throw Invalid("Certificate not found.");
    private CertificateInfo Info(Row row)
    {
        using var certificate = new X509Certificate2(row.Der);
        var expires = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        return new(row.Id, row.Label, certificate.Subject, certificate.Issuer, new(certificate.NotBefore.ToUniversalTime()),
            expires, row.State, row.Key is not null, IsAuthority(certificate), row.ReplacedBy, (int)Math.Floor((expires - _clock.GetUtcNow()).TotalDays));
    }
    private static CertificateRequest Request(string commonName, RSA key) => new(new X500DistinguishedName("CN=" + commonName),
        key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    private bool IsCurrent(X509Certificate2 certificate) => certificate.NotBefore.ToUniversalTime() <= _clock.GetUtcNow().UtcDateTime &&
        certificate.NotAfter.ToUniversalTime() > _clock.GetUtcNow().UtcDateTime;
    private static bool IsAuthority(X509Certificate2 certificate) => certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority);
    private static string Id(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    private void Admin() => new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator, allowAdopted: false);
    private static void Label(string label) { if (!ObjectName.IsValid(label.ToUpperInvariant())) throw Invalid("Certificate label must be a valid object name."); }
    private static void CommonName(string name) { if (name.Length is < 1 or > 200 || name.Any(c => char.IsControl(c) || c is ',' or '+' or '=' or '"' or '\\' or '<' or '>' or ';')) throw Invalid("Invalid certificate common name."); }
    private static void Days(int days, int maximum) { if (days < 1 || days > maximum) throw Invalid($"Certificate lifetime must be 1 through {maximum} days."); }
    private static void Purpose(CertificatePurpose purpose) { if (!Enum.IsDefined(purpose)) throw Invalid("Unknown certificate purpose."); }
    internal static CpfException Invalid(string message) => new("IPC0110", message);
    private sealed record Row(string Id, string Label, byte[] Der, string? Key, string State, string? ReplacedBy);
    public void Dispose() => _protector.Dispose();
}
