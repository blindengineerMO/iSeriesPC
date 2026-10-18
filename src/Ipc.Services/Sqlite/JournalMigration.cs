namespace Ipc.Services.Sqlite;

internal static class JournalMigration
{
    internal const string Sql = """
        CREATE TABLE sys_journals (
            id TEXT PRIMARY KEY, library TEXT NOT NULL, name TEXT NOT NULL,
            type TEXT NOT NULL DEFAULT '*JRN' CHECK(type='*JRN'),
            current_receiver TEXT REFERENCES sys_journal_receivers(id) ON DELETE RESTRICT,
            sequence INTEGER NOT NULL DEFAULT 0 CHECK(typeof(sequence)='integer' AND sequence>=0),
            receiver_chain INTEGER NOT NULL DEFAULT 0 CHECK(receiver_chain>=0),
            UNIQUE(library,name),
            FOREIGN KEY(library,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_journal_receivers (
            id TEXT PRIMARY KEY, library TEXT NOT NULL, name TEXT NOT NULL,
            type TEXT NOT NULL DEFAULT '*JRNRCV' CHECK(type='*JRNRCV'),
            journal_id TEXT REFERENCES sys_journals(id) ON DELETE SET NULL,
            origin_journal TEXT, origin_id TEXT, chain INTEGER, attached_at TEXT, detached_at TEXT,
            first_sequence INTEGER, last_sequence INTEGER,
            bytes INTEGER NOT NULL DEFAULT 0 CHECK(bytes>=0),
            threshold_kb INTEGER CHECK(threshold_kb BETWEEN 100000 AND 1000000000),
            saved INTEGER NOT NULL DEFAULT 0 CHECK(saved IN (0,1)),
            UNIQUE(library,name),
            FOREIGN KEY(library,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE INDEX ipc_journal_receiver_chain ON sys_journal_receivers(journal_id,attached_at);
        CREATE INDEX ipc_journal_receiver_origin ON sys_journal_receivers(origin_id,chain);
        CREATE TABLE sys_journal_bindings (
            id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES sys_journals(id) ON DELETE RESTRICT,
            library TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL DEFAULT '*FILE' CHECK(type='*FILE'),
            images TEXT NOT NULL CHECK(images IN ('*AFTER','*BOTH')),
            UNIQUE(library,name),
            FOREIGN KEY(library,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_journal_triggers (
            name TEXT PRIMARY KEY, binding_id TEXT NOT NULL REFERENCES sys_journal_bindings(id) ON DELETE CASCADE,
            library TEXT NOT NULL, file TEXT NOT NULL, type TEXT NOT NULL DEFAULT '*FILE' CHECK(type='*FILE'), member TEXT NOT NULL,
            FOREIGN KEY(library,file,type,member) REFERENCES sys_file_members(lib,name,type,mbr) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_journal_entries (
            journal_id TEXT NOT NULL, sequence INTEGER NOT NULL CHECK(sequence>0),
            receiver_id TEXT NOT NULL REFERENCES sys_journal_receivers(id) ON DELETE CASCADE,
            version INTEGER NOT NULL DEFAULT 1 CHECK(version=1),
            code TEXT NOT NULL CHECK(length(code)=1), entry_type TEXT NOT NULL CHECK(length(entry_type)=2),
            captured_at TEXT NOT NULL, principal TEXT, job TEXT,
            object_library TEXT, object_name TEXT, member TEXT, record_number INTEGER, binding_id TEXT,
            schema_description TEXT, before_image TEXT, after_image TEXT, user_data BLOB,
            PRIMARY KEY(journal_id,sequence)
        ) WITHOUT ROWID;
        CREATE INDEX ipc_journal_entry_receiver ON sys_journal_entries(receiver_id,sequence);
        CREATE TRIGGER ipc_journal_detach_on_delete BEFORE DELETE ON sys_journals
        BEGIN
            UPDATE sys_journal_receivers SET detached_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=OLD.current_receiver;
        END;
        CREATE TRIGGER ipc_journal_receiver_delete BEFORE DELETE ON sys_journal_receivers
        BEGIN
            SELECT CASE WHEN EXISTS(SELECT 1 FROM sys_journals WHERE current_receiver=OLD.id)
                THEN RAISE(ABORT,'IPC0210: Attached journal receivers cannot be deleted') END;
            SELECT CASE WHEN EXISTS(SELECT 1 FROM sys_journal_receivers WHERE origin_id=OLD.origin_id AND chain<OLD.chain)
                THEN RAISE(ABORT,'IPC0210: Delete receivers from the oldest end of the chain') END;
            SELECT CASE WHEN OLD.attached_at IS NOT NULL AND OLD.saved=0 AND ipc_journal_force_delete()=0
                THEN RAISE(ABORT,'IPC0210: Receiver has not been saved; explicit deletion acknowledgement is required') END;
        END;
        CREATE TRIGGER ipc_journal_entry_bound BEFORE INSERT ON sys_journal_entries
        BEGIN
            SELECT CASE WHEN coalesce(length(CAST(NEW.before_image AS BLOB)),0)+coalesce(length(CAST(NEW.after_image AS BLOB)),0)+coalesce(length(NEW.user_data),0)>16777216
                THEN RAISE(ABORT,'IPC0210: Journal entry exceeds the 16 MiB runtime limit') END;
            SELECT CASE WHEN (SELECT bytes FROM sys_journal_receivers WHERE id=NEW.receiver_id)
                +coalesce(length(CAST(NEW.before_image AS BLOB)),0)+coalesce(length(CAST(NEW.after_image AS BLOB)),0)
                +coalesce(length(CAST(NEW.schema_description AS BLOB)),0)+coalesce(length(NEW.user_data),0)+256>1073741824
                THEN RAISE(ABORT,'IPC0210: Journal receiver exceeds the 1 GiB runtime limit; rotate it') END;
        END;
        CREATE TRIGGER ipc_journal_entry_account AFTER INSERT ON sys_journal_entries
        BEGIN
            UPDATE sys_journal_receivers SET first_sequence=coalesce(first_sequence,NEW.sequence),last_sequence=NEW.sequence,saved=0,
                bytes=bytes+coalesce(length(CAST(NEW.before_image AS BLOB)),0)+coalesce(length(CAST(NEW.after_image AS BLOB)),0)
                +coalesce(length(CAST(NEW.schema_description AS BLOB)),0)+coalesce(length(NEW.user_data),0)+256
                WHERE id=NEW.receiver_id;
            UPDATE sys_objects SET changed=NEW.captured_at WHERE type='*JRNRCV' AND (lib,name) IN
                (SELECT library,name FROM sys_journal_receivers WHERE id=NEW.receiver_id);
            UPDATE sys_objects SET changed=NEW.captured_at WHERE type='*JRN' AND (lib,name) IN
                (SELECT library,name FROM sys_journals WHERE id=NEW.journal_id);
        END;
        """;
}
