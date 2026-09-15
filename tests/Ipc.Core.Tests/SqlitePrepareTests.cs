using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqlitePrepareTests
{
    [Fact]
    public void Preparing_during_a_schema_write_returns_SQLITE_LOCKED_and_can_retry_after_commit()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        using var writer = factory.Open(); using var reader = factory.Open();
        using var transaction = writer.BeginTransaction(deferred: false);
        using var create = writer.CreateCommand(); create.Transaction = transaction;
        create.CommandText = "CREATE TABLE pending_schema(value INTEGER)"; create.ExecuteNonQuery();
        var sql = global::System.Text.Encoding.UTF8.GetBytes("SELECT count(*) FROM pending_schema\0");
        var result = SQLitePCL.raw.sqlite3_prepare_v2(reader.Handle!, sql, out var statement, out _);
        statement.Dispose(); Assert.Equal(SQLitePCL.raw.SQLITE_LOCKED, result & 255);
        transaction.Commit();
        result = SQLitePCL.raw.sqlite3_prepare_v2(reader.Handle!, sql, out statement, out _);
        using (statement) Assert.Equal(SQLitePCL.raw.SQLITE_OK, result);
    }
}
