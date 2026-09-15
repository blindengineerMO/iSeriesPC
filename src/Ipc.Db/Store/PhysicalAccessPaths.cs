using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static void CreatePhysicalAccessPath(SqliteConnection connection, SqliteTransaction transaction, string library, string file, string member, FileDefinition definition)
    {
        if (definition.Unique && !definition.PrimaryFormat.Fields.Any(f => f.Sequence > 0) || !definition.Unique && definition.ExcludeNullKeys)
            throw LogicalError("Invalid physical key uniqueness definition.");
        var logical = new LogicalFileDefinition { SourceLibrary = library, SourceFile = file, SourceFormat = definition.PrimaryFormat.Name,
            Unique = definition.Unique, ExcludeNullKeys = definition.ExcludeNullKeys };
        _ = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat, definition.PrimaryFormat, new[] { member }, physicalOwner: true);
    }

    private static void EnsureExactDecimalStorage(SqliteConnection connection, SqliteTransaction transaction, string table, RecordFormat format)
    {
        var fields = format.Fields.Where(f => f.Type is FieldType.Packed or FieldType.Zoned && f.Decimals == 0 && f.Length > 18).ToArray();
        if (fields.Length == 0) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT name,type FROM pragma_table_xinfo($table)"; command.Parameters.AddWithValue("$table", table);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader()) while (reader.Read()) types[reader.GetString(0)] = reader.GetString(1).ToUpperInvariant();
        if (fields.Any(f => !types.TryGetValue(f.Name, out var type) || !(type.Contains("TEXT") || type.Contains("CHAR") || type.Contains("CLOB"))))
            throw LogicalError("Legacy large-decimal member requires TEXT storage migration before writes; floating-point conversion is not allowed.");
    }
}
