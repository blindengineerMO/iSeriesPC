namespace Ipc.Services.Sqlite;

internal static class CryptoContextMigration
{
    internal const string Sql = """
        CREATE TABLE sys_crypto_keys (
            token BLOB PRIMARY KEY CHECK(length(token)=8),
            job_number INTEGER NOT NULL REFERENCES sys_jobs(number) ON DELETE CASCADE,
            key_type INTEGER NOT NULL CHECK(key_type IN (1,2,3,4,5,22)),
            key_value TEXT NOT NULL CHECK(length(key_value)<=100000)
        );
        CREATE INDEX ipc_crypto_key_owner ON sys_crypto_keys(job_number);
        CREATE TRIGGER ipc_crypto_key_active BEFORE INSERT ON sys_crypto_keys
        WHEN NOT EXISTS(SELECT 1 FROM sys_jobs WHERE number=NEW.job_number AND execution_state='Running' AND cancel_requested=0)
        BEGIN SELECT RAISE(ABORT,'Cryptographic context requires an active job'); END;
        CREATE TRIGGER ipc_crypto_key_end AFTER UPDATE OF execution_state ON sys_jobs
        WHEN NEW.execution_state NOT IN ('Queued','Running')
        BEGIN DELETE FROM sys_crypto_keys WHERE job_number=NEW.number; END;
        """;
}
