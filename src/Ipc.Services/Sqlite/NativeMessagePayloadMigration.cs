namespace Ipc.Services.Sqlite;

internal static class NativeMessagePayloadMigration
{
    internal const string Sql = """
        CREATE TABLE sys_message_entries_v27 (
            key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(key BETWEEN 1 AND 4294967295),
            queue_lib TEXT, queue_name TEXT,
            queue_type TEXT DEFAULT '*MSGQ' CHECK(queue_type='*MSGQ'),
            program_queue TEXT REFERENCES sys_program_message_queues(id) ON DELETE CASCADE,
            kind TEXT NOT NULL CHECK(kind IN ('INFO','INQ','RPY','COMP','DIAG','STATUS','ESCAPE','NOTIFY','COPY')),
            message_id TEXT NOT NULL DEFAULT '', severity INTEGER NOT NULL CHECK(severity BETWEEN 0 AND 99),
            data BLOB NOT NULL CHECK(length(data)<=6000), ccsid INTEGER NOT NULL,
            sender TEXT NOT NULL, sender_job INTEGER, sent TEXT NOT NULL,
            seen INTEGER NOT NULL DEFAULT 0 CHECK(seen IN (0,1)),
            replied INTEGER NOT NULL DEFAULT 0 CHECK(replied IN (0,1)),
            reply_lib TEXT, reply_name TEXT, reply_type TEXT CHECK(reply_type='*MSGQ'),
            reply_program_queue TEXT REFERENCES sys_program_message_queues(id) ON DELETE SET NULL,
            default_reply BLOB NOT NULL DEFAULT X'' CHECK(length(default_reply)<=132),
            correlation_key INTEGER,
            exception_handled INTEGER NOT NULL DEFAULT 0 CHECK(exception_handled IN (0,1)),
            reply_type_code TEXT NOT NULL DEFAULT '21' CHECK(reply_type_code IN ('21','22','23','24','25','26')),
            default_reply_code TEXT NOT NULL DEFAULT '24' CHECK(default_reply_code IN ('23','24')),
            sender_job_name TEXT NOT NULL DEFAULT '' CHECK(length(sender_job_name)<=10),
            sender_job_user TEXT NOT NULL DEFAULT '' CHECK(length(sender_job_user)<=10),
            sender_program TEXT NOT NULL DEFAULT '' CHECK(length(sender_program)<=12),
            recipient_program TEXT NOT NULL DEFAULT '' CHECK(length(recipient_program)<=10),
            origin_sent TEXT NOT NULL DEFAULT '',
            predefined TEXT NOT NULL DEFAULT '' CHECK(length(CAST(predefined AS BLOB))<=32767),
            CHECK((queue_lib IS NOT NULL AND queue_name IS NOT NULL AND queue_type IS NOT NULL AND queue_type='*MSGQ' AND program_queue IS NULL)
                OR (queue_lib IS NULL AND queue_name IS NULL AND queue_type IS NULL AND program_queue IS NOT NULL)),
            CHECK(reply_program_queue IS NULL OR (reply_lib IS NULL AND reply_name IS NULL AND reply_type IS NULL)),
            FOREIGN KEY(queue_lib,queue_name,queue_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY(reply_lib,reply_name,reply_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE SET NULL
        );
        INSERT INTO sys_message_entries_v27 SELECT * FROM sys_message_entries;
        INSERT INTO sqlite_sequence(name,seq) SELECT 'sys_message_entries_v27',0
            WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='sys_message_entries_v27');
        UPDATE sqlite_sequence SET seq=max(seq,coalesce((SELECT seq FROM sqlite_sequence WHERE name='sys_message_entries'),0))
            WHERE name='sys_message_entries_v27';
        DROP TABLE sys_message_entries;
        ALTER TABLE sys_message_entries_v27 RENAME TO sys_message_entries;
        CREATE INDEX ix_message_queue_key ON sys_message_entries(queue_lib,queue_name,key);
        CREATE INDEX ix_message_queue_reply ON sys_message_entries(queue_lib,queue_name,correlation_key);
        CREATE INDEX ix_message_program_key ON sys_message_entries(program_queue,key);
        CREATE INDEX ix_message_program_reply ON sys_message_entries(program_queue,correlation_key);
        CREATE TABLE sys_message_receivers (
            lib TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL DEFAULT '*MSGQ' CHECK(type='*MSGQ'),
            job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            token TEXT NOT NULL UNIQUE,
            PRIMARY KEY(lib,name),
            FOREIGN KEY(lib,name,type) REFERENCES sys_objects(lib,name,type) ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE TRIGGER ipc_message_receiver_job_end AFTER UPDATE OF execution_state ON sys_jobs
        WHEN NEW.execution_state NOT IN ('Queued','Running')
        BEGIN DELETE FROM sys_message_receivers WHERE job_number=NEW.number; END;
        """;
}
