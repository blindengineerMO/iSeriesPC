using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Record_wait_attributes_survive_restart_copy_and_legacy_JSON_without_a_catalog_migration()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "FIRST", null, 17);
        Sql("UPDATE sys_file_defs SET def=json_remove(def,'$.recordLockWaitSeconds') WHERE lib='QGPL' AND name='SECOND'");
        _system.Dispose();
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var files = new SqliteFileStore(reopened.Connections, reopened.Objects);
        Assert.Equal(17, files.GetDefinition("QGPL", "FIRST")!.RecordLockWaitSeconds);
        Assert.Equal(60, files.GetDefinition("QGPL", "SECOND")!.RecordLockWaitSeconds);
        reopened.ObjectOperations.Relocate(First, ObjectType.File, new("QGPL", "COPY"), copy: true);
        Assert.Equal(17, files.GetDefinition("QGPL", "COPY")!.RecordLockWaitSeconds); Assert.Equal(1, files.RowCount("QGPL", "COPY", "FIRST"));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Long_and_indefinite_waiters_across_factories_keep_their_deadline_and_cancel_cleanly(bool indefinite, bool shutdown)
    {
        var reader = _system.Jobs.CreateInteractive("QSECOFR"); DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) { held = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(held.Read()); }
        using var factory = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(factory, _system.Objects);
        using var cancellation = new CancellationTokenSource();
        var running = Task.Run(() =>
        {
            using var owner = OperationIdentity.Enter("QSECOFR", reader.Key);
            using var cursor = files.OpenUpdateCursor("QGPL", "FIRST", "FIRST", lockWait: indefinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(32767), cancellationToken: cancellation.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read());
        });
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)).Any(l => l.Waiting && l.Job == reader.Key))
            { Assert.True(DateTime.UtcNow < deadline, "A long record wait must be visible across connection factories."); await Task.Delay(20); }
            using (var connection = _system.Connections.Locks.Open())
            {
                using var command = connection.CreateCommand(); command.CommandText = "SELECT expires_at FROM waiters WHERE job_number=$job";
                command.Parameters.AddWithValue("$job", reader.Key.Number); var expires = (long)command.ExecuteScalar()!;
                if (indefinite) Assert.Equal(long.MaxValue, expires);
                else Assert.True(expires > DateTimeOffset.UtcNow.AddSeconds(32700).ToUnixTimeMilliseconds());
            }
            if (shutdown) factory.Dispose(); else cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)), l => l.Job == reader.Key);
        }
        finally
        {
            cancellation.Cancel(); using (OperationIdentity.Enter("QSECOFR", _job.Key)) held.Unlock();
            await running.WaitAsync(TimeSpan.FromSeconds(10)); held.Dispose();
        }
    }
}
