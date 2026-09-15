namespace Ipc.Services.Sqlite;

internal static class ProgramMessageQueueMigration
{
    internal const string Sql = """
        CREATE TABLE sys_program_message_queues (
            id TEXT PRIMARY KEY,
            job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            program TEXT NOT NULL,
            parent TEXT REFERENCES sys_program_message_queues(id) ON DELETE CASCADE,
            active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
            external INTEGER NOT NULL DEFAULT 0 CHECK(external IN (0,1))
        );
        CREATE UNIQUE INDEX ix_program_message_external ON sys_program_message_queues(job_number) WHERE external=1;
        CREATE INDEX ix_program_message_job ON sys_program_message_queues(job_number,active);
        CREATE TRIGGER ipc_program_message_job_end AFTER UPDATE OF execution_state ON sys_jobs
        WHEN NEW.execution_state NOT IN ('Queued','Running')
        BEGIN UPDATE sys_program_message_queues SET active=0 WHERE job_number=NEW.number; END;
        CREATE TABLE sys_message_entries_v20 (
            key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(key BETWEEN 1 AND 4294967295),
            queue_lib TEXT, queue_name TEXT,
            queue_type TEXT DEFAULT '*MSGQ' CHECK(queue_type='*MSGQ'),
            program_queue TEXT REFERENCES sys_program_message_queues(id) ON DELETE CASCADE,
            kind TEXT NOT NULL CHECK(kind IN ('INFO','INQ','RPY','COMP','DIAG','STATUS','ESCAPE','NOTIFY','COPY')),
            message_id TEXT NOT NULL DEFAULT '', severity INTEGER NOT NULL CHECK(severity BETWEEN 0 AND 99),
            data BLOB NOT NULL CHECK(length(data)<=4096), ccsid INTEGER NOT NULL,
            sender TEXT NOT NULL, sender_job INTEGER, sent TEXT NOT NULL,
            seen INTEGER NOT NULL DEFAULT 0 CHECK(seen IN (0,1)),
            replied INTEGER NOT NULL DEFAULT 0 CHECK(replied IN (0,1)),
            reply_lib TEXT, reply_name TEXT, reply_type TEXT CHECK(reply_type='*MSGQ'),
            reply_program_queue TEXT REFERENCES sys_program_message_queues(id) ON DELETE SET NULL,
            default_reply BLOB NOT NULL DEFAULT X'' CHECK(length(default_reply)<=132),
            correlation_key INTEGER,
            CHECK((queue_lib IS NOT NULL AND queue_name IS NOT NULL AND queue_type IS NOT NULL AND queue_type='*MSGQ' AND program_queue IS NULL)
                OR (queue_lib IS NULL AND queue_name IS NULL AND queue_type IS NULL AND program_queue IS NOT NULL)),
            CHECK(reply_program_queue IS NULL OR (reply_lib IS NULL AND reply_name IS NULL AND reply_type IS NULL)),
            FOREIGN KEY(queue_lib,queue_name,queue_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY(reply_lib,reply_name,reply_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE SET NULL
        );
        INSERT INTO sys_message_entries_v20(key,queue_lib,queue_name,queue_type,kind,message_id,severity,data,ccsid,sender,sender_job,sent,seen,replied,reply_lib,reply_name,reply_type,default_reply,correlation_key)
        SELECT key,queue_lib,queue_name,queue_type,kind,message_id,severity,data,ccsid,sender,sender_job,sent,seen,replied,reply_lib,reply_name,reply_type,default_reply,correlation_key FROM sys_message_entries;
        UPDATE sqlite_sequence SET seq=max(seq,coalesce((SELECT seq FROM sqlite_sequence WHERE name='sys_message_entries'),0)) WHERE name='sys_message_entries_v20';
        DROP TABLE sys_message_entries;
        ALTER TABLE sys_message_entries_v20 RENAME TO sys_message_entries;
        CREATE INDEX ix_message_queue_key ON sys_message_entries(queue_lib,queue_name,key);
        CREATE INDEX ix_message_queue_reply ON sys_message_entries(queue_lib,queue_name,correlation_key);
        CREATE INDEX ix_message_program_key ON sys_message_entries(program_queue,key);
        CREATE INDEX ix_message_program_reply ON sys_message_entries(program_queue,correlation_key);
        """;
}
