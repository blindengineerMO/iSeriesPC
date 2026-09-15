using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed class FileCopyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public FileCopyTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        Create("SOURCE"); Create("TARGET");
        for (var i = 1; i <= 3; i++) Add("SOURCE", i, "row" + i);
        Add("TARGET", 9, "original");
    }
    private void Create(string name)
    {
        var format = new RecordFormat { Name = "RECORD", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 12, NullCapable = true }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", name, new() { Name = name, Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
    }
    private void Add(string file, int id, string text) => _files.Insert("QGPL", file, file, "RECORD", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = text });
    private int Copy(bool replace = false, CancellationToken token = default) => _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "TARGET"), "TARGET", replace: replace, cancellationToken: token);
    private void Sql(string sql) { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    [Fact]
    public void Add_replace_and_self_copy_use_stable_snapshots_and_release_temporary_state()
    {
        Assert.Equal(3, Copy()); Assert.Equal(4, _files.RowCount("QGPL", "TARGET", "TARGET"));
        Assert.Equal(3, Copy(replace: true)); Assert.Equal(3, _files.RowCount("QGPL", "TARGET", "TARGET"));
        Assert.Equal(3, _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "SOURCE"), "SOURCE"));
        Assert.Equal(6, _files.RowCount("QGPL", "SOURCE", "SOURCE"));
        Assert.Equal(6, _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "SOURCE"), "SOURCE", replace: true));
        Assert.Equal(6, _files.RowCount("QGPL", "SOURCE", "SOURCE"));
    }

    [Fact]
    public void Late_target_trigger_failure_rolls_back_replacement_and_trigger_side_effects()
    {
        Sql("CREATE TABLE copy_audit(id INTEGER); CREATE TRIGGER copy_target_insert AFTER INSERT ON \"QGPL.TARGET.TARGET\" BEGIN INSERT INTO copy_audit VALUES(new.ID); SELECT CASE WHEN new.ID=3 THEN RAISE(ABORT,'copy failure') END; END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Copy(replace: true));
        Assert.Equal(9L, Assert.Single(_files.ReadAll("QGPL", "TARGET", "TARGET"))["ID"]);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM copy_audit";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Logical_source_selection_order_and_null_values_reach_the_physical_target()
    {
        Sql("UPDATE \"QGPL.SOURCE.SOURCE\" SET TEXT=NULL WHERE ID=3");
        var source = string.Join('\n', LogicalFileTests.Line('R', "LOGREC", "PFILE(SOURCE)"), LogicalFileTests.Line('K', "ID", "DESCEND"), LogicalFileTests.Line('S', "ID", "COMP(GE 2)"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "SOURCE"), _files.GetDefinition("QGPL", "SOURCE")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
        Assert.Equal(2, _files.CopyRecords(new("QGPL", "VIEW"), "VIEW", new("QGPL", "TARGET"), "TARGET", replace: true));
        var rows = _files.ReadAll("QGPL", "TARGET", "TARGET"); Assert.Equal(new long[] { 3, 2 }, rows.Select(r => (long)r["ID"]!)); Assert.Null(rows[0]["TEXT"]);
    }

    [Fact]
    public void Cancellation_and_missing_authority_preserve_the_target()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Copy(replace: true, token: cancelled.Token));
        _system.Security.Authority.Grant("QGPL", "TARGET", ObjectType.File, "QUSER", AuthorityBit.Read | AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Copy(replace: true)).MessageId);
        Assert.Equal(9L, Assert.Single(_files.ReadAll("QGPL", "TARGET", "TARGET"))["ID"]);
    }

    [Fact]
    public void Command_requires_explicit_member_option_and_rejects_unimplemented_copy_modes()
    {
        var commands = new CommandService(_system); const string prefix = "CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/TARGET)";
        Assert.True(commands.Execute(prefix).IsError);
        Assert.True(commands.Execute(prefix + " MBROPT(*UPDADD)").IsError);
        Assert.True(commands.Execute(prefix + " MBROPT(*ADD) FMTOPT(*NOCHK)").IsError);
        var copy = commands.Execute(prefix + " MBROPT(*REPLACE)"); Assert.False(copy.IsError, copy.Message);
        Assert.Equal(3, _files.RowCount("QGPL", "TARGET", "TARGET"));
    }
    [Fact]
    public void Mapping_and_dropping_are_explicit_and_invalid_values_cannot_partially_replace_data()
    {
        var format = new RecordFormat { Name = "MAPPED", Fields = new() {
            new() { Name = "ID", Type = FieldType.Alpha, Length = 4 },
            new() { Name = "EXTRA", Type = FieldType.Binary, Length = 4 }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "MAPPED", new() { Name = "MAPPED", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "MAPPED"), "MAPPED", map: true));
        Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "MAPPED"), "MAPPED", drop: true));
        Assert.Equal(3, _files.CopyRecords(new("QGPL", "SOURCE"), "SOURCE", new("QGPL", "MAPPED"), "MAPPED", map: true, drop: true));
        var mapped = _files.ReadAll("QGPL", "MAPPED", "MAPPED"); Assert.Equal("1", mapped[0]["ID"]); Assert.Equal(0L, mapped[0]["EXTRA"]);
        Sql("UPDATE \"QGPL.SOURCE.SOURCE\" SET ID='bad' WHERE ID=3");
        Assert.Throws<CpfException>(() => Copy(replace: true));
        Assert.Equal(9L, Assert.Single(_files.ReadAll("QGPL", "TARGET", "TARGET"))["ID"]);
    }

    [Fact]
    public void A_locked_source_record_aborts_the_copy_and_preserves_the_target()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var other = _system.Jobs.CreateInteractive("QUSER");
        var locks = new Ipc.Services.Work.JobLockStore(_system.Connections);
        using var held = locks.Acquire(other.Key, new("QGPL", "SOURCE", ObjectType.File, "SOURCE", 3), Ipc.Services.Work.JobLockMode.Exclusive, TimeSpan.Zero);
        using (OperationIdentity.Enter("QUSER", job.Key)) using (locks.EnterCommand(job.Key)) Assert.Throws<CpfException>(() => Copy(replace: true));
        Assert.Equal(9L, Assert.Single(_files.ReadAll("QGPL", "TARGET", "TARGET"))["ID"]);
    }
    [Fact]
    public void First_member_uses_creation_order_instead_of_file_name_or_alphabetical_order()
    {
        _files.AddMember("QGPL", "SOURCE", "AAA");
        var commands = new CommandService(_system);
        Assert.Equal("SOURCE", _files.FirstMember("QGPL", "SOURCE"));
        Assert.False(commands.Execute("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/TARGET) MBROPT(*REPLACE)").IsError);
        Assert.Equal(3, _files.RowCount("QGPL", "TARGET", "TARGET"));
        _files.RemoveMember("QGPL", "SOURCE", "SOURCE");
        Assert.Equal("AAA", _files.FirstMember("QGPL", "SOURCE"));
        Assert.False(commands.Execute("DSPPFM FILE(QGPL/SOURCE)").IsError);
        _files.AddMember("QGPL", "TARGET", "AAA");
        Assert.False(commands.Execute("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/TARGET) TOMBR(*FROMMBR) MBROPT(*REPLACE)").IsError);
        Assert.Equal(3, _files.RowCount("QGPL", "TARGET", "TARGET"));
    }
    public void Dispose() => _system.Dispose();
}
