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
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.CreateFunction("execution_started", () => { started.TrySetResult(); return 1; });
        var run = Task.Run(() => SqliteCancellation.Run(connection, stop.Token, () => {
            using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE numbers(n) AS (SELECT execution_started() UNION ALL SELECT n+1 FROM numbers WHERE n<1000000000) SELECT sum(n) FROM numbers";
            return command.ExecuteScalar();
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        using var next = connection.CreateCommand();
        next.CommandText = "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<10000) SELECT sum(n) FROM numbers";
        Assert.Equal(50005000L, next.ExecuteScalar());
        var invoked = false;
        Assert.Throws<OperationCanceledException>(() => SqliteCancellation.Run(connection, stop.Token, () => { invoked = true; return 0; }));
        Assert.False(invoked);
    }
}
