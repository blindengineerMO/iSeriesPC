using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Services;
using Ipc.Session.Transport;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class NativeJobIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-native-job-identity-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private static readonly JobKey First = new(1, "FIRST", "QUSER");
    private static readonly JobKey Second = new(2, "SECOND", "QUSER");
    public NativeJobIdentityTests()
    {
        _factory = new(_directory); new Migrator(_factory, Migrator.Migrations.Take(27).ToArray()).MigrateToLatest();
        Execute("""
            INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QUSRSYS','QPRINT','*OUTQ','QSYS','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z');
            INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QSYS','QSYSOPR','*MSGQ','QSYS','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z');
            INSERT INTO sys_jobs(number,name,user,type,status,user_profile,current_lib,libl,ccsid,execution_state,submitted_at,started_at)
            VALUES(1,'FIRST','QUSER','Interactive','Active','QUSER','QGPL','QGPL',37,'Running','2024-01-23T04:05:06Z','2024-01-23T04:05:07Z'),
                  (2,'SECOND','QUSER','Batch','JobQueue','QUSER','QGPL','QGPL',1208,'Queued','2024-01-24T04:05:06Z',NULL);
            """);
    }
    [Fact]
    public void Upgrade_backfills_unique_identifiers_without_changing_jobs_and_future_inserts_and_deletes_are_bound()
    {
        var before = Snapshot(); var migrator = new Migrator(_factory, Migrator.Migrations.Take(28).ToArray()); migrator.MigrateToLatest(); Assert.Equal(28, migrator.CurrentVersion()); Assert.Equal(before, Snapshot());
        var store = new NativeJobStore(_factory); var first = store.Get(First).Identifier; var second = store.Get(Second).Identifier;
        Assert.Equal(16, first.Length); Assert.Equal(16, second.Length); Assert.NotEqual(first, second);
        using (var reopened = new SqliteConnectionFactory(_directory))
        {
            Assert.Equal(first, new NativeJobStore(reopened).Get(First).Identifier); Assert.Equal(store.CursorSecret(), new NativeJobStore(reopened).CursorSecret());
        }
        Execute("INSERT INTO sys_jobs(number,name,user,type,status,execution_state) VALUES(3,'THIRD','QUSER','Batch','JobQueue','Queued')");
        Assert.Equal(16, store.Get(new JobKey(3, "THIRD", "QUSER")).Identifier.Length);
        Execute("DELETE FROM sys_jobs WHERE number=2"); Assert.Equal(2L, Scalar("SELECT count(*) FROM sys_job_api_ids"));
        Assert.Equal("ok", Scalar("PRAGMA integrity_check"));
        using var connection = _factory.Open(); using var check = connection.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check"; using var reader = check.ExecuteReader(); Assert.False(reader.Read());
    }
    [Fact]
    public void Failed_identity_upgrade_rolls_back_tables_triggers_history_and_existing_job_rows()
    {
        var before = Snapshot(); var failing = new SchemaMigration(28, "Injected native ID failure", [NativeJobIdentityMigration.Sql, "SELECT * FROM native_job_migration_failure"]);
        Assert.Throws<SqliteException>(() => new Migrator(_factory, Migrator.Migrations.Take(27).Append(failing).ToArray()).MigrateToLatest());
        Assert.Equal(27, new Migrator(_factory).CurrentVersion()); Assert.Equal(before, Snapshot());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_master WHERE name IN ('sys_job_api_ids','sys_job_api_epoch','ipc_native_job_identifier')"));
        new Migrator(_factory, Migrator.Migrations.Take(28).ToArray()).MigrateToLatest(); Assert.Equal(before, Snapshot()); Assert.Equal(2L, Scalar("SELECT count(*) FROM sys_job_api_ids"));
    }
    [Fact]
    public void Exclusive_host_epoch_rotation_is_atomic_invalidates_old_ids_and_cannot_be_requested_by_a_job()
    {
        new Migrator(_factory).MigrateToLatest(); var store = new NativeJobStore(_factory); var before = Snapshot();
        var first = store.Get(First).Identifier; var secret = store.CursorSecret();
        using (OperationIdentity.Enter("QUSER", First)) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => store.BeginHostEpoch()).MessageId);
        Assert.Equal(first, store.Get(First).Identifier); Assert.Equal(secret, store.CursorSecret());
        Execute("CREATE TRIGGER fail_native_epoch BEFORE UPDATE ON sys_job_api_ids BEGIN SELECT RAISE(ABORT,'injected epoch failure'); END");
        Assert.Throws<SqliteException>(() => store.BeginHostEpoch()); Assert.Equal(first, store.Get(First).Identifier); Assert.Equal(secret, store.CursorSecret());
        Execute("DROP TRIGGER fail_native_epoch"); store.BeginHostEpoch();
        Assert.NotEqual(first, store.Get(First).Identifier); Assert.NotEqual(secret, store.CursorSecret()); Assert.Equal(before, Snapshot());
        Assert.Equal("CPF3C52", Assert.Throws<CpfException>(() => store.Get(identifier: first)).MessageId);
    }
    [Fact]
    public async Task Ordinary_host_opens_preserve_ids_and_the_exclusive_server_refreshes_the_epoch_before_readiness()
    {
        new Migrator(_factory).MigrateToLatest(); var store = new NativeJobStore(_factory); var id = store.Get(Second).Identifier; var secret = store.CursorSecret();
        using (var ordinary = IpcSystem.Create(_directory)) { ordinary.Start(); Assert.Equal(id, new NativeJobStore(ordinary.Connections).Get(Second).Identifier); }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token); Assert.NotEqual(id, store.Get(Second).Identifier); Assert.NotEqual(secret, store.CursorSecret());
            Assert.Equal(JobExecutionState.Queued, store.Get(Second).Job.ExecutionState);
            Assert.Equal("CPF3C52", Assert.Throws<CpfException>(() => store.Get(identifier: id)).MessageId);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    private object? Scalar(string sql)
    { using var connection = _factory.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void Execute(string sql)
    { using var connection = _factory.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private string Snapshot()
    {
        using var connection = _factory.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM sys_jobs ORDER BY number";
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) for (var n = 0; n < reader.FieldCount; n++) result.Add(reader.GetName(n) + "=" + reader.GetValue(n));
        return string.Join('\n', result);
    }
    public void Dispose() { _factory.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
