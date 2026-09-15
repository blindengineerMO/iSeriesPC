using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed class SqliteConnectionFactory : IDisposable
{
    private readonly string _dataSource;
    private readonly string _dataDirectory;
    private readonly object _gate = new();
    private SqliteConnection? _keepAlive;
    private bool _disposed;
    private LockCatalog? _locks;
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
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _locks?.Dispose();
            _keepAlive?.Dispose();
            _keepAlive = null;
            using var connection = new SqliteConnection(_dataSource);
            SqliteConnection.ClearPool(connection);
        }
    }
}
