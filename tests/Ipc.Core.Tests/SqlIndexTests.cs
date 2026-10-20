using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlIndexTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlIndexTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SqlIndexFixture128");
        _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,CODE VARCHAR(10),AMOUNT DECIMAL(29,2) NOT NULL)");
        Run("INSERT INTO QGPL/DATA VALUES(2,'b',100),(1,'a',2),(3,NULL,10)");
        _session = new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }
    private long Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Keyed(string index = "IDX") => _files.ReadKeyed("QGPL", index, index);
    [Theory]
    [InlineData(120)]
    [InlineData(121)]
    public void The_parser_enforces_the_native_120_key_boundary_before_publication(int count)
    {
        var names = Enumerable.Range(1, count).Select(number => "K" + number).ToArray();
        Run("CREATE TABLE QGPL/WIDE(" + string.Join(',', names.Select(name => name + " INTEGER")) + ")");
        var sql = "CREATE INDEX QGPL/IDX ON QGPL/WIDE(" + string.Join(',', names) + ")";
        if (count == 120) { Run(sql); Assert.Equal(count, _files.GetDefinition("QGPL", "IDX")!.PrimaryFormat.Fields.Count); }
        else { Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run(sql)).MessageId); Assert.False(_files.FileExists("QGPL", "IDX")); }
    }
    [Fact]
    public void Copying_a_base_table_does_not_copy_constraints_owned_only_by_its_SQL_indexes()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Run("INSERT INTO QGPL/COPIED VALUES(1,'copy',0)"); Assert.Equal(4, _files.RowCount("QGPL", "COPIED", "DATA"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,'original',0)")).MessageId);
    }
    [Fact]
    public void CREATE_INDEX_builds_an_ordinary_keyed_logical_object_with_a_physical_dependency()
    {
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID DESC)");
        var descriptor = _system.Objects.GetRequired("QGPL", "IDX", ObjectType.File); Assert.Equal("*LF", descriptor.Attribute); Assert.Equal("IDX", descriptor.Format);
        var definition = _files.GetDefinition("QGPL", "IDX")!; Assert.True(definition.SqlIndex); Assert.Equal("DATA", definition.Logical!.SourceFormat);
        Assert.Equal(new[] { "DATA" }, definition.Logical.Members["IDX"]); Assert.Equal(3, definition.PrimaryFormat.Fields.Count);
        Assert.Equal(new long[] { 3, 2, 1 }, Keyed().Select(row => (long)row["ID"]!));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_object_dependencies WHERE source_name='IDX' AND target_name='DATA'"));
        Assert.True((long)Scalar("SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.DATA.DATA' AND name LIKE 'ipc_path_%'")! > 0);
    }
    [Fact]
    public void System_naming_defaults_the_index_schema_to_its_table_instead_of_the_current_library()
    {
        _system.Libraries.CreateLibrary("OTHER"); _files.ExecuteSql("CREATE INDEX IDX ON QGPL/DATA(ID)", Options with { CurrentLibrary = "OTHER" });
        Assert.True(_files.FileExists("QGPL", "IDX")); Assert.False(_files.FileExists("OTHER", "IDX"));
    }
    [Theory]
    [InlineData("", new[] { "ID", "CODE", "AMOUNT" })]
    [InlineData("RCDFMT IXREC", new[] { "ID", "CODE", "AMOUNT" })]
    [InlineData("RCDFMT IXREC ADD ALL COLUMNS", new[] { "ID", "CODE", "AMOUNT" })]
    [InlineData("RCDFMT IXREC ADD KEYS ONLY", new[] { "CODE", "ID" })]
    [InlineData("RCDFMT IXREC ADD AMOUNT", new[] { "CODE", "ID", "AMOUNT" })]
    public void RCDFMT_selects_the_external_record_and_column_layout(string suffix, string[] fields)
    {
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(CODE ASC,ID DESC) " + suffix);
        var definition = _files.GetDefinition("QGPL", "IDX")!; Assert.Equal(fields, definition.PrimaryFormat.Fields.Select(field => field.Name));
        Assert.Equal(suffix.Length == 0 ? "IDX" : "IXREC", definition.PrimaryFormat.Name);
        Assert.Equal(new long[] { 1, 2, 3 }, Keyed().Select(row => (long)row["ID"]!));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unique_index_null_policy_matches_UNIQUE_and_UNIQUE_WHERE_NOT_NULL(bool exclude)
    {
        Run("CREATE UNIQUE " + (exclude ? "WHERE NOT NULL " : "") + "INDEX QGPL/IDX ON QGPL/DATA(CODE)");
        var statement = "INSERT INTO QGPL/DATA VALUES(4,NULL,0)";
        if (exclude) Run(statement); else Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run(statement)).MessageId);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(5,'a',0)")).MessageId);
        Run("DROP INDEX QGPL/IDX"); Run("INSERT INTO QGPL/DATA VALUES(6,'a',0)");
    }
    [Fact]
    public void Duplicate_existing_rows_roll_back_the_complete_index_catalog_and_schema()
    {
        Run("INSERT INTO QGPL/DATA VALUES(4,'a',0)");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(CODE)")).MessageId);
        Assert.False(_files.FileExists("QGPL", "IDX")); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_defs WHERE name='IDX'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_object_dependencies WHERE source_name='IDX'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='index' AND name LIKE 'ipc_unique_%'"));
    }
    [Fact]
    public void Shared_unique_paths_remain_enforced_until_the_last_index_is_dropped()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)"); Run("CREATE UNIQUE INDEX QGPL/SECOND ON QGPL/DATA(ID)");
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name LIKE 'ipc_unique_%'")); Run("DROP INDEX QGPL/IDX");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,'again',0)")).MessageId);
        Run("DROP INDEX QGPL/SECOND"); Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name LIKE 'ipc_unique_%'"));
        Run("INSERT INTO QGPL/DATA VALUES(1,'again',0)");
    }
    [Fact]
    public void Key_paths_track_SQL_and_record_writes_with_exact_decimal_order()
    {
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(AMOUNT DESC,ID ASC)");
        _files.Insert("QGPL", "DATA", "DATA", "DATA", new Dictionary<string, object?> { ["ID"] = 4L, ["CODE"] = "x", ["AMOUNT"] = 10000000000000000000000000.01m });
        Run("INSERT INTO QGPL/DATA VALUES(5,'y',10000000000000000000000000.02)");
        Assert.Equal(new long[] { 5, 4, 2, 3, 1 }, Keyed().Select(row => (long)row["ID"]!));
        Run("UPDATE QGPL/DATA SET AMOUNT=1 WHERE ID=2"); Run("DELETE FROM QGPL/DATA WHERE ID=4");
        Assert.Equal(new long[] { 5, 3, 1, 2 }, Keyed().Select(row => (long)row["ID"]!));
    }
    [Fact]
    public void Searched_unique_updates_use_statement_end_validation_on_index_keys()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)"); Run("UPDATE QGPL/DATA SET ID=4-ID");
        Assert.Equal(new long[] { 1, 2, 3 }, Keyed().Select(row => (long)row["ID"]!));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/DATA SET ID=1")).MessageId);
        Assert.Equal(new long[] { 1, 2, 3 }, Keyed().Select(row => (long)row["ID"]!));
    }
    [Theory]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(BAD)", "SQL0206")]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID,ID)", "SQL0612")]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(LOWER(CODE))", "SQL0104")]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID) WHERE ID>1", "SQL0104")]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID) INCLUDE(CODE)", "SQL0104")]
    [InlineData("CREATE ENCODED VECTOR INDEX QGPL/IDX ON QGPL/DATA(ID)", "SQL0104")]
    [InlineData("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID); DROP TABLE QGPL/DATA", "SQL0104")]
    public void Unsupported_or_invalid_index_syntax_never_changes_storage(string sql, string error)
    {
        Assert.Equal(error, Assert.Throws<CpfException>(() => Run(sql)).MessageId); Assert.False(_files.FileExists("QGPL", "IDX")); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Validation_resolves_columns_and_authority_without_building_paths_or_checking_rows()
    {
        Run("INSERT INTO QGPL/DATA VALUES(4,'a',0)");
        _files.ValidateSql("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(CODE)", Options); Assert.False(_files.FileExists("QGPL", "IDX"));
        Assert.Equal("SQL0206", Assert.Throws<CpfException>(() => _files.ValidateSql("CREATE INDEX QGPL/IDX ON QGPL/DATA(BAD)", Options)).MessageId);
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)"); _files.ValidateSql("DROP INDEX QGPL/IDX", Options); Assert.True(_files.FileExists("QGPL", "IDX"));
    }
    [Fact]
    public void DROP_INDEX_rejects_tables_and_DROP_TABLE_cascades_logical_index_objects_atomically()
    {
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("DROP INDEX QGPL/DATA")).MessageId);
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)"); Run("DROP TABLE QGPL/DATA");
        Assert.False(_files.FileExists("QGPL", "IDX")); Assert.False(_files.FileExists("QGPL", "DATA"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_object_dependencies WHERE source_name='IDX'"));
    }
    [Fact]
    public void Generic_index_copy_rename_move_delete_and_base_rename_keep_shared_paths_and_dependencies()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)"); _system.Libraries.CreateLibrary("OTHER");
        _system.ObjectOperations.Relocate(new("QGPL", "IDX"), ObjectType.File, new("OTHER", "COPY"), copy: true);
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "RENAMED"));
        Assert.Equal("RENAMED", _files.GetDefinition("OTHER", "COPY")!.Logical!.SourceFile);
        Run("DROP INDEX QGPL/IDX"); Assert.Equal(3, _files.RowCount("OTHER", "COPY", "IDX"));
        Run("DROP INDEX OTHER/COPY"); Run("INSERT INTO QGPL/RENAMED VALUES(1,'again',0)");
    }
    [Fact]
    public void Nonunique_index_member_scope_tracks_base_member_addition_and_removal()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", 3); Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        _files.AddMember("QGPL", "DATA", "SECOND");
        _files.Insert("QGPL", "DATA", "SECOND", "DATA", new Dictionary<string, object?> { ["ID"] = 9L, ["AMOUNT"] = 0m });
        Assert.Equal(new long[] { 1, 2, 3, 9 }, Keyed().Select(row => (long)row["ID"]!));
        _files.RemoveMember("QGPL", "DATA", "SECOND"); Assert.Equal(3, Keyed().Count);
        Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "IDX", "IDX"));
    }
    [Fact]
    public void Unsupported_multi_member_uniqueness_rejects_without_creating_a_member_or_index()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", 3); Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => _files.AddMember("QGPL", "DATA", "SECOND")).MessageId);
        Assert.False(_files.MemberExists("QGPL", "DATA", "SECOND")); Run("DROP INDEX QGPL/IDX"); _files.AddMember("QGPL", "DATA", "SECOND");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)")).MessageId);
        Assert.False(_files.FileExists("QGPL", "IDX"));
    }
}
