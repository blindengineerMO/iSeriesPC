namespace Ipc.Services.Sqlite;

internal static class JobLifecycleMigration
{
    internal const string Sql = """
        ALTER TABLE sys_jobs ADD COLUMN cancel_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancel_requested IN (0,1));
        ALTER TABLE sys_jobs ADD COLUMN cancel_reason TEXT;
        CREATE TABLE sys_job_accounting (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE CASCADE,
            cpu_nanoseconds INTEGER NOT NULL DEFAULT 0, elapsed_milliseconds INTEGER NOT NULL DEFAULT 0,
            active_threads INTEGER NOT NULL DEFAULT 0, commands INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE sys_job_threads (
            job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            managed_thread INTEGER NOT NULL,native_thread INTEGER NOT NULL,state TEXT NOT NULL,
            cpu_nanoseconds INTEGER NOT NULL DEFAULT 0,elapsed_milliseconds INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL,PRIMARY KEY(job_number,managed_thread)
        );
        CREATE TABLE sys_activation_groups (
            job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            name TEXT NOT NULL,created_at TEXT NOT NULL,active_calls INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(job_number,name)
        );
        CREATE TABLE sys_job_processes (
            invocation TEXT PRIMARY KEY,job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            process_id INTEGER NOT NULL,state TEXT NOT NULL,cpu_nanoseconds INTEGER NOT NULL DEFAULT 0,
            peak_threads INTEGER NOT NULL DEFAULT 0,exit_code INTEGER,started_at TEXT NOT NULL,ended_at TEXT
        );
        CREATE INDEX ipc_process_job ON sys_job_processes(job_number);
        INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority)
        SELECT 'QUSRSYS','QPRINT','*OUTQ','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),7 WHERE EXISTS(SELECT 1 FROM sys_jobs)
        ON CONFLICT(lib,name,type) DO NOTHING;
        INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority)
        SELECT 'QSYS','QSYSOPR','*MSGQ','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),7 WHERE EXISTS(SELECT 1 FROM sys_jobs)
        ON CONFLICT(lib,name,type) DO NOTHING;
        CREATE TABLE sys_job_bindings (
            job_number INTEGER PRIMARY KEY REFERENCES sys_jobs(number) ON DELETE CASCADE,
            output_lib TEXT,output_name TEXT,output_type TEXT NOT NULL DEFAULT '*OUTQ' CHECK(output_type='*OUTQ'),
            message_lib TEXT,message_name TEXT,message_type TEXT NOT NULL DEFAULT '*MSGQ' CHECK(message_type='*MSGQ'),
            output_snapshot TEXT NOT NULL DEFAULT 'QUSRSYS/QPRINT',message_snapshot TEXT NOT NULL DEFAULT 'QSYS/QSYSOPR',
            FOREIGN KEY(output_lib,output_name,output_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE RESTRICT,
            FOREIGN KEY(message_lib,message_name,message_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE RESTRICT
        );
        INSERT INTO sys_job_bindings(job_number,output_lib,output_name,message_lib,message_name)
        SELECT number,'QUSRSYS','QPRINT','QSYS','QSYSOPR' FROM sys_jobs;
        UPDATE sys_job_bindings SET output_lib=NULL,output_name=NULL,message_lib=NULL,message_name=NULL
        WHERE job_number IN (SELECT number FROM sys_jobs WHERE execution_state NOT IN ('Queued','Running'));
        INSERT INTO sys_job_accounting(job_number) SELECT number FROM sys_jobs;
        CREATE TRIGGER ipc_job_runtime_insert AFTER INSERT ON sys_jobs BEGIN
            INSERT INTO sys_job_bindings(job_number,output_lib,output_name,message_lib,message_name)
            VALUES(new.number,'QUSRSYS','QPRINT','QSYS','QSYSOPR');
            INSERT INTO sys_job_accounting(job_number) VALUES(new.number);
        END;
        """;
}
