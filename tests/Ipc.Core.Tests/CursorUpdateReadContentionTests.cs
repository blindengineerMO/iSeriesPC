using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    private async Task AwaitUpdateWaiter()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)).Any(l => l.Waiting)) return;
            await Task.Delay(20);
        }
        Assert.Fail("An update-read waiter did not appear within 15 seconds.");
    }
    [Fact]
    public async Task A_waiting_update_read_gets_the_fresh_image_and_does_not_block_the_owners_write_upgrade()
    {
        var first = UpdateJob(); var second = UpdateJob(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2))); }
        try
        {
            var waiting = Task.Run(() =>
            {
                using var owner = OperationIdentity.Enter("QSECOFR", second.Key); using var other = UpdateCursor(TimeSpan.FromSeconds(20));
                Assert.True(other.Chain(Key(2))); return other.Current!["TEXT"];
            });
            await AwaitUpdateWaiter();
            using (OperationIdentity.Enter("QSECOFR", first.Key)) cursor.Update(TextChange("fresh after wait"));
            Assert.Equal("fresh after wait", await waiting.WaitAsync(TimeSpan.FromSeconds(25)));
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); cursor.Dispose(); }
    }
    [Fact]
    public async Task Cancelled_update_read_waits_remove_the_waiter_and_leave_no_record_lease()
    {
        var first = UpdateJob(); var second = UpdateJob(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2))); }
        using var cancel = new CancellationTokenSource();
        try
        {
            var waiting = Task.Run(() =>
            {
                using var owner = OperationIdentity.Enter("QSECOFR", second.Key); using var other = UpdateCursor(TimeSpan.FromSeconds(20), cancel.Token);
                Assert.ThrowsAny<OperationCanceledException>(() => other.Chain(Key(2))); Assert.False(other.HasUpdateLock);
            });
            await AwaitUpdateWaiter(); cancel.Cancel(); await waiting.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)), l => l.Waiting || l.Job == second.Key);
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); cursor.Dispose(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timed_out_sequential_input_retries_the_blocked_position_even_if_records_are_inserted_before_it(bool prior)
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var held = UpdateCursor(); using var cursor = UpdateCursor();
        Assert.True(held.Chain(Key(2))); Assert.True(cursor.Chain(prior ? Key(9007199254740992.01m) : Key(1, "B")));
        Assert.Throws<CpfException>(() => { if (prior) cursor.ReadPrior(); else cursor.Read(); });
        Add(99, prior ? 3 : 1.5m, "RETRY"); held.Unlock();
        Assert.Equal(4, Id(prior ? cursor.ReadPrior() : cursor.Read()));
    }
    [Fact]
    public void A_failed_locked_CHAIN_requires_explicit_repositioning_before_sequential_input()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var held = UpdateCursor(); using var cursor = UpdateCursor();
        Assert.True(held.Chain(Key(2))); Assert.True(cursor.Chain(Key(1)));
        Assert.Throws<CpfException>(() => cursor.Chain(Key(2))); held.Unlock(); Assert.Throws<CpfException>(() => cursor.Read());
        cursor.SetLowerBound(Key(2)); Assert.Equal(4, Id(cursor.Read()));
    }
    [Fact]
    public void Revoked_update_authority_does_not_prevent_the_owner_from_releasing_a_retained_lease()
    {
        _system.Security.Profiles.Create(new() { Name = "UPDUSER" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "UPDUSER", Authorities.ChangeBits);
        var job = _system.Jobs.CreateInteractive("UPDUSER"); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("UPDUSER", job.Key)) { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2))); }
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "UPDUSER", AuthorityBit.None);
        using var owner = OperationIdentity.Enter("UPDUSER", job.Key);
        try { cursor.Unlock(); Assert.False(cursor.HasUpdateLock); Assert.Throws<CpfException>(() => cursor.Read()); }
        finally { cursor.Dispose(); }
    }
    [Fact]
    public void Logical_update_input_locks_the_physical_source_record()
    {
        var view = DescendingView(); var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
        using var logical = UpdateCursor(name: view); Assert.True(logical.Chain(Key(2))); using var physical = UpdateCursor();
        Assert.Throws<CpfException>(() => physical.ChainRecord(4)); logical.Update(TextChange("logical retained"));
        Assert.True(physical.ChainRecord(4)); Assert.Equal("logical retained", physical.Current!["TEXT"]);
    }
    [Fact]
    public void A_failed_trigger_keeps_the_read_lock_until_a_valid_current_record_retry_succeeds()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
        Sql("CREATE TRIGGER fail_locked_write BEFORE UPDATE ON \"QGPL.NAV.NAV\" BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cursor.Update(TextChange("failed"))); Assert.True(cursor.HasUpdateLock);
        Sql("DROP TRIGGER fail_locked_write"); cursor.Update(TextChange("retry after trigger")); Assert.False(cursor.HasUpdateLock);
    }
    [Fact]
    public void Retained_update_leases_are_released_at_job_end()
    {
        var job = UpdateJob(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", job.Key)) { cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2))); }
        Assert.Contains(new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)), l => l.Lifetime == "UpdateRead" && l.Job == job.Key);
        _system.Jobs.Complete(job.Key); Assert.False(cursor.HasUpdateLock);
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)), l => l.Job == job.Key);
        using var ended = OperationIdentity.Enter("QSECOFR", job.Key);
        Assert.Throws<CpfException>(() => cursor.Read()); cursor.Dispose();
    }
    [Fact]
    public async Task Update_authority_is_rechecked_after_a_record_lock_wait()
    {
        var first = UpdateJob(); _system.Security.Profiles.Create(new() { Name = "WAITUSER" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "WAITUSER", Authorities.ChangeBits);
        var second = _system.Jobs.CreateInteractive("WAITUSER"); DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { held = UpdateCursor(); Assert.True(held.Chain(Key(2))); }
        try
        {
            var waiting = Task.Run(() =>
            {
                using var owner = OperationIdentity.Enter("WAITUSER", second.Key); using var cursor = UpdateCursor(TimeSpan.FromSeconds(20));
                Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.Chain(Key(2))).MessageId); Assert.False(cursor.HasUpdateLock);
            });
            await AwaitUpdateWaiter(); _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "WAITUSER", AuthorityBit.None);
            using (OperationIdentity.Enter("QSECOFR", first.Key)) held.Unlock();
            await waiting.WaitAsync(TimeSpan.FromSeconds(25)); Assert.Empty(UpdateLocks());
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); held.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Direct_file_mutations_outside_a_command_scope_honor_retained_update_reads(bool otherJob, bool delete)
    {
        var first = UpdateJob(); var writerJob = otherJob ? UpdateJob() : first; DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { held = UpdateCursor(); Assert.True(held.Chain(Key(2))); }
        try
        {
            using var writer = OperationIdentity.Enter("QSECOFR", writerJob.Key);
            var values = Key(2, "A"); values["TEXT"] = "blocked";
            var error = delete ? Assert.Throws<CpfException>(() => _files.Delete("QGPL", "NAV", "NAV", "REC", Key(2, "A"))) :
                Assert.Throws<CpfException>(() => _files.Update("QGPL", "NAV", "NAV", "REC", values));
            Assert.Equal("CPF1002", error.MessageId); Assert.Equal("row 4", Stored(4)["TEXT"]);
            Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)), l => l.Lifetime == "Command");
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); held.Dispose(); }
    }
    [Fact]
    public void A_blocked_direct_bulk_write_rolls_back_and_releases_leases_for_earlier_duplicate_rows()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key); using var held = UpdateCursor(); Assert.True(held.ChainRecord(2));
        var values = Key(1, "A"); values["TEXT"] = "must roll back";
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _files.Update("QGPL", "NAV", "NAV", "REC", values)).MessageId);
        Assert.Equal("row 1", Stored(1)["TEXT"]); Assert.Equal("row 2", Stored(2)["TEXT"]);
        using var earlier = UpdateCursor(); Assert.True(earlier.ChainRecord(1));
    }
    [Fact]
    public void A_successful_direct_record_write_releases_its_automatic_command_scope()
    {
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
        var values = Key(2, "A"); values["TEXT"] = "written directly"; _files.Update("QGPL", "NAV", "NAV", "REC", values);
        Assert.Equal("written directly", Stored(4)["TEXT"]);
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "NAV", ObjectType.File)), l => l.Lifetime == "Command");
        using var cursor = UpdateCursor(); Assert.True(cursor.Chain(Key(2)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Direct_clear_and_replace_copy_honor_a_retained_update_read_without_an_enclosing_command(bool otherJob, bool copy)
    {
        var first = UpdateJob(); var second = otherJob ? UpdateJob() : first; DatabaseRecordCursor held;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { held = UpdateCursor(); Assert.True(held.Chain(Key(2))); }
        try
        {
            using var writer = OperationIdentity.Enter("QSECOFR", second.Key);
            var failure = copy ? Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL", "NAV"), "NAV", new("QGPL", "NAV"), "NAV", replace: true)) :
                Assert.Throws<CpfException>(() => _files.ClearMember("QGPL", "NAV", "NAV"));
            Assert.Equal("CPF1002", failure.MessageId); Assert.Equal(8, _files.RowCount("QGPL", "NAV", "NAV"));
        }
        finally { using var owner = OperationIdentity.Enter("QSECOFR", first.Key); held.Dispose(); }
    }
    [Fact]
    public void Direct_source_save_cannot_replace_a_member_with_a_retained_input_lock()
    {
        _files.CreateSourceFile("QGPL", "SRCLOCK", sourceWidth: 80); _files.SaveSourceMember("QGPL", "SRCLOCK", "MEMBER", "old source", null);
        var job = UpdateJob(); using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
        using var cursor = _files.OpenUpdateCursor("QGPL", "SRCLOCK", "MEMBER", keyed: false); Assert.NotNull(cursor.Read());
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _files.SaveSourceMember("QGPL", "SRCLOCK", "MEMBER", "changed", null)).MessageId);
        Assert.Contains("old source", _files.ReadSourceMember("QGPL", "SRCLOCK", "MEMBER").Source);
    }

}
