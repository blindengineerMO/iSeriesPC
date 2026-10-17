using System.Security.Cryptography;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Session.Transport;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class CryptoContextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-crypto-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly byte[] _key = Enumerable.Range(0, 32).Select(v => (byte)v).ToArray();
    public CryptoContextTests() { _system = IpcSystem.Create(_directory); _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CryptoContextAdministrator29"); }
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void Execute(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private byte[] Hmac(JobKey job, byte[] token) => _system.Cryptography.Hmac(job, token, 3, [0x80, 0xFF, 0x00]);
    [Fact]
    public void Context_is_encrypted_shared_between_hosts_and_bound_to_the_executing_job_even_for_JOBCTL()
    {
        var first = _system.Jobs.CreateInteractive("QUSER"); var other = _system.Jobs.CreateInteractive("QSECOFR"); byte[] token;
        using (OperationIdentity.Enter("QUSER", first.Key)) token = _system.Cryptography.CreateKeyContext(first.Key, 3, _key);
        var envelope = (string)Scalar("SELECT key_value FROM sys_crypto_keys")!;
        Assert.StartsWith("v1.", envelope); Assert.DoesNotContain(Convert.ToHexString(_key), envelope); Assert.DoesNotContain(Convert.ToBase64String(_key), envelope);
        var expected = HMACSHA256.HashData(_key, new byte[] { 0x80, 0xFF, 0x00 }); Assert.Equal(expected, Hmac(first.Key, token));
        using (var reopened = IpcSystem.Create(_directory))
        { reopened.Start(); Assert.Equal(expected, reopened.Cryptography.Hmac(first.Key, token, 3, [0x80, 0xFF, 0x00])); }
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        {
            Assert.Equal("CPF9DF5", Assert.Throws<CpfException>(() => Hmac(other.Key, token)).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Hmac(first.Key, token)).MessageId);
            Assert.Equal("CPF9DF5", Assert.Throws<CpfException>(() => _system.Cryptography.DestroyKeyContext(other.Key, token)).MessageId);
        }
        using (OperationIdentity.Enter("QUSER", first.Key)) _system.Cryptography.DestroyKeyContext(first.Key, token);
        Assert.Equal("CPF9DF5", Assert.Throws<CpfException>(() => Hmac(first.Key, token)).MessageId); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
    }
    [Fact]
    public void Key_type_and_ciphertext_tampering_are_rejected_without_leaking_material()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var token = _system.Cryptography.CreateKeyContext(job.Key, 22, _key);
        Assert.Equal("CPF9DF7", Assert.Throws<CpfException>(() => Hmac(job.Key, token)).MessageId);
        var envelope = ((string)Scalar("SELECT key_value FROM sys_crypto_keys")!).Split('.'); var encrypted = Convert.FromBase64String(envelope[3]); encrypted[0] ^= 1; envelope[3] = Convert.ToBase64String(encrypted);
        using (var connection = _system.Connections.Open()) using (var update = connection.CreateCommand())
        { update.CommandText = "UPDATE sys_crypto_keys SET key_value=$value"; update.Parameters.AddWithValue("$value", string.Join('.', envelope)); update.ExecuteNonQuery(); }
        Assert.Equal("CPF9DC3", Assert.Throws<CpfException>(() => _system.Cryptography.Transform(job.Key, token, new byte[16], 1, 0, 0, new byte[16], false)).MessageId);
        Assert.Equal("CPF9DF4", Assert.Throws<CpfException>(() => Hmac(job.Key, new byte[8])).MessageId);
    }
    [Fact]
    public void Completion_cancellation_and_crash_recovery_delete_contexts_with_the_job_transaction()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        _system.Jobs.Complete(job.Key); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
        job = _system.Jobs.CreateInteractive("QUSER"); _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        _system.Jobs.RequestEnd(job.Key); Assert.ThrowsAny<OperationCanceledException>(() => _system.Cryptography.CreateKeyContext(job.Key, 3, _key));
        _system.Jobs.Complete(job.Key, JobCompletion.Abnormal); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
        job = _system.Jobs.CreateInteractive("QUSER"); _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        _system.Jobs.RecoverInterruptedInteractiveJobs(); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
    }
    [Fact]
    public void Failed_completion_and_failed_context_creation_roll_back_their_rows()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var token = _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        Execute("CREATE TRIGGER fail_crypto_cleanup BEFORE DELETE ON sys_crypto_keys BEGIN SELECT RAISE(ABORT,'injected crypto cleanup'); END");
        Assert.Throws<SqliteException>(() => _system.Jobs.Complete(job.Key)); Assert.Equal(JobExecutionState.Running, _system.Jobs.GetRequired(job.Key).ExecutionState);
        Assert.Equal(32, Hmac(job.Key, token).Length); Execute("DROP TRIGGER fail_crypto_cleanup");
        Execute("CREATE TRIGGER fail_crypto_insert BEFORE INSERT ON sys_crypto_keys BEGIN SELECT RAISE(ABORT,'injected crypto creation'); END");
        Assert.Throws<SqliteException>(() => _system.Cryptography.CreateKeyContext(job.Key, 3, _key)); Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
    }
    [Fact]
    public void Wrapping_key_inventory_and_rotation_cover_live_contexts_and_preserve_their_tokens()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var token = _system.Cryptography.CreateKeyContext(job.Key, 3, _key); var expected = Hmac(job.Key, token);
        var before = (string)Scalar("SELECT key_value FROM sys_crypto_keys")!;
        Assert.Equal(1, _system.Certificates.WrappingKeys().Sum(k => k.EncryptedValues));
        var id = _system.Certificates.RotateWrappingKey(); Assert.StartsWith("v1." + id + ".", (string)Scalar("SELECT key_value FROM sys_crypto_keys")!);
        Assert.NotEqual(before, Scalar("SELECT key_value FROM sys_crypto_keys")); Assert.Equal(expected, Hmac(job.Key, token));
        Assert.Equal(1, _system.Certificates.WrappingKeys().Single(k => k.Active).EncryptedValues);
    }
    [Fact]
    public void Failed_rewrap_preserves_encrypted_context_rows_and_old_key_files_still_decrypt_them()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var token = _system.Cryptography.CreateKeyContext(job.Key, 3, _key); var expected = Hmac(job.Key, token);
        var before = Scalar("SELECT key_value FROM sys_crypto_keys");
        Execute("CREATE TRIGGER fail_crypto_rewrap BEFORE UPDATE ON sys_crypto_keys BEGIN SELECT RAISE(ABORT,'injected crypto rewrap'); END");
        Assert.Throws<SqliteException>(() => _system.Certificates.RotateWrappingKey()); Assert.Equal(before, Scalar("SELECT key_value FROM sys_crypto_keys")); Assert.Equal(expected, Hmac(job.Key, token));
        Execute("DROP TRIGGER fail_crypto_rewrap"); _system.Certificates.RotateWrappingKey(); Assert.Equal(expected, Hmac(job.Key, token));
    }
    [Fact]
    public void Schema_28_upgrade_preserves_job_identifiers_and_failed_29_upgrade_is_atomic()
    {
        var path = Path.Combine(_directory, "legacy"); using var factory = new SqliteConnectionFactory(path);
        new Migrator(factory, Migrator.Migrations.Take(28).ToArray()).MigrateToLatest();
        using (var connection = factory.Open()) using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                CREATE TABLE crypto_preserved(value TEXT); INSERT INTO crypto_preserved VALUES('unchanged');
                INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QUSRSYS','QPRINT','*OUTQ','QSYS','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z');
                INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QSYS','QSYSOPR','*MSGQ','QSYS','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z');
                INSERT INTO sys_jobs(number,name,user,type,status,execution_state) VALUES(1,'LEGACY','QUSER','Batch','JobQueue','Queued');
                """; seed.ExecuteNonQuery();
        }
        byte[] identifier;
        using (var connection = factory.Open()) using (var query = connection.CreateCommand()) { query.CommandText = "SELECT identifier FROM sys_job_api_ids"; identifier = (byte[])query.ExecuteScalar()!; }
        var failing = new SchemaMigration(29, "Injected crypto failure", [CryptoContextMigration.Sql, "SELECT * FROM missing_crypto_upgrade"]);
        Assert.Throws<SqliteException>(() => new Migrator(factory, Migrator.Migrations.Take(28).Append(failing).ToArray()).MigrateToLatest());
        Assert.Equal(28, new Migrator(factory).CurrentVersion());
        using (var connection = factory.Open()) using (var query = connection.CreateCommand())
        { query.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('sys_crypto_keys','ipc_crypto_key_owner','ipc_crypto_key_active','ipc_crypto_key_end')"; Assert.Equal(0L, query.ExecuteScalar()); }
        new Migrator(factory).MigrateToLatest();
        using (var connection = factory.Open()) using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT value FROM crypto_preserved"; Assert.Equal("unchanged", query.ExecuteScalar());
            query.CommandText = "SELECT identifier FROM sys_job_api_ids"; Assert.Equal(identifier, (byte[])query.ExecuteScalar()!);
        }
    }
    [Fact]
    public async Task Exclusive_server_startup_recovers_job_contexts_before_readiness()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token);
        try { await server.Ready.WaitAsync(stop.Token); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_crypto_keys")); Assert.Equal(JobExecutionState.Interrupted, _system.Jobs.GetRequired(job.Key).ExecutionState); }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public async Task Cancellation_interrupts_waiting_for_a_context_write_reservation_without_a_new_row()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); _system.Cryptography.CreateKeyContext(job.Key, 3, _key);
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = Task.Run(() => { started.SetResult(); return _system.Cryptography.CreateKeyContext(job.Key, 3, _key, stop.Token); });
        await started.Task; await Task.Delay(150); Assert.False(waiting.IsCompleted); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_crypto_keys"));
    }
    public void Dispose() { _system.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
