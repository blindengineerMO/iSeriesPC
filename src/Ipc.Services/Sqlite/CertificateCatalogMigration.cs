namespace Ipc.Services.Sqlite;

internal static class CertificateCatalogMigration
{
    internal const string Sql = """
        CREATE TABLE sys_certificates (
            id TEXT PRIMARY KEY, label TEXT NOT NULL, certificate BLOB NOT NULL,
            private_key TEXT, state TEXT NOT NULL DEFAULT 'Active' CHECK(state IN ('Active','Retired','Revoked')),
            created TEXT NOT NULL, replaced_by TEXT REFERENCES sys_certificates(id)
        );
        CREATE INDEX ipc_certificate_labels ON sys_certificates(label,created);
        CREATE TABLE sys_certificate_trust (
            certificate_id TEXT NOT NULL REFERENCES sys_certificates(id),
            purpose TEXT NOT NULL, PRIMARY KEY(certificate_id,purpose)
        );
        CREATE TABLE sys_certificate_bindings (
            service TEXT PRIMARY KEY, purpose TEXT NOT NULL,
            certificate_id TEXT NOT NULL REFERENCES sys_certificates(id), changed TEXT NOT NULL
        );
        CREATE TABLE sys_integrity_results (
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, principal TEXT NOT NULL,
            lib TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, result TEXT NOT NULL
        );
        CREATE TABLE sys_object_signature_policy (
            lib TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
            PRIMARY KEY(lib,name,type),
            FOREIGN KEY(lib,name,type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_verified_artifacts (
            id TEXT PRIMARY KEY, purpose TEXT NOT NULL, resource TEXT NOT NULL,
            content BLOB NOT NULL, signature TEXT NOT NULL, admitted TEXT NOT NULL, principal TEXT NOT NULL
        );
        CREATE TABLE sys_service_deployments (
            name TEXT PRIMARY KEY, lib TEXT NOT NULL, program TEXT NOT NULL,
            artifact_id TEXT NOT NULL REFERENCES sys_verified_artifacts(id), changed TEXT NOT NULL,
            program_type TEXT NOT NULL DEFAULT '*PGM' CHECK(program_type='*PGM'),
            FOREIGN KEY(lib,program,program_type) REFERENCES sys_objects(lib,name,type) ON UPDATE CASCADE ON DELETE RESTRICT
        );
        CREATE TRIGGER ipc_certificate_import_event AFTER INSERT ON sys_certificates BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.certificate.imported',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('certificate',new.id,'label',new.label,'hasPrivateKey',new.private_key IS NOT NULL));
        END;
        CREATE TRIGGER ipc_certificate_state_event AFTER UPDATE OF state ON sys_certificates WHEN old.state<>new.state BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.certificate.state',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('certificate',new.id,'state',new.state));
        END;
        CREATE TRIGGER ipc_certificate_trusted_event AFTER INSERT ON sys_certificate_trust BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.certificate.trusted',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('certificate',new.certificate_id,'purpose',new.purpose));
        END;
        CREATE TRIGGER ipc_certificate_untrusted_event AFTER DELETE ON sys_certificate_trust BEGIN
            INSERT INTO sys_events(at,kind,principal,job,payload)
            VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'security.certificate.untrusted',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),
                json_object('certificate',old.certificate_id,'purpose',old.purpose));
        END;
        """;
}
