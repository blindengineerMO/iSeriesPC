namespace Ipc.Services.Sqlite;

internal static class NativeJobIdentityMigration
{
    internal const string Sql = """
        CREATE TABLE sys_job_api_ids (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE CASCADE,
            identifier BLOB NOT NULL UNIQUE CHECK(length(identifier)=16)
        );
        INSERT INTO sys_job_api_ids SELECT number,randomblob(16) FROM sys_jobs;
        CREATE TRIGGER ipc_native_job_identifier AFTER INSERT ON sys_jobs
        BEGIN INSERT INTO sys_job_api_ids VALUES(NEW.number,randomblob(16)); END;
        CREATE TABLE sys_job_api_epoch (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            secret BLOB NOT NULL CHECK(length(secret)=32)
        );
        INSERT INTO sys_job_api_epoch VALUES(1,randomblob(32));
        """;
}
