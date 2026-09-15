using Ipc.Core.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Owns a catalog transaction and its explicit allocations. Callers must finish the transaction through this owner.</summary>
public sealed class JobLockTransaction : IDisposable
{
    private readonly JobLockStore _store;
    private readonly JobKey _job;
    private readonly SqliteTransaction _transaction;
    private readonly string _scope = Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _allocations = new();
    private bool _finished;
    internal JobLockTransaction(JobLockStore store, JobKey job, SqliteTransaction transaction)
    { _store = store; _job = job; _transaction = transaction; }
    public void Acquire(JobLockResource resource, JobLockMode mode, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        // Waiting while holding SQLite's data writer could deadlock with another lock owner.
        _allocations.Add(_store.AcquireWithLifetime(_job, resource, mode, TimeSpan.Zero, cancellationToken, "Commitment", _scope));
    }
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _transaction.Commit(); Finish();
    }
    public void Rollback()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _transaction.Rollback(); Finish();
    }
    private void Finish()
    {
        // Transaction durability precedes unlocking. A failed commit leaves allocations held
        // until rollback/disposal; callers must not expose the transaction to another owner.
        _finished = true;
        try { foreach (var allocation in _allocations) allocation.Dispose(); }
        finally { _allocations.Clear(); _transaction.Dispose(); }
    }
    public void Dispose()
    {
        if (_finished) return;
        _transaction.Dispose(); Finish(); // SQLite rolls back before any allocation is released.
    }
}
