using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryOutputTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlQueryOutputTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "QueryOutputFixture412");
        _files = new(_system.Connections, _system.Objects);
        _files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,NAME VARCHAR(20))", Options);
        _files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1,'one'),(2,'two'),(3,NULL)", Options);
        _session = new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }
    private SqlQueryFileResult Save(string sql = "SELECT ID,NAME FROM QGPL/DATA ORDER BY ID", string name = "OUT", bool replace = true,
        string member = "*FIRST", SqlQueryLimits? limits = null, CancellationToken token = default, Action? validate = null)
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        return _files.WriteSqlQueryFile(sql, Options, new("QGPL", name), member, replace, limits: limits, cancellationToken: token, validateQuery: validate);
    }
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string file = "OUT", string? member = null) => _files.ReadAll("QGPL", file, member ?? file);
    private void Query(string sql, string name = "REPORT") => new QueryManagementStore(_system.Connections).Create("QGPL", name, sql);
    [Fact]
    public void New_output_is_a_private_typed_table_with_normal_members_slots_and_no_source_constraints()
    {
        var result = Save(); Assert.True(result.Created); Assert.Equal(3, result.Rows); Assert.Equal("OUT", result.Member);
        var descriptor = _system.Objects.GetRequired("QGPL", "OUT", ObjectType.File);
        Assert.Equal(AuthorityBit.None, descriptor.PublicAuthority); Assert.Equal("QSECOFR", descriptor.Owner);
        var definition = _files.GetDefinition("QGPL", "OUT")!; Assert.NotNull(definition.SqlTable); Assert.Empty(definition.SqlTable.Constraints);
        Assert.Equal(new[] { "ID", "NAME" }, definition.PrimaryFormat.Fields.Select(field => field.Name));
        Assert.False(definition.PrimaryFormat.Fields[0].NullCapable); Assert.True(definition.PrimaryFormat.Fields[1].NullCapable);
        Assert.Equal(new long[] { 1, 2, 3 }, Rows().Select(row => (long)row["ID"]!)); Assert.Null(Rows()[2]["NAME"]);
        Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='OUT'"));
    }
    [Fact]
    public void Calculated_and_duplicate_column_names_follow_the_independent_IBM_SEL_COL_counter_example()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/EXAMPLE(WEEKS INTEGER,SALARY INTEGER,MAXBENEFIT INTEGER)", Options);
        _files.ExecuteSql("INSERT INTO QGPL/EXAMPLE VALUES(2,7,10)", Options);
        Save("SELECT 7*WEEKS,SALARY,SALARY,SALARY/7*WEEKS,MAXBENEFIT,MAXBENEFIT FROM QGPL/EXAMPLE");
        Assert.Equal(new[] { "SEL", "SALARY", "SALARY1", "SEL2", "MAXBENEFIT", "COL3" }, _files.GetDefinition("QGPL", "OUT")!.PrimaryFormat.Fields.Select(field => field.Name));
    }
    [Theory]
    [InlineData("CAST(ID AS SMALLINT)", Ipc.Db.Definitions.FieldType.Binary, 2)]
    [InlineData("CAST(ID AS DECIMAL(9,2))", Ipc.Db.Definitions.FieldType.Packed, 9)]
    [InlineData("CAST(ID AS NUMERIC(9,2))", Ipc.Db.Definitions.FieldType.Zoned, 9)]
    [InlineData("CAST(ID AS DOUBLE)", Ipc.Db.Definitions.FieldType.Float, 8)]
    [InlineData("DATE('2026-10-19')", Ipc.Db.Definitions.FieldType.Date, 10)]
    [InlineData("TIME('12:34:56')", Ipc.Db.Definitions.FieldType.Time, 8)]
    [InlineData("TIMESTAMP('2026-10-19 12:34:56.123456')", Ipc.Db.Definitions.FieldType.Timestamp, 26)]
    public void Typed_calculations_are_materialized_using_physical_field_layouts(string expression, Ipc.Db.Definitions.FieldType type, int length)
    {
        Save("SELECT " + expression + " AS VALUE FROM QGPL/DATA WHERE ID=1");
        var field = Assert.Single(_files.GetDefinition("QGPL", "OUT")!.PrimaryFormat.Fields); Assert.Equal(type, field.Type); Assert.Equal(length, field.Length);
        Assert.Single(Rows());
    }
    [Fact]
    public void Empty_results_still_create_headings_and_replace_clears_an_existing_member()
    {
        Assert.Equal(0, Save("SELECT ID,NAME FROM QGPL/DATA WHERE 1=0").Rows); Assert.Empty(Rows());
        Save(); Assert.Equal(3, Rows().Count); Assert.Equal(0, Save("SELECT ID,NAME FROM QGPL/DATA WHERE 1=0").Rows); Assert.Empty(Rows());
    }
    [Fact]
    public void Append_and_replace_match_columns_by_ordinal_and_preserve_the_existing_file_definition()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/OUT(N INTEGER NOT NULL,TEXT VARCHAR(30))", Options);
        _files.ExecuteSql("INSERT INTO QGPL/OUT VALUES(99,'old')", Options); var before = _files.GetDefinition("QGPL", "OUT")!.ToJson();
        Save(replace: false); Assert.Equal(new long[] { 99, 1, 2, 3 }, Rows().Select(row => (long)row["N"]!));
        Save(); Assert.Equal(new long[] { 1, 2, 3 }, Rows().Select(row => (long)row["N"]!)); Assert.Equal(before, _files.GetDefinition("QGPL", "OUT")!.ToJson());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Self_output_uses_the_complete_pre_write_snapshot(bool replace)
    {
        var result = Save("SELECT ID,NAME FROM QGPL/DATA ORDER BY ID", "DATA", replace);
        Assert.Equal(3, result.Rows); Assert.Equal(replace ? 3 : 6, Rows("DATA").Count);
    }
    [Theory]
    [InlineData("SELECT ID FROM QGPL/DATA", "SQL0117")]
    [InlineData("SELECT ID,CAST(NAME AS VARCHAR(50)) FROM QGPL/DATA", "SQL0408")]
    [InlineData("SELECT CAST(NULL AS INTEGER),NAME FROM QGPL/DATA", "SQL0408")]
    public void Incompatible_metadata_is_rejected_before_the_clear_boundary(string sql, string error)
    {
        Save(); var before = Rows(); Assert.Equal(error, Assert.Throws<CpfException>(() => Save(sql)).MessageId);
        Assert.Equal(before.Select(row => row["ID"]), Rows().Select(row => row["ID"]));
    }
    [Theory]
    [InlineData("SELECT ID/(ID-2) AS N FROM QGPL/DATA", "SQL0802")]
    [InlineData("SELECT CAST(ID AS DECIMAL(63,0)) FROM QGPL/DATA", "SQL0406")]
    public void Query_failure_or_unimplemented_external_precision_has_no_output_side_effects(string sql, string error)
    {
        Assert.Equal(error, Assert.Throws<CpfException>(() => Save(sql)).MessageId); Assert.False(_files.FileExists("QGPL", "OUT"));
    }
    [Fact]
    public void Resource_quota_failure_happens_while_spooling_before_any_target_change()
    {
        Save(); Assert.Equal("SQL0904", Assert.Throws<CpfException>(() => Save(limits: new(MaximumRows: 1))).MessageId); Assert.Equal(3, Rows().Count);
    }
    [Fact]
    public void Unique_failure_after_replacement_keeps_the_clear_and_the_successful_prefix()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/OUT(ID INTEGER PRIMARY KEY)", Options); _files.ExecuteSql("INSERT INTO QGPL/OUT VALUES(99)", Options);
        var error = Assert.Throws<CpfException>(() => Save("SELECT 1 AS ID FROM QGPL/DATA")); Assert.Equal("SQL0803", error.MessageId);
        Assert.Contains("was cleared; 1 rows were written", error.Message); Assert.Equal(1L, Assert.Single(Rows())["ID"]);
    }
    [Fact]
    public void Append_failure_keeps_old_records_and_the_successful_prefix()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/OUT(ID INTEGER PRIMARY KEY)", Options); _files.ExecuteSql("INSERT INTO QGPL/OUT VALUES(2)", Options);
        var error = Assert.Throws<CpfException>(() => Save("SELECT ID FROM QGPL/DATA ORDER BY ID", replace: false)); Assert.Equal("SQL0803", error.MessageId);
        Assert.Contains("was changed; 1 rows were written", error.Message); Assert.Equal(new long[] { 2, 1 }, Rows().Select(row => (long)row["ID"]!));
    }
    [Fact]
    public void Canceling_between_publications_keeps_completed_rows_and_releases_output_allocations()
    {
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Save("SELECT ID FROM QGPL/DATA ORDER BY ID", token: stop.Token, validate: () => {
            if (_files.FileExists("QGPL", "OUT") && _files.RowCount("QGPL", "OUT", "OUT") == 1) stop.Cancel(); }));
        Assert.Single(Rows()); Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "OUT", ObjectType.File)), item => item.Job == _session.Job.Key);
    }
    [Fact]
    public void Output_cli_accepts_the_native_first_member_replace_add_and_SQL_naming_modes()
    {
        Query("SELECT ID,NAME FROM QGPL/DATA WHERE ID=&ID");
        Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) SETVAR((ID 1))"));
        Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(*FIRST *ADD) SETVAR((ID 2))")); Assert.Equal(2, Rows().Count);
        Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) SETVAR((ID 3))")); Assert.Single(Rows());
        Query("SELECT ID,NAME FROM QGPL.DATA", "SQLREPORT");
        Ok(_session.Execute("STRQMQRY QGPL/SQLREPORT OUTPUT(*OUTFILE) OUTFILE(OTHER) NAMING(*SQL)")); Assert.Equal(3, Rows("OTHER").Count);
        Assert.Equal("QWM2707", _session.Execute("STRQMQRY QGPL/SQLREPORT OUTPUT(*OUTFILE) OUTFILE(*LIBL/OTHER) NAMING(*SQL)").MessageId);
    }
    [Theory]
    [InlineData("OUTPUT(*OUTFILE)")]
    [InlineData("OUTFILE(QGPL/OUT)")]
    [InlineData("OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(OTHER *ADD)")]
    [InlineData("OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(*FIRST *BAD)")]
    [InlineData("OUTPUT(*OUTFILE) OUTFILE(QGPL/../OUT)")]
    public void Invalid_output_parameters_fail_before_running_SQL(string parameters)
    {
        Query("DELETE FROM QGPL/DATA"); Assert.True(_session.Execute("STRQMQRY REPORT " + parameters).IsError); Assert.Equal(3, Rows("DATA").Count);
    }
    [Fact]
    public void A_non_SELECT_query_does_not_create_an_outfile()
    {
        Query("UPDATE QGPL/DATA SET ID=ID+10"); Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)"));
        Assert.False(_files.FileExists("QGPL", "OUT")); Assert.Equal(new long[] { 11, 12, 13 }, Rows("DATA").Select(row => (long)row["ID"]!));
    }
    [Fact]
    public void Output_overrides_redirect_to_the_selected_existing_member_without_replacing_other_members()
    {
        Save(); _files.ChangePhysicalMemberLimit("QGPL", "OUT", 2); _files.AddMember("QGPL", "OUT", "SECOND");
        Query("SELECT ID,NAME FROM QGPL/DATA WHERE ID=2");
        Ok(_session.Execute("OVRDBF FILE(ALIAS) TOFILE(QGPL/OUT) MBR(SECOND) OVRSCOPE(*JOB)"));
        Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/ALIAS)"));
        Assert.Equal(3, Rows().Count); Assert.Equal(2L, Assert.Single(Rows(member: "SECOND"))["ID"]); Assert.False(_files.FileExists("QGPL", "ALIAS"));
    }
    [Fact]
    public void Unsupported_or_corrupt_query_sort_metadata_cannot_be_used_to_create_output()
    {
        Query("SELECT ID FROM QGPL/DATA"); var query = _system.Objects.GetRequired("QGPL", "REPORT", ObjectType.QueryManagementQuery);
        query.ExtendedAttributes = new Dictionary<string, string>(query.ExtendedAttributes!) { ["ipc.query.sortSequence"] = "*LANGIDSHR" }; _system.Objects.Update(query);
        Assert.Equal("QWM2701", _session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)").MessageId); Assert.False(_files.FileExists("QGPL", "OUT"));
    }
}
