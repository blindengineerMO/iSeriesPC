using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlTableTests
{
    [Fact]
    public void Statement_record_sets_protect_selected_records_and_share_the_normal_object_lock_matrix()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3),(4),(5)"); var locks = new JobLockStore(_system.Connections);
        var a = _system.Jobs.CreateInteractive("QSECOFR"); var b = _system.Jobs.CreateInteractive("QSECOFR");
        var resource = new JobLockResource("QGPL", "DATA", ObjectType.File, "DATA");
        using (locks.AcquireStatementRows(a.Key, resource, new HashSet<long> { 1, 3, 5 }, AuthorityBit.Update, CancellationToken.None))
        {
            foreach (var row in new long[] { 1, 3, 5 })
                Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => locks.Acquire(b.Key, resource with { RowNumber = row }, JobLockMode.SharedRead, TimeSpan.Zero)).MessageId);
            using (locks.Acquire(b.Key, resource with { RowNumber = 2 }, JobLockMode.Exclusive, TimeSpan.Zero)) { }
            using (locks.Acquire(b.Key, resource with { Member = "" }, JobLockMode.SharedRead, TimeSpan.Zero)) { }
            Assert.Throws<CpfException>(() => locks.Acquire(b.Key, resource with { Member = "" }, JobLockMode.ExclusiveRead, TimeSpan.Zero));
            using (locks.AcquireStatementRows(b.Key, resource, new HashSet<long> { 2, 4 }, AuthorityBit.Update, CancellationToken.None)) { }
            Assert.Throws<CpfException>(() => locks.AcquireStatementRows(b.Key, resource, new HashSet<long> { 2, 5 }, AuthorityBit.Update, CancellationToken.None));
            using (OperationIdentity.Enter("QSECOFR", a.Key))
            using (locks.Acquire(a.Key, resource with { RowNumber = 3 }, JobLockMode.Exclusive, TimeSpan.Zero)) { }
            Assert.Single(locks.Inspect(resource with { Member = "" }));
        }
        using (locks.Acquire(b.Key, resource with { RowNumber = 3 }, JobLockMode.Exclusive, TimeSpan.Zero)) { }
        Assert.Empty(locks.Inspect(resource with { Member = "" }));
        Assert.Empty(locks.Inspect(new("QSYS", "QGPL", ObjectType.Library)));
    }

    [Fact]
    public void A_large_record_set_uses_one_holder_and_releasing_it_removes_all_membership_rows()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3),(4),(5)"); var locks = new JobLockStore(_system.Connections);
        var job = _system.Jobs.CreateInteractive("QSECOFR"); var resource = new JobLockResource("QGPL", "DATA", ObjectType.File, "DATA");
        using (locks.AcquireStatementRows(job.Key, resource, Enumerable.Range(1, 10000).Select(i => (long)i).ToHashSet(), AuthorityBit.Delete, CancellationToken.None))
        {
            Assert.Single(locks.Inspect(resource with { Member = "" }));
            using var connection = _system.Connections.Locks.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM holder_records"; Assert.Equal(10000L, command.ExecuteScalar());
            command.CommandText = "SELECT count(*) FROM holders"; Assert.Equal(2L, command.ExecuteScalar());
        }
        using var after = _system.Connections.Locks.Open(); using var count = after.CreateCommand();
        count.CommandText = "SELECT count(*) FROM holder_records"; Assert.Equal(0L, count.ExecuteScalar());
        count.CommandText = "SELECT count(*) FROM holders"; Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void Independent_update_read_cursors_of_the_same_job_block_statement_mutations()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3),(4),(5)"); var locks = new JobLockStore(_system.Connections);
        var job = _system.Jobs.CreateInteractive("QSECOFR"); var resource = new JobLockResource("QGPL", "DATA", ObjectType.File, "DATA", 1);
        using (locks.AcquireWithLifetime(job.Key, resource, JobLockMode.SharedRead, TimeSpan.Zero, CancellationToken.None, "UpdateRead", "cursor"))
        {
            Assert.Throws<CpfException>(() => locks.AcquireStatementRows(job.Key, resource with { RowNumber = 0 }, new HashSet<long> { 1 }, AuthorityBit.Update, CancellationToken.None));
            using (locks.AcquireStatementRows(job.Key, resource with { RowNumber = 0 }, new HashSet<long> { 2 }, AuthorityBit.Update, CancellationToken.None)) { }
        }
        Assert.Empty(locks.Inspect(resource with { Member = "", RowNumber = 0 }));
    }

    [Fact]
    public void Cancellation_and_ended_job_recovery_remove_record_set_memberships_and_parent_holders()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3),(4),(5)"); var locks = new JobLockStore(_system.Connections);
        var job = _system.Jobs.CreateInteractive("QSECOFR"); var resource = new JobLockResource("QGPL", "DATA", ObjectType.File, "DATA");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => locks.AcquireStatementRows(job.Key, resource, new HashSet<long> { 1 }, AuthorityBit.Update, cancel.Token));
        Assert.Empty(locks.Inspect(new("QSYS", "QGPL", ObjectType.Library)));
        using var lease = locks.AcquireStatementRows(job.Key, resource, new HashSet<long> { 1, 2 }, AuthorityBit.Update, CancellationToken.None);
        _system.Jobs.RequestEnd(job.Key); locks.RecoverEndedJobs();
        Assert.Single(locks.Inspect(resource with { Member = "" }));
        _system.Jobs.Complete(job.Key); locks.RecoverEndedJobs();
        Assert.Empty(locks.Inspect(resource with { Member = "" }));
        using var connection = _system.Connections.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM holder_records"; Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Record_set_acquisition_respects_earlier_waiters_and_own_exclusive_coverage()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2)");
        var locks = new JobLockStore(_system.Connections);
        var a = _system.Jobs.CreateInteractive("QSECOFR"); var b = _system.Jobs.CreateInteractive("QSECOFR"); var c = _system.Jobs.CreateInteractive("QSECOFR");
        var resource = new JobLockResource("QGPL", "DATA", ObjectType.File, "DATA", 1);
        using var held = locks.Acquire(a.Key, resource, JobLockMode.Exclusive, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waiting = Task.Factory.StartNew(() => locks.Acquire(b.Key, resource, JobLockMode.SharedRead, Timeout.InfiniteTimeSpan, cancellation.Token),
            cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            for (var attempt = 0; attempt < 500 && !locks.Inspect(resource).Any(item => item.Waiting); attempt++) await Task.Delay(10);
            Assert.Contains(locks.Inspect(resource), item => item.Waiting);
            using (locks.AcquireStatementRows(a.Key, resource with { RowNumber = 0 }, new HashSet<long> { 1, 2 }, AuthorityBit.Update, cancellation.Token))
            {
                held.Dispose();
                Assert.Throws<CpfException>(() => locks.AcquireStatementRows(c.Key, resource with { RowNumber = 0 }, new HashSet<long> { 1 }, AuthorityBit.Update, cancellation.Token));
            }
            using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { cancellation.Cancel(); held.Dispose(); try { using var cleanup = await waiting; } catch (OperationCanceledException) { } }
    }
}
