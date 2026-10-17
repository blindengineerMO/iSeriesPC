using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqliteCancellationTests
{
    [Fact]
    public async Task Cancels_an_executing_SQLite_vm_and_clears_callback_before_reusing_connection()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        using var connection = factory.Open();
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        connection.CreateFunction("execution_started", () => { started.Set(); return 1; });
        // This deliberately blocking native query needs its own worker. A queued
        // thread-pool task can miss the startup deadline under the full suite.
        var run = Task.Factory.StartNew(() => SqliteCancellation.Run(connection, stop.Token, () => {
            using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE numbers(n) AS (SELECT execution_started() UNION ALL SELECT n+1 FROM numbers WHERE n<1000000000) SELECT sum(n) FROM numbers";
            return command.ExecuteScalar();
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "The SQLite query did not start.");
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            // A failed startup assertion must still stop the native VM before
            // disposing its connection, rather than waiting for a billion rows.
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
        using var next = connection.CreateCommand();
        next.CommandText = "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<10000) SELECT sum(n) FROM numbers";
        Assert.Equal(50005000L, next.ExecuteScalar());
        var invoked = false;
        Assert.Throws<OperationCanceledException>(() => SqliteCancellation.Run(connection, stop.Token, () => { invoked = true; return 0; }));
        Assert.False(invoked);
    }
}
