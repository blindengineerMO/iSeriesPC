using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class SqlTablePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-sql-table-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"]);
    [Fact]
    public void SQL_metadata_defaults_constraints_and_rows_survive_restarting_the_catalog()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, CODE NUMERIC(7,2) NOT NULL DEFAULT 1.25)", Options);
            files.ExecuteSql("INSERT INTO QGPL.DATA(ID) VALUES(1)", Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var restored = new SqliteFileStore(reopened.Connections, reopened.Objects);
        var definition = restored.GetDefinition("QGPL", "DATA")!;
        Assert.NotNull(definition.SqlTable); Assert.Equal(Ipc.Db.Definitions.FieldType.Zoned, definition.PrimaryFormat.Find("CODE")!.Type);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => restored.ExecuteSql("INSERT INTO QGPL.DATA(ID) VALUES(1)", Options)).MessageId);
        restored.ExecuteSql("INSERT INTO QGPL.DATA(ID) VALUES(2)", Options);
        Assert.Equal(new[] { 1.25m, 1.25m }, restored.ReadAll("QGPL", "DATA", "DATA").Select(row => (decimal)row["CODE"]!));
    }
    [Fact]
    public async Task Concurrent_factories_create_one_catalog_object_and_the_loser_reports_SQL0601()
    {
        using var system = IpcSystem.Create(_directory); system.Start();
        using var other = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory);
        var first = new SqliteFileStore(system.Connections, system.Objects);
        var second = new SqliteFileStore(other, new Ipc.Services.Sqlite.SqliteObjectStore(other));
        var errors = await Task.WhenAll(new[] { first, second }.Select(files => Task.Run(() => Record.Exception(() => files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY)", Options)))));
        Assert.Single(errors, error => error is null);
        Assert.Equal("SQL0601", Assert.IsType<CpfException>(Assert.Single(errors, error => error is not null)).MessageId);
        first.ExecuteSql("INSERT INTO QGPL.DATA VALUES(1)", Options); Assert.Equal(1, second.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Legacy_lock_catalog_migrates_atomically_to_record_set_coordination()
    {
        string path;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); path = system.Connections.DatabasePath! + ".locks";
            using var connection = system.Connections.Locks.Open();
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER release_holder_records; DROP TABLE holder_records; UPDATE ipc_lock_version SET version=1"; command.ExecuteNonQuery();
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        using var restored = reopened.Connections.Locks.Open(); using var read = restored.CreateCommand();
        read.CommandText = "SELECT version FROM ipc_lock_version"; Assert.Equal(2L, read.ExecuteScalar());
        read.CommandText = "SELECT count(*) FROM holder_records"; Assert.Equal(0L, read.ExecuteScalar());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
