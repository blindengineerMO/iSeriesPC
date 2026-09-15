using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class SequentialCursorTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public SequentialCursorTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        var format = new RecordFormat { Name = "REC", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 29, Decimals = 2, NullCapable = true, Sequence = 1 },
            new() { Name = "LABEL", Type = FieldType.Alpha, Length = 8, NullCapable = true, Sequence = 2 }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "DATA", new() { Name = "DATA", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
    }
    private void Add(int id, decimal? amount, string? label, string member = "DATA") => _files.Insert("QGPL", "DATA", member, "REC", new Dictionary<string, object?> { ["ID"] = id, ["AMOUNT"] = amount, ["LABEL"] = label });
    private string Logical(bool descending = false)
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/DATA)"),
            LogicalFileTests.Line('K', "AMOUNT", descending ? "DESCEND" : ""), LogicalFileTests.Line('K', "LABEL", descending ? "DESCEND" : ""),
            LogicalFileTests.Line('S', "ID", "COMP(GT 0)"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source); return "VIEW";
    }
    private static long[] Read(DatabaseRecordCursor cursor)
    {
        var ids = new List<long>();
        while (cursor.Read() is { } row) ids.Add((long)row["ID"]!);
        return ids.ToArray();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nullable_exact_keys_and_duplicate_ties_follow_live_access_path(bool descending)
    {
        Add(1, null, null); Add(2, 0, null); Add(3, 0, "A"); Add(4, -1, "A"); Add(5, null, "A"); Add(6, 0, "A");
        Add(7, 9007199254740992.01m, "A"); Add(8, 9007199254740992.02m, "A"); Add(-1, 0, "A");
        var view = Logical(descending);
        using var cursor = _files.OpenSequentialCursor("QGPL", view, view);
        Assert.Equal(descending ? new long[] { 1, 5, 8, 7, 2, 3, 6, 4 } : new long[] { 4, 3, 6, 2, 7, 8, 5, 1 }, Read(cursor));
        Assert.Null(cursor.Read());
    }
    [Fact]
    public void Physical_rrn_order_and_logical_member_ties_are_preserved()
    {
        Add(1, 2, "A"); Add(2, 1, "B"); _files.AddMember("QGPL", "DATA", "OTHER"); Add(3, 1, "B", "OTHER");
        using (var physical = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false)) Assert.Equal(new long[] { 1, 2 }, Read(physical));
        var view = Logical(); using var logical = _files.OpenSequentialCursor("QGPL", view, view);
        Assert.Equal(new long[] { 2, 3, 1 }, Read(logical));
    }
    [Fact]
    public void Reads_are_lazy_and_later_malformed_rows_do_not_prevent_earlier_records()
    {
        Add(1, 1, "A"); Add(2, 2, "B");
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET LABEL=NULL,ID=NULL WHERE ID=2"; command.ExecuteNonQuery();
        }
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.Equal(1L, cursor.Read()!["ID"]);
        Assert.Throws<CpfException>(() => cursor.Read());
    }
    [Fact]
    public void Later_committed_changes_are_visible_without_rewinding_or_reopening()
    {
        Add(1, 1, "A"); Add(2, 2, "B");
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.Equal(1L, cursor.Read()!["ID"]);
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET LABEL='NEW' WHERE ID=2"; command.ExecuteNonQuery();
        }
        Assert.Equal("NEW", cursor.Read()!["LABEL"]); Assert.Null(cursor.Read());
        Add(3, 3, "C"); Assert.Null(cursor.Read());
        using var reopened = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.Equal(new long[] { 1, 2, 3 }, Read(reopened));
    }
    [Fact]
    public void Only_current_record_is_locked_and_cursor_allocations_survive_command_boundaries()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CursorFixture22");
        var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR");
        Add(1, 1, "A"); Add(2, 2, "B");
        var locks = new JobLockStore(_system.Connections);
        IDisposable blocked;
        using (OperationIdentity.Enter("QSECOFR", second.Key)) blocked = locks.Acquire(second.Key, new("QGPL", "DATA", "*FILE", "DATA", 2), JobLockMode.Exclusive, TimeSpan.Zero);
        using (blocked)
        using (OperationIdentity.Enter("QSECOFR", first.Key))
        {
            DatabaseRecordCursor cursor;
            using (locks.EnterCommand(first.Key))
            {
                cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
                Assert.Equal(1L, cursor.Read()!["ID"]);
                Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => cursor.Read()).MessageId);
                Assert.DoesNotContain(locks.Inspect(new("QGPL", "DATA", "*FILE")), l => l.Job == first.Key && l.Resource.IsRecord);
            }
            Assert.Contains(locks.Inspect(new("QGPL", "DATA", "*FILE")), l => l.Job == first.Key && l.Lifetime == "Read");
            using (OperationIdentity.Enter("QSECOFR", second.Key))
                Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.Read()).MessageId);
            cursor.Dispose();
            Assert.DoesNotContain(locks.Inspect(new("QGPL", "DATA", "*FILE")), l => l.Job == first.Key);
            Assert.Throws<ObjectDisposedException>(() => cursor.Read());
        }
    }
    [Fact]
    public void Authority_revocation_and_cancellation_are_checked_between_reads()
    {
        Add(1, 1, "A"); Add(2, 2, "B");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QGPL", "DATA", "*FILE", "READER", Authorities.UseBits);
        DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("READER")) { cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA"); Assert.NotNull(cursor.Read()); }
        _system.Security.Authority.Grant("QGPL", "DATA", "*FILE", "READER", AuthorityBit.None);
        using (cursor)
        using (OperationIdentity.Enter("READER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.Read()).MessageId);
        using var stop = new CancellationTokenSource(); using var cancellable = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", cancellationToken: stop.Token);
        Assert.NotNull(cancellable.Read()); stop.Cancel(); Assert.Throws<OperationCanceledException>(() => cancellable.Read());
    }

    [Fact]
    public void One_command_reads_more_than_the_job_lock_budget_without_retaining_record_locks()
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n<8300) INSERT INTO \"QGPL.DATA.DATA\" (ID,AMOUNT,LABEL) SELECT n,NULL,NULL FROM rows";
            command.ExecuteNonQuery();
        }
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CursorFixture22");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        using (locks.EnterCommand(job.Key))
        using (var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false))
        {
            long count = 0;
            while (cursor.Read() is { } row) Assert.Equal(++count, row["ID"]);
            Assert.Equal(8300, count);
            Assert.DoesNotContain(locks.Inspect(new("QGPL", "DATA", "*FILE")), l => l.Job == job.Key && l.Resource.IsRecord);
        }
    }

    [Fact]
    public void Member_limit_changes_do_not_invalidate_an_existing_record_layout()
    {
        Add(1, 1, "A"); Add(2, 2, "B");
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA");
        Assert.Equal(1L, cursor.Read()!["ID"]);
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", 2);
        Assert.Equal(2L, cursor.Read()!["ID"]);
    }
    public void Dispose() => _system.Dispose();
}
