using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests
{
    private Ipc.Core.Work.Job BeginQueryUnit()
    {
        Seed(); _system.Journals.CreateReceiver(new("QGPL", "QRYRCV")); _system.Journals.Create(new("QGPL", "QRYJRN"), new("QGPL", "QRYRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "QRYJRN"), "*BOTH");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        { _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, new("QGPL", "DATA")); }
        return job;
    }

    [Fact]
    public async Task A_private_query_snapshot_can_be_fetched_and_closed_on_a_different_thread()
    {
        var job = BeginQueryUnit();
        using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        using (var change = _system.RecordTransactions.Open(job.Key))
        { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='9.50' WHERE ID=1"); change.Complete(); }
        using var cursor = _files.OpenSqlQuery("SELECT ID,AMOUNT FROM QGPL.DATA ORDER BY ID", Options);
        await Task.Run(() => { Assert.True(cursor.Read()); Assert.Equal(9.50m, Number(cursor.Current![1])); });
        using (var change = _system.RecordTransactions.Open(job.Key))
        { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='8.50' WHERE ID=2"); change.Complete(); }
        await Task.Run(() => { Assert.True(cursor.Read()); Assert.Equal(2.75m, Number(cursor.Current![1])); cursor.Dispose(); });
        Assert.Equal(8.50m, Number(Assert.Single(Query("SELECT AMOUNT FROM QGPL.DATA WHERE ID=2").Rows)[0]));
        _system.RecordTransactions.Rollback(job.Key); _system.RecordTransactions.End(job.Key);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("end")]
    public void Private_query_boundaries_require_closed_readers_before_changing_the_unit(string boundary)
    {
        var job = BeginQueryUnit(); using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        using var cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options);
        void Finish() { if (boundary == "commit") _system.RecordTransactions.Commit(job.Key); else if (boundary == "rollback") _system.RecordTransactions.Rollback(job.Key); else _system.RecordTransactions.End(job.Key); }
        Assert.Equal("IPC0211", Assert.Throws<CpfException>(Finish).MessageId);
        Assert.True(cursor.Read()); cursor.Dispose(); Finish();
        if (boundary != "end") _system.RecordTransactions.End(job.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ending_a_job_closes_private_query_snapshots_without_thread_owned_gates(bool request)
    {
        var job = BeginQueryUnit(); SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", job.Key)) cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options);
        using (cursor)
        {
            await Task.Run(() => { if (request) _system.Jobs.RequestEnd(job.Key); else _system.Jobs.Complete(job.Key, JobCompletion.Normal); }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read()); Assert.Null(cursor.Current);
        }
        if (request) _system.Jobs.Complete(job.Key, JobCompletion.Abnormal);
        Assert.Empty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("shutdown")]
    [InlineData("unit-shutdown")]
    public async Task Factories_coordinate_job_query_cleanup_and_private_workspace_ownership(string finish)
        => await Task.Factory.StartNew(() => CheckFactoriesQueryCleanup(finish),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private void CheckFactoriesQueryCleanup(string finish)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-query-cross-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var system = IpcSystem.Create(directory); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "SqlQueryDiskFixture99"); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER)", Options); files.ExecuteSql("INSERT INTO QGPL.DATA VALUES(1),(2)", Options);
            system.Journals.CreateReceiver(new("QGPL", "QRYRCV")); system.Journals.Create(new("QGPL", "QRYJRN"), new("QGPL", "QRYRCV"));
            system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "QRYJRN"), "*BOTH");
            var job = system.Jobs.CreateInteractive("QSECOFR"); using var factory = new SqliteConnectionFactory(directory);
            var other = new SqliteFileStore(factory, system.Objects); SqlQueryCursor cursor;
            using (OperationIdentity.Enter("QSECOFR", job.Key))
            {
                system.RecordTransactions.Begin(job.Key); system.RecordTransactions.Enlist(job.Key, new("QGPL", "DATA"));
                cursor = other.OpenSqlQuery("SELECT ID FROM QGPL.DATA ORDER BY ID", Options); Assert.True(cursor.Read());
            }
            using (cursor)
            {
                var closing=Task.Factory.StartNew(() => { if (finish == "complete") system.Jobs.Complete(job.Key, JobCompletion.Normal); else if (finish == "shutdown") factory.Dispose(); else system.Connections.Dispose(); },
                    CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                try
                {
                    closing.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read());
                }
                finally { cursor.Dispose(); closing.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            }
            if (finish == "shutdown") system.Jobs.Complete(job.Key, JobCompletion.Normal);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory, ".record-units")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task A_live_query_keeps_its_WAL_snapshot_while_a_different_connection_publishes_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-query-wal-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "SqlQueryDiskFixture99"); var files = new SqliteFileStore(system.Connections, system.Objects);
                files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER,VALUE INTEGER)", Options); files.ExecuteSql("INSERT INTO QGPL.DATA VALUES(1,10),(2,20)", Options);
                using var cursor = files.OpenSqlQuery("SELECT VALUE FROM QGPL.DATA ORDER BY ID", Options); Assert.True(cursor.Read());
                await Task.Run(() => files.ExecuteSql("UPDATE QGPL.DATA SET VALUE=99 WHERE ID=2", Options));
                Assert.True(cursor.Read()); Assert.Equal(20L, cursor.Current![0]); Assert.False(cursor.Read());
                using var fresh = files.OpenSqlQuery("SELECT VALUE FROM QGPL.DATA WHERE ID=2", Options); Assert.True(fresh.Read()); Assert.Equal(99L, fresh.Current![0]);
            }
            using var restarted = IpcSystem.Create(directory); restarted.Start(); var reopened = new SqliteFileStore(restarted.Connections, restarted.Objects);
            using var check = reopened.OpenSqlQuery("SELECT VALUE FROM QGPL.DATA WHERE ID=2", Options); Assert.True(check.Read()); Assert.Equal(99L, check.Current![0]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Query_functions_are_removed_from_pooled_connections_after_success_and_failure(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-query-pool-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var system = IpcSystem.Create(directory); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "SqlQueryDiskFixture99"); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER)", Options); files.ExecuteSql("INSERT INTO QGPL.DATA VALUES(1),(2)", Options);
            for (var iteration = 0; iteration < 8; iteration++)
            {
                try { using var query = files.OpenSqlQuery(fail ? "SELECT 1/(ID-2) FROM QGPL.DATA ORDER BY ID" : "SELECT COUNT(*),SUM(ID) FROM QGPL.DATA", Options); while (query.Read()) { } Assert.False(fail); }
                catch (CpfException error) { Assert.True(fail); Assert.Equal("22012", error.SqlState); }
                using var early = files.OpenSqlQuery("SELECT ID FROM QGPL.DATA ORDER BY ID", Options); Assert.True(early.Read());
            }
            var connections = new List<Microsoft.Data.Sqlite.SqliteConnection>();
            try
            {
                for (var index = 0; index < 16; index++)
                {
                    var connection = system.Connections.Open(); connections.Add(connection); using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM pragma_function_list WHERE name LIKE 'ipc_q_%'"; Assert.Equal(0L, command.ExecuteScalar());
                }
            }
            finally { foreach (var connection in connections) connection.Dispose(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
