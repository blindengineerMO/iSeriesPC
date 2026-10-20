using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Cancellation for one exclusive synchronous use of an open connection.
/// Microsoft.Data.Sqlite's Cancel method is a no-op; the SQLite VM must poll instead.</summary>
public static class SqliteCancellation
{
    internal static SqliteTransaction BeginWrite(SqliteConnection connection, CancellationToken cancellationToken)
    {
        connection.DefaultTimeout = 1; var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return connection.BeginTransaction(deferred: false); }
            catch (SqliteException failure) when (failure.SqliteErrorCode is 5 or 6 && System.Diagnostics.Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(30))
            { cancellationToken.WaitHandle.WaitOne(25); }
        }
    }

    public static T Run<T>(SqliteConnection connection, CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) return Execute();
        var handle = connection.Handle ?? throw new InvalidOperationException("Cancellation requires an open SQLite connection.");
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, cancellationToken);
        try
        {
            var result = Execute();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("SQLite operation cancelled.", error, cancellationToken); }
        finally { SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null); }
        T Execute()
        {
            try { return operation(); }
            catch (SqliteException error) when (Ipc.Db.Sql.SqlCheckRuntime.DecodeError(error) is not null)
            { throw Ipc.Db.Sql.SqlCheckRuntime.DecodeError(error)!; }
        }
    }
}
