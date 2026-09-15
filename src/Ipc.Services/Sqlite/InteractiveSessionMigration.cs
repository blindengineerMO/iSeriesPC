namespace Ipc.Services.Sqlite;

internal static class InteractiveSessionMigration
{
    internal const string Sql = """
        CREATE TABLE sys_terminal_jobs (
            job_number INTEGER PRIMARY KEY REFERENCES sys_job_environment(job_number) ON DELETE CASCADE,
            family TEXT NOT NULL, partition INTEGER NOT NULL CHECK(partition IN (0,1)),
            group_name TEXT, description TEXT NOT NULL DEFAULT '',
            UNIQUE(family,partition,group_name)
        );
        CREATE INDEX ipc_terminal_family ON sys_terminal_jobs(family);
        """;
}
