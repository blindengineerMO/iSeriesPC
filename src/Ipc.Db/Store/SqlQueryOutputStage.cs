using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

// A private on-disk spool separates the SELECT snapshot from output DDL/writes.
// SQLite's page cache is bounded; only one typed result row enters managed memory.
internal sealed class SqlQueryOutputStage : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-query-output-" + Guid.NewGuid().ToString("N"));
    internal SqliteConnection Connection { get; private set; } = null!;
    internal long Rows { get; private set; }
    internal SqlQueryOutputStage(SqlQueryCursor query, CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(_directory);
            Connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "data.db"), Pooling = false }.ToString()); Connection.Open();
            using var command = Connection.CreateCommand();
            command.CommandText = "PRAGMA page_size=4096; PRAGMA max_page_count=32768; PRAGMA cache_size=-2048; PRAGMA journal_mode=OFF; CREATE TABLE data(" +
                string.Join(',', query.Columns.Select((_, index) => "c" + index)) + ")"; command.ExecuteNonQuery();
            using var transaction = Connection.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO data VALUES(" + string.Join(',', query.Columns.Select((_, index) => "$v" + index)) + ")";
            for (var index = 0; index < query.Columns.Count; index++) command.Parameters.AddWithValue("$v" + index, DBNull.Value);
            while (query.Read())
            {
                token.ThrowIfCancellationRequested();
                for (var index = 0; index < query.Columns.Count; index++) command.Parameters[index].Value = query.Current![index] switch {
                    null => DBNull.Value, SqlDecimalValue number => number.Text, var value => value };
                SqliteCancellation.Run(Connection, token, command.ExecuteNonQuery); Rows++;
            }
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try { Connection?.Dispose(); }
        finally { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
