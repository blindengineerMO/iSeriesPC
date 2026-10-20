using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class SqlQueryOutputPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-output-" + Guid.NewGuid().ToString("N"));
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Fact]
    public void File_backed_output_types_data_and_private_authority_survive_restart()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
            files.ExecuteSql("CREATE TABLE QGPL/SOURCE(N DECIMAL(29,4),DAY DATE)", Options);
            files.ExecuteSql("INSERT INTO QGPL/SOURCE VALUES(1234567890123456789012345.6789,'2026-10-19')", Options);
            files.WriteSqlQueryFile("SELECT N,DAY FROM QGPL/SOURCE", Options, new("QGPL", "OUT"));
        }
        using var restored = IpcSystem.Create(_directory); restored.Start(); var reopened = new SqliteFileStore(restored.Connections, restored.Objects);
        var row = Assert.Single(reopened.ReadAll("QGPL", "OUT", "OUT")); Assert.Equal(1234567890123456789012345.6789m, row["N"]);
        Assert.Equal(AuthorityBit.None, restored.Objects.GetRequired("QGPL", "OUT", ObjectType.File).PublicAuthority);
        Assert.Equal(29, reopened.GetDefinition("QGPL", "OUT")!.PrimaryFormat.Fields[0].Length);
    }
    [Fact]
    public async Task A_writer_wait_revalidates_credentials_before_creating_output()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "QueryOutputWait827");
        system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        var files = new SqliteFileStore(system.Connections, system.Objects); files.ExecuteSql("CREATE TABLE QGPL/SOURCE(ID INTEGER)", Options); files.ExecuteSql("INSERT INTO QGPL/SOURCE VALUES(1)", Options);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using var blocker = system.Connections.Open(); using var transaction = blocker.BeginTransaction(deferred: false);
        using var stageReady = new ManualResetEventSlim();
        var pending = Task.Run(() => { using var owner = OperationIdentity.Enter("QUSER", session.Job.Key); return Record.Exception(() =>
            files.WriteSqlQueryFile("SELECT ID FROM QGPL/SOURCE", Options, new("QGPL", "OUT"), validateQuery: () => stageReady.Set())); });
        Assert.True(stageReady.Wait(TimeSpan.FromSeconds(10)));
        var locks = new Ipc.Services.Work.JobLockStore(system.Connections); var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!locks.Inspect(new("QGPL", "OUT", ObjectType.File)).Any(item => item.Job == session.Job.Key))
        { Assert.True(DateTime.UtcNow < deadline); await Task.Delay(10); }
        using (var disable = blocker.CreateCommand()) { disable.Transaction = transaction; disable.CommandText = "UPDATE sys_profiles SET status='Disabled' WHERE name='QUSER'"; disable.ExecuteNonQuery(); }
        transaction.Commit(); Assert.Equal("CPF9802", Assert.IsType<CpfException>(await pending.WaitAsync(TimeSpan.FromSeconds(10))).MessageId);
        Assert.False(files.FileExists("QGPL", "OUT"));
    }
}
