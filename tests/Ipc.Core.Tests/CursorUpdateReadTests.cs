using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    private Job UpdateJob()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "UpdateReadFixture41");
        return _system.Jobs.CreateInteractive("QSECOFR");
    }
    private DatabaseRecordCursor UpdateCursor(TimeSpan? wait = null, CancellationToken cancellation = default, string name = "NAV")
        => _files.OpenUpdateCursor("QGPL", name, name, lockWait: wait, cancellationToken: cancellation);
    private IReadOnlyList<JobLockInfo> UpdateLocks() => new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)).Where(l => l.Lifetime == "UpdateRead").ToArray();
    [Fact]
    public void Update_read_retains_one_physical_record_and_allows_inquiry_from_another_job()
    {
        var first = UpdateJob(); var second = UpdateJob(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key))
        { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(1, "A"))); Assert.True(cursor.HasUpdateLock); Assert.Equal(1, Assert.Single(UpdateLocks()).Resource.RowNumber); }
        try
        {
            using var other = OperationIdentity.Enter("QSECOFR", second.Key); using var command = new JobLockStore(_system.Connections).EnterCommand(second.Key);
            using var inquiry = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(inquiry.Chain(Key(1, "A"))); Assert.Equal(1, Id(inquiry.Current));
            using var update = UpdateCursor(); Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => update.Chain(Key(1, "A"))).MessageId);
            Assert.False(update.HasUpdateLock); Assert.Null(update.Current);
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); cursor.Dispose(); }
    }
    [Fact]
    public void Independent_update_cursors_in_the_same_job_contend_but_a_cursor_can_update_its_own_lock()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
        using var first = UpdateCursor(); using var second = UpdateCursor(); Assert.True(first.Chain(Key(2)));
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => second.Chain(Key(2))).MessageId);
        first.Update(TextChange("retained write")); Assert.False(first.HasUpdateLock); Assert.Equal("retained write", Stored(4)["TEXT"]);
        Assert.True(second.Chain(Key(2))); Assert.True(second.HasUpdateLock);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retained_update_reads_block_ordinary_point_writes_in_the_same_or_another_job(bool otherJob)
    {
        var firstJob = UpdateJob(); var secondJob = otherJob ? UpdateJob() : firstJob; DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", firstJob.Key)) { held = UpdateCursor(); Assert.True(held.Chain(Key(2))); }
        try
        {
            using var other = OperationIdentity.Enter("QSECOFR", secondJob.Key);
            using var point = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(point.Chain(Key(2)));
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => point.Update(TextChange("blocked"))).MessageId);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => point.Delete()).MessageId);
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", firstJob.Key); held.Dispose(); }
    }
    [Theory]
    [InlineData("read")]
    [InlineData("prior")]
    [InlineData("chain")]
    [InlineData("lower")]
    [InlineData("upper")]
    [InlineData("rewind")]
    [InlineData("unlock")]
    [InlineData("invalidate")]
    [InlineData("close")]
    [InlineData("eof")]
    [InlineData("missing")]
    public void Input_and_positioning_boundaries_release_the_previous_update_lock(string action)
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        switch (action)
        {
            case "read": Assert.Equal(7, Id(cursor.Read())); break;
            case "prior": Assert.Equal(3, Id(cursor.ReadPrior())); break;
            case "chain": Assert.True(cursor.Chain(Key(1, "A"))); break;
            case "lower": cursor.SetLowerBound(Key(1)); break;
            case "upper": cursor.SetUpperBound(Key(1)); break;
            case "rewind": cursor.Rewind(); break;
            case "unlock": cursor.Unlock(); break;
            case "invalidate": cursor.InvalidateCurrent(); break;
            case "close": cursor.Dispose(); break;
            case "eof": Assert.True(cursor.Chain(Key(null, null))); Assert.Null(cursor.Read()); break;
            case "missing": Assert.False(cursor.Chain(Key(999))); break;
        }
        Assert.DoesNotContain(UpdateLocks(), l => l.Resource.RowNumber == 4);
        using var other = UpdateCursor(); Assert.True(other.Chain(Key(2)));
    }
    [Fact]
    public void Unlock_preserves_current_values_and_position_but_requires_new_locked_input_before_mutation()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        cursor.Unlock(); Assert.Equal(4, Id(cursor.Current)); Assert.False(cursor.HasUpdateLock);
        Assert.Throws<CpfException>(() => cursor.Update(TextChange("unlocked"))); Assert.Throws<CpfException>(() => cursor.Delete());
        Assert.Equal(7, Id(cursor.Read())); Assert.True(cursor.HasUpdateLock);
    }
    [Theory]
    [InlineData("read")]
    [InlineData("prior")]
    [InlineData("chain")]
    [InlineData("rrn")]
    [InlineData("equal")]
    public void Inquiry_input_on_an_update_cursor_releases_the_previous_lease_and_cannot_supply_an_update_target(string operation)
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        switch (operation)
        {
            case "read": cursor.Read(lockRecord: false); break;
            case "prior": cursor.ReadPrior(lockRecord: false); break;
            case "chain": cursor.Chain(Key(1), lockRecord: false); break;
            case "rrn": cursor.ChainRecord(1, lockRecord: false); break;
            case "equal": cursor.Chain(Key(1)); cursor.ReadEqual(Key(1), prior: false, lockRecord: false); break;
        }
        Assert.NotNull(cursor.Current); Assert.False(cursor.HasUpdateLock); Assert.Empty(UpdateLocks());
        Assert.Throws<CpfException>(() => cursor.Update(TextChange("not locked")));
    }
    [Fact]
    public void Invalid_update_values_keep_the_retained_lock_for_a_valid_retry()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        Assert.Throws<CpfException>(() => cursor.Update(new Dictionary<string, object?> { ["NOFIELD"] = 1 })); Assert.True(cursor.HasUpdateLock);
        cursor.Update(TextChange("retry")); Assert.False(cursor.HasUpdateLock); Assert.Equal("retry", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Successful_delete_releases_the_retained_lease_and_preserves_duplicate_siblings()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(1, "A")));
        cursor.Delete(); Assert.False(cursor.HasUpdateLock); Assert.Equal("row 2", Stored(2)["TEXT"]); Assert.Equal(2, Id(cursor.Read()));
    }
    [Fact]
    public void Relative_record_update_reads_share_the_same_physical_lease()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.ChainRecord(4));
        using var other = UpdateCursor(); Assert.Throws<CpfException>(() => other.Chain(Key(2)));
        cursor.Unlock(); Assert.True(other.Chain(Key(2))); other.Update(TextChange("RRN shared identity")); Assert.Equal("RRN shared identity", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Update_read_requires_a_job_and_update_authority()
    {
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => UpdateCursor()).MessageId);
        _system.Security.Profiles.Create(new UserProfile { Name = "READONLY" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "READONLY", Authorities.UseBits);
        var job = _system.Jobs.CreateInteractive("READONLY"); using var owner = OperationIdentity.Enter("READONLY", job.Key);
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => UpdateCursor()).MessageId);
    }
    [Fact]
    public void Another_execution_context_cannot_unlock_or_rebind_a_retained_lease()
    {
        var first = UpdateJob(); var other = UpdateJob(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2))); }
        try
        {
            using var stranger = OperationIdentity.Enter("QSECOFR", other.Key);
            Assert.Throws<CpfException>(() => cursor.Unlock()); Assert.Throws<CpfException>(() => cursor.RebindCancellation(default)); Assert.True(cursor.HasUpdateLock);
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); cursor.Dispose(); }
    }
    [Fact]
    public void A_timed_out_read_has_no_stale_mutation_target_and_can_retry_the_same_record()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var first = UpdateCursor(); using var second = UpdateCursor(TimeSpan.FromMilliseconds(60));
        Assert.True(first.Chain(Key(2))); Assert.True(second.Chain(Key(1, "B")));
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => second.Read()).MessageId); Assert.False(second.HasUpdateLock); Assert.Null(second.Current);
        Assert.Throws<CpfException>(() => second.Update(TextChange("stale"))); first.Unlock(); Assert.Equal(4, Id(second.Read()));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(32768)]
    public void Invalid_update_read_waits_are_rejected_before_allocating_a_path(int seconds)
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
        Assert.Throws<CpfException>(() => UpdateCursor(TimeSpan.FromSeconds(seconds))); Assert.Empty(UpdateLocks());
    }
    [Fact]
    public void Adding_a_different_record_does_not_release_the_current_update_read()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        Add(99, 3m, "ADDED"); Assert.True(cursor.HasUpdateLock); Assert.Equal(4, Id(cursor.Current));
        using var other = UpdateCursor(); Assert.Throws<CpfException>(() => other.Chain(Key(2)));
    }
    [Fact]
    public void Refresh_preserves_a_locked_target_and_inquiry_refresh_cannot_upgrade_it()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        cursor.RefreshCurrent(); Assert.True(cursor.HasUpdateLock); Assert.Single(UpdateLocks());
        cursor.Unlock(); cursor.RefreshCurrent(); Assert.False(cursor.HasUpdateLock); Assert.Equal(4, Id(cursor.Current));
        Assert.Throws<CpfException>(() => cursor.Update(TextChange("inquiry refresh")));
    }
    [Fact]
    public void Keyed_delete_uses_and_releases_its_own_update_read_scope()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor();
        Assert.True(cursor.Delete(Key(1, "A"))); Assert.False(cursor.HasUpdateLock); Assert.Equal("row 2", Stored(2)["TEXT"]);
        Assert.Equal(2, Id(cursor.Read())); Assert.True(cursor.HasUpdateLock);
    }
    [Fact]
    public void Rebinding_call_cancellation_does_not_drop_a_completed_update_read_lease()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cancel = new CancellationTokenSource();
        using var cursor = UpdateCursor(cancellation: cancel.Token); Assert.True(cursor.Chain(Key(2))); cancel.Cancel();
        cursor.RebindCancellation(default); Assert.True(cursor.HasUpdateLock); cursor.Update(TextChange("rebound update")); Assert.False(cursor.HasUpdateLock);
    }
    [Fact]
    public void An_update_open_honors_another_jobs_no_update_file_allocation()
    {
        var first = UpdateJob(); var second = UpdateJob(); IDisposable allocation;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) allocation = new JobLockStore(_system.Connections).Acquire(first.Key,
            new("QGPL", "NAV", ObjectType.File), JobLockMode.SharedNoUpdate, TimeSpan.Zero);
        try
        {
            using var other = OperationIdentity.Enter("QSECOFR", second.Key);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => UpdateCursor()).MessageId);
        }
        finally { allocation.Dispose(); }
    }

}
