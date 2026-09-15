using System.Security.Cryptography;
using System.Text;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class MfaCryptographyTests
{
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_matches_rfc_6238_sha1_reference_vectors(long seconds, string expected)
    {
        Assert.Equal(expected, TotpCode.Generate(Encoding.ASCII.GetBytes("12345678901234567890"), seconds, digits: 8));
    }

    [Fact]
    public void Totp_verification_bounds_clock_skew_and_rejects_replayed_steps()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var current = TotpCode.Generate(secret, now.ToUnixTimeSeconds());
        Assert.Equal(now.ToUnixTimeSeconds() / 30, TotpCode.Verify(secret, current, now));
        Assert.Null(TotpCode.Verify(secret, current, now, now.ToUnixTimeSeconds() / 30));
        Assert.NotNull(TotpCode.Verify(secret, TotpCode.Generate(secret, now.ToUnixTimeSeconds() - 30), now));
        Assert.Null(TotpCode.Verify(secret, TotpCode.Generate(secret, now.ToUnixTimeSeconds() - 60), now));
        Assert.Null(TotpCode.Verify(secret, "12 456", now));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpCode.EncodeBase32(secret));
    }

    [Fact]
    public void Encrypted_secrets_are_bound_to_purpose_and_reject_tampering_or_another_key_ring()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        using var protector = new SecretProtector(factory);
        var secret = RandomNumberGenerator.GetBytes(20);
        var envelope = protector.Protect("totp/ALICE", secret);
        Assert.Equal(secret, protector.Unprotect("totp/ALICE", envelope));
        Assert.DoesNotContain(Convert.ToBase64String(secret), envelope);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("totp/BOB", envelope));
        var parts = envelope.Split('.');
        var ciphertext = Convert.FromBase64String(parts[3]);
        ciphertext[0] ^= 1;
        parts[3] = Convert.ToBase64String(ciphertext);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("totp/ALICE", string.Join('.', parts)));
        using var other = new SecretProtector(factory);
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect("totp/ALICE", envelope));
    }

    [Fact]
    public void Disk_key_ring_survives_restart_and_fails_closed_if_a_key_is_missing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-keyring-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new SqliteConnectionFactory(directory);
            var secret = RandomNumberGenerator.GetBytes(20);
            string envelope;
            using (var first = new SecretProtector(factory)) envelope = first.Protect("totp/ALICE", secret);
            using var restarted = new SecretProtector(factory);
            Assert.Equal(secret, restarted.Unprotect("totp/ALICE", envelope));
            var keyDirectory = factory.DatabasePath + ".keys";
            var key = Directory.GetFiles(keyDirectory, "*.key").Single();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(keyDirectory));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
                File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                Assert.ThrowsAny<CryptographicException>(() => restarted.Unprotect("totp/ALICE", envelope));
                File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Delete(key);
            Assert.ThrowsAny<CryptographicException>(() => restarted.Unprotect("totp/ALICE", envelope));
            Assert.Empty(Directory.GetFiles(keyDirectory, "*.key"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
