namespace Ipc.Services.Sqlite;

internal static class MessageReturnTypeMigration
{
    internal const string Sql = """
        ALTER TABLE sys_message_entries ADD COLUMN exception_handled INTEGER NOT NULL DEFAULT 0 CHECK(exception_handled IN (0,1));
        ALTER TABLE sys_message_entries ADD COLUMN reply_type_code TEXT NOT NULL DEFAULT '21' CHECK(reply_type_code IN ('21','22','23','24','25','26'));
        ALTER TABLE sys_message_entries ADD COLUMN default_reply_code TEXT NOT NULL DEFAULT '24' CHECK(default_reply_code IN ('23','24'));
        UPDATE sys_message_entries SET default_reply_code='23' WHERE length(default_reply)>0;
        """;
}
