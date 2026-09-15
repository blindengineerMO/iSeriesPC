using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Runtime coordination is separate from data transactions so lock checks can fail without waiting on their own writer.</summary>
internal sealed class LockCatalog : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _anchor;
    internal LockCatalog(SqliteConnectionFactory factory)
    {
        var source = new SqliteConnectionStringBuilder(factory.ConnectionString);
        source.DataSource = factory.DatabasePath is { } path ? path + ".locks" : source.DataSource + "-locks";
        source.DefaultTimeout = 5;
        _connectionString = source.ToString();
        if (factory.UsesMemoryDatabase) { _anchor = new SqliteConnection(_connectionString); _anchor.Open(); }
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ipc_lock_version(version INTEGER NOT NULL);
            INSERT INTO ipc_lock_version SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM ipc_lock_version);
            CREATE TABLE IF NOT EXISTS holders (
              token TEXT PRIMARY KEY,job_number INTEGER NOT NULL,job_name TEXT NOT NULL,job_user TEXT NOT NULL,
              library TEXT NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL,member TEXT NOT NULL,row_number INTEGER NOT NULL,
              mode INTEGER NOT NULL,lifetime TEXT NOT NULL,scope TEXT NOT NULL,references_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS holder_resource ON holders(library,name,type,member,row_number);
            CREATE INDEX IF NOT EXISTS holder_job ON holders(job_number,scope);
            CREATE TABLE IF NOT EXISTS waiters (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT,token TEXT NOT NULL UNIQUE,
              job_number INTEGER NOT NULL,job_name TEXT NOT NULL,job_user TEXT NOT NULL,
              library TEXT NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL,member TEXT NOT NULL,row_number INTEGER NOT NULL,
              mode INTEGER NOT NULL,expires_at INTEGER NOT NULL,blockers TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS waiter_resource ON waiters(library,name,type,member,row_number);
            CREATE INDEX IF NOT EXISTS waiter_job ON waiters(job_number);
            """;
        command.ExecuteNonQuery(); command.CommandText = "SELECT version FROM ipc_lock_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("Unsupported lock catalog version.");
        transaction.Commit();
        if (factory.DatabasePath is { } database && OperatingSystem.IsLinux())
            File.SetUnixFileMode(database + ".locks", UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL"; command.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Dispose()
    {
        _anchor?.Dispose(); _anchor = null;
        using var connection = new SqliteConnection(_connectionString); SqliteConnection.ClearPool(connection);
    }
}
