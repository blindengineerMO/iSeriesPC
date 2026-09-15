using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

// This versioned data-upgrade handler is frozen with migration 18. Future key
// changes require a new migration; existing index identities and predicates survive.
internal static class NullKeyCatalogMigration
{
    internal const string Contract = """
        -- null-key-upgrade-v1: rebuild managed member indexes with a null flag
        -- before each key; unique paths coalesce null to zero under that flag.
        -- Preserve names, direction and selection predicates. Duplicate existing
        -- null keys fail the entire migration, retaining schema 17 and its backup.
        SELECT 1;
        """;

    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT i.name,i.tbl_name,i.sql,d.def FROM sqlite_schema i
            JOIN sys_file_members m ON i.tbl_name=m.lib||'.'||m.name||'.'||m.mbr
            JOIN sys_file_defs d ON d.lib=m.lib AND d.name=m.name AND d.type=m.type
            WHERE i.type='index' AND i.sql IS NOT NULL AND json_extract(d.def,'$.logical') IS NULL
            """;
        var indexes = new List<(string Name, string Table, string Sql, string Definition)>();
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                if (Managed(reader.GetString(0))) indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        foreach (var index in indexes)
        {
            var unique = index.Sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal);
            var prefix = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + Quote(index.Name) + " ON " + Quote(index.Table) + " (";
            if (!index.Sql.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected managed index declaration during null-key upgrade.");
            using var definition = JsonDocument.Parse(index.Definition);
            var expressions = definition.RootElement.GetProperty("formats")[0].GetProperty("fields").EnumerateArray().Select(field =>
            {
                var column = Quote(field.GetProperty("name").GetString()!);
                var expression = field.GetProperty("type").GetString() switch {
                    "Alpha" => $"ipc_text_key_v1({column},{field.GetProperty("ccsid").GetInt32()},{field.GetProperty("length").GetInt32()})",
                    "Packed" or "Zoned" => $"ipc_decimal_key_v1({column})", _ => column };
                return (Expression: expression, Nullable: field.TryGetProperty("nullCapable", out var nullable) && nullable.GetBoolean());
            }).ToArray();
            var offset = prefix.Length; var columns = new List<string>();
            while (true)
            {
                var expression = expressions.FirstOrDefault(e => index.Sql.AsSpan(offset).StartsWith(e.Expression, StringComparison.Ordinal));
                if (expression.Expression is null) throw new InvalidDataException("Unexpected managed key expression during null-key upgrade.");
                offset += expression.Expression.Length;
                var descending = index.Sql.AsSpan(offset).StartsWith(" DESC", StringComparison.Ordinal);
                if (descending) offset += 5;
                columns.Add(DatabaseSortKeys.KeyColumns(expression.Expression, descending, unique, expression.Nullable));
                if (offset >= index.Sql.Length) throw new InvalidDataException("Incomplete managed index declaration.");
                var delimiter = index.Sql[offset++];
                if (delimiter == ')') break;
                if (delimiter != ',') throw new InvalidDataException("Unexpected managed index separator.");
            }
            var upgraded = prefix + string.Join(',', columns) + ")" + index.Sql[offset..];
            if (upgraded == index.Sql) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DROP INDEX " + Quote(index.Name); command.ExecuteNonQuery();
            command.CommandText = upgraded;
            try { command.ExecuteNonQuery(); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            { throw new InvalidDataException("Null-key upgrade found duplicate keys in " + index.Table + "; reconcile those records before retrying. No catalog changes were applied.", error); }
        }
    }

    private static bool Managed(string name)
    {
        // Object copies retain the original managed name after '.index.'.
        var tail = name[(name.LastIndexOf(".index.", StringComparison.Ordinal) is var n && n >= 0 ? n + 7 : 0)..];
        var prefix = tail.StartsWith("ipc_unique_", StringComparison.Ordinal) ? 11 : tail.StartsWith("ipc_path_", StringComparison.Ordinal) ? 9 : 0;
        return prefix != 0 && tail.Length == prefix + 64 && tail[prefix..].All(char.IsAsciiHexDigit);
    }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
