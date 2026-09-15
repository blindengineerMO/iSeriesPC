using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public sealed record SessionCredential(string Id, string Token, string Profile, DateTimeOffset Expires);

/// <summary>Opaque bearer credentials, stored only as hashes. All validity checks read current catalog state.</summary>
internal sealed class SessionTokenStore(SqliteConnectionFactory factory, TimeProvider clock)
{
    private const string Active = """
        s.revoked=0 AND s.expires>$now AND s.last_seen>$idle AND s.issued<=$now
        AND EXISTS(SELECT 1 FROM sys_profiles p WHERE p.name=s.profile AND p.status='Enabled'
          AND p.password_hash IS s.credential_hash
          AND (p.password_expires IS NULL OR julianday(p.password_expires)>julianday($now,'unixepoch')))
        AND s.mfa_revision IS (SELECT revision FROM sys_mfa WHERE profile=s.profile)
        AND NOT EXISTS(SELECT 1 FROM sys_eim_mappings e WHERE e.profile=s.profile AND
          (e.enabled=0 OR e.directory_state<>'Active' OR e.checked_at IS NULL
           OR julianday(e.checked_at)<julianday($now,'unixepoch')-60.0/86400
           OR julianday(e.checked_at)>julianday($now,'unixepoch')+5.0/86400))
        """;

    internal SessionCredential Issue(UserProfile proof, string? mfaRevision)
    {
        var now = clock.GetUtcNow();
        var token = MfaStore.NewToken();
        var id = Guid.NewGuid().ToString("N");
        var expires = now.AddHours(8);
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM sys_auth_sessions WHERE expires<=$now OR revoked=1 OR last_seen<=$idle;
            INSERT INTO sys_auth_sessions(id,token_hash,profile,credential_hash,mfa_revision,issued,last_seen,expires)
            SELECT $id,$hash,p.name,p.password_hash,$mfa,$now,$now,$expires FROM sys_profiles p
            WHERE p.name=$profile AND p.status='Enabled' AND p.password_hash IS $credential
              AND (p.password_expires IS NULL OR julianday(p.password_expires)>julianday($now,'unixepoch'))
              AND $mfa IS (SELECT revision FROM sys_mfa WHERE profile=p.name)
              AND (SELECT count(*) FROM sys_auth_sessions WHERE profile=p.name)<32;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hash", MfaStore.Hash(token));
        command.Parameters.AddWithValue("$profile", proof.Name);
        command.Parameters.AddWithValue("$credential", (object?)proof.PasswordHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$mfa", (object?)mfaRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$idle", now.AddMinutes(-30).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        using var validate = connection.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = "SELECT count(*) FROM sys_auth_sessions s WHERE s.id=$id AND " + Active;
        validate.Parameters.AddWithValue("$id", id);
        validate.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        validate.Parameters.AddWithValue("$idle", now.AddMinutes(-30).ToUnixTimeSeconds());
        if (Convert.ToInt64(validate.ExecuteScalar()) != 1) throw new CpfException("IPC0101", "Authentication changed or session limit reached.");
        MfaStore.Audit(connection, transaction, "security.session.issued", proof.Name, true, now);
        transaction.Commit();
        return new(id, token, proof.Name, expires);
    }

    internal SessionCredential? Resolve(string token)
    {
        if (token.Length != 43) return null;
        var now = clock.GetUtcNow();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_auth_sessions AS s SET last_seen=$now WHERE token_hash=$hash AND " + Active + " RETURNING id,profile,expires";
        command.Parameters.AddWithValue("$hash", MfaStore.Hash(token));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$idle", now.AddMinutes(-30).ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), token, reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))) : null;
    }

    internal bool IsActive(string id, string profile, bool touch = false)
    {
        var now = clock.GetUtcNow();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = (touch ? "UPDATE sys_auth_sessions AS s SET last_seen=$now WHERE " : "SELECT count(*) FROM sys_auth_sessions s WHERE ") +
            "id=$id AND profile=$profile AND " + Active;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$idle", now.AddMinutes(-30).ToUnixTimeSeconds());
        return (touch ? command.ExecuteNonQuery() : Convert.ToInt64(command.ExecuteScalar())) == 1;
    }

    internal void RevokeToken(string token)
    {
        if (token.Length != 43) return;
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_auth_sessions SET revoked=1 WHERE token_hash=$hash";
        command.Parameters.AddWithValue("$hash", MfaStore.Hash(token));
        command.ExecuteNonQuery();
    }
}
