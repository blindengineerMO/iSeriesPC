using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

internal sealed class RecordReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, Action release) : IDisposable
{
    internal SqliteConnection Connection => connection;
    internal SqliteTransaction Transaction => transaction;
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { transaction.Dispose(); } finally { try { connection.Dispose(); } finally { release(); } }
    }
}

public sealed partial class RecordOperation
{
    // Finish the synchronous import scope, then anchor an independent WAL reader
    // before releasing the unit gate. Fetch/dispose can now run on any thread.
    internal RecordReadSnapshot OpenReadSnapshot()
    {
        Complete(); _disposed = true;
        try
        {
            try { _unit.FinishOperation(Transaction, true); } finally { Transaction.Dispose(); }
            return _unit.CreateReadSnapshot();
        }
        finally { Monitor.Exit(_unit.Gate); }
    }
}

public sealed partial class JobRecordTransactions
{
    internal sealed partial class RecordUnit
    {
        private int _querySnapshots;
        internal void RequireNoQuerySnapshots()
        { if (Volatile.Read(ref _querySnapshots) != 0) throw Invalid("Close SQL query cursors before committing, rolling back or ending the record unit."); }
        internal RecordReadSnapshot CreateReadSnapshot()
        {
            var connection = SqliteConnectionFactory.OpenPrivate(Data.DataSource);
            SqliteTransaction? transaction = null;
            try
            {
                using (var readOnly = connection.CreateCommand()) { readOnly.CommandText = "PRAGMA query_only=ON"; readOnly.ExecuteNonQuery(); }
                transaction = connection.BeginTransaction(deferred: true);
                using var anchor = connection.CreateCommand(); anchor.Transaction = transaction;
                anchor.CommandText = "SELECT count(*) FROM sqlite_schema"; anchor.ExecuteScalar();
                Interlocked.Increment(ref _querySnapshots);
                return new(connection, transaction, () => Interlocked.Decrement(ref _querySnapshots));
            }
            catch { try { transaction?.Dispose(); } finally { connection.Dispose(); } throw; }
        }
    }
}
