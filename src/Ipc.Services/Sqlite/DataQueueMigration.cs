namespace Ipc.Services.Sqlite;

internal static class DataQueueMigration
{
    internal const string Sql = """
        CREATE TABLE sys_data_queue_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            queue_lib TEXT NOT NULL, queue_name TEXT NOT NULL,
            queue_type TEXT NOT NULL DEFAULT '*DTAQ' CHECK(queue_type='*DTAQ'),
            queue_identity TEXT NOT NULL,
            key_data BLOB NOT NULL CHECK(length(key_data)<=256),
            data BLOB NOT NULL CHECK(length(data) BETWEEN 1 AND 64512),
            ccsid INTEGER NOT NULL, sent TEXT NOT NULL,
            sender TEXT, sender_job INTEGER, sender_job_name TEXT, sender_job_user TEXT,
            FOREIGN KEY(queue_lib,queue_name,queue_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE INDEX ix_data_queue_fifo ON sys_data_queue_entries(queue_lib,queue_name,id);
        CREATE INDEX ix_data_queue_key ON sys_data_queue_entries(queue_lib,queue_name,key_data,id);
        """;
}
