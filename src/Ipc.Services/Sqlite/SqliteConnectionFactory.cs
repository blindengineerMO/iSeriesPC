using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed class SqliteConnectionFactory : IDisposable
{
    private readonly string _dataSource;
    private readonly string _dataDirectory;
    private readonly object _gate = new();
    private SqliteConnection? _keepAlive;
    private bool _disposed;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<IDisposable> _runtimeResources = new(ReferenceEqualityComparer.Instance);
    internal CancellationToken ShutdownToken => _shutdown.Token;
    internal IDisposable RegisterRuntimeResource(IDisposable resource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
            _runtimeResources.Add(resource); return new RuntimeResourceRegistration(this, resource);
        }
    }
    private sealed class RuntimeResourceRegistration(SqliteConnectionFactory owner, IDisposable resource) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._runtimeResources.Remove(resource); }
    }
    private bool _disposing;
    private LockCatalog? _locks;
    private Ipc.Services.Security.CryptoKeyContexts? _cryptoKeys;
    private Ipc.Services.Work.JobRecordTransactions? _transactions;
    public Ipc.Services.Work.JobRecordTransactions RecordTransactions { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed || _disposing, this); return _transactions ??= new(this); } } }
    internal Ipc.Services.Security.CryptoKeyContexts CryptoKeys { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _cryptoKeys ??= new(this); } } }
    internal LockCatalog Locks { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _locks ??= new LockCatalog(this); } } }

    public SqliteConnectionFactory(string dataDirectory, string databaseFile = "system.db", bool useWriters = true)
    {
        _dataDirectory = dataDirectory;
        DatabasePath = UsesMemoryDatabase ? null : Path.GetFullPath(Path.Combine(dataDirectory, databaseFile));
        _dataSource = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath ?? $"ipcsys-{Guid.NewGuid():N}",
            Mode = UsesMemoryDatabase ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = UsesMemoryDatabase ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = !UsesMemoryDatabase,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DataDirectory => _dataDirectory;

    public string? DatabasePath { get; }

    public bool UsesMemoryDatabase =>
        _dataDirectory.Equals(":memory:", StringComparison.Ordinal);

    public string ConnectionString => _dataSource;

    public SqliteConnection Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return OpenCore();
        }
    }

    private SqliteConnection OpenCore()
    {
        if (!UsesMemoryDatabase)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath!)!);
        }
        else
        {
            lock (_gate)
            {
                EnsureKeepAlive();
            }
        }

        var connection = new SqliteConnection(_dataSource);
        try
        {
            connection.Open();
            Enable(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureKeepAlive()
    {
        if (_keepAlive is { } existing && existing.State != System.Data.ConnectionState.Closed)
        {
            return;
        }

        var keepAlive = new SqliteConnection(_dataSource);
        try
        {
            keepAlive.Open();
            Enable(keepAlive);
            _keepAlive = keepAlive;
        }
        catch
        {
            keepAlive.Dispose();
            throw;
        }
    }

    private static void Enable(SqliteConnection connection)
    {
        DatabaseSortKeys.Register(connection);
        connection.CreateFunction("ipc_actor", () => Ipc.Services.Events.OperationIdentity.Current?.Principal);
        connection.CreateFunction("ipc_job", () => Ipc.Services.Events.OperationIdentity.Current?.Job?.ToString());
        connection.CreateFunction("ipc_journal_replay", () => 0);
        connection.CreateFunction("ipc_journal_force_delete", () => 0);
        connection.CreateFunction("ipc_journal_timestamp", () => DateTimeOffset.UtcNow.ToString("O", global::System.Globalization.CultureInfo.InvariantCulture));
        connection.CreateFunction("ipc_journal_real", (double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", global::System.Globalization.CultureInfo.InvariantCulture));
        connection.CreateFunction("ipc_commit_before", () => 0);
        connection.CreateFunction<string?>("ipc_commit_cycle", () => null);
        using var pragma = connection.CreateCommand();
        // REPLACE deletes must fire capture/retention triggers as well as inserts.
        pragma.CommandText = "PRAGMA recursive_triggers=ON; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }

    internal static SqliteConnection OpenPrivate(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        try { connection.Open(); Enable(connection); return connection; } catch { connection.Dispose(); throw; }
    }

    public void Dispose()
    {
        Ipc.Services.Work.JobRecordTransactions? transactions;
        IDisposable[] resources;
        lock (_gate)
        {
            if (_disposed || _disposing) return;
            _disposing = true; transactions = _transactions; resources = _runtimeResources.ToArray(); _runtimeResources.Clear();
        }
        _shutdown.Cancel();
        // A statement may still need catalog reads while unwinding. Do not hold
        // the connection-factory gate while waiting for its workspace gate.
        try
        {
            List<Exception>? errors = null;
            foreach (var resource in resources)
                try { resource.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            transactions?.Dispose();
            if (errors is not null) throw new AggregateException("Could not release all connection-factory resources.", errors);
        }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                try { _cryptoKeys?.Dispose(); }
                finally
                {
                    try { _locks?.Dispose(); }
                    finally
                    {
                        _keepAlive?.Dispose(); _keepAlive = null;
                        using var connection = new SqliteConnection(_dataSource);
                        SqliteConnection.ClearPool(connection); _shutdown.Dispose();
                    }
                }
            }
        }
    }
}
