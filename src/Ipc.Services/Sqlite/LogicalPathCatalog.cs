using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class LogicalPathCatalog
{
    internal static void ReleaseFile(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file)
    {
        var definition = Read(connection, transaction, file);
        if (definition["logical"] is not JsonObject logical) return;
        foreach (var name in Paths(logical).Select(p => p.Value!.GetValue<string>()).Distinct()) Release(connection, transaction, file, logical, name);
    }
    internal static void RemoveMember(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file, string member)
    {
        var definition = Read(connection, transaction, file);
        var logical = definition["logical"] as JsonObject ?? throw Invalid();
        var bindings = logical["members"] as JsonObject ?? throw Invalid();
        bindings.Remove(member);
        var used = bindings.SelectMany(p => (p.Value as JsonArray ?? throw Invalid()).Select(v => v!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        var paths = Paths(logical); var obsolete = paths.Where(p => !used.Contains(p.Key)).Select(p => (Member: p.Key, Name: p.Value!.GetValue<string>())).ToArray();
        foreach (var path in obsolete) paths.Remove(path.Member);
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'";
        update.Parameters.AddWithValue("$lib", file.Library); update.Parameters.AddWithValue("$name", file.Name.Value);
        update.Parameters.AddWithValue("$def", definition.ToJsonString()); update.ExecuteNonQuery();
        foreach (var path in obsolete) Release(connection, transaction, file, logical, path.Name);
    }
    private static JsonObject Paths(JsonObject logical) => logical["uniquePaths"] as JsonObject ?? new JsonObject();
    private static JsonObject Read(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
        query.Parameters.AddWithValue("$lib", file.Library); query.Parameters.AddWithValue("$name", file.Name.Value);
        return JsonNode.Parse(query.ExecuteScalar() as string ?? throw Invalid()) as JsonObject ?? throw Invalid();
    }
    private static void Release(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file, JsonObject logical, string name)
    {
        if (!name.StartsWith("ipc_unique_", StringComparison.Ordinal) || name.Length != 75 || name[11..].Any(c => !char.IsAsciiHexDigit(c))) throw Invalid();
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT count(*) FROM sys_file_defs d,json_each(d.def,'$.logical.uniquePaths') p WHERE p.value=$index AND NOT(d.lib=$lib AND d.name=$name AND d.type='*FILE')";
        query.Parameters.AddWithValue("$index", name); query.Parameters.AddWithValue("$lib", file.Library); query.Parameters.AddWithValue("$name", file.Name.Value);
        if (Convert.ToInt64(query.ExecuteScalar()) != 0) return;
        query.CommandText = "SELECT tbl_name FROM sqlite_schema WHERE type='index' AND name=$index";
        if (query.ExecuteScalar() is not string table) return;
        var sourceLibrary = logical["sourceLibrary"]?.GetValue<string>(); var sourceFile = logical["sourceFile"]?.GetValue<string>();
        if (!ObjectName.IsValid(sourceLibrary) || !ObjectName.IsValid(sourceFile) || !table.StartsWith(sourceLibrary + "." + sourceFile + ".", StringComparison.Ordinal)) throw Invalid();
        query.CommandText = "DROP INDEX \"" + name + "\""; query.ExecuteNonQuery();
    }
    private static CpfException Invalid() => new("IPC0138", "Malformed logical access-path metadata.");
}
