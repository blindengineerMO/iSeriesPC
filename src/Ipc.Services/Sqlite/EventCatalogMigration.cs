namespace Ipc.Services.Sqlite;

internal static class EventCatalogMigration
{
    public const string Sql = """
        ALTER TABLE sys_hst ADD COLUMN kind TEXT NOT NULL DEFAULT 'HST';
        ALTER TABLE sys_hst ADD COLUMN principal TEXT NULL;
        ALTER TABLE sys_joblog ADD COLUMN principal TEXT NULL;
        CREATE TABLE sys_events (
            seq INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, kind TEXT NOT NULL,
            principal TEXT NOT NULL, job TEXT NULL, payload TEXT NOT NULL
        );
        CREATE INDEX sys_events_retention ON sys_events(at,seq);
        CREATE TABLE sys_event_consumers (name TEXT PRIMARY KEY, last_seq INTEGER NOT NULL DEFAULT 0);
        CREATE TRIGGER ipc_object_created_event AFTER INSERT ON sys_objects BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'object.created',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('library',new.lib,'name',new.name,'type',new.type));
        END;
        CREATE TRIGGER ipc_object_updated_event AFTER UPDATE ON sys_objects BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'object.updated',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('library',new.lib,'name',new.name,'type',new.type,'previousLibrary',old.lib,'previousName',old.name));
        END;
        CREATE TRIGGER ipc_object_deleted_event AFTER DELETE ON sys_objects BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'object.deleted',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('library',old.lib,'name',old.name,'type',old.type));
        END;
        CREATE TRIGGER ipc_job_created_event AFTER INSERT ON sys_jobs BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'job.created',coalesce(ipc_actor(),'*SYSTEM'),
                printf('%d/%s/%s',new.number,new.name,new.user),json_object('profile',new.user_profile,'status',new.status));
        END;
        CREATE TRIGGER ipc_job_updated_event AFTER UPDATE ON sys_jobs WHEN old.status<>new.status BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'job.status',coalesce(ipc_actor(),'*SYSTEM'),
                printf('%d/%s/%s',new.number,new.name,new.user),json_object('profile',new.user_profile,'status',new.status,'previousStatus',old.status));
        END;
        CREATE TRIGGER ipc_profile_created_event AFTER INSERT ON sys_profiles BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.profile.created',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('profile',new.name));
        END;
        CREATE TRIGGER ipc_profile_updated_event AFTER UPDATE ON sys_profiles BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.profile.updated',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('profile',new.name));
        END;
        CREATE TRIGGER ipc_profile_deleted_event AFTER DELETE ON sys_profiles BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.profile.deleted',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('profile',old.name));
        END;
        CREATE TRIGGER ipc_sys_authorities_insert_event AFTER INSERT ON sys_authorities BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authority.insert',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('lib',new.lib,'name',new.name,'type',new.type,'holder',new.holder,'is_authl',new.is_authl,'bits',new.bits));
        END;
        CREATE TRIGGER ipc_sys_authorities_update_event AFTER UPDATE ON sys_authorities BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authority.update',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('lib',new.lib,'name',new.name,'type',new.type,'holder',new.holder,'is_authl',new.is_authl,'bits',new.bits));
        END;
        CREATE TRIGGER ipc_sys_authorities_delete_event AFTER DELETE ON sys_authorities BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authority.delete',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('lib',old.lib,'name',old.name,'type',old.type,'holder',old.holder,'is_authl',old.is_authl,'bits',old.bits));
        END;
        CREATE TRIGGER ipc_sys_authl_members_insert_event AFTER INSERT ON sys_authl_members BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authl.member.insert',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('authl',new.authl,'holder',new.holder,'bits',new.bits));
        END;
        CREATE TRIGGER ipc_sys_authl_members_update_event AFTER UPDATE ON sys_authl_members BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authl.member.update',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('authl',new.authl,'holder',new.holder,'bits',new.bits));
        END;
        CREATE TRIGGER ipc_sys_authl_members_delete_event AFTER DELETE ON sys_authl_members BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.authl.member.delete',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('authl',old.authl,'holder',old.holder,'bits',old.bits));
        END;
        CREATE TRIGGER ipc_sys_sysvals_insert_event AFTER INSERT ON sys_sysvals BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'system.value.insert',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('name',new.name));
        END;
        CREATE TRIGGER ipc_sys_sysvals_update_event AFTER UPDATE ON sys_sysvals BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'system.value.update',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('name',new.name));
        END;
        CREATE TRIGGER ipc_sys_sysvals_delete_event AFTER DELETE ON sys_sysvals BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'system.value.delete',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),json_object('name',old.name));
        END;
        CREATE TRIGGER ipc_job_message_event AFTER INSERT ON sys_joblog BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(new.time,'job.message',coalesce(new.principal,'*SYSTEM'),printf('%d/%s/%s',new.job_number,new.job_name,new.job_user),
                json_object('messageId',new.message_id,'severity',new.severity,'messageType',new.message_type,'sequence',new.seq));
        END;
        """;
}
