namespace Ipc.Services.Sqlite;

internal static class SchedulerCatalogMigration
{
    internal const string Sql = """
        ALTER TABLE sys_jobqs ADD COLUMN held INTEGER NOT NULL DEFAULT 0 CHECK(held IN (0,1));
        CREATE TABLE sys_jobq_entries (
            queue_lib TEXT NOT NULL, queue_name TEXT NOT NULL,
            subsystem_lib TEXT NOT NULL, subsystem_name TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9999),
            max_active INTEGER NOT NULL CHECK(max_active BETWEEN 0 AND 32000),
            PRIMARY KEY(queue_lib,queue_name), UNIQUE(subsystem_lib,subsystem_name,sequence),
            FOREIGN KEY(queue_lib,queue_name) REFERENCES sys_jobqs(library,name) ON UPDATE CASCADE ON DELETE RESTRICT,
            FOREIGN KEY(subsystem_lib,subsystem_name) REFERENCES sys_subsystems(library,name) ON UPDATE CASCADE ON DELETE CASCADE
        );
        INSERT INTO sys_jobq_entries(queue_lib,queue_name,subsystem_lib,subsystem_name,sequence,max_active)
        SELECT q.library,q.name,s.library,s.name,9999,s.max_active FROM sys_subsystems s
        JOIN sys_jobqs q ON q.name=s.name AND q.library=CASE WHEN s.library='QSYS' THEN 'QUSRSYS' ELSE s.library END;
        UPDATE sys_jobs SET jobq='QUSRSYS/'||jobq WHERE type='Batch' AND instr(jobq,'/')=0;
        CREATE TABLE sys_classes (
            library TEXT NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL DEFAULT '*CLS' CHECK(type='*CLS'),
            run_priority INTEGER NOT NULL CHECK(run_priority BETWEEN 1 AND 99),
            time_slice_ms INTEGER NOT NULL CHECK(time_slice_ms BETWEEN 1 AND 10000),
            PRIMARY KEY(library,name),
            FOREIGN KEY(library,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_job_descriptions (
            library TEXT NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL DEFAULT '*JOBD' CHECK(type='*JOBD'),
            queue_lib TEXT NOT NULL,queue_name TEXT NOT NULL,
            priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 9),
            run_as TEXT REFERENCES sys_profiles(name) ON UPDATE CASCADE ON DELETE RESTRICT,
            routing_data TEXT NOT NULL DEFAULT 'QCMDB',
            current_library TEXT,
            current_library_namespace TEXT NOT NULL DEFAULT 'QSYS' CHECK(current_library_namespace='QSYS'),
            current_library_type TEXT NOT NULL DEFAULT '*LIB' CHECK(current_library_type='*LIB'),
            library_mode TEXT NOT NULL DEFAULT 'CURRENT' CHECK(library_mode IN ('CURRENT','SYSVAL','EXPLICIT')),
            PRIMARY KEY(library,name),
            FOREIGN KEY(library,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY(queue_lib,queue_name) REFERENCES sys_jobqs(library,name) ON UPDATE CASCADE ON DELETE RESTRICT,
            FOREIGN KEY(current_library_namespace,current_library,current_library_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE RESTRICT
        );
        CREATE TABLE sys_jobd_libraries (
            jobd_lib TEXT NOT NULL,jobd_name TEXT NOT NULL,ordinal INTEGER NOT NULL,
            library TEXT NOT NULL,
            library_namespace TEXT NOT NULL DEFAULT 'QSYS' CHECK(library_namespace='QSYS'),
            library_type TEXT NOT NULL DEFAULT '*LIB' CHECK(library_type='*LIB'),
            PRIMARY KEY(jobd_lib,jobd_name,ordinal),UNIQUE(jobd_lib,jobd_name,library),
            FOREIGN KEY(jobd_lib,jobd_name) REFERENCES sys_job_descriptions(library,name) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY(library_namespace,library,library_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE RESTRICT
        );
        ALTER TABLE sys_routing ADD COLUMN compare_mode TEXT NOT NULL DEFAULT '*EQ';
        ALTER TABLE sys_routing ADD COLUMN start_position INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE sys_routing ADD COLUMN class_lib TEXT;
        ALTER TABLE sys_routing ADD COLUMN class_name TEXT;
        UPDATE sys_routing SET compare_mode='*ANY' WHERE compare_value='*ANY';
        UPDATE sys_routing SET seq=9999 WHERE seq=1 AND compare_value='*ANY' AND program='QSYS/QCMD'
          AND (SELECT count(*) FROM sys_routing r WHERE r.subsystem=sys_routing.subsystem)=1;
        INSERT INTO sys_routing(subsystem,seq,compare_value,program,user_class,compare_mode)
        SELECT CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END,9999,'*ANY','QSYS/QCMD','*USER','*ANY'
        FROM sys_subsystems s WHERE NOT EXISTS(SELECT 1 FROM sys_routing r
          WHERE r.subsystem=CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END);
        ALTER TABLE sys_jobs ADD COLUMN job_description TEXT;
        ALTER TABLE sys_jobs ADD COLUMN job_class TEXT;
        ALTER TABLE sys_jobs ADD COLUMN run_priority INTEGER NOT NULL DEFAULT 50;
        ALTER TABLE sys_jobs ADD COLUMN time_slice_ms INTEGER NOT NULL DEFAULT 2000;
        ALTER TABLE sys_jobs ADD COLUMN routing_program TEXT;
        ALTER TABLE sys_jobs ADD COLUMN startup_error TEXT;
        CREATE TRIGGER ipc_class_changed_event AFTER UPDATE ON sys_classes BEGIN
            UPDATE sys_objects SET changed=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE lib=new.library AND name=new.name AND type='*CLS';
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'work.class.changed',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('library',new.library,'name',new.name));
        END;
        CREATE TRIGGER ipc_jobd_changed_event AFTER UPDATE ON sys_job_descriptions BEGIN
            UPDATE sys_objects SET changed=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE lib=new.library AND name=new.name AND type='*JOBD';
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'work.job-description.changed',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('library',new.library,'name',new.name));
        END;
        """;
}
