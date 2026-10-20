using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static SqlExecutionOptions ViewReadOptions(string library) => new(SqlNaming.System, library, library, [library]);
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadSqlViewRows(string library, string name)
    {
        using var query = OpenSqlQuery("SELECT * FROM " + Quote(library) + "/" + Quote(name), ViewReadOptions(library));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (query.Read()) rows.Add(query.Columns.Select((column, index) => (column.Name, Value: query.Current![index] is SqlDecimalValue number && number.Precision <= 29 && number.Scale <= 28 ? number.ToDecimal() : query.Current[index]))
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase));
        return rows;
    }
    private DatabaseRecordCursor OpenSqlViewCursor(string library, string name, FileDefinition definition, CancellationToken token)
    {
        IReadOnlyList<QualifiedName> sources;
        using (var metadata = _factory.Open()) using (var snapshot = metadata.BeginTransaction(deferred: true))
            sources = RequireViewSourcesUnenlisted(metadata, snapshot, new(library, name), "SQL view native cursor commitment boundaries");
        if (definition.PrimaryFormat.Fields.Any(field => field.Type is FieldType.Packed or FieldType.Zoned && (field.Length > 29 || field.Decimals > 28)))
            throw new CpfException("CPF4131", "This SQL view exceeds the current native record decimal range; CAST its result columns.");
        var cache = new ViewRecordCache(this, library, name, definition.PrimaryFormat, token, () => {
            foreach (var source in sources) RequireUnenlisted(source.Library, source.Name.Value, "SQL view native cursor commitment boundaries");
        });
        return new(cache.Read, [cache], rebind: cache.Rebind);
    }
    // A scrollable snapshot for nonkeyed native record input. Fetch only as far as
    // requested; rows stay in bounded private disk storage rather than a managed list.
    private sealed class ViewRecordCache : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-view-records-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _call;
        private readonly RecordFormat _format;
        private readonly OperationIdentity? _identity = OperationIdentity.Current;
        private readonly Action _validateBoundary;
        private SqlQueryCursor? _query;
        private SqliteConnection? _cache;
        private IDisposable? _registration, _allocation;
        private long _count;
        private bool _end, _disposed;
        internal ViewRecordCache(SqliteFileStore files, string library, string name, RecordFormat format, CancellationToken token, Action validateBoundary)
        {
            _format = format; _validateBoundary = validateBoundary; _stop = CancellationTokenSource.CreateLinkedTokenSource(files._factory.ShutdownToken); _token = _stop.Token;
            lock (_gate) try
            {
                Rebind(token); _token.ThrowIfCancellationRequested();
                var identity = OperationIdentity.Current;
                _registration = identity?.Job is { } job ? files._factory.RegisterJobRuntimeResource(job, this) : files._factory.RegisterRuntimeResource(this);
                if (identity?.Job is { } owner) _allocation = new JobLockStore(files._factory).AcquireWithLifetime(owner, new(library, name, FileType),
                    JobLockMode.SharedRead, TimeSpan.Zero, _token, "Query", Guid.NewGuid().ToString("N"));
                if (OperatingSystem.IsLinux()) Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); else Directory.CreateDirectory(_directory);
                _cache = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "rows.db"), Pooling = false }.ToString()); _cache.Open();
                using var command = _cache.CreateCommand();
                command.CommandText = "PRAGMA max_page_count=32768; PRAGMA cache_size=-2048; PRAGMA journal_mode=OFF; CREATE TABLE rows(n INTEGER PRIMARY KEY," +
                    string.Join(',', format.Fields.Select((_, index) => "c" + index)) + ")"; command.ExecuteNonQuery();
                _query = files.OpenSqlQuery("SELECT * FROM " + Quote(library) + "/" + Quote(name), ViewReadOptions(library), cancellationToken: _token);
                _token.ThrowIfCancellationRequested();
            }
            catch { Dispose(); throw; }
        }
        internal void Rebind(CancellationToken token)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (OperationIdentity.Current?.Principal != _identity?.Principal || OperationIdentity.Current?.Job != _identity?.Job || OperationIdentity.Current?.AuthSessionId != _identity?.AuthSessionId)
                    throw new CpfException("CPF9802", "View cursor belongs to another execution context.");
                _call.Dispose();
                _call = token.Register(() => { try { _stop.Cancel(); } catch (ObjectDisposedException) { } });
            }
        }
        internal DatabaseRecordCursor.Row? Read(DatabaseRecordCursor.Request request)
        {
            lock (_gate) try
            {
                _token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); _query!.CheckAccess(); _validateBoundary();
                if (request.Key is not null || request.EqualPosition is not null || request.RecordNumber is not null || request.Action is DatabaseRecordCursor.Operation.Chain or DatabaseRecordCursor.Operation.Lower or DatabaseRecordCursor.Operation.Upper or DatabaseRecordCursor.Operation.Equal or DatabaseRecordCursor.Operation.PriorEqual)
                    throw LogicalError("SQL views expose nonkeyed sequential records; keyed and physical RRN operations require the remaining view identity implementation.");
                if (request.Action is DatabaseRecordCursor.Operation.Validate or DatabaseRecordCursor.Operation.Refresh) return null;
                var number = request.Position is { Length: > 0 } position ? Convert.ToInt64(position[^1]) + (request.Reverse ? -1 : 1) : request.Reverse ? long.MaxValue : 1;
                if (number < 1) return null;
                Fill(number);
                if (number == long.MaxValue) number = _count;
                if (number is < 1 || number > _count) return null;
                using var command = _cache!.CreateCommand(); command.CommandText = "SELECT " + string.Join(',', _format.Fields.Select((_, index) => "c" + index)) + " FROM rows WHERE n=$n";
                command.Parameters.AddWithValue("$n", number); using var reader = command.ExecuteReader();
                return reader.Read() ? new(ReadRow(reader, _format), number, 0, [number]) : null;
            }
            catch { Dispose(); throw; }
        }
        private void Fill(long target)
        {
            using var transaction = _cache!.BeginTransaction(); using var insert = _cache.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO rows VALUES($n," + string.Join(',', _format.Fields.Select((_, index) => "$c" + index)) + ")";
            insert.Parameters.AddWithValue("$n", 0L); foreach (var (field, index) in _format.Fields.Select((field, index) => (field, index))) insert.Parameters.AddWithValue("$c" + index, DBNull.Value);
            while (!_end && _count < target)
            {
                _token.ThrowIfCancellationRequested(); if (!_query!.Read()) { _end = true; break; }
                insert.Parameters[0].Value = ++_count;
                for (var index = 0; index < _format.Fields.Count; index++) insert.Parameters[index + 1].Value = _query.Current![index] switch { null => DBNull.Value, SqlDecimalValue number => number.Text, var value => value };
                SqliteCancellation.Run(_cache, _token, insert.ExecuteNonQuery);
            }
            transaction.Commit();
        }
        public void Dispose()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            lock (_gate)
            {
                if (_disposed) return; _disposed = true;
                try { _query?.Dispose(); }
                finally
                {
                    try { _cache?.Dispose(); }
                    finally
                    {
                        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
                        finally { _allocation?.Dispose(); _registration?.Dispose(); _call.Dispose(); _stop.Dispose(); }
                    }
                }
            }
        }
    }
}
