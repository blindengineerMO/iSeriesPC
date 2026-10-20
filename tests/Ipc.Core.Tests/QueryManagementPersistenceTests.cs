using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class QueryManagementPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-qmqry-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Fact]
    public void Saved_queries_survive_restart_without_source_files_and_execute_in_a_new_job()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "QueryRestartFixture643");
            new SqliteFileStore(system.Connections, system.Objects).ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER)", new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]));
            new QueryManagementStore(system.Connections).Create("QGPL", "SAVED", "INSERT INTO QGPL/DATA VALUES(&ID)");
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        using var session = new ExecutionSession(reopened, reopened.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("STRQMQRY SAVED SETVAR((ID 71))"); Assert.False(result.IsError, result.Message);
        Assert.Equal(71L, Assert.Single(new SqliteFileStore(reopened.Connections, reopened.Objects).ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }
    [Fact]
    public async Task Concurrent_factories_serialize_no_replace_creation_and_leave_one_complete_object()
    {
        using var system = IpcSystem.Create(_directory); system.Start();
        using var other = new SqliteConnectionFactory(_directory);
        var stores = new[] { new QueryManagementStore(system.Connections), new QueryManagementStore(other) };
        var results = await Task.WhenAll(stores.Select(store => Task.Run(() => Record.Exception(() => store.Create("QGPL", "QUERY", "SELECT ID FROM DATA", replace: false)))));
        Assert.Single(results, result => result is null); Assert.Equal("QWM2706", Assert.IsType<CpfException>(Assert.Single(results, result => result is not null)).MessageId);
        Assert.Contains("SELECT ID FROM DATA", stores[0].Read("QGPL", "QUERY"));
    }
}
