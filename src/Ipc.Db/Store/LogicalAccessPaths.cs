using System.Security.Cryptography;
using System.Text;
using Ipc.Db.Definitions;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    // Non-unique paths are a bounded cache owned by the physical member. They may
    // be shared by LFs and retained after an LF is removed; dropping the PF drops them.
    private static IReadOnlyDictionary<string, string> CreateLogicalAccessPaths(SqliteConnection connection, SqliteTransaction transaction,
        LogicalFileDefinition logical, RecordFormat format, RecordFormat physical, IReadOnlyList<string> members, bool physicalOwner = false)
    {
        var paths = new Dictionary<string, string>(logical.UniquePaths);
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToArray();
        if (keys.Length == 0) return paths;
        var columns = string.Join(',', keys.Select(k => DatabaseSortKeys.KeyColumns(LogicalColumn(k), k.Descending, logical.Unique && !logical.ExcludeNullKeys, k.NullCapable)));
        using var predicates = connection.CreateCommand(); predicates.Transaction = transaction;
        var where = logical.Unique ? " WHERE " + LogicalPredicate(predicates, logical, physical, inlineConstants: true) : "";
        foreach (var member in members)
        {
            var table = MemberTable(logical.SourceLibrary, logical.SourceFile, member);
            var name = (logical.Unique ? physicalOwner ? "ipc_pf_unique_" : "ipc_unique_" : "ipc_path_") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table + "\0" + columns + where)));
            // ALTER TABLE preserves index identity when the based-on PF moves.
            if (logical.Unique && logical.UniquePaths.TryGetValue(member, out var existing)) name = existing;
            if (logical.Unique) paths[member] = name;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name=$name";
            command.Parameters.AddWithValue("$name", name); if (Convert.ToInt64(command.ExecuteScalar()) != 0) continue;
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name=$table";
            command.Parameters.AddWithValue("$table", table);
            if (Convert.ToInt64(command.ExecuteScalar()) >= 64) throw LogicalError("Physical member has reached its 64-index limit.");
            command.CommandText = "CREATE " + (logical.Unique ? "UNIQUE " : "") + "INDEX " + Quote(name) + " ON " + Quote(table) + " (" + columns + ")" + where;
            command.ExecuteNonQuery();
        }
        return paths;
    }
}
