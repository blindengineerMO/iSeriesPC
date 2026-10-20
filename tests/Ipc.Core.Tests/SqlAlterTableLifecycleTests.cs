using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests
{
    [Fact]
    public async Task A_canceled_writer_wait_cannot_publish_an_altered_definition_or_member()
    {
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false); using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => Record.Exception(() => _files.ExecuteSql("ALTER TABLE QGPL/DATA ADD FLAG INTEGER", Options, cancel.Token)));
        try { await Task.Delay(150); cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(await task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancel.Cancel(); transaction.Rollback(); await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("FLAG"));
    }
    [Fact]
    public void All_members_keep_their_identities_and_receive_one_statement_clock_and_layout()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", 2); _files.AddMember("QGPL", "DATA", "OTHER");
        Scalar("INSERT INTO \"QGPL.DATA.OTHER\"(ID,CODE,AMOUNT) VALUES(7,'other',1)");
        var first = Scalar("SELECT id FROM sys_record_slots WHERE name='DATA' AND mbr='DATA'");
        var second = Scalar("SELECT id FROM sys_record_slots WHERE name='DATA' AND mbr='OTHER'");
        Run("ALTER TABLE QGPL/DATA ADD FLAG INTEGER NOT NULL DEFAULT 5 ADD STAMP TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP");
        Assert.Equal(first, Scalar("SELECT id FROM sys_record_slots WHERE name='DATA' AND mbr='DATA'"));
        Assert.Equal(second, Scalar("SELECT id FROM sys_record_slots WHERE name='DATA' AND mbr='OTHER'"));
        Assert.Equal(5L, Scalar("SELECT FLAG FROM \"QGPL.DATA.OTHER\""));
        Assert.Equal(Scalar("SELECT STAMP FROM \"QGPL.DATA.DATA\" LIMIT 1"), Scalar("SELECT STAMP FROM \"QGPL.DATA.OTHER\""));
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD UNIQUE(ID)")).MessageId);
    }
    [Fact]
    public void Validation_checks_the_resulting_types_and_defaults_without_executing_data_conversion()
    {
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        _files.ValidateSql("ALTER TABLE QGPL/DATA ALTER AMOUNT SET DATA TYPE DECIMAL(2,1)", Options);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Assert.Equal("SQL0190", Assert.Throws<CpfException>(() => _files.ValidateSql("ALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE INTEGER", Options)).MessageId);
    }
    [Fact]
    public void Validation_checks_column_dependencies_without_running_catalog_hooks_or_rewriting_views()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT CODE FROM QGPL/DATA");
        Scalar("CREATE TABLE observed(n INTEGER); CREATE TRIGGER observe_alter AFTER UPDATE OF def ON sys_file_defs BEGIN INSERT INTO observed VALUES(1); END");
        var before = _files.GetDefinition("QGPL", "REPORT")!.ToJson();
        var error = Assert.Throws<CpfException>(() => _files.ValidateSql("ALTER TABLE QGPL/DATA DROP CODE RESTRICT", Options));
        Assert.Equal("SQL0478", error.MessageId); Assert.Equal("42893", error.SqlState);
        _files.ValidateSql("ALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE VARCHAR(5)", Options);
        Assert.Equal(before, _files.GetDefinition("QGPL", "REPORT")!.ToJson()); Assert.Equal(0L, Scalar("SELECT count(*) FROM observed"));
    }
    [Fact]
    public void Unregistered_physical_constraints_and_foreign_relationships_are_rejected_before_any_rewrite()
    {
        Scalar("CREATE TABLE child(id INTEGER REFERENCES \"QGPL.DATA.DATA\"(rowid))");
        Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId);
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("N"));
    }
    [Fact]
    public void Failed_CCSID_conversion_is_reported_as_SQL_and_preserves_the_original_character_data()
    {
        var options = Options with { Ccsid = 1208 };
        _files.ExecuteSql("CREATE TABLE QGPL/TEXTDATA(VALUE VARCHAR(12))", options); _files.ExecuteSql("INSERT INTO QGPL/TEXTDATA VALUES('東京')", options);
        Assert.Equal("SQL0332", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/TEXTDATA ALTER VALUE SET DATA TYPE VARCHAR(12) CCSID 37")).MessageId);
        Assert.Equal("東京", Query("SELECT VALUE FROM QGPL/TEXTDATA")[0][0]);
    }
}

public sealed class SqlAlterTablePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-sql-alter-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    [Fact]
    public void Restart_preserves_altered_formats_defaults_and_dependent_view_index_bindings()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            foreach (var sql in new[] { "CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY,CODE VARCHAR(12))", "INSERT INTO QGPL/DATA VALUES(1,'before')", "CREATE VIEW QGPL/REPORT AS SELECT * FROM QGPL/DATA", "CREATE INDEX QGPL/BYCODE ON QGPL/DATA(CODE)", "ALTER TABLE QGPL/DATA ADD FLAG INTEGER NOT NULL DEFAULT 5 ALTER CODE SET DATA TYPE VARCHAR(5) DEFAULT 'new'" }) files.ExecuteSql(sql, Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var restored = new SqliteFileStore(reopened.Connections, reopened.Objects);
        Assert.Equal(5, restored.GetDefinition("QGPL", "REPORT")!.PrimaryFormat.Find("CODE")!.Length);
        Assert.Equal(5, restored.GetDefinition("QGPL", "BYCODE")!.PrimaryFormat.Find("CODE")!.Length);
        restored.ExecuteSql("INSERT INTO QGPL/DATA(ID) VALUES(2)", Options);
        Assert.Equal(5L, restored.ReadAll("QGPL", "DATA", "DATA")[1]["FLAG"]);
        Assert.Equal("new", restored.ReadAll("QGPL", "REPORT", "REPORT")[1]["CODE"]);
    }
    [Fact]
    public async Task Competing_factories_extend_the_same_live_definition_without_lost_columns()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var first = new SqliteFileStore(system.Connections, system.Objects);
        first.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER)", Options); first.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options);
        using var other = new SqliteConnectionFactory(_directory); var second = new SqliteFileStore(other, new SqliteObjectStore(other));
        await Task.WhenAll(Task.Run(() => first.ExecuteSql("ALTER TABLE QGPL/DATA ADD FIRST INTEGER DEFAULT 2", Options)), Task.Run(() => second.ExecuteSql("ALTER TABLE QGPL/DATA ADD SECOND INTEGER DEFAULT 3", Options)));
        Assert.Equal(3, first.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Fields.Count);
        var row = Assert.Single(second.ReadAll("QGPL", "DATA", "DATA")); Assert.Equal(2L, row["FIRST"]); Assert.Equal(3L, row["SECOND"]);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
