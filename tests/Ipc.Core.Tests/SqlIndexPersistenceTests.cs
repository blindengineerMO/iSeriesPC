using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqlIndexPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-sql-index-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    [Fact]
    public void Restart_preserves_index_metadata_order_and_uniqueness_and_DROP_releases_the_constraint()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL)", Options);
            files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1),(2)", Options);
            files.ExecuteSql("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID DESC) RCDFMT IXREC ADD KEYS ONLY", Options);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); var restored = new SqliteFileStore(reopened.Connections, reopened.Objects);
        var definition = restored.GetDefinition("QGPL", "IDX")!; Assert.True(definition.SqlIndex); Assert.Equal("IXREC", definition.PrimaryFormat.Name);
        Assert.Equal(new long[] { 2, 1 }, restored.ReadKeyed("QGPL", "IDX", "IDX").Select(row => (long)row["ID"]!));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => restored.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options)).MessageId);
        restored.ExecuteSql("DROP INDEX QGPL/IDX", Options); restored.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options);
    }
    [Fact]
    public async Task Competing_factories_publish_one_index_and_report_SQL0601_for_the_loser()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var first = new SqliteFileStore(system.Connections, system.Objects);
        first.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL)", Options);
        using var other = new SqliteConnectionFactory(_directory); var second = new SqliteFileStore(other, new SqliteObjectStore(other));
        var errors = await Task.WhenAll(new[] { first, second }.Select(files => Task.Run(() => Record.Exception(() => files.ExecuteSql("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)", Options)))));
        Assert.Single(errors, error => error is null); Assert.Equal("SQL0601", Assert.IsType<CpfException>(Assert.Single(errors, error => error is not null)).MessageId);
        first.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => second.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options)).MessageId);
        Assert.Single(second.ReadKeyed("QGPL", "IDX", "IDX"));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
