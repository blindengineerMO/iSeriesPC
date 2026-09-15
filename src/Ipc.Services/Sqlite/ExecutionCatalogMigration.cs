namespace Ipc.Services.Sqlite;

internal static class ExecutionCatalogMigration
{
    internal const string Sql = """
        CREATE UNIQUE INDEX ipc_job_number_unique ON sys_jobs(number);
        ALTER TABLE sys_jobs ADD COLUMN execution_state TEXT NOT NULL DEFAULT 'Queued'
          CHECK(execution_state IN ('Queued','Running','Succeeded','Failed','Cancelled','Interrupted'));
        UPDATE sys_jobs SET execution_state=CASE
          WHEN status IN ('Completed','Ended') THEN CASE WHEN completion IN ('Normal','Warning') THEN 'Succeeded' ELSE 'Failed' END
          WHEN status IN ('Active','MessageWait') OR status='Held' AND started_at IS NOT NULL THEN 'Running'
          ELSE 'Queued' END;
        ALTER TABLE sys_job_execution RENAME TO ipc_previous_job_execution;
        CREATE TABLE sys_job_execution (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE CASCADE,
            command TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts>=0),
            host_id TEXT, claim_token TEXT UNIQUE, claimed_at TEXT, finished_at TEXT
        );
        INSERT INTO sys_job_execution(job_number,command,attempts,claimed_at,finished_at)
        SELECT x.job_number,x.command,x.attempts,j.started_at,j.completed_at
        FROM ipc_previous_job_execution x LEFT JOIN sys_jobs j ON j.number=x.job_number;
        DROP TABLE ipc_previous_job_execution;
        """;
}
