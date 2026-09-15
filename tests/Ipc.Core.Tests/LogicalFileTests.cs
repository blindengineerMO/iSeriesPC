using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class LogicalFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public LogicalFileTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        var format = new RecordFormat { Name = "DATAREC", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "STATE", Type = FieldType.Alpha, Length = 1 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 29, Decimals = 2 },
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "DATA", new() { Name = "DATA", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        Add(1, "A", 100m); Add(2, "I", 10m); Add(3, "A", 2m);
    }
    private void Add(int id, string state, decimal amount, string member = "DATA") => _files.Insert("QGPL", "DATA", member, "DATAREC", new Dictionary<string, object?> { ["ID"] = id, ["STATE"] = state, ["AMOUNT"] = amount });
    internal static string Line(char level, string field = "", string keywords = "") => "     A" + new string(' ', 10) + level + " " + field.PadRight(10) + new string(' ', 16) + keywords;
    private void Create(string name, params string[] tail) => CreateCore(name, false, tail);
    private void CreateCore(string name, bool unique, params string[] tail)
    {
        var source = string.Join('\n', (unique ? new[] { Line(' ', "", "UNIQUE") } : Array.Empty<string>()).Concat(new[] { Line('R', "LOGREC", "PFILE(QGPL/DATA)") }).Concat(tail));
        var definition = new LogicalDdsCompiler(target => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile(name, source);
        _files.CreateLogicalFile("QGPL", name, definition, source);
    }
    [Fact]
    public void Logical_reads_track_physical_changes_project_fields_and_sort_selected_records()
    {
        Create("ACTIVE", Line(' ', "ID"), Line(' ', "STATE"), Line('K', "ID", "DESCEND"), Line('S', "STATE", "COMP(EQ 'A')"));
        Assert.Equal(new long[] { 3, 1 }, _files.ReadKeyed("QGPL", "ACTIVE", "ACTIVE").Select(r => (long)r["ID"]!));
        Assert.DoesNotContain("AMOUNT", _files.ReadAll("QGPL", "ACTIVE", "ACTIVE")[0].Keys);
        Add(4, "A", 1m); Assert.Equal(3, _files.RowCount("QGPL", "ACTIVE", "ACTIVE"));
        Assert.Single(_files.ReadKeyPrefix("QGPL", "ACTIVE", "ACTIVE", new Dictionary<string, object?> { ["ID"] = 4 }));
        Assert.Throws<CpfException>(() => _files.DeleteFile("QGPL", "DATA"));
        _system.ObjectOperations.Relocate(new("QGPL", "ACTIVE"), ObjectType.File, new("QGPL", "COPY"), copy: true);
        Assert.Equal(3, _files.RowCount("QGPL", "COPY", "ACTIVE"));
        _files.DeleteFile("QGPL", "ACTIVE"); _files.DeleteFile("QGPL", "COPY"); _files.DeleteFile("QGPL", "DATA");
    }
    [Fact]
    public void Ordered_rules_AND_continuation_ALL_and_exact_decimal_order_are_preserved()
    {
        Add(4, "A", 10000000000000000000000000.01m); Add(5, "A", 10000000000000000000000000.02m);
        Create("SELECTED", Line('K', "AMOUNT"), Line('O', "STATE", "COMP(EQ 'I')"), Line('S', "AMOUNT", "RANGE(2 100)"), Line(' ', "STATE", "COMP(EQ 'A')"), Line('S', "", "ALL"));
        Assert.Equal(new long[] { 3, 1, 4, 5 }, _files.ReadKeyed("QGPL", "SELECTED", "SELECTED").Select(r => (long)r["ID"]!));
        Create("LARGE", Line('S', "AMOUNT", "COMP(GT 10000000000000000000000000.01)"));
        Assert.Equal(5L, Assert.Single(_files.ReadAll("QGPL", "LARGE", "LARGE"))["ID"]);
    }
    [Fact]
    public void Physical_member_bindings_are_snapshotted_at_creation_and_rows_lock_the_physical_file()
    {
        _files.AddMember("QGPL", "DATA", "SECOND"); Add(8, "A", 4m, "SECOND"); Create("ALLDATA", Line('K', "ID"));
        _files.AddMember("QGPL", "DATA", "LATER"); Add(9, "A", 5m, "LATER");
        Assert.Equal(4, _files.RowCount("QGPL", "ALLDATA", "ALLDATA"));
        var job = _system.Jobs.CreateInteractive("QUSER"); var other = _system.Jobs.CreateInteractive("QUSER");
        var locks = new JobLockStore(_system.Connections);
        using var held = locks.Acquire(other.Key, new("QGPL", "DATA", ObjectType.File, "DATA", 1), JobLockMode.Exclusive, TimeSpan.Zero);
        using var identity = OperationIdentity.Enter("QUSER", job.Key); using var command = locks.EnterCommand(job.Key);
        Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "ALLDATA", "ALLDATA"));
    }
    [Fact]
    public void Revoking_physical_authority_blocks_access_through_an_authorized_logical_file()
    {
        Create("LOGICAL");
        using (OperationIdentity.Enter("QUSER")) Assert.Equal(3, _files.RowCount("QGPL", "LOGICAL", "LOGICAL"));
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "LOGICAL", "LOGICAL")).MessageId);
    }
    [Fact]
    public void CRTLF_compiles_a_real_source_member_and_DSPPFM_displays_its_selected_rows()
    {
        _files.CreateSourceFile("QGPL", "DDS"); _files.AddMember("QGPL", "DDS", "LOGICAL");
        var source = Line('R', "LOGREC", "PFILE(DATA)") + "\n" + Line('S', "ID", "COMP(EQ 3)");
        _files.SaveSourceMember("QGPL", "DDS", "LOGICAL", source, _files.ReadSourceMember("QGPL", "DDS", "LOGICAL").Revision);
        var commands = new CommandService(_system);
        var result = commands.Execute("CRTLF FILE(QGPL/LOGICAL) SRCFILE(QGPL/DDS)"); Assert.False(result.IsError, result.Message);
        Assert.Equal(1, _files.RowCount("QGPL", "LOGICAL", "LOGICAL"));
        Assert.False(commands.Execute("DSPPFM FILE(QGPL/LOGICAL)").IsError);
    }
    [Fact]
    public void Logical_writes_update_the_base_and_cannot_delete_records_outside_the_selection()
    {
        Create("ACTIVE", Line('K', "ID"), Line('S', "STATE", "COMP(EQ 'A')"));
        _files.Insert("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = 4, ["STATE"] = "A", ["AMOUNT"] = 7m });
        Assert.Equal(4, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Throws<CpfException>(() => _files.Insert("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = 5, ["STATE"] = "I" }));
        _files.Update("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = 4, ["STATE"] = "I", ["AMOUNT"] = 8m });
        Assert.Equal(2, _files.RowCount("QGPL", "ACTIVE", "ACTIVE"));
        _files.Delete("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = 2 });
        Assert.Equal(4, _files.RowCount("QGPL", "DATA", "DATA"));
        _files.Delete("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = 1 });
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Throws<CpfException>(() => _files.Update("QGPL", "ACTIVE", "ACTIVE", "LOGREC", new Dictionary<string, object?> { ["ID"] = "invalid", ["AMOUNT"] = 8m }));
    }
    [Fact]
    public void Logical_member_removal_preserves_base_data_and_physical_member_removal_checks_every_binding()
    {
        Create("LOGICAL"); _files.AddMember("QGPL", "DATA", "SECOND"); Add(8, "A", 4m, "SECOND");
        _files.AddLogicalMember("QGPL", "LOGICAL", "ONLYSECOND", new[] { "SECOND" });
        Assert.Equal(1, _files.RowCount("QGPL", "LOGICAL", "ONLYSECOND"));
        Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "DATA", "SECOND"));
        var commands = new CommandService(_system);
        Assert.False(commands.Execute("ADDLFM FILE(QGPL/LOGICAL) MBR(ALLNOW)").IsError);
        Assert.Equal(4, _files.RowCount("QGPL", "LOGICAL", "ALLNOW"));
        _files.RemoveMember("QGPL", "LOGICAL", "ONLYSECOND");
        Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "DATA", "SECOND"));
        _files.RemoveMember("QGPL", "LOGICAL", "ALLNOW"); _files.RemoveMember("QGPL", "DATA", "SECOND");
        Assert.Equal(3, _files.RowCount("QGPL", "LOGICAL", "LOGICAL"));
    }
    [Theory]
    [InlineData("COMP(GT invalid)")]
    [InlineData("COMP(EQ 1.5)")]
    [InlineData("RANGE(1 invalid)")]
    public void Invalid_numeric_selection_constants_fail_before_creation(string predicate)
    {
        Assert.Throws<CpfException>(() => Create("INVALID", Line('S', "ID", predicate)));
        Assert.False(_files.FileExists("QGPL", "INVALID"));
    }
    [Fact]
    public void Character_keys_and_comparisons_use_the_physical_CCSID_byte_order()
    {
        Add(4, "a", 1m); Add(5, "9", 1m);
        Create("BYCHAR", Line('K', "STATE"));
        Assert.Equal(new[] { "a", "A", "A", "I", "9" }, _files.ReadKeyed("QGPL", "BYCHAR", "BYCHAR").Select(r => r["STATE"]));
        Create("HIGHCHAR", Line('S', "STATE", "COMP(GT 'I')"));
        Assert.Equal(5L, Assert.Single(_files.ReadAll("QGPL", "HIGHCHAR", "HIGHCHAR"))["ID"]);
    }
    [Fact]
    public void Malformed_stored_decimal_is_a_managed_error_even_when_encountered_inside_SQLite_predicates()
    {
        Create("BYSUM", Line('S', "AMOUNT", "COMP(GT 0)"));
        using var connection = _system.Connections.Open(); using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='invalid' WHERE ID=1"; corrupt.ExecuteNonQuery();
        Assert.Equal("IPC0138", Assert.Throws<CpfException>(() => _files.ReadKeyed("QGPL", "BYSUM", "BYSUM")).MessageId);
        corrupt.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='1.00' WHERE ID=1"; corrupt.ExecuteNonQuery();
        Assert.Equal(3, _files.ReadKeyed("QGPL", "BYSUM", "BYSUM").Count);
    }
    [Fact]
    public void Keyed_paths_are_shared_maintained_and_used_by_SQLite_without_losing_decimal_precision()
    {
        Create("BYSUM", Line('K', "AMOUNT")); Create("BYSUM2", Line('K', "AMOUNT"));
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.DATA.DATA'";
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "EXPLAIN QUERY PLAN SELECT AMOUNT FROM \"QGPL.DATA.DATA\" ORDER BY ipc_decimal_key_v1(AMOUNT)";
        using (var reader = query.ExecuteReader()) { Assert.True(reader.Read()); Assert.Contains("USING INDEX ipc_path_", reader.GetString(3)); }
        Add(4, "A", -0.01m); Assert.Equal(4L, _files.ReadKeyed("QGPL", "BYSUM", "BYSUM")[0]["ID"]);
        query.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='invalid' WHERE ID=1";
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => query.ExecuteNonQuery());
        Assert.Equal(4, _files.ReadKeyed("QGPL", "BYSUM", "BYSUM").Count);
        _files.DeleteFile("QGPL", "BYSUM"); _files.DeleteFile("QGPL", "BYSUM2"); _files.DeleteFile("QGPL", "DATA");
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.DATA.DATA'";
        Assert.Equal(0L, query.ExecuteScalar());
    }
    [Fact]
    public void Unique_selection_is_enforced_on_PF_writes_and_released_only_after_the_last_logical_owner()
    {
        CreateCore("UNIQUEA", true, Line('K', "ID"), Line('S', "STATE", "COMP(EQ 'A')"));
        Add(1, "I", 1m);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(1, "A", 1m));
        _system.ObjectOperations.Relocate(new("QGPL", "UNIQUEA"), ObjectType.File, new("QGPL", "COPYLF"), copy: true);
        _files.DeleteFile("QGPL", "UNIQUEA");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(1, "A", 1m));
        _files.DeleteFile("QGPL", "COPYLF"); Add(1, "A", 1m);
        Assert.Equal(5, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Failed_unique_creation_rolls_back_and_copying_a_PF_does_not_copy_external_LF_constraints()
    {
        Add(1, "I", 1m);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => CreateCore("BADUNIQUE", true, Line('K', "ID")));
        Assert.False(_files.FileExists("QGPL", "BADUNIQUE"));
        CreateCore("UNIQUEA", true, Line('K', "ID"), Line('S', "STATE", "COMP(EQ 'A')"));
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPYDATA"), copy: true);
        _files.Insert("QGPL", "COPYDATA", "DATA", "DATAREC", new Dictionary<string, object?> { ["ID"] = 1, ["STATE"] = "A", ["AMOUNT"] = 1m });
        Assert.Equal(5, _files.RowCount("QGPL", "COPYDATA", "DATA"));
    }
    [Fact]
    public void Removing_a_unique_member_releases_its_constraint_without_changing_other_members()
    {
        CreateCore("UNIQUEID", true, Line('K', "ID"));
        _files.AddMember("QGPL", "DATA", "SECOND"); Add(1, "A", 1m, "SECOND");
        _files.AddLogicalMember("QGPL", "UNIQUEID", "SECOND", new[] { "SECOND" });
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(1, "A", 1m, "SECOND"));
        _files.RemoveMember("QGPL", "UNIQUEID", "SECOND"); Add(1, "A", 1m, "SECOND");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(1, "A", 1m));
        Assert.Equal(3, _files.RowCount("QGPL", "UNIQUEID", "UNIQUEID"));
    }
    [Fact]
    public void Native_DDS_keyword_continuations_preserve_plus_minus_and_implicit_statement_rules()
    {
        var compiler = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!));
        var source = string.Join('\n', Line('R', "LOGREC"), Line(' ', "", "PFILE(QGPL/DA+"),
            Line(' ', "", "    TA) TEXT('First-"), Line(' ', "", " second')"),
            Line('K', "ID"), Line(' ', "", "DESCEND"), Line('S', "STATE", "COMP(EQ +"), Line(' ', "", "  'A')"));
        var definition = compiler.Compile("CONTINUED", source, "QGPL/DDS(CONTINUED)");
        Assert.Equal("First second", definition.Text);
        _files.CreateLogicalFile("QGPL", "CONTINUED", definition, source);
        Assert.Equal(new long[] { 3, 1 }, _files.ReadKeyed("QGPL", "CONTINUED", "CONTINUED").Select(r => (long)r["ID"]!));
        var incomplete = Assert.Throws<DdsCompileException>(() => compiler.Compile("BAD", Line('R', "LOGREC", "PFILE(+"), "DDS(BAD)"));
        Assert.Contains("DDS(BAD):1:1", incomplete.Message);
        var wrongLine = Assert.Throws<DdsCompileException>(() => compiler.Compile("BAD", Line('R', "LOGREC", "PFILE(+") + "\n" + Line('K', "ID"), "DDS(BAD)"));
        Assert.Contains("DDS(BAD):2:1", wrongLine.Message);
        Assert.Throws<DdsCompileException>(() => compiler.Compile("BAD", Line('R', "LOGREC", "PFILE(DATA) TEXT('" + new string('a', 5000) + "')")));
    }
    [Fact]
    public void Logical_commands_support_explicit_member_order_empty_bindings_and_maximum_members()
    {
        _files.CreateSourceFile("QGPL", "DDS");
        _files.SaveSourceMember("QGPL", "DDS", "VIEW", Line('R', "LOGREC", "PFILE(DATA)"), null);
        _files.AddMember("QGPL", "DATA", "SECOND"); Add(8, "A", 1m, "SECOND");
        var commands = new CommandService(_system);
        var created = commands.Execute("CRTLF FILE(QGPL/VIEW) SRCFILE(QGPL/DDS) MBR(CHOSEN) DTAMBRS((*CURRENT/DATA (SECOND DATA))) MAXMBRS(3)");
        Assert.False(created.IsError, created.Message);
        Assert.Equal(new long[] { 8, 1, 2, 3 }, _files.ReadAll("QGPL", "VIEW", "CHOSEN").Select(r => (long)r["ID"]!));
        Assert.False(commands.Execute("ADDLFM FILE(QGPL/VIEW) MBR(EMPTY) DTAMBRS((DATA *NONE))").IsError);
        Assert.Empty(_files.ReadAll("QGPL", "VIEW", "EMPTY"));
        Assert.False(commands.Execute("ADDLFM FILE(QGPL/VIEW) MBR(ONLYONE) DTAMBRS((QGPL/DATA SECOND))").IsError);
        Assert.True(commands.Execute("ADDLFM FILE(QGPL/VIEW) MBR(TOOMANY)").IsError);
        Assert.True(commands.Execute("CRTLF FILE(QGPL/BADVIEW) SRCFILE(QGPL/DDS) SRCMBR(VIEW) DTAMBRS((OTHER DATA))").IsError);
        Assert.False(_files.FileExists("QGPL", "BADVIEW"));
        Assert.False(commands.Execute("CRTLF FILE(QGPL/NOMEMBER) SRCFILE(QGPL/DDS) SRCMBR(VIEW) MBR(*NONE)").IsError);
        Assert.Empty(_files.ListMembers("QGPL", "NOMEMBER"));
        Assert.False(commands.Execute("ADDLFM FILE(QGPL/NOMEMBER) MBR(FIRST)").IsError);
        Assert.True(commands.Execute("ADDLFM FILE(QGPL/NOMEMBER) MBR(SECOND)").IsError);
    }
    [Theory]
    [InlineData("AMOUNT", "1.001")]
    [InlineData("AMOUNT", "1000000000000000000000000000")]
    [InlineData("AMOUNT", "1.00000000000000000000000000001")]
    [InlineData("ID", "2147483648")]
    public void Logical_output_rejects_field_overflow_and_silent_numeric_rounding(string field, string value)
    {
        Create("VIEW", Line('K', "ID"));
        var row = new Dictionary<string, object?> { ["ID"] = 8, ["STATE"] = "A", ["AMOUNT"] = 1m };
        row[field] = value;
        Assert.Throws<CpfException>(() => _files.Insert("QGPL", "VIEW", "VIEW", "LOGREC", row));
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Changed_physical_field_metadata_prevents_logical_writes()
    {
        Create("VIEW", Line('K', "ID"));
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_file_defs SET def=json_set(def,'$.formats[0].fields[1].ccsid',1208) WHERE lib='QGPL' AND name='DATA'";
        command.ExecuteNonQuery();
        Assert.Throws<CpfException>(() => _files.Update("QGPL", "VIEW", "VIEW", "LOGREC", new Dictionary<string, object?> { ["ID"] = 1, ["STATE"] = "I" }));
    }
    [Fact]
    public void Physical_relocation_updates_live_logical_bindings_and_unique_constraint_ownership_atomically()
    {
        CreateCore("UNIQUEID", true, Line('K', "ID"));
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QUSRSYS", "RENAMED"));
        Assert.Equal(3, _files.RowCount("QGPL", "UNIQUEID", "UNIQUEID"));
        var logical = _files.GetDefinition("QGPL", "UNIQUEID")!.Logical!;
        Assert.Equal("QUSRSYS", logical.SourceLibrary); Assert.Equal("RENAMED", logical.SourceFile);
        Assert.Throws<CpfException>(() => _files.DeleteFile("QUSRSYS", "RENAMED"));
        _files.AddLogicalMember("QGPL", "UNIQUEID", "SHARED");
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QUSRSYS.RENAMED.DATA'";
        Assert.Equal(1L, command.ExecuteScalar());
        var duplicate = new Dictionary<string, object?> { ["ID"] = 1, ["STATE"] = "A", ["AMOUNT"] = 1m };
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.Insert("QUSRSYS", "RENAMED", "DATA", "DATAREC", duplicate));
        _files.DeleteFile("QGPL", "UNIQUEID");
        _files.Insert("QUSRSYS", "RENAMED", "DATA", "DATAREC", duplicate);
        Assert.Equal(4, _files.RowCount("QUSRSYS", "RENAMED", "DATA"));
    }
    [Fact]
    public void Allocated_logical_file_blocks_base_relocation_without_partial_catalog_changes()
    {
        Create("VIEW"); var job = _system.Jobs.CreateInteractive("QUSER");
        using (new JobLockStore(_system.Connections).Acquire(job.Key, new("QGPL", "VIEW", ObjectType.File), JobLockMode.SharedRead, TimeSpan.Zero))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "RENAMED"))).MessageId);
        Assert.Equal("DATA", _files.GetDefinition("QGPL", "VIEW")!.Logical!.SourceFile);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA")); Assert.False(_files.FileExists("QGPL", "RENAMED"));
    }
    public void Dispose() => _system.Dispose();
}
