using Ipc.Core.Objects;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    // Catalog metadata stays live. Only explicitly enlisted record resources
    // use the job's workspace; this scope never owns its shared connection.
    private RecordDataScope OpenRecordData(string library, string file, bool write = false, CancellationToken cancellationToken = default, IReadOnlyList<string>? members = null) =>
        new(_factory, new QualifiedName(library.ToUpperInvariant(), file.ToUpperInvariant()), write, cancellationToken, members);

    private void RequireUnenlisted(string library, string file, string operation)
    {
        if (_factory.RecordTransactions.IsEnlisted(new(library.ToUpperInvariant(), file.ToUpperInvariant())))
            throw JobRecordTransactions.Invalid(operation + " is not supported on an enlisted record resource; end the record unit first.");
    }

    private sealed class RecordDataScope : IDisposable
    {
        private readonly RecordOperation? _operation;
        private readonly IDisposable? _recordLocks;
        private readonly List<ForeignCascadeLocks> _cascades = [];
        public bool IsWorkspace => _operation is not null;
        public SqliteConnection Connection { get; }
        public SqliteTransaction? Transaction { get; }
        public RecordDataScope(Ipc.Services.Sqlite.SqliteConnectionFactory factory, QualifiedName file, bool write, CancellationToken cancellationToken, IReadOnlyList<string>? members)
        {
            _recordLocks = write ? new JobLockStore(factory).EnterRecordWrite(cancellationToken) : null;
            try
            {
                _operation = factory.RecordTransactions.OpenEnlisted([file], cancellationToken, members);
                if (_operation is not null) { Connection = _operation.Connection; Transaction = _operation.Transaction; }
                else
                {
                    Connection = factory.Open();
                    try { if (write) Transaction = Ipc.Services.Sqlite.SqliteCancellation.BeginWrite(Connection, cancellationToken); }
                    catch { Connection.Dispose(); throw; }
                }
            }
            catch { _recordLocks?.Dispose(); throw; }
        }
        public void Prepare(SqliteCommand command) => _operation?.Prepare(command);
        internal void AddCascadeLocks(ForeignCascadeLocks? locks) { if (locks is not null) _cascades.Add(locks); }
        public void Complete()
        { foreach (var locks in _cascades) locks.Validate(); if (_operation is not null) _operation.Complete(); else Transaction?.Commit(); }
        public void Dispose()
        {
            try
            {
                if (_operation is not null) _operation.Dispose();
                else { try { Transaction?.Dispose(); } finally { Connection.Dispose(); } }
            }
            finally { try { foreach (var locks in _cascades.AsEnumerable().Reverse()) locks.Dispose(); } finally { _recordLocks?.Dispose(); } }
        }
    }
}
