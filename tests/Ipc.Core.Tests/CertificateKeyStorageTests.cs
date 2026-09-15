using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class CertificateKeyStorageTests
{
    [Fact]
    public void Failed_rewrap_retains_the_keys_needed_to_read_the_previous_ciphertext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-key-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var system = IpcSystem.Create(directory); system.Start();
            var ca = system.Certificates.CreateAuthority("CA", "Rewrap rollback CA");
            using var connection = system.Connections.Open(); using var inject = connection.CreateCommand();
            inject.CommandText = "CREATE TRIGGER fail_rewrap BEFORE UPDATE OF private_key ON sys_certificates BEGIN SELECT RAISE(ABORT,'injected rewrap failure'); END";
            inject.ExecuteNonQuery();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => system.Certificates.RotateWrappingKey());
            Assert.Equal(2, system.Certificates.WrappingKeys().Count);
            Assert.Equal(1, system.Certificates.WrappingKeys().Single(k => !k.Active).EncryptedValues);
            // Issuing a certificate proves the previous wrapping key still decrypts the CA key.
            Assert.True(system.Certificates.Issue("LEAF", "After failure", ca.Id, CertificatePurpose.ObjectSigning).HasPrivateKey);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OpenSsl_independently_verifies_the_certificate_chain_and_rsa_pss_signature()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-openssl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var system = IpcSystem.Create(":memory:"); system.Start();
            var ca = system.Certificates.CreateAuthority("CA", "OpenSSL reference CA");
            system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
            var leaf = system.Certificates.Issue("LEAF", "OpenSSL reference leaf", ca.Id, CertificatePurpose.ObjectSigning);
            using var rootCertificate = new X509Certificate2(system.Certificates.ExportCertificate(ca.Id));
            using var leafCertificate = new X509Certificate2(system.Certificates.ExportCertificate(leaf.Id));
            File.WriteAllText(Path.Combine(directory, "root.pem"), rootCertificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, "leaf.pem"), leafCertificate.ExportCertificatePem());
            using var publicKey = leafCertificate.GetRSAPublicKey()!;
            File.WriteAllText(Path.Combine(directory, "public.pem"), publicKey.ExportSubjectPublicKeyInfoPem());
            var content = global::System.Text.Encoding.UTF8.GetBytes("independent content");
            var signature = system.ContentTrust.Sign(content, CertificatePurpose.ObjectSigning, "reference", leaf.Id);
            File.WriteAllBytes(Path.Combine(directory, "signature.bin"), Convert.FromBase64String(signature.Value));
            var message = "iSeriesPC-signed-content-v1\0ObjectSigning\0reference\0" + Convert.ToHexString(SHA256.HashData(content)) +
                "\0" + signature.SignedAt.ToUniversalTime().ToString("o");
            File.WriteAllText(Path.Combine(directory, "message.bin"), message, new global::System.Text.UTF8Encoding(false));
            Assert.Equal(0, await OpenSsl("verify", "-CAfile", "root.pem", "leaf.pem"));
            Assert.Equal(0, await OpenSsl("dgst", "-sha256", "-verify", "public.pem", "-signature", "signature.bin", "-sigopt", "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:digest", "message.bin"));
            File.AppendAllText(Path.Combine(directory, "message.bin"), "altered");
            Assert.NotEqual(0, await OpenSsl("dgst", "-sha256", "-verify", "public.pem", "-signature", "signature.bin", "-sigopt", "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:digest", "message.bin"));
        }
        finally { Directory.Delete(directory, true); }

        async Task<int> OpenSsl(params string[] args)
        {
            var start = new global::System.Diagnostics.ProcessStartInfo("openssl") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = global::System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            await output; await error; return process.ExitCode;
        }
    }

    [Fact]
    public void Private_certificate_import_and_wrapping_key_rotation_survive_restart_without_losing_mfa()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-cert-storage-" + Guid.NewGuid().ToString("N"));
        string caId, signerId, recovery, token;
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start();
                using var key = RSA.Create(3072);
                var request = new CertificateRequest("CN=Imported root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(3));
                caId = system.Certificates.Import("IMPORTED", root.Export(X509ContentType.Pkcs12, "FixturePass2"), "FixturePass2").Id;
                Assert.Throws<CryptographicException>(() => system.Certificates.Import("WRONGPASS", root.Export(X509ContentType.Pkcs12, "FixturePass2"), "wrong"));
                system.Certificates.SetTrust(caId, CertificatePurpose.ObjectSigning, true);
                signerId = system.Certificates.Issue("SIGNER", "Imported CA signer", caId, CertificatePurpose.ObjectSigning).Id;
                system.Security.Profiles.Create(new UserProfile { Name = "ALICE" });
                system.Security.Profiles.SetPassword(system.Security.Profiles.Get("ALICE"), "Password2");
                var enrollment = system.Security.BeginMfaEnrollment("ALICE", "Password2");
                var confirmation = system.Security.ConfirmMfaEnrollment(enrollment.EnrollmentToken,
                    TotpCode.Generate(MfaLifecycleTests.Decode(enrollment.SharedSecret), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                recovery = confirmation.RecoveryCodes[1];
                token = system.Security.OpenSession("ALICE", "Password2", confirmation.RecoveryCodes[0]).Session!.Token;
                var previous = Assert.Single(system.Certificates.WrappingKeys());
                var active = system.Certificates.RotateWrappingKey();
                Assert.NotEqual(previous.Id, active);
                var inventory = system.Certificates.WrappingKeys();
                Assert.Equal(0, inventory.Single(k => !k.Active).EncryptedValues);
                Assert.Equal(3, inventory.Single(k => k.Active).EncryptedValues);
                Assert.True(system.Security.ResumeSession(token).Success);
                using var connection = system.Connections.Open(); using var read = connection.CreateCommand();
                read.CommandText = "SELECT private_key FROM sys_certificates WHERE id=$id"; read.Parameters.AddWithValue("$id", caId);
                var envelope = (string)read.ExecuteScalar()!;
                Assert.DoesNotContain("PRIVATE KEY", envelope); Assert.DoesNotContain("FixturePass2", envelope);
                Assert.Equal(2, Directory.GetFiles(directory + "/system.db.keys", "*.key").Length);
            }
            using var restarted = IpcSystem.Create(directory); restarted.Start();
            Assert.True(restarted.Security.OpenSession("ALICE", "Password2", recovery).Success);
            var signature = restarted.ContentTrust.Sign(new byte[] { 1, 2 }, CertificatePurpose.ObjectSigning, "restart", signerId);
            restarted.ContentTrust.Verify(new byte[] { 1, 2 }, CertificatePurpose.ObjectSigning, "restart", signature);
            Assert.True(restarted.Security.ResumeSession(token).Success);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
