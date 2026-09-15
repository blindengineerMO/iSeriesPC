using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

public sealed record MfaEnrollment(string EnrollmentToken, string SharedSecret, string AuthenticatorUri, DateTimeOffset Expires);
public sealed record MfaConfirmation(string Profile, IReadOnlyList<string> RecoveryCodes);
internal enum MfaVerification { Accepted, Rejected, Locked, Unavailable }

/// <summary>Credential-proof admission belongs to SecurityService; this store is not a transport API.</summary>
internal sealed class MfaStore(SqliteConnectionFactory factory, SecretProtector protector, TimeProvider clock)
{
    internal string? Revision(string profile)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM sys_mfa WHERE profile=$profile";
        command.Parameters.AddWithValue("$profile", profile);
        return command.ExecuteScalar() as string;
    }

    internal MfaEnrollment Begin(UserProfile proof, string issuer, string? mfaRevision = null)
    {
        var now = clock.GetUtcNow();
        var secret = RandomNumberGenerator.GetBytes(20);
        var token = NewToken();
        var expires = now.AddMinutes(10);
        try
        {
            var envelope = protector.Protect("totp/" + proof.Name, secret);
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM sys_mfa_enrollments WHERE profile=$profile OR expires<=$now;
                INSERT INTO sys_mfa_enrollments(token_hash,profile,secret,credential_hash,mfa_revision,expires)
                SELECT $token,name,$secret,password_hash,$mfa,$expires
                FROM sys_profiles WHERE name=$profile AND password_hash IS $credential AND status='Enabled'
                  AND (password_expires IS NULL OR julianday(password_expires)>julianday($now,'unixepoch'))
                  AND $mfa IS (SELECT revision FROM sys_mfa WHERE profile=name);
                """;
            command.Parameters.AddWithValue("$profile", proof.Name);
            command.Parameters.AddWithValue("$credential", (object?)proof.PasswordHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$mfa", (object?)mfaRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$token", Hash(token));
            command.Parameters.AddWithValue("$secret", envelope);
            command.Parameters.AddWithValue("$expires", expires.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT count(*) FROM sys_mfa_enrollments WHERE token_hash=$token";
            exists.Parameters.AddWithValue("$token", Hash(token));
            if (Convert.ToInt64(exists.ExecuteScalar()) != 1) throw new CpfException("IPC0101", "Credentials changed; authenticate again.");
            Audit(connection, transaction, "security.mfa.enrollment.started", proof.Name, true, now);
            transaction.Commit();
            var shared = TotpCode.EncodeBase32(secret);
            return new(token, shared, "otpauth://totp/" + Uri.EscapeDataString(issuer + ":" + proof.Name) +
                "?secret=" + shared + "&issuer=" + Uri.EscapeDataString(issuer) + "&algorithm=SHA1&digits=6&period=30", expires);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal MfaConfirmation Confirm(string enrollmentToken, string code)
    {
        if (enrollmentToken.Length != 43) throw Denied();
        var now = clock.GetUtcNow();
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string profile, envelope;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT e.profile,e.secret FROM sys_mfa_enrollments e JOIN sys_profiles p ON p.name=e.profile
                WHERE e.token_hash=$token AND e.expires>$now AND e.attempts<5 AND p.status='Enabled'
                  AND p.password_hash IS e.credential_hash
                  AND (p.password_expires IS NULL OR julianday(p.password_expires)>julianday($now,'unixepoch'))
                  AND e.mfa_revision IS (SELECT revision FROM sys_mfa WHERE profile=e.profile)
                """;
            select.Parameters.AddWithValue("$token", Hash(enrollmentToken));
            select.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            using var reader = select.ExecuteReader();
            if (!reader.Read()) throw Denied();
            profile = reader.GetString(0);
            envelope = reader.GetString(1);
        }
        if (OperationIdentity.Current is { } caller && caller.Principal != profile) throw Denied();
        if (new EimMappingStore(factory).GetInternal(profile) is { } mapping && !mapping.AllowsExecution(now)) throw Denied();
        var secret = protector.Unprotect("totp/" + profile, envelope);
        long? step;
        try { step = TotpCode.Verify(secret, code, now); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        if (step is null)
        {
            using var fail = connection.CreateCommand();
            fail.Transaction = transaction;
            fail.CommandText = "UPDATE sys_mfa_enrollments SET attempts=attempts+1 WHERE token_hash=$token";
            fail.Parameters.AddWithValue("$token", Hash(enrollmentToken));
            fail.ExecuteNonQuery();
            Audit(connection, transaction, "security.mfa.enrollment.confirmed", profile, false, now);
            transaction.Commit();
            throw Denied();
        }
        using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                INSERT INTO sys_mfa(profile,secret,revision,last_step) VALUES($profile,$secret,$revision,$step)
                ON CONFLICT(profile) DO UPDATE SET secret=excluded.secret,revision=excluded.revision,
                    last_step=excluded.last_step,failures=0,locked_until=NULL;
                DELETE FROM sys_mfa_enrollments WHERE profile=$profile;
                DELETE FROM sys_mfa_recovery WHERE profile=$profile;
                """;
            activate.Parameters.AddWithValue("$profile", profile);
            activate.Parameters.AddWithValue("$secret", envelope);
            activate.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
            activate.Parameters.AddWithValue("$step", step.Value);
            activate.ExecuteNonQuery();
        }
        var codes = Enumerable.Range(0, 8).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16))).ToArray();
        foreach (var recovery in codes)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO sys_mfa_recovery(profile,code_hash) VALUES($profile,$hash)";
            insert.Parameters.AddWithValue("$profile", profile);
            insert.Parameters.AddWithValue("$hash", Hash(recovery));
            insert.ExecuteNonQuery();
        }
        Audit(connection, transaction, "security.mfa.enrollment.confirmed", profile, true, now);
        transaction.Commit();
        return new(profile, Array.AsReadOnly(codes));
    }

    internal MfaVerification Verify(string profile, string code, string revision)
    {
        var now = clock.GetUtcNow();
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string envelope;
        long lastStep, failures, lockedUntil;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT secret,last_step,failures,coalesce(locked_until,0) FROM sys_mfa WHERE profile=$profile AND revision=$revision";
            select.Parameters.AddWithValue("$profile", profile);
            select.Parameters.AddWithValue("$revision", revision);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) return MfaVerification.Rejected;
            envelope = reader.GetString(0); lastStep = reader.GetInt64(1); failures = reader.GetInt64(2); lockedUntil = reader.GetInt64(3);
        }
        var recovery = false;
        if (code.Length == 32 && code.All(char.IsAsciiHexDigit))
        {
            using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = "DELETE FROM sys_mfa_recovery WHERE profile=$profile AND code_hash=$hash";
            consume.Parameters.AddWithValue("$profile", profile);
            consume.Parameters.AddWithValue("$hash", Hash(code.ToUpperInvariant()));
            recovery = consume.ExecuteNonQuery() == 1;
        }
        if (!recovery && lockedUntil > now.ToUnixTimeSeconds()) return MfaVerification.Locked;
        long? step = null;
        if (!recovery)
        {
            try
            {
                var secret = protector.Unprotect("totp/" + profile, envelope);
                try { step = TotpCode.Verify(secret, code, now, lastStep); }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                Audit(connection, transaction, "security.mfa.unavailable", profile, false, now);
                transaction.Commit();
                return MfaVerification.Unavailable;
            }
        }
        var success = recovery || step is not null;
        failures = lockedUntil > 0 ? 0 : failures;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE sys_mfa SET last_step=$step,failures=$failures,locked_until=$locked WHERE profile=$profile";
        update.Parameters.AddWithValue("$profile", profile);
        update.Parameters.AddWithValue("$step", step ?? lastStep);
        update.Parameters.AddWithValue("$failures", success ? 0 : failures + 1);
        update.Parameters.AddWithValue("$locked", !success && failures + 1 >= 5 ? (object)now.AddMinutes(15).ToUnixTimeSeconds() : DBNull.Value);
        update.ExecuteNonQuery();
        Audit(connection, transaction, recovery ? "security.mfa.recovery.used" : "security.mfa.verified", profile, success, now);
        transaction.Commit();
        return success ? MfaVerification.Accepted : MfaVerification.Rejected;
    }

    internal void Disable(UserProfile proof, string? revision)
    {
        var profile = proof.Name;
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM sys_mfa WHERE profile=$profile AND revision IS $revision
              AND EXISTS(SELECT 1 FROM sys_profiles WHERE name=$profile AND status='Enabled' AND password_hash IS $credential);
            """;
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$credential", (object?)proof.PasswordHash ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0) throw Denied();
        command.CommandText = "DELETE FROM sys_mfa_enrollments WHERE profile=$profile";
        command.ExecuteNonQuery();
        Audit(connection, transaction, "security.mfa.disabled", profile, true, clock.GetUtcNow());
        transaction.Commit();
    }

    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static CpfException Denied() => new("IPC0102", "MFA verification failed or enrollment expired.");

    internal static void Audit(SqliteConnection connection, SqliteTransaction transaction, string kind, string profile, bool success, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sys_events(at,kind,principal,job,payload) VALUES($at,$kind,$principal,$job,$payload)";
        command.Parameters.AddWithValue("$at", now.ToString("o"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$principal", OperationIdentity.Current?.Principal ?? (success ? profile : "*UNAUTHENTICATED"));
        command.Parameters.AddWithValue("$job", (object?)OperationIdentity.Current?.Job?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { profile, success }));
        command.ExecuteNonQuery();
    }
}
