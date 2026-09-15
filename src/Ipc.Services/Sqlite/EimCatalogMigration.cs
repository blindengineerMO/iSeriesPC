namespace Ipc.Services.Sqlite;

internal static class EimCatalogMigration
{
    internal const string Sql = """
        CREATE TABLE sys_eim_mappings (
            profile TEXT PRIMARY KEY REFERENCES sys_profiles(name) ON DELETE CASCADE,
            directory TEXT NOT NULL, distinguished_name TEXT NOT NULL,
            entry_id TEXT NOT NULL, principal TEXT NOT NULL UNIQUE,
            linux_account TEXT NOT NULL UNIQUE, revision TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
            directory_state TEXT NOT NULL DEFAULT 'Unknown', checked_at TEXT,
            UNIQUE(directory,entry_id), UNIQUE(directory,distinguished_name)
        );
        CREATE TRIGGER ipc_eim_insert_event AFTER INSERT ON sys_eim_mappings BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.eim.created',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('profile',new.profile,'directory',new.directory,'principal',new.principal));
        END;
        CREATE TRIGGER ipc_eim_update_event AFTER UPDATE ON sys_eim_mappings
        WHEN old.revision<>new.revision OR old.enabled<>new.enabled OR old.directory_state<>new.directory_state BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.eim.updated',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('profile',new.profile,'enabled',new.enabled,'state',new.directory_state));
        END;
        CREATE TRIGGER ipc_eim_delete_event AFTER DELETE ON sys_eim_mappings BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.eim.deleted',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('profile',old.profile,'directory',old.directory));
        END;
        """;
}
