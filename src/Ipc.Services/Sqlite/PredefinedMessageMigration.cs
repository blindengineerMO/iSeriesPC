namespace Ipc.Services.Sqlite;

internal static class PredefinedMessageMigration
{
    internal const string Sql = """
        ALTER TABLE sys_message_entries ADD COLUMN predefined TEXT NOT NULL DEFAULT '' CHECK(length(CAST(predefined AS BLOB))<=32767);
        """;
}
