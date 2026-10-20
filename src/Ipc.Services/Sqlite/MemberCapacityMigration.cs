namespace Ipc.Services.Sqlite;

internal static class MemberCapacityMigration
{
    internal const string Sql = """
        CREATE TABLE sys_member_size_waits (
            reply_queue TEXT PRIMARY KEY REFERENCES sys_program_message_queues(id) ON DELETE CASCADE
        );
        CREATE TRIGGER ipc_member_size_wait_end AFTER UPDATE OF execution_state ON sys_jobs
        WHEN NEW.execution_state NOT IN ('Queued','Running') BEGIN
            UPDATE sys_message_entries SET replied=1 WHERE kind='INQ' AND reply_program_queue IN
                (SELECT w.reply_queue FROM sys_member_size_waits w JOIN sys_program_message_queues q ON q.id=w.reply_queue WHERE q.job_number=NEW.number);
            DELETE FROM sys_program_message_queues WHERE job_number=NEW.number AND id IN(SELECT reply_queue FROM sys_member_size_waits);
        END;
        CREATE TABLE sys_member_capacity (
            member_id TEXT PRIMARY KEY REFERENCES sys_record_slots(id) ON DELETE CASCADE,
            definition_signature TEXT NOT NULL,
            capacity INTEGER NOT NULL CHECK(capacity BETWEEN 1 AND 4294967288)
        );
        """;
}
