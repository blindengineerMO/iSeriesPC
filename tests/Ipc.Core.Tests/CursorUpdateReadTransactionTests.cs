using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Retained_update_input_rejects_enlisted_workspaces_until_commitment_read_lock_policy_is_implemented()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key); Begin();
        Assert.Throws<CpfException>(() => _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"));
        _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Late_enlistment_cannot_silently_change_the_retained_update_read_contract()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read()); Assert.True(cursor.HasUpdateLock);
        Begin(); Assert.Throws<CpfException>(() => cursor.Read()); Assert.False(cursor.HasUpdateLock); Assert.Null(cursor.Current);
        _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Retained_update_leases_are_shared_across_reopened_factories()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read());
        using var other = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(other, _system.Objects);
        using var contender = files.OpenUpdateCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => contender.Read()).MessageId);
        cursor.Unlock(); Assert.NotNull(contender.Read()); Assert.True(contender.HasUpdateLock);
    }
    [Fact]
    public void Disposing_the_owning_factory_closes_update_cursors_and_releases_persistent_leases()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        var factory = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(factory, _system.Objects);
        var cursor = files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read()); Assert.True(cursor.HasUpdateLock);
        factory.Dispose(); Assert.False(cursor.HasUpdateLock); Assert.Throws<ObjectDisposedException>(() => cursor.Read()); cursor.Dispose();
        using var other = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(other.Read());
    }
    [Fact]
    public async Task Factory_disposal_cancels_a_live_update_read_wait_before_closing_its_leases()
    {
        var readerJob = _system.Jobs.CreateInteractive("QSECOFR"); DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) { held = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(held.Read()); }
        using var factory = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(factory, _system.Objects);
        try
        {
            var waiting = Task.Run(() =>
            {
                using var owner = OperationIdentity.Enter("QSECOFR", readerJob.Key);
                using var cursor = files.OpenUpdateCursor("QGPL", "FIRST", "FIRST", lockWait: TimeSpan.FromMinutes(3));
                Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read());
            });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)).Any(l => l.Waiting))
            { Assert.True(DateTime.UtcNow < deadline, "Update read must become a live waiter."); await Task.Delay(20); }
            factory.Dispose(); await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)), l => l.Job == readerJob.Key);
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", _job.Key); held.Dispose(); }
    }

    [Fact]
    public void Another_jobs_commitment_writer_cannot_stage_a_change_over_a_retained_update_read()
    {
        var readerJob = _system.Jobs.CreateInteractive("QSECOFR"); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", readerJob.Key)) { cursor = _files.OpenUpdateCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read()); }
        try
        {
            using var writer = OperationIdentity.Enter("QSECOFR", _job.Key); Begin();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Change("FIRST", "blocked by reader"));
            Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
            _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key);
        }
        finally { using var reader = OperationIdentity.Enter("QSECOFR", readerJob.Key); cursor.Dispose(); }
        Assert.Equal("original", Sql("SELECT TEXT FROM [QGPL.FIRST.FIRST]"));
    }

}
