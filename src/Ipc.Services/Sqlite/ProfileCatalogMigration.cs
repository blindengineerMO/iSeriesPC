namespace Ipc.Services.Sqlite;

internal static class ProfileCatalogMigration
{
    // Immutable migration SQL: profile secrets stay exclusively in sys_profiles.
    public const string Sql = """
        INSERT INTO sys_objects (lib,name,type,owner,created,changed,description,ccsid,attribute,public_authority)
        SELECT 'QSYS',name,'*USRPRF',coalesce(owner,'QSECOFR'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),
            strftime('%Y-%m-%dT%H:%M:%fZ','now'),description,ccsid,user_class,0 FROM sys_profiles WHERE true
        ON CONFLICT(lib,name,type) DO NOTHING;

        CREATE TRIGGER ipc_profile_catalog_insert AFTER INSERT ON sys_profiles BEGIN
            INSERT INTO sys_objects (lib,name,type,owner,created,changed,description,ccsid,attribute,public_authority)
            VALUES ('QSYS',new.name,'*USRPRF',coalesce(new.owner,'QSECOFR'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),new.description,new.ccsid,new.user_class,0);
        END;
        CREATE TRIGGER ipc_profile_catalog_update AFTER UPDATE ON sys_profiles BEGIN
            UPDATE sys_objects SET name=new.name,owner=coalesce(new.owner,'QSECOFR'),description=new.description,
                ccsid=new.ccsid,attribute=new.user_class,changed=strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                attrs=json_remove(attrs,'$."ipc.signature"')
            WHERE lib='QSYS' AND name=old.name AND type='*USRPRF';
        END;
        """;
}
