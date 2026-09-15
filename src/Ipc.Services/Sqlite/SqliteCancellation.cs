using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Cancellation for one exclusive synchronous use of an open connection.
/// Microsoft.Data.Sqlite's Cancel method is a no-op; the SQLite VM must poll instead.</summary>
public static class SqliteCancellation
{
    public static T Run<T>(SqliteConnection connection, CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) return operation();
        var handle = connection.Handle ?? throw new InvalidOperationException("Cancellation requires an open SQLite connection.");
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, cancellationToken);
        try
        {
            var result = operation();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("SQLite operation cancelled.", error, cancellationToken); }
        finally { SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null); }
    }
}
