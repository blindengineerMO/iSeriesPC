using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlTableTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options(int ccsid = 37) => new(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"], ccsid);
    public SqlTableTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SqlTableFixtureAdmin43");
    }
    private long Run(string sql, int ccsid = 37) => _files.ExecuteSql(sql, Options(ccsid));
    private object? Scalar(string sql)
    {
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = sql; return command.ExecuteScalar();
    }
    public void Dispose() => _system.Dispose();

    [Fact]
    public void SQL_table_creation_publishes_one_native_file_member_format_and_record_slot_identity()
    {
        var sql = "CREATE TABLE QGPL.ACCOUNTS (ID INTEGER PRIMARY KEY, NAME VARCHAR(20), AMOUNT DECIMAL(12,2) NOT NULL DEFAULT 1.25) RCDFMT ACCOUNT";
        Assert.Equal(0, Run(sql));
        var descriptor = _system.Objects.GetRequired("QGPL", "ACCOUNTS", ObjectType.File);
        Assert.Equal("*PF", descriptor.Attribute); Assert.Equal("ACCOUNT", descriptor.Format); Assert.Equal(sql, descriptor.Source);
        var definition = _files.GetDefinition("QGPL", "ACCOUNTS")!;
        Assert.NotNull(definition.SqlTable); Assert.Equal(1, definition.MaximumMembers); Assert.True(definition.ReuseDeletedRecords); Assert.True(definition.MemberSize!.Unlimited);
        Assert.All(definition.PrimaryFormat.Fields, field => Assert.Equal(0, field.Sequence));
        Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='ACCOUNTS'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.ACCOUNTS.ACCOUNTS' AND name LIKE 'ipc_sql_unique_%'"));
        Assert.Equal(1, Run("INSERT INTO QGPL.ACCOUNTS(ID,NAME) VALUES(7,'Ada')"));
        var row = Assert.Single(_files.ReadAll("QGPL", "ACCOUNTS", "ACCOUNTS"));
        Assert.Equal(7L, row["ID"]); Assert.Equal("Ada", row["NAME"]); Assert.Equal(1.25m, row["AMOUNT"]);
    }

    [Theory]
    [InlineData(37, "café")]
    [InlineData(1208, "東京")]
    public void Multirow_VALUES_supports_comments_escaping_NULL_DEFAULT_and_typed_datetime_literals(int ccsid, string name)
    {
        Run("CREATE TABLE QGPL.DATA (ID INTEGER NOT NULL, NAME VARCHAR(24), DAY DATE DEFAULT DATE '2026-10-19', VALUE DECIMAL(8,2) DEFAULT -1.25)", ccsid);
        Assert.Equal(2, Run($"/* outer /* nested */ end */ INSERT INTO QGPL.DATA VALUES(1,'{name}',DEFAULT,DEFAULT),(2,'O''Neil',DATE '2026-10-18',NULL); -- suffix", ccsid));
        var rows = _files.ReadAll("QGPL", "DATA", "DATA"); Assert.Equal(2, rows.Count);
        Assert.Equal(name, rows[0]["NAME"]); Assert.Equal(-1.25m, rows[0]["VALUE"]); Assert.Null(rows[1]["VALUE"]); Assert.Equal("O'Neil", rows[1]["NAME"]);
    }

    [Theory]
    [InlineData("INSERT INTO QGPL.DATA(NAME) VALUES('missing')", "SQL0407")]
    [InlineData("INSERT INTO QGPL.DATA VALUES(NULL,'null')", "SQL0407")]
    [InlineData("INSERT INTO QGPL.DATA VALUES(2147483648,'big')", "SQL0406")]
    [InlineData("INSERT INTO QGPL.DATA VALUES(1,'too long value')", "SQL0404")]
    [InlineData("INSERT INTO QGPL.DATA(BAD) VALUES(1)", "SQL0206")]
    [InlineData("INSERT INTO QGPL.DATA VALUES(1)", "SQL0117")]
    public void Invalid_values_fail_before_any_row_or_extent_is_published(string sql, string error)
    {
        Run("CREATE TABLE QGPL.DATA (ID INTEGER NOT NULL, NAME VARCHAR(8))");
        Assert.Equal(error, Assert.Throws<CpfException>(() => Run(sql)).MessageId);
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }

    [Theory]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER); DROP TABLE QGPL.OTHER")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER REFERENCES QGPL.OTHER)")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER GENERATED ALWAYS AS IDENTITY)")]
    [InlineData("CREATE TABLE QGPL.DATA(ID DECIMAL(31,2))")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER, ID INTEGER)")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER) WITH REPLACE")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER /* unclosed")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER, CONSTRAINT P PRIMARY KEY(BAD))")]
    [InlineData("CREATE TABLE QGPL.\"mixedCase\"(ID INTEGER)")]
    [InlineData("CREATE TABLE QGPL.VERYLONGNAME(ID INTEGER)")]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER, PRIMARY KEY(ID), UNIQUE(ID))")]
    [InlineData("CREATE TABLE QGPL.DATA(TEXT CHAR(4) CCSID 37 CCSID 1208)")]
    public void Unsupported_or_malformed_SQL_does_not_leave_catalog_or_storage_fragments(string sql)
    {
        var objects = _system.Objects.ObjectCount;
        Assert.Throws<CpfException>(() => Run(sql)); Assert.Equal(objects, _system.Objects.ObjectCount);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_defs WHERE lib='QGPL'"));
    }

    [Fact]
    public void A_late_unique_failure_rolls_back_all_VALUES_rows_slots_and_journal_entries()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, TEXT CHAR(8))");
        _system.Journals.CreateReceiver(new("QGPL", "SQLRCV")); _system.Journals.Create(new("QGPL", "SQLJRN"), new("QGPL", "SQLRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "SQLJRN"), "*BOTH");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(1,'first'),(1,'second')")).MessageId);
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        Assert.Empty(_system.Journals.Read(new("QGPL", "SQLJRN")));
    }

    [Fact]
    public void Defaults_and_primary_unique_constraints_apply_to_ordinary_file_writes_and_survive_copy_move()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, CODE CHAR(4) NOT NULL UNIQUE, VALUE DECIMAL(8,2) DEFAULT 2.5)");
        Run("INSERT INTO QGPL.DATA(ID,CODE) VALUES(1,'A')");
        var objects = new ObjectCatalogOperations(_system.Connections);
        objects.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        objects.Relocate(new("QGPL", "COPIED"), ObjectType.File, new("QGPL", "MOVED"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.MOVED(ID,CODE) VALUES(2,'A ')")).MessageId);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.Insert("QGPL", "MOVED", "DATA", "DATA", new Dictionary<string, object?> { ["ID"] = 1, ["CODE"] = "B" }));
        Assert.NotNull(_files.GetDefinition("QGPL", "MOVED")!.SqlTable);
        Assert.Equal(1, Run("INSERT INTO QGPL.MOVED(ID,CODE) VALUES(2,'B')"));
        Assert.Equal(2, _files.RowCount("QGPL", "MOVED", "DATA"));
        Run("DROP TABLE QGPL.MOVED"); Assert.False(_files.FileExists("QGPL", "MOVED")); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void SQL_and_system_naming_use_their_respective_default_schema_collection_or_library_list()
    {
        var system = Options() with { Naming = SqlNaming.System };
        Assert.Equal(0, _files.ExecuteSql("CREATE TABLE DATA(ID INTEGER)", system));
        Assert.Equal(1, _files.ExecuteSql("INSERT INTO DATA VALUES(1)", system));
        Assert.Equal(1, _files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(2)", system));
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => _files.ExecuteSql("INSERT INTO QGPL.DATA VALUES(3)", system)).MessageId);
        Assert.Equal(1, _files.ExecuteSql("INSERT INTO DATA VALUES(3)", Options() with { DefaultSchema = "MISSING", DefaultCollection = "QGPL" }));
        Assert.Equal("SQL0204", Assert.Throws<CpfException>(() => _files.ExecuteSql("INSERT INTO DATA VALUES(4)", Options() with { DefaultSchema = "MISSING" })).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Type_defaults_current_datetime_and_explicit_NULL_defaults_are_preserved()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER NOT NULL WITH DEFAULT, NAME VARCHAR(8) DEFAULT NULL, DAY DATE NOT NULL WITH DEFAULT, STAMP TIMESTAMP DEFAULT CURRENT_TIMESTAMP)");
        Assert.Equal(2, Run("INSERT INTO QGPL.DATA VALUES(DEFAULT,DEFAULT,DEFAULT,DEFAULT),(DEFAULT,DEFAULT,DEFAULT,DEFAULT)"));
        var rows = _files.ReadAll("QGPL", "DATA", "DATA");
        Assert.Equal(0L, rows[0]["ID"]); Assert.Null(rows[0]["NAME"]); Assert.Equal(rows[0]["STAMP"], rows[1]["STAMP"]);
        Assert.Equal(1, Run("INSERT INTO QGPL.DATA DEFAULT VALUES"));
    }

    [Fact]
    public void Failure_during_SQL_constraint_creation_rolls_back_file_member_and_slot_catalogs()
    {
        using (var connection = _system.Connections.Open()) using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_sql_member AFTER INSERT ON sys_file_members BEGIN SELECT RAISE(ABORT,'reject SQL member'); END"; command.ExecuteNonQuery();
        }
        Assert.Equal("SQL0901", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY)")).MessageId);
        Assert.False(_files.FileExists("QGPL", "DATA")); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_record_slots WHERE name='DATA'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.DATA.DATA'"));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_RUNSQL_creates_private_tables_and_DCLF_reads_them(int ccsid)
    {
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        void Ok(string command) { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
        Ok("RUNSQL SQL('CREATE TABLE QTEMP/DATA(ID INTEGER NOT NULL, NAME CHAR(8) DEFAULT ''seed'')') COMMIT(*NONE)");
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "SQLAPP", """
            PGM
            DCLF FILE(QTEMP/DATA)
            RUNSQL SQL('INSERT INTO QTEMP/DATA(ID) VALUES(7)') COMMIT(*NC)
            RCVF
            IF COND(&ID *EQ 7 *AND &NAME *EQ 'seed') THEN(SNDPGMMSG MSG('SQL CL ACCEPTED'))
            ENDPGM
            """, null);
        Ok("CRTCLPGM PGM(QGPL/SQLAPP) SRCFILE(QGPL/SOURCE)"); _files.RemoveMember("QGPL", "SOURCE", "SQLAPP");
        var called = session.Execute("CALL QGPL/SQLAPP"); Assert.False(called.IsError, called.Message); Assert.Contains("SQL CL ACCEPTED", called.Message);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_defs WHERE lib='QTEMP' AND name='DATA'"));
        Ok("RUNSQL SQL('DROP TABLE QTEMP/DATA') COMMIT(*NONE)");
    }

    [Fact]
    public void RUNSQL_metadata_enforces_native_length_and_unsupported_commitment_mode_is_explicit()
    {
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var parameter = Assert.Single(session.CommandMetadata("RUNSQL").Parameters, parameter => parameter.Keyword == "SQL");
        Assert.Equal(5000, parameter.MaximumLength); Assert.True(parameter.Literal);
        Assert.True(session.Execute("RUNSQL SQL('CREATE TABLE QGPL/DATA(ID INTEGER)')").IsError);
        Assert.False(_files.FileExists("QGPL", "DATA"));
        Assert.True(session.Execute("RUNSQL SQL('" + new string(' ', 5001) + "') COMMIT(*NONE)").IsError);
        Assert.False(session.Execute("RUNSQL SQL('CREATE TABLE QGPL.DATA(ID INTEGER)') COMMIT(*NONE) NAMING(*SQL)").IsError);
    }
}
