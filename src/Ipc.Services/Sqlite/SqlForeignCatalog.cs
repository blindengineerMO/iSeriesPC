using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static partial class SqlForeignCatalog
{
    internal static IReadOnlyList<(QualifiedName Key, FileDefinition Definition)> Files(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT lib,name,def FROM sys_file_defs WHERE json_type(def,'$.sqlTable')='object'";
        var result = new List<(QualifiedName,FileDefinition)>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((new(reader.GetString(0), reader.GetString(1)), FileDefinition.FromJson(reader.GetString(2))));
        return result;
    }
    internal static bool IsParent(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, bool enforcedOnly = false) =>
        Files(connection, transaction).Any(file => (file.Definition.SqlTable?.ForeignKeys ?? []).Any(item => (!enforcedOnly || SqlForeignSchema.Enforced(item)) && item.Parent.Schema == key.Library && item.Parent.Name == key.Name.Value));
    internal static bool OwnsNullHook(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        foreach (var file in Files(connection,transaction))
        {
            var foreign = (file.Definition.SqlTable?.ForeignKeys ?? []).Where(item => SqlForeignSchema.PartialNull(file.Definition.PrimaryFormat,item)).ToArray();
            if (foreign.Length == 0) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$lib",file.Key.Library); command.Parameters.AddWithValue("$name",file.Key.Name.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (foreign.Any(item => SqlForeignSchema.NullTriggerName(file.Key.Library + "." + file.Key.Name.Value + "." + reader.GetString(0),item) == name)) return true;
        }
        return false;
    }
    internal static void NullHooks(SqliteConnection connection, SqliteTransaction transaction, bool install, QualifiedName? only = null)
    {
        foreach (var file in Files(connection, transaction).Where(file => only is null || file.Key == only))
        {
            var foreign = (file.Definition.SqlTable?.ForeignKeys ?? []).Where(item => SqlForeignSchema.PartialNull(file.Definition.PrimaryFormat, item)).ToArray();
            if (foreign.Length == 0) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$lib", file.Key.Library); command.Parameters.AddWithValue("$name", file.Key.Name.Value);
            var members = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) members.Add(reader.GetString(0));
            command.Parameters.Clear();
            foreach (var member in members)
                foreach (var item in foreign)
                {
                    var child = file.Key.Library + "." + file.Key.Name.Value + "." + member;
                    command.CommandText = "DROP TRIGGER IF EXISTS " + SqlForeignSchema.Quote(SqlForeignSchema.NullTriggerName(child, item)); command.ExecuteNonQuery();
                    if (install) { command.CommandText = SqlForeignSchema.NullTrigger(child, file.Definition.PrimaryFormat, item); command.ExecuteNonQuery(); }
                }
        }
    }
    internal static bool IsManagedSchema(SqliteConnection connection, SqliteTransaction transaction, string type, string name, string table, string sql)
    {
        foreach (var file in Files(connection, transaction))
        {
            var prefix = file.Key.Library + "." + file.Key.Name.Value + ".";
            var foreign = file.Definition.SqlTable?.ForeignKeys;
            if (foreign is not { Count: > 0 }) continue;
            if (type == "table" && table.StartsWith(prefix, StringComparison.Ordinal))
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT count(DISTINCT id) FROM pragma_foreign_key_list($table)"; command.Parameters.AddWithValue("$table", table);
                var enforced = foreign.Where(SqlForeignSchema.Enforced).ToArray();
                return Convert.ToInt32(command.ExecuteScalar()) == enforced.Length && enforced.All(item => sql.Contains(SqlForeignSchema.Clause(file.Definition.PrimaryFormat, item), StringComparison.Ordinal));
            }
            if (type == "trigger")
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
                command.Parameters.AddWithValue("$lib", file.Key.Library); command.Parameters.AddWithValue("$name", file.Key.Name.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    foreach (var item in foreign.Where(item => SqlForeignSchema.PartialNull(file.Definition.PrimaryFormat, item)))
                    {
                        var child = prefix + reader.GetString(0);
                        if (name == SqlForeignSchema.NullTriggerName(child, item) && sql == SqlForeignSchema.NullTrigger(child, file.Definition.PrimaryFormat, item)) return true;
                    }
            }
        }
        return false;
    }
    internal static bool HasUnregisteredRelations(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT s.name,s.sql FROM sqlite_schema s JOIN pragma_foreign_key_list(s.name) f WHERE s.type='table' AND (s.name=$table OR f.\"table\"=$table)";
        command.Parameters.AddWithValue("$table", table); var rows = new List<(string Name,string Sql)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows.Any(row => !IsManagedSchema(connection, transaction, "table", row.Name, row.Name, row.Sql));
    }
}
