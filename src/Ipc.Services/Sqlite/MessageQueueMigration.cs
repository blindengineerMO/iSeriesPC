namespace Ipc.Services.Sqlite;

internal static class MessageQueueMigration
{
    internal const string Sql = """
        CREATE TABLE sys_message_entries (
            key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(key BETWEEN 1 AND 4294967295),
            queue_lib TEXT NOT NULL, queue_name TEXT NOT NULL,
            queue_type TEXT NOT NULL DEFAULT '*MSGQ' CHECK(queue_type='*MSGQ'),
            kind TEXT NOT NULL CHECK(kind IN ('INFO','INQ','RPY','COMP','DIAG','STATUS','ESCAPE','NOTIFY')),
            message_id TEXT NOT NULL DEFAULT '', severity INTEGER NOT NULL CHECK(severity BETWEEN 0 AND 99),
            data BLOB NOT NULL CHECK(length(data)<=4096), ccsid INTEGER NOT NULL,
            sender TEXT NOT NULL, sender_job INTEGER, sent TEXT NOT NULL,
            seen INTEGER NOT NULL DEFAULT 0 CHECK(seen IN (0,1)),
            replied INTEGER NOT NULL DEFAULT 0 CHECK(replied IN (0,1)),
            reply_lib TEXT, reply_name TEXT, reply_type TEXT CHECK(reply_type='*MSGQ'),
            default_reply BLOB NOT NULL DEFAULT X'' CHECK(length(default_reply)<=132),
            correlation_key INTEGER,
            FOREIGN KEY(queue_lib,queue_name,queue_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY(reply_lib,reply_name,reply_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE SET NULL
        );
        CREATE INDEX ix_message_queue_key ON sys_message_entries(queue_lib,queue_name,key);
        CREATE INDEX ix_message_queue_reply ON sys_message_entries(queue_lib,queue_name,correlation_key);
        """;
}
