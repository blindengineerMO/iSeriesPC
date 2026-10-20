using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class SqlConstraintCatalog
{
    internal static bool IsConstraintIndex(string name)
    {
        foreach (var prefix in new[] { "ipc_sql_unique_", "ipc_sql_reference_", "ipc_sql_foreign_" })
        {
            var offset = name.LastIndexOf(prefix, StringComparison.Ordinal);
            if (offset >= 0 && name.Length - offset == prefix.Length + 64 &&
                (offset == 0 || name[..offset].EndsWith(".index.", StringComparison.Ordinal)) &&
                name[(offset + prefix.Length)..].All(char.IsAsciiHexDigit)) return true;
        }
        return false;
    }

    internal static void ValidateLimits(SqliteConnection connection, SqliteTransaction transaction,
        params (QualifiedName Key, FileDefinition Definition)[] replacements)
    {
        // Count the proposed catalog, rather than adding a replacement to its old
        // definition. Cascaded drops and copies can change several files at once.
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        foreach (var (key, definition) in replacements) files[key] = definition;
        var counts = files.ToDictionary(file => file.Key, file =>
            (long)(file.Value.SqlTable?.Constraints.Count ?? 0) + (file.Value.SqlTable?.Checks?.Count ?? 0) +
            (file.Value.SqlTable?.ForeignKeys?.Count ?? 0));
        foreach (var (child, definition) in files)
            foreach (var foreign in definition.SqlTable?.ForeignKeys ?? [])
            {
                var parent = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
                // A self reference is already counted among this file's constraints.
                if (parent != child) counts[parent] = counts.GetValueOrDefault(parent) + 1;
            }
        foreach (var (key, count) in counts.OrderBy(item => item.Key.Library, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Name.Value, StringComparer.Ordinal))
            if (count > 5000) throw new CpfException("SQL0642", "Maximum of 5000 constraint relations exceeded for " + key + ".", sqlState: "54021");
    }

    internal static Func<string, string>? CheckCopySql(SqliteConnection connection, SqliteTransaction transaction, QualifiedName source, QualifiedName targetKey, string? targetDefinition)
    {
        if (targetDefinition is null) return null;
        var target = FileDefinition.FromJson(targetDefinition);
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
        read.Parameters.AddWithValue("$lib", source.Library); read.Parameters.AddWithValue("$name", source.Name.Value);
        var original = FileDefinition.FromJson((string)read.ExecuteScalar()!);
        var beforeChecks = original.SqlTable?.Checks ?? []; var afterChecks = target.SqlTable?.Checks ?? [];
        var beforeForeign = original.SqlTable?.ForeignKeys ?? []; var afterForeign = target.SqlTable?.ForeignKeys ?? [];
        if (afterChecks.Count != 0 && beforeChecks.Count != afterChecks.Count || afterForeign.Count != 0 && beforeForeign.Count != afterForeign.Count)
            throw new CpfException("IPC0201", "Constraint copy metadata changed shape.");
        var changes = beforeChecks.Select((check, ordinal) => (Old: check.Disabled ? "" : SqlCheckRuntime.Sql(original.PrimaryFormat, check),
                New: afterChecks.Count == 0 || afterChecks[ordinal].Disabled ? "" : SqlCheckRuntime.Sql(target.PrimaryFormat, afterChecks[ordinal])))
            .Concat(beforeForeign.Select((foreign, ordinal) => {
                // SQLite's schema probe already renamed references to the copied file itself.
                var probed = foreign.Parent.Schema == source.Library && foreign.Parent.Name == source.Name.Value ?
                    foreign with { Parent = new(targetKey.Library,targetKey.Name.Value) } : foreign;
                return (Old: SqlForeignSchema.Enforced(probed) ? SqlForeignSchema.Clause(original.PrimaryFormat,probed) : "",
                    New: afterForeign.Count != 0 && SqlForeignSchema.Enforced(afterForeign[ordinal]) ? SqlForeignSchema.Clause(target.PrimaryFormat,afterForeign[ordinal]) : "");
            })).Concat((original.SqlTable?.Constraints ?? []).Where(item => item.Primary &&
                !(target.SqlTable?.Constraints ?? []).Any(after => after.Primary && after.Columns.SequenceEqual(item.Columns)))
                .Select(item => (Old: "CHECK (" + string.Join(" AND ", item.Columns.Select(column => SqlForeignSchema.Quote(column) + " IS NOT NULL")) + ")", New: "")))
            .Where(pair => pair.Old != pair.New).ToArray();
        if (changes.Length == 0) return null;
        return sql => {
            foreach (var (before, after) in changes)
            {
                if (before.Length == 0)
                {
                    var end = sql.LastIndexOf(')'); if (end < 0) throw new CpfException("IPC0201", "Invalid member schema during constraint copy.");
                    sql = sql[..end] + ", " + after + sql[end..]; continue;
                }
                var offset = sql.IndexOf(before, StringComparison.Ordinal);
                if (offset < 0 || sql.IndexOf(before, offset+before.Length, StringComparison.Ordinal) >= 0) throw new CpfException("IPC0201", "Constraint schema and metadata disagree during object copy.");
                if (after.Length == 0)
                {
                    var comma = offset - 1; while (comma >= 0 && char.IsWhiteSpace(sql[comma])) comma--;
                    if (comma < 0 || sql[comma] != ',') throw new CpfException("IPC0201", "Invalid constraint separator during object copy.");
                    sql = sql[..comma] + sql[(offset + before.Length)..]; continue;
                }
                sql = sql[..offset] + after + sql[(offset+before.Length)..];
            }
            return sql;
        };
    }
    internal static HashSet<string> Names(SqliteConnection connection, SqliteTransaction transaction, string library, QualifiedName? except = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT json_extract(c.value,'$.name') FROM sys_file_defs d,json_each(d.def,'$.sqlTable.constraints') c WHERE d.lib=$lib AND NOT(d.lib=$exceptLib AND d.name=$exceptName) AND json_extract(c.value,'$.name') IS NOT NULL UNION ALL SELECT json_extract(c.value,'$.name') FROM sys_file_defs d,json_each(d.def,'$.sqlTable.checks') c WHERE d.lib=$lib AND NOT(d.lib=$exceptLib AND d.name=$exceptName) AND json_extract(c.value,'$.name') IS NOT NULL UNION ALL SELECT json_extract(c.value,'$.name') FROM sys_file_defs d,json_each(d.def,'$.sqlTable.foreignKeys') c WHERE d.lib=$lib AND NOT(d.lib=$exceptLib AND d.name=$exceptName) AND json_extract(c.value,'$.name') IS NOT NULL";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$exceptLib", except?.Library ?? ""); command.Parameters.AddWithValue("$exceptName", except?.Name.Value ?? "");
        var result = new HashSet<string>(StringComparer.Ordinal); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    internal static string? PrepareRelocation(SqliteConnection connection, SqliteTransaction transaction, QualifiedName source, QualifiedName target, bool copy,
        bool copyConstraints = true, bool copyData = true, CancellationToken token = default)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", source.Library); command.Parameters.AddWithValue("$name", source.Name.Value);
        if (command.ExecuteScalar() is not string json || JsonNode.Parse(json) is not JsonObject definition || definition["sqlTable"]?["constraints"] is not JsonArray constraints) return null;
        if (copy && !copyConstraints)
        {
            definition["sqlTable"]!["constraints"] = new JsonArray();
            definition["sqlTable"]!.AsObject().Remove("checks"); definition["sqlTable"]!.AsObject().Remove("foreignKeys");
            definition["name"] = target.Name.Value;
            ValidateLimits(connection, transaction, (target, FileDefinition.FromJson(definition.ToJsonString())));
            return definition.ToJsonString();
        }
        var names = Names(connection, transaction, target.Library, copy ? null : source); var changed = false;
        foreach (var item in constraints.Concat(definition["sqlTable"]?["checks"] is JsonArray checks ? checks : new JsonArray()).Concat(definition["sqlTable"]?["foreignKeys"] is JsonArray foreign ? foreign : new JsonArray()))
        {
            if (item?["name"]?.GetValue<string>() is not { } name) continue;
            if (names.Add(name)) continue;
            if (!copy) throw new CpfException("SQL0601", "Constraint " + name + " already exists in the target schema.");
            string generated;
            do { generated = "Q" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(); } while (!names.Add(generated));
            item["name"] = generated; changed = true;
        }
        if (copy && definition["sqlTable"]?["foreignKeys"] is JsonArray { Count: > 0 })
        {
            var copied = FileDefinition.FromJson(definition.ToJsonString());
            SqlForeignCatalog.CopyBindings(connection,transaction,source,target,copied,copyData,token);
            ValidateLimits(connection, transaction, (target, copied));
            definition["sqlTable"]!["foreignKeys"] = JsonNode.Parse(copied.ToJson())!["sqlTable"]!["foreignKeys"]!.DeepClone();
            changed = true;
        }
        if (!changed) return null;
        definition["name"] = target.Name.Value; return definition.ToJsonString();
    }
}
