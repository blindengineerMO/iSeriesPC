using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Uses SQLite's parsed rename propagation to detect external schema dependents.</summary>
internal static class MemberTableDependencies
{
    public static bool HasExternalReferences(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyCollection<string> tables)
    {
        if (tables.Count == 0) return false;
        var owned = tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var original = ReadSchema(connection, transaction).Where(row => !owned.Contains(row.Table))
            .ToDictionary(row => (row.Type, row.Name), row => row.Sql);
        Run(connection, transaction, "SAVEPOINT ipc_dependency_probe");
        try
        {
            foreach (var table in tables)
                Run(connection, transaction, $"ALTER TABLE {Quote(table)} RENAME TO {Quote("ipc_probe_" + Guid.NewGuid().ToString("N"))}");
            return ReadSchema(connection, transaction).Any(row =>
                original.TryGetValue((row.Type, row.Name), out var sql) && sql != row.Sql);
        }
        finally
        {
            Run(connection, transaction, "ROLLBACK TO ipc_dependency_probe");
            Run(connection, transaction, "RELEASE ipc_dependency_probe");
        }
    }

    private static List<(string Type, string Name, string Table, string Sql)> ReadSchema(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
