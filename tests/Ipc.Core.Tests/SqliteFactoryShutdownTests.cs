using Ipc.Services.Sqlite;
using Ipc.Services;
using Ipc.Session;
using Ipc.Core.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqliteFactoryShutdownTests
{
    [Fact]
    public async Task Ending_an_owned_session_cancels_its_resources_before_waiting_for_catalog_access()
        => await Task.Factory.StartNew(CheckOwnedSessionShutdown,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);

    private sealed class CancellationProbe(ManualResetEventSlim canceled) : IDisposable
    { public void Dispose() => canceled.Set(); }

    private static void CheckOwnedSessionShutdown()
    {
        using var system=IpcSystem.Create(":memory:"); system.Start();
        using var session=new ExecutionSession(system,system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None);
        using var canceled=new ManualResetEventSlim();
        using var registration=system.Connections.RegisterJobRuntimeResource(session.Job.Key,new CancellationProbe(canceled));
        using var connection=system.Connections.Open(); using var writer=connection.BeginTransaction(deferred:false);
        using var schema=connection.CreateCommand(); schema.Transaction=writer;
        schema.CommandText="CREATE TABLE held_schema(ID INTEGER)"; schema.ExecuteNonQuery();
        var closing=Task.Factory.StartNew(() => Record.Exception(session.Dispose),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try { Assert.True(canceled.Wait(TimeSpan.FromSeconds(2)),"Owned resources must be canceled before session cleanup waits for the catalog."); }
        finally { writer.Rollback(); closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        Assert.Null(closing.Result); Assert.Equal(JobStatus.Completed,system.Jobs.GetRequired(session.Job.Key).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_signals_cancellation_while_an_open_waits_for_a_schema_writer(bool initializeFactory)
        => await Task.Factory.StartNew(() => CheckBlockedOpenShutdown(initializeFactory),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);

    private static void CheckBlockedOpenShutdown(bool initializeFactory)
    {
        using var factory=new SqliteConnectionFactory(":memory:");
        using var connection=initializeFactory ? factory.Open() : new SqliteConnection(factory.ConnectionString);
        if (!initializeFactory) connection.Open();
        using var writer=connection.BeginTransaction(deferred:false);
        using var schema=connection.CreateCommand(); schema.Transaction=writer;
        schema.CommandText="CREATE TABLE held_schema(ID INTEGER)"; schema.ExecuteNonQuery();
        using var canceled=new ManualResetEventSlim(); using var cancelRegistration=factory.ShutdownToken.Register(canceled.Set);
        using var started=new ManualResetEventSlim();
        var opening=Task.Factory.StartNew(() => { started.Set(); return Record.Exception(() => { using var extra=factory.Open(); }); },
            CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Task<Exception>? closing=null;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)),"Open worker did not start.");
            Assert.False(opening.Wait(TimeSpan.FromMilliseconds(100)),"The schema writer must keep the new connection open pending.");
            closing=Task.Factory.StartNew(() => Record.Exception(factory.Dispose),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Assert.True(canceled.Wait(TimeSpan.FromSeconds(2)),"Factory shutdown could not signal cancellation while Open was blocked.");
        }
        finally
        {
            writer.Rollback();
            opening.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (closing is not null) closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        Assert.NotNull(closing); Assert.Null(closing.Result);
    }
}
