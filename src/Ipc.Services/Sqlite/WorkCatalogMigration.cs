namespace Ipc.Services.Sqlite;

internal static class WorkCatalogMigration
{
    public const string Sql = """
        ALTER TABLE sys_subsystems RENAME TO ipc_old_subsystems;
        CREATE TABLE sys_subsystems (
            name TEXT NOT NULL, description TEXT NULL, status TEXT NOT NULL DEFAULT 'Stopped',
            max_active INTEGER NOT NULL DEFAULT 1, library TEXT NOT NULL DEFAULT 'QSYS',
            PRIMARY KEY(library,name)
        );
        INSERT INTO sys_subsystems(name,description,status,max_active) SELECT name,description,status,max_active FROM ipc_old_subsystems;
        DROP TABLE ipc_old_subsystems;
        ALTER TABLE sys_jobqs RENAME TO ipc_old_jobqs;
        CREATE TABLE sys_jobqs (name TEXT NOT NULL, library TEXT NOT NULL DEFAULT 'QGPL', description TEXT NULL,
            PRIMARY KEY(library,name));
        INSERT INTO sys_jobqs SELECT * FROM ipc_old_jobqs;
        DROP TABLE ipc_old_jobqs;

        INSERT INTO sys_objects(lib,name,type,owner,created,changed,description,public_authority)
        SELECT library,name,'*SBSD','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),description,3
        FROM sys_subsystems WHERE true ON CONFLICT(lib,name,type) DO NOTHING;
        INSERT INTO sys_objects(lib,name,type,owner,created,changed,description,public_authority)
        SELECT library,name,'*JOBQ','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),description,3
        FROM sys_jobqs WHERE true ON CONFLICT(lib,name,type) DO NOTHING;

        CREATE TRIGGER ipc_subsystem_catalog_insert AFTER INSERT ON sys_subsystems BEGIN
            INSERT INTO sys_objects(lib,name,type,owner,created,changed,description,public_authority)
            VALUES(new.library,new.name,'*SBSD','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),new.description,3) ON CONFLICT(lib,name,type) DO NOTHING;
        END;
        CREATE TRIGGER ipc_subsystem_catalog_update AFTER UPDATE ON sys_subsystems BEGIN
            UPDATE sys_objects SET description=new.description,changed=strftime('%Y-%m-%dT%H:%M:%fZ','now'),attrs=json_remove(attrs,'$."ipc.signature"')
            WHERE lib=new.library AND name=new.name AND type='*SBSD';
        END;
        CREATE TRIGGER ipc_jobq_catalog_insert AFTER INSERT ON sys_jobqs BEGIN
            INSERT INTO sys_objects(lib,name,type,owner,created,changed,description,public_authority)
            VALUES(new.library,new.name,'*JOBQ','QSYS',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),new.description,3) ON CONFLICT(lib,name,type) DO NOTHING;
        END;
        CREATE TRIGGER ipc_jobq_catalog_update AFTER UPDATE ON sys_jobqs BEGIN
            UPDATE sys_objects SET description=new.description,changed=strftime('%Y-%m-%dT%H:%M:%fZ','now'),attrs=json_remove(attrs,'$."ipc.signature"')
            WHERE lib=new.library AND name=new.name AND type='*JOBQ';
        END;
        """;
}
