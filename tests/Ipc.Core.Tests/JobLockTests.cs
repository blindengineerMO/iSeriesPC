using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobLockTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly JobLockStore _locks;
    private readonly Job _a, _b;
    private readonly JobLockResource _resource = new("QGPL", "DATA", ObjectType.File);
    public JobLockTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ChangedPassword2"); _locks = new(_system.Connections);
        _a = _system.Jobs.CreateInteractive("QSECOFR"); _b = _system.Jobs.CreateInteractive("QSECOFR");
        new SqliteFileStore(_system.Connections, _system.Objects).CreateSourceFile("QGPL", "DATA");
    }
    public static IEnumerable<object[]> Modes() =>
        from left in Enum.GetValues<JobLockMode>() from right in Enum.GetValues<JobLockMode>()
        select new object[] { left, right };
    [Theory, MemberData(nameof(Modes))]
    public void Compatibility_is_enforced_for_other_jobs(JobLockMode left, JobLockMode right)
    {
        using var held = _locks.Acquire(_a.Key, _resource, left, TimeSpan.Zero);
        var expected = left != JobLockMode.Exclusive && right != JobLockMode.Exclusive &&
            (left == JobLockMode.SharedRead || right == JobLockMode.SharedRead || left == right);
        if (expected) { using var second = _locks.Acquire(_b.Key, _resource, right, TimeSpan.Zero); }
        else Assert.Contains("CPF1002", Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, _resource, right, TimeSpan.Zero)).Message);
    }
    [Fact]
    public async Task Waiters_are_visible_and_cancelled_and_timed_out_requests_are_removed()
    {
        using var held = _locks.Acquire(_a.Key, _resource, JobLockMode.Exclusive, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var waiting = Task.Run(() => _locks.Acquire(_b.Key, _resource, JobLockMode.SharedRead, TimeSpan.FromSeconds(5), cancellation.Token));
        await WaitFor(() => _locks.Inspect(_resource).Any(x => x.Waiting));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.DoesNotContain(_locks.Inspect(_resource), x => x.Waiting);
        Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, _resource, JobLockMode.SharedRead, TimeSpan.FromMilliseconds(50)));
        Assert.DoesNotContain(_locks.Inspect(_resource), x => x.Waiting);
    }
    [Fact]
    public async Task Deadlock_rejects_newest_request_and_releasing_holder_wakes_survivor()
    {
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "SECOND"), ObjectType = ObjectType.File });
        var second = _resource with { Name = "SECOND" };
        using var firstHeld = _locks.Acquire(_a.Key, _resource, JobLockMode.Exclusive, TimeSpan.Zero);
        var secondHeld = _locks.Acquire(_b.Key, second, JobLockMode.Exclusive, TimeSpan.Zero);
        var waiting = Task.Run(() => _locks.Acquire(_a.Key, second, JobLockMode.Exclusive, TimeSpan.FromSeconds(5)));
        await WaitFor(() => _locks.Inspect(second).Any(x => x.Waiting));
        try
        {
            var error = Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, _resource, JobLockMode.Exclusive, TimeSpan.FromSeconds(2)));
            Assert.Contains("CPF1003", error.Message);
        }
        finally { secondHeld.Dispose(); }
        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task Existing_holder_can_reenter_without_bypassing_conflicting_holders()
    {
        var held = _locks.Acquire(_a.Key, _resource, JobLockMode.SharedUpdate, TimeSpan.Zero);
        var waiting = Task.Run(() => _locks.Acquire(_b.Key, _resource, JobLockMode.Exclusive, TimeSpan.FromSeconds(5)));
        await WaitFor(() => _locks.Inspect(_resource).Any(x => x.Waiting));
        using (var reentry = _locks.Acquire(_a.Key, _resource, JobLockMode.SharedRead, TimeSpan.Zero)) { }
        held.Dispose(); using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public void Typed_identity_job_ownership_and_end_cleanup_are_enforced()
    {
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DATA"), ObjectType = ObjectType.Program });
        using var held = _locks.Acquire(_a.Key, _resource, JobLockMode.Exclusive, TimeSpan.Zero);
        using var otherType = _locks.Acquire(_b.Key, _resource with { Type = ObjectType.Program }, JobLockMode.Exclusive, TimeSpan.Zero);
        using (OperationIdentity.Enter("QSECOFR", _a.Key))
            Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, _resource, JobLockMode.SharedRead, TimeSpan.Zero));
        _system.Jobs.RequestEnd(_a.Key); _locks.RecoverEndedJobs();
        Assert.Contains(_locks.Inspect(_resource), x => x.Job == _a.Key);
        _system.Jobs.Complete(_a.Key); Assert.Empty(_locks.Inspect(_resource));
    }
    [Fact]
    public void Actual_command_access_obeys_object_locks_and_releases_automatic_locks()
    {
        using var execution = new ExecutionSession(_system, _b, CancellationToken.None);
        var held = _locks.Acquire(_a.Key, _resource, JobLockMode.Exclusive, TimeSpan.Zero);
        var denied = execution.Execute("DSPPFM FILE(QGPL/DATA)"); Assert.True(denied.IsError); Assert.Contains("CPF1002", denied.Message);
        held.Dispose(); var result = execution.Execute("DSPPFM FILE(QGPL/DATA)"); Assert.False(result.IsError, result.Message);
        Assert.Empty(_locks.Inspect(_resource));
    }
    [Fact]
    public void Lock_commands_inspect_release_and_rollback_multiple_allocations()
    {
        using var execution = new ExecutionSession(_system, _a, CancellationToken.None);
        var result = execution.Execute("ALCOBJ OBJ((QGPL/DATA *FILE *EXCL)) WAIT(0)"); Assert.False(result.IsError, result.Message);
        using var other = new ExecutionSession(_system, _b, CancellationToken.None);
        result = other.Execute("WRKOBJLCK OBJ(QGPL/DATA) OBJTYPE(*FILE)"); Assert.False(result.IsError, result.Message);
        Assert.Contains(result.Listing!, x => x.Contains("HELD *EXCL"));
        result = execution.Execute("RNMOBJ OBJ(QGPL/DATA) OBJTYPE(*FILE) NEWOBJ(RENAMED)"); Assert.True(result.IsError);
        result = execution.Execute("DLCOBJ OBJ((QGPL/DATA *FILE *EXCL))"); Assert.False(result.IsError, result.Message);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "SECOND"), ObjectType = ObjectType.File });
        using var held = _locks.Acquire(_b.Key, _resource with { Name = "SECOND" }, JobLockMode.Exclusive, TimeSpan.Zero);
        result = execution.Execute("ALCOBJ OBJ((QGPL/DATA *FILE *EXCL) (QGPL/SECOND *FILE *EXCL)) WAIT(0)");
        Assert.True(result.IsError); Assert.Empty(_locks.Inspect(_resource));
    }
    [Fact]
    public void Record_locks_protect_actual_reads_updates_deletes_and_clear_without_blocking_other_rows()
    {
        var files = new SqliteFileStore(_system.Connections, _system.Objects);
        var format = new Ipc.Db.Definitions.RecordFormat { Name = "ROWS", Fields = new() {
            new() { Name = "ID", Type = Ipc.Db.Definitions.FieldType.Binary, Length = 4, Sequence = 1 },
            new() { Name = "VALUE", Type = Ipc.Db.Definitions.FieldType.Alpha, Length = 20 } } };
        format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "ROWS", new() { Name = "ROWS", Attribute = Ipc.Db.Definitions.FileAttribute.Physical, Formats = new() { format } }, "");
        Dictionary<string, object?> Row(int id, string value) => new() { ["ID"] = id, ["VALUE"] = value };
        files.Insert("QGPL", "ROWS", "ROWS", "ROWS", Row(1, "first")); files.Insert("QGPL", "ROWS", "ROWS", "ROWS", Row(2, "second"));
        var resource = new JobLockResource("QGPL", "ROWS", ObjectType.File, "ROWS", 1);
        using var held = _locks.Acquire(_a.Key, resource, JobLockMode.Exclusive, TimeSpan.Zero);
        using (OperationIdentity.Enter("QSECOFR", _b.Key))
        using (_locks.EnterCommand(_b.Key))
        {
            Assert.Throws<CpfException>(() => files.ReadKeyPrefix("QGPL", "ROWS", "ROWS", Row(1, "")));
            Assert.Single(files.ReadKeyPrefix("QGPL", "ROWS", "ROWS", Row(2, "")));
            Assert.Throws<CpfException>(() => files.Update("QGPL", "ROWS", "ROWS", "ROWS", Row(1, "denied")));
            files.Update("QGPL", "ROWS", "ROWS", "ROWS", Row(2, "updated"));
            Assert.Throws<CpfException>(() => files.Delete("QGPL", "ROWS", "ROWS", "ROWS", Row(1, "")));
            Assert.Throws<CpfException>(() => files.ClearMember("QGPL", "ROWS", "ROWS"));
        }
        Assert.Equal("first", files.ReadKeyPrefix("QGPL", "ROWS", "ROWS", Row(1, "")).Single()["VALUE"]);
        Assert.Equal("updated", files.ReadKeyPrefix("QGPL", "ROWS", "ROWS", Row(2, "")).Single()["VALUE"]);
        Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, resource with { RowNumber = 99 }, JobLockMode.Exclusive, TimeSpan.Zero));
    }
    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("dispose")]
    public void Transaction_finishes_data_before_releasing_commitment_allocations(string finish)
    {
        using var connection = _system.Connections.Open();
        using (var create = connection.CreateCommand()) { create.CommandText = "CREATE TABLE lock_effect(value INTEGER)"; create.ExecuteNonQuery(); }
        var transaction = connection.BeginTransaction();
        using var commitment = _locks.EnlistTransaction(_a.Key, transaction);
        commitment.Acquire(_resource, JobLockMode.Exclusive);
        Assert.Contains(_locks.Inspect(_resource), x => x.Lifetime == "Commitment");
        using (var write = connection.CreateCommand()) { write.Transaction = transaction; write.CommandText = "INSERT INTO lock_effect VALUES(42)"; write.ExecuteNonQuery(); }
        Assert.Throws<CpfException>(() => _locks.Acquire(_b.Key, _resource, JobLockMode.SharedRead, TimeSpan.Zero));
        if (finish == "commit") commitment.Commit(); else if (finish == "rollback") commitment.Rollback(); else commitment.Dispose();
        Assert.Empty(_locks.Inspect(_resource));
        using var acquired = _locks.Acquire(_b.Key, _resource, JobLockMode.Exclusive, TimeSpan.Zero);
        using var read = connection.CreateCommand(); read.CommandText = "SELECT count(*) FROM lock_effect";
        Assert.Equal(finish == "commit" ? 1L : 0L, (long)read.ExecuteScalar()!);
    }
    [Fact]
    public async Task Later_reader_cannot_pass_an_older_exclusive_waiter()
    {
        var third = _system.Jobs.CreateInteractive("QSECOFR");
        var held = _locks.Acquire(_a.Key, _resource, JobLockMode.SharedRead, TimeSpan.Zero);
        var waiting = Task.Run(() => _locks.Acquire(_b.Key, _resource, JobLockMode.Exclusive, TimeSpan.FromSeconds(5)));
        await WaitFor(() => _locks.Inspect(_resource).Any(x => x.Waiting));
        try { Assert.Throws<CpfException>(() => _locks.Acquire(third.Key, _resource, JobLockMode.SharedRead, TimeSpan.Zero)); }
        finally { held.Dispose(); }
        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public void Failed_commit_retains_locks_until_rollback()
    {
        using var connection = _system.Connections.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE lock_parent(id INTEGER PRIMARY KEY); CREATE TABLE lock_child(id INTEGER REFERENCES lock_parent(id) DEFERRABLE INITIALLY DEFERRED)";
            create.ExecuteNonQuery();
        }
        var transaction = connection.BeginTransaction(); using var commitment = _locks.EnlistTransaction(_a.Key, transaction);
        commitment.Acquire(_resource, JobLockMode.Exclusive);
        using (var write = connection.CreateCommand()) { write.Transaction = transaction; write.CommandText = "INSERT INTO lock_child VALUES(99)"; write.ExecuteNonQuery(); }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => commitment.Commit());
        Assert.Contains(_locks.Inspect(_resource), x => x.Lifetime == "Commitment");
        commitment.Rollback(); Assert.Empty(_locks.Inspect(_resource));
        using var read = connection.CreateCommand(); read.CommandText = "SELECT count(*) FROM lock_child"; Assert.Equal(0L, (long)read.ExecuteScalar()!);
    }
    internal static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    public void Dispose() => _system.Dispose();
}
