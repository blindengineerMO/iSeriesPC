namespace Ipc.Services.Sqlite;

internal static class JobEnvironmentMigration
{
    internal const string Sql = """
        CREATE TABLE sys_job_groups (
            id TEXT PRIMARY KEY,owner TEXT NOT NULL,gda BLOB NOT NULL CHECK(typeof(gda)='blob' AND length(gda)=512)
        );
        CREATE TABLE sys_job_environment (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE CASCADE,
            lda BLOB NOT NULL CHECK(typeof(lda)='blob' AND length(lda)=1024),
            group_id TEXT REFERENCES sys_job_groups(id) ON DELETE RESTRICT,
            pip BLOB CHECK(pip IS NULL OR typeof(pip)='blob' AND length(pip)<=2000),
            pda BLOB CHECK(pda IS NULL OR typeof(pda)='blob' AND length(pda)=2000)
        );
        CREATE INDEX ipc_job_group_members ON sys_job_environment(group_id);
        INSERT INTO sys_job_environment(job_number,lda)
          SELECT number,CAST(replace(hex(zeroblob(512)),'0',CASE WHEN ccsid IN (37,500,1047) THEN '@' ELSE ' ' END) AS BLOB) FROM sys_jobs WHERE execution_state IN ('Queued','Running');
        CREATE TRIGGER ipc_job_environment_insert AFTER INSERT ON sys_jobs BEGIN
            INSERT INTO sys_job_environment(job_number,lda) VALUES(new.number,CAST(replace(hex(zeroblob(512)),'0',CASE WHEN new.ccsid IN (37,500,1047) THEN '@' ELSE ' ' END) AS BLOB));
        END;
        CREATE TRIGGER ipc_job_environment_end AFTER UPDATE OF execution_state ON sys_jobs
        WHEN new.execution_state NOT IN ('Queued','Running') BEGIN
            DELETE FROM sys_job_environment WHERE job_number=new.number;
            DELETE FROM sys_job_groups WHERE NOT EXISTS(SELECT 1 FROM sys_job_environment WHERE group_id=sys_job_groups.id);
        END;
        """;
}
