using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public DatabaseNavigationTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        var format = new RecordFormat { Name = "REC", Fields = [
            new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 29, Decimals = 2, NullCapable = true, Sequence = 1 },
            new() { Name = "LABEL", Type = FieldType.Alpha, Length = 8, NullCapable = true, Sequence = 2 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100 }
        ] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "NAV", new() { Name = "NAV", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        Add(1, 1, "A"); Add(2, 1, "A"); Add(3, 1, "B"); Add(4, 2, "A");
        Add(5, null, "A"); Add(6, null, null); Add(7, 9007199254740992.01m, "A"); Add(8, 9007199254740992.02m, "B");
    }
    private void Add(long id, decimal? amount, string? label) => _files.Insert("QGPL", "NAV", "NAV", "REC",
        new Dictionary<string, object?> { ["ID"] = id, ["AMOUNT"] = amount, ["LABEL"] = label, ["TEXT"] = "row " + id });
    private static Dictionary<string, object?> Key(decimal? amount) => new() { ["AMOUNT"] = amount };
    private static Dictionary<string, object?> Key(decimal? amount, string? label) => new() { ["AMOUNT"] = amount, ["LABEL"] = label };
    private static long Id(IReadOnlyDictionary<string, object?>? row) => (long)row!["ID"]!;
    private void Sql(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private string DescendingView()
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)"), LogicalFileTests.Line('K', "AMOUNT", "DESCEND"), LogicalFileTests.Line('K', "LABEL", "DESCEND"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("NAVDESC", source);
        _files.CreateLogicalFile("QGPL", "NAVDESC", definition, source); return "NAVDESC";
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forward_and_reverse_reads_follow_exact_nullable_access_path_order(bool descending)
    {
        var name = descending ? DescendingView() : "NAV"; using var cursor = _files.OpenSequentialCursor("QGPL", name, name);
        var expected = descending ? new long[] { 6, 5, 8, 7, 4, 3, 1, 2 } : [1, 2, 3, 4, 7, 8, 5, 6];
        var actual = new List<long>(); while (cursor.Read() is { } row) actual.Add(Id(row)); Assert.Equal(expected, actual);
        actual.Clear(); while (cursor.ReadPrior() is { } row) actual.Add(Id(row)); Assert.Equal(expected.Reverse(), actual);
        Assert.True(cursor.AtBeginning); Assert.Equal(expected[0], Id(cursor.Read()));
    }
    [Fact]
    public void Chain_positions_forward_and_reverse_reads_at_the_found_record()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2))); Assert.Equal(4, Id(cursor.Current));
        Assert.Equal(7, Id(cursor.Read())); Assert.Equal(4, Id(cursor.ReadPrior())); Assert.Equal(3, Id(cursor.ReadPrior()));
    }
    [Fact]
    public void Lower_and_upper_limits_preserve_record_buffers_and_position_inclusively_or_exclusively()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read()));
        Assert.True(cursor.SetLowerBound(Key(1, "B"))); Assert.Null(cursor.Current); Assert.Equal(3, Id(cursor.Read()));
        cursor.SetUpperBound(Key(1)); Assert.Equal(3, Id(cursor.ReadPrior()));
        Assert.True(cursor.SetLowerBound(Key(1, "B"))); Assert.Equal(2, Id(cursor.ReadPrior()));
        Assert.False(cursor.SetLowerBound(Key(1.5m))); Assert.Equal(4, Id(cursor.Read()));
    }
    [Fact]
    public void Missing_limits_position_at_EOF_and_prior_reads_retrieve_the_last_record()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); cursor.SetUpperBound(Key(null));
        Assert.True(cursor.AtEnd); Assert.Null(cursor.Read()); Assert.Equal(6, Id(cursor.ReadPrior()));
        Assert.False(cursor.SetLowerBound(Key(1, "Z"))); Assert.Equal(4, Id(cursor.Read()));
    }
    [Fact]
    public void Keyed_limits_work_in_descending_access_path_order()
    {
        var name = DescendingView(); using var cursor = _files.OpenSequentialCursor("QGPL", name, name);
        Assert.True(cursor.SetLowerBound(Key(1))); Assert.Equal(3, Id(cursor.Read()));
        cursor.SetUpperBound(Key(1)); Assert.True(cursor.AtEnd); Assert.Equal(2, Id(cursor.ReadPrior()));
        Assert.True(cursor.SetLowerBound(Key(1, "A"))); Assert.Equal(1, Id(cursor.Read())); Assert.Equal(3, Id(cursor.ReadPrior()));
    }
    [Fact]
    public void Exact_large_decimal_key_search_does_not_collapse_adjacent_values()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        Assert.True(cursor.Chain(Key(9007199254740992.01m))); Assert.Equal(7, Id(cursor.Current));
        Assert.True(cursor.Chain(Key(9007199254740992.02m))); Assert.Equal(8, Id(cursor.Current));
        Assert.True(cursor.SetLowerBound(Key(9007199254740992.01m))); Assert.Equal(7, Id(cursor.Read()));
    }
    [Fact]
    public void Null_and_character_key_components_match_native_normalized_values()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        Assert.True(cursor.Chain(Key(null, null))); Assert.Equal(6, Id(cursor.Current));
        Assert.True(cursor.Chain(new Dictionary<string, object?> { ["amount"] = 1m, ["label"] = "A       " })); Assert.Equal(1, Id(cursor.Current));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_reads_do_not_skip_an_adjacent_nonmatching_record(bool prior)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        if (prior) cursor.Rewind(end: true);
        Assert.Null(cursor.ReadEqual(Key(2), prior)); Assert.Throws<CpfException>(() => cursor.Read());
        Assert.True(cursor.Chain(Key(2))); Assert.Equal(4, Id(cursor.Current));
    }
    [Fact]
    public void Equal_reads_traverse_duplicate_keys_and_require_repositioning_after_a_group_ends()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.SetLowerBound(Key(1, "A")));
        Assert.Equal(1, Id(cursor.ReadEqual())); Assert.Equal(2, Id(cursor.ReadEqual())); Assert.Null(cursor.ReadEqual());
        Assert.Throws<CpfException>(() => cursor.ReadEqual()); Assert.Equal(1, Id(cursor.ReadEqual(Key(1, "A"))));
        cursor.SetUpperBound(Key(1)); Assert.Equal(3, Id(cursor.ReadEqual(Key(1), prior: true))); Assert.Equal(2, Id(cursor.ReadEqual(Key(1), prior: true))); Assert.Equal(1, Id(cursor.ReadEqual(prior: true)));
    }
    [Fact]
    public void Failed_chain_requires_repositioning_and_does_not_return_a_stale_record()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Assert.False(cursor.Chain(Key(123))); Assert.Null(cursor.Current); Assert.Throws<CpfException>(() => cursor.Read());
        cursor.Rewind(); Assert.Equal(1, Id(cursor.Read()));
    }
    [Fact]
    public void Positioning_does_not_decode_malformed_nonkey_record_fields()
    {
        Sql("UPDATE \"QGPL.NAV.NAV\" SET ID=NULL WHERE ID=4");
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.SetLowerBound(Key(2)));
        Assert.Throws<CpfException>(() => cursor.Read()); Assert.True(cursor.SetLowerBound(Key(1))); Assert.Equal(1, Id(cursor.Read()));
    }
    [Fact]
    public void Invalid_key_prefixes_are_rejected_without_losing_the_current_position()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read()));
        Assert.Throws<CpfException>(() => cursor.Chain(new Dictionary<string, object?> { ["LABEL"] = "A" }));
        Assert.Throws<CpfException>(() => cursor.SetLowerBound(new Dictionary<string, object?> { ["MISSING"] = 1 }));
        Assert.Equal(2, Id(cursor.Read()));
    }
    [Fact]
    public void An_ordinary_RPG_cursor_opens_lazily_and_later_corrupt_rows_do_not_poison_earlier_reads()
    {
        Sql("UPDATE \"QGPL.NAV.NAV\" SET ID=NULL WHERE ID=6");
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV");
        Assert.True(cursor.ReadNext(out var record, out var eof)); Assert.False(eof); Assert.Equal(1, Id(record));
        Assert.True(cursor.Chain(Key(2))); Assert.Equal(4, Id(cursor.Current));
    }
    [Fact]
    public void RPG_reads_and_reload_observe_live_data_without_retaining_a_record_list()
    {
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV");
        Assert.True(cursor.ReadNext(out var first, out _)); Assert.Equal(1, Id(first));
        Sql("UPDATE \"QGPL.NAV.NAV\" SET TEXT='changed after open' WHERE ID=2"); cursor.Reload();
        Assert.True(cursor.ReadNext(out var next, out _)); Assert.Equal("changed after open", next!["TEXT"]);
    }
    [Fact]
    public void RPG_reverse_and_equal_reads_share_the_bounded_navigation_state()
    {
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV");
        cursor.SetUpperBound(Key(1)); Assert.True(cursor.ReadPriorKeyed(Key(1), out var third, out _)); Assert.Equal(3, Id(third));
        Assert.True(cursor.ReadPriorKeyed(Key(1), out var second, out _)); Assert.Equal(2, Id(second));
        Assert.True(cursor.SetLowerBound(Key(1))); Assert.True(cursor.ReadNextKeyed(null, out var first, out _)); Assert.Equal(1, Id(first));
    }
    [Fact]
    public void RPG_cursor_cancellation_reaches_SQL_navigation()
    {
        using var cancellation = new CancellationTokenSource();
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV", cancellation.Token);
        cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => cursor.ReadNext(out _, out _));
    }
    [Fact]
    public void Navigation_validation_and_rewind_enforce_current_authority()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "NAVREADER" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "NAVREADER", Authorities.UseBits);
        DatabaseRecordCursor cursor; using (OperationIdentity.Enter("NAVREADER")) cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "NAVREADER", AuthorityBit.None);
        using (OperationIdentity.Enter("NAVREADER")) { Assert.Throws<CpfException>(() => cursor.Validate()); Assert.Throws<CpfException>(() => cursor.Rewind()); cursor.Dispose(); }
    }
    [Fact]
    public void Opening_and_reading_an_ordinary_large_RPG_file_has_bounded_managed_allocations()
    {
        using (var connection = _system.Connections.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "DELETE FROM \"QGPL.NAV.NAV\""; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO \"QGPL.NAV.NAV\" VALUES($id,$amount,'A','record')";
            command.Parameters.AddWithValue("$id", 0L); command.Parameters.AddWithValue("$amount", "0.00");
            for (var index = 1; index <= 20000; index++)
            {
                command.Parameters["$id"].Value = (long)index;
                command.Parameters["$amount"].Value = index.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ".00";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV");
        Assert.True(cursor.ReadNext(out var first, out _)); Assert.Equal(1, Id(first));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 4 * 1024 * 1024, "Opening and reading one of 20000 rows must not allocate the complete file: " + bytes);
    }
    [Fact]
    public void RPG_open_and_first_read_do_not_lock_later_records()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "NavigationFixture32");
        var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new Ipc.Services.Work.JobLockStore(_system.Connections); IDisposable blocked;
        using (OperationIdentity.Enter("QSECOFR", second.Key))
            blocked = locks.Acquire(second.Key, new("QGPL", "NAV", ObjectType.File, "NAV", 2), Ipc.Services.Work.JobLockMode.Exclusive, TimeSpan.Zero);
        using (blocked)
        using (OperationIdentity.Enter("QSECOFR", first.Key))
        using (locks.EnterCommand(first.Key))
        using (var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "NAV", "NAV"))
        {
            Assert.True(cursor.ReadNext(out var record, out _)); Assert.Equal(1, Id(record));
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => cursor.ReadNext(out _, out _)).MessageId);
        }
    }
    public void Dispose() => _system.Dispose();
}
