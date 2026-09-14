using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string _dataSource;
    private readonly string _dataDirectory;
    private readonly object _gate = new();
    private SqliteConnection? _keepAlive;

    public SqliteConnectionFactory(string dataDirectory, string databaseFile = "system.db", bool useWriters = true)
    {
        _dataDirectory = dataDirectory;
        _dataSource = dataDirectory.Equals(":memory:", StringComparison.Ordinal)
            ? $"Data Source=ipcsys-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
            : $"Data Source={Path.Combine(dataDirectory, databaseFile)}";
    }

    public string DataDirectory => _dataDirectory;

    public bool UsesMemoryDatabase =>
        _dataDirectory.Equals(":memory:", StringComparison.Ordinal);

    public string ConnectionString => _dataSource;

    public SqliteConnection Open()
    {
        if (!UsesMemoryDatabase)
        {
            Directory.CreateDirectory(_dataDirectory);
        }
        else
        {
            lock (_gate)
            {
                EnsureKeepAlive();
            }
        }

        var connection = new SqliteConnection(_dataSource);
        connection.Open();
        Enable(connection);
        return connection;
    }

    private void EnsureKeepAlive()
    {
        if (_keepAlive is { } existing && existing.State != System.Data.ConnectionState.Closed)
        {
            return;
        }

        var keepAlive = new SqliteConnection(_dataSource);
        keepAlive.Open();
        Enable(keepAlive);
        _keepAlive = keepAlive;
    }

    private static void Enable(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }
}