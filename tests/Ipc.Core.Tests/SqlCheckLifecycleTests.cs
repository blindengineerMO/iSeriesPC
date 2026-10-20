using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Session;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlCheckConstraintTests
{
    [Fact]
    public void Failed_multirow_checks_roll_back_records_slots_and_journal_entries_together()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))");
        _system.Journals.CreateReceiver(new("QGPL", "CKRCV")); _system.Journals.Create(new("QGPL", "CKJRN"), new("QGPL", "CKRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "CKJRN"), "*BOTH");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1),(-1)")).MessageId);
        Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'")); Assert.Empty(_system.Journals.Read(new("QGPL", "CKJRN")));
        Run("INSERT INTO QGPL/DATA VALUES(1),(2)"); Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/DATA SET ID=ID-2")).MessageId);
        Assert.Equal(3L, Scalar("SELECT sum(ID) FROM \"QGPL.DATA.DATA\"")); Assert.Equal(new[] { "PT", "PT" }, _system.Journals.Read(new("QGPL", "CKJRN")).Select(entry => entry.Type));
    }
    [Fact]
    public void Type_changes_recheck_normalized_existing_values_before_publishing_a_new_layout()
    {
        Run("CREATE TABLE QGPL/DATA(V DECIMAL(10,2),CONSTRAINT FLOORVAL CHECK(V>10.20))"); Run("INSERT INTO QGPL/DATA VALUES(10.25)");
        Run("CREATE VIEW QGPL/REPORT AS SELECT V FROM QGPL/DATA");
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ALTER V SET DATA TYPE DECIMAL(10,1)")).MessageId);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson()); Assert.Equal(2, _files.GetDefinition("QGPL", "REPORT")!.PrimaryFormat.Find("V")!.Decimals);
    }
    [Fact]
    public void Validation_binds_checks_without_evaluating_existing_data_or_running_catalog_hooks()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(-1)");
        Scalar("CREATE TABLE seen(n INTEGER); CREATE TRIGGER mark_check AFTER UPDATE OF def ON sys_file_defs BEGIN INSERT INTO seen VALUES(1); END");
        _files.ValidateSql("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(ID>0)", Options());
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks); Assert.Equal(0L, Scalar("SELECT count(*) FROM seen"));
        var invalid = Assert.Throws<CpfException>(() => _files.ValidateSql("ALTER TABLE QGPL/DATA ADD CHECK(CURRENT_DATE IS NOT NULL)", Options()));
        Assert.Equal("SQL0546", invalid.MessageId); Assert.Equal("42621", invalid.SqlState);
    }
    [Fact]
    public void Native_CPYF_reports_CPF502F_and_preserves_the_target_when_a_later_row_fails()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))"); Run("INSERT INTO QGPL/DATA VALUES(9)");
        Run("CREATE TABLE QGPL/SOURCE(ID INTEGER)"); Run("INSERT INTO QGPL/SOURCE VALUES(1),(-1)");
        using var session = Session(); var result = session.Execute("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/DATA) MBROPT(*REPLACE)");
        Assert.True(result.IsError); Assert.Contains("CPF502F", result.Message); Assert.Equal(9L, Scalar("SELECT ID FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void Compiled_RPG_WRITE_uses_base_checks_and_recovers_through_the_E_extender()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0)) RCDFMT ROW");
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "CHECKPGM", """
            **free
            dcl-f DATA extdesc('QGPL/DATA') extfile(*extdesc) usage(*output);
            eval ID=-1;
            write(e) ROW;
            if not %error or %status(DATA)<>1299;
            sndpgmmsg 'BAD CHECK STATUS';
            return;
            endif;
            eval ID=2;
            write(e) ROW;
            if not %error and %status(DATA)=0;
            sndpgmmsg 'CHECK RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        using var session = Session(); var compile = session.Execute("CRTBNDRPG PGM(QGPL/CHECKPGM) SRCFILE(QGPL/SOURCE)"); Assert.False(compile.IsError, compile.Message);
        var result = session.Execute("CALL QGPL/CHECKPGM"); Assert.False(result.IsError, result.Message); Assert.Contains("CHECK RPG ACCEPTED", result.Message);
        Assert.Equal(2L, Scalar("SELECT ID FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void Saved_scripts_and_queries_share_check_creation_and_failure_diagnostics()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "CHECKS", "CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0));\nINSERT INTO QGPL/DATA VALUES(1);", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "QUERY", "INSERT INTO QGPL/DATA VALUES(-1)", null);
        using var session = Session(); Ok("RUNSQLSTM QGPL/SOURCE CHECKS COMMIT(*NONE) OPTION(*NOLIST)"); Ok("CRTQMQRY QGPL/QUERY SRCFILE(QGPL/SOURCE)");
        var result = session.Execute("STRQMQRY QGPL/QUERY"); Assert.True(result.IsError); Assert.Contains("SQL0545", result.Message); Assert.Equal(1L, Scalar("SELECT ID FROM \"QGPL.DATA.DATA\""));
        void Ok(string command) { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
    }
    private ExecutionSession Session()
    { _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CheckNativeAdmin329"); return new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None); }
    [Fact]
    public void Native_initialization_cannot_populate_default_rows_that_violate_a_check()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,CHECK(ID>0))");
        using var session = Session(); var result = session.Execute("INZPFM FILE(QGPL/DATA) TOTRCDS(3)");
        Assert.True(result.IsError); Assert.Contains("CPF502F", result.Message);
        Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'")); Assert.Equal(0L, Scalar("SELECT count(*) FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void Query_outfile_append_cannot_bypass_target_checks()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))"); Run("INSERT INTO QGPL/DATA VALUES(9)");
        Run("CREATE TABLE QGPL/SOURCE(ID INTEGER)"); Run("INSERT INTO QGPL/SOURCE VALUES(1),(-1)");
        using var session = Session(); using var identity = Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => _files.WriteSqlQueryFile("SELECT ID FROM QGPL/SOURCE ORDER BY ID DESC", Options(), new("QGPL", "DATA"), replace: false)).MessageId);
        Assert.Equal(new long[] { 9, 1 }, _files.ReadAll("QGPL", "DATA", "DATA").Select(row => (long)row["ID"]!));
    }
}

public sealed class SqlCheckPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-sql-check-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    [Fact]
    public void Restart_enforces_persisted_checks_on_service_connections_before_a_database_store_exists()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))", Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        using (var connection = reopened.Connections.Open()) using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO \"QGPL.DATA.DATA\" VALUES(-1)"; Assert.Equal(275, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteExtendedErrorCode);
        }
        var restored = new SqliteFileStore(reopened.Connections, reopened.Objects); restored.ExecuteSql("ALTER TABLE QGPL/DATA ADD FLAG INTEGER", Options);
        restored.ExecuteSql("INSERT INTO QGPL/DATA(ID) VALUES(1)", Options);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => restored.ExecuteSql("UPDATE QGPL/DATA SET ID=-1", Options)).MessageId);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
