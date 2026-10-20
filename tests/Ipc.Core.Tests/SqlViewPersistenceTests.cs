using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqlViewPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-sql-view-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    [Fact]
    public void Restart_preserves_views_source_bindings_types_and_live_results_without_source_files()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,AMOUNT DECIMAL(29,2))", Options);
            files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1,10000000000000000000000000.01)", Options);
            files.ExecuteSql("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA", Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var restored = new SqliteFileStore(reopened.Connections, reopened.Objects);
        Assert.NotNull(restored.GetDefinition("QGPL", "REPORT")!.SqlView);
        using (var query = restored.OpenSqlQuery("SELECT AMOUNT FROM QGPL/REPORT WHERE ID=1", Options))
        { Assert.True(query.Read()); Assert.Equal("10000000000000000000000000.01", query.Current![0]!.ToString()); }
        restored.ExecuteSql("UPDATE QGPL/DATA SET ID=2", Options); Assert.Equal(2L, restored.ReadAll("QGPL", "REPORT", "REPORT")[0]["ID"]);
        restored.ExecuteSql("DROP VIEW QGPL/REPORT", Options); Assert.True(restored.FileExists("QGPL", "DATA"));
    }
    [Fact]
    public async Task Competing_factories_create_one_complete_view_and_report_a_native_duplicate_for_the_loser()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var first = new SqliteFileStore(system.Connections, system.Objects);
        first.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER)", Options); first.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options);
        using var other = new SqliteConnectionFactory(_directory); var second = new SqliteFileStore(other, new SqliteObjectStore(other));
        var errors = await Task.WhenAll(new[] { first, second }.Select(files => Task.Run(() => Record.Exception(() => files.ExecuteSql("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA", Options)))));
        Assert.Single(errors, error => error is null); Assert.Equal("SQL0601", Assert.IsType<CpfException>(Assert.Single(errors, error => error is not null)).MessageId);
        Assert.Equal(1L, second.ReadAll("QGPL", "REPORT", "REPORT")[0]["ID"]);
    }
    [Fact]
    public async Task Restart_preserves_check_options_and_competing_view_writes_share_base_constraints()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY)", Options);
            files.ExecuteSql("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA WHERE ID<5 WITH LOCAL CHECK OPTION", Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var first = new SqliteFileStore(reopened.Connections, reopened.Objects);
        Assert.Equal(Ipc.Db.Definitions.SqlViewCheckOption.Local, first.GetDefinition("QGPL", "REPORT")!.SqlView!.CheckOption);
        Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => first.ExecuteSql("INSERT INTO QGPL/REPORT VALUES(5)", Options)).MessageId);
        using var other = new SqliteConnectionFactory(_directory); var second = new SqliteFileStore(other, new SqliteObjectStore(other));
        var errors = await Task.WhenAll(new[] { first, second }.Select(files => Task.Run(() => Record.Exception(() => files.ExecuteSql("INSERT INTO QGPL/REPORT VALUES(1)", Options)))));
        Assert.Single(errors, error => error is null); Assert.Equal("SQL0803", Assert.IsType<CpfException>(Assert.Single(errors, error => error is not null)).MessageId);
        Assert.Equal(1, first.ExecuteSql("UPDATE QGPL/REPORT SET ID=2", Options));
        Assert.Equal(2L, second.ReadAll("QGPL", "REPORT", "REPORT")[0]["ID"]);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
