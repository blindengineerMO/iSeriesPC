namespace Ipc.Services.Sqlite;

internal static class MessageSenderMigration
{
    internal const string Sql = """
        ALTER TABLE sys_message_entries ADD COLUMN sender_job_name TEXT NOT NULL DEFAULT '' CHECK(length(sender_job_name)<=10);
        ALTER TABLE sys_message_entries ADD COLUMN sender_job_user TEXT NOT NULL DEFAULT '' CHECK(length(sender_job_user)<=10);
        ALTER TABLE sys_message_entries ADD COLUMN sender_program TEXT NOT NULL DEFAULT '' CHECK(length(sender_program)<=12);
        ALTER TABLE sys_message_entries ADD COLUMN recipient_program TEXT NOT NULL DEFAULT '' CHECK(length(recipient_program)<=10);
        ALTER TABLE sys_message_entries ADD COLUMN origin_sent TEXT NOT NULL DEFAULT '';
        UPDATE sys_message_entries SET
          sender_job_name=coalesce((SELECT name FROM sys_jobs WHERE number=sender_job),''),
          sender_job_user=coalesce((SELECT user FROM sys_jobs WHERE number=sender_job),''),
          recipient_program=coalesce((SELECT program FROM sys_program_message_queues WHERE id=program_queue AND external=0),''),
          origin_sent=sent;
        """;
}
