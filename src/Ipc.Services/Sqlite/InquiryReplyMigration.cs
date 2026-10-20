namespace Ipc.Services.Sqlite;

internal static class InquiryReplyMigration
{
    internal const string Sql = """
        ALTER TABLE sys_jobs ADD COLUMN inquiry_reply TEXT NOT NULL DEFAULT '*RQD' CHECK(inquiry_reply IN ('*RQD','*DFT','*SYSRPYL'));
        ALTER TABLE sys_job_descriptions ADD COLUMN inquiry_reply TEXT NOT NULL DEFAULT '*RQD' CHECK(inquiry_reply IN ('*RQD','*DFT','*SYSRPYL'));
        CREATE TABLE sys_reply_list (
            sequence INTEGER PRIMARY KEY CHECK(sequence BETWEEN 1 AND 9999),
            message_id TEXT NOT NULL, comparison BLOB, start INTEGER NOT NULL CHECK(start BETWEEN 1 AND 999),
            ccsid INTEGER NOT NULL, reply TEXT NOT NULL
        );
        """;
}
