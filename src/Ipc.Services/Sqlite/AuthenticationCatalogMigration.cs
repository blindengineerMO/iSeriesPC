namespace Ipc.Services.Sqlite;

internal static class AuthenticationCatalogMigration
{
    internal const string Sql = """
        CREATE TABLE sys_mfa (
            profile TEXT PRIMARY KEY REFERENCES sys_profiles(name) ON DELETE CASCADE,
            secret TEXT NOT NULL, revision TEXT NOT NULL, last_step INTEGER NOT NULL DEFAULT -1,
            failures INTEGER NOT NULL DEFAULT 0, locked_until INTEGER
        );
        CREATE TABLE sys_mfa_enrollments (
            token_hash TEXT PRIMARY KEY, profile TEXT NOT NULL REFERENCES sys_profiles(name) ON DELETE CASCADE,
            secret TEXT NOT NULL, credential_hash TEXT, mfa_revision TEXT,
            expires INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE sys_mfa_recovery (
            profile TEXT NOT NULL REFERENCES sys_mfa(profile) ON DELETE CASCADE,
            code_hash TEXT NOT NULL, PRIMARY KEY(profile,code_hash)
        );
        CREATE TABLE sys_auth_sessions (
            id TEXT PRIMARY KEY, token_hash TEXT NOT NULL UNIQUE,
            profile TEXT NOT NULL REFERENCES sys_profiles(name) ON DELETE CASCADE,
            credential_hash TEXT, mfa_revision TEXT, issued INTEGER NOT NULL,
            last_seen INTEGER NOT NULL, expires INTEGER NOT NULL, revoked INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ipc_auth_session_profile ON sys_auth_sessions(profile,revoked);
        ALTER TABLE sys_jobs ADD COLUMN auth_session TEXT;
        CREATE TRIGGER ipc_credentials_revoke_sessions AFTER UPDATE ON sys_profiles
        WHEN new.status<>'Enabled' OR old.password_hash IS NOT new.password_hash OR old.password_expires IS NOT new.password_expires BEGIN
            UPDATE sys_auth_sessions SET revoked=1 WHERE profile=new.name;
            DELETE FROM sys_mfa_enrollments WHERE profile=new.name;
        END;
        CREATE TRIGGER ipc_directory_revoke_sessions AFTER UPDATE ON sys_eim_mappings
        WHEN new.directory_state<>'Active' OR new.enabled=0 OR old.revision<>new.revision BEGIN
            UPDATE sys_auth_sessions SET revoked=1 WHERE profile=new.profile;
            DELETE FROM sys_mfa_enrollments WHERE profile=new.profile;
        END;
        CREATE TRIGGER ipc_mfa_insert_revoke_sessions AFTER INSERT ON sys_mfa BEGIN
            UPDATE sys_auth_sessions SET revoked=1 WHERE profile=new.profile;
        END;
        CREATE TRIGGER ipc_mfa_update_revoke_sessions AFTER UPDATE ON sys_mfa WHEN old.revision<>new.revision BEGIN
            UPDATE sys_auth_sessions SET revoked=1 WHERE profile=new.profile;
        END;
        CREATE TRIGGER ipc_mfa_delete_revoke_sessions AFTER DELETE ON sys_mfa BEGIN
            UPDATE sys_auth_sessions SET revoked=1 WHERE profile=old.profile;
        END;
        CREATE TRIGGER ipc_session_revoked_event AFTER UPDATE ON sys_auth_sessions WHEN old.revoked=0 AND new.revoked=1 BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.session.revoked',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('profile',new.profile,'session',new.id));
        END;
        """;
}
