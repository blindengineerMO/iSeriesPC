using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static bool IsForeignFailure(SqliteException error) => DatabaseRecordErrors.IsReferentialConstraintFailure(error);
    private static CpfException? ForeignMutationError(SqliteConnection connection, SqliteTransaction transaction, SqlStatement statement, string? target, bool failed = false)
    {
        using var check = connection.CreateCommand(); check.Transaction = transaction; check.CommandText = "PRAGMA foreign_key_check";
        string child, parent; int identifier;
        using (var reader = check.ExecuteReader())
        {
            if (!reader.Read()) return failed ? new CpfException(statement is SqlDelete ? "SQL0532" : "SQL0531", "Operation prevented by a RESTRICT referential constraint.",sqlState:"23001") : null;
            child = reader.GetString(0); parent = reader.GetString(2); identifier = reader.GetInt32(3);
        }
        var id = "SQL0530"; var state = "23503";
        check.CommandText = "SELECT on_delete,on_update FROM pragma_foreign_key_list($table) WHERE id=$id LIMIT 1";
        check.Parameters.AddWithValue("$table",child); check.Parameters.AddWithValue("$id",identifier);
        using (var reader = check.ExecuteReader()) if (reader.Read())
        {
            if (statement is SqlDelete && reader.GetString(0) != "SET DEFAULT") { id = "SQL0532"; state = reader.GetString(0) == "RESTRICT" ? "23001" : "23504"; }
            else if (statement is SqlUpdate && parent == target && child != target) { id = "SQL0531"; state = reader.GetString(1) == "RESTRICT" ? "23001" : "23504"; }
            else if (statement is SqlUpdate update && parent == target && child == target)
            {
                var self = SqlForeignCatalog.Files(connection,transaction).FirstOrDefault(file => target!.StartsWith(file.Key.Library + "." + file.Key.Name.Value + ".",StringComparison.Ordinal));
                if (self.Definition is not null && (self.Definition.SqlTable?.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced).Any(foreign =>
                    SqlForeignSchema.ParentTable(foreign) == parent && update.Assignments.Any(assignment => foreign.ParentColumns!.Contains(assignment.Column)) &&
                    !update.Assignments.Any(assignment => foreign.Columns.Contains(assignment.Column))))
                { id = "SQL0531"; state = reader.GetString(1) == "RESTRICT" ? "23001" : "23504"; }
            }
        }
        return new CpfException(id,"Referential constraint failed between " + child + " and " + parent + ".",sqlState:state);
    }
    private sealed record RestrictedForeignUpdate(SqliteCommand Command, IReadOnlyList<int> Ordinals);
    private static IReadOnlyList<RestrictedForeignUpdate> BindRestrictedForeignUpdates(SqliteConnection connection, SqliteTransaction transaction, string table, string rowid,
        IReadOnlyList<(FieldSpec Field,SqlBound Value)> assignments)
    {
        var plans = new List<RestrictedForeignUpdate>();
        try
        {
            foreach (var file in SqlForeignCatalog.Files(connection,transaction))
                foreach (var foreign in (file.Definition.SqlTable?.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced))
                {
                    if (foreign.UpdateRule != SqlForeignUpdate.Restrict || SqlForeignSchema.ParentTable(foreign) != table || !assignments.Any(item => foreign.ParentColumns!.Contains(item.Field.Name))) continue;
                    using var member = connection.CreateCommand(); member.Transaction = transaction;
                    member.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' LIMIT 1";
                    member.Parameters.AddWithValue("$lib",file.Key.Library); member.Parameters.AddWithValue("$name",file.Key.Name.Value);
                    var child = file.Key.Library + "." + file.Key.Name.Value + "." + (string)member.ExecuteScalar()!;
                    var command = connection.CreateCommand(); command.Transaction = transaction;
                    var ordinals = Enumerable.Range(0,assignments.Count).Where(index => foreign.ParentColumns!.Contains(assignments[index].Field.Name)).ToArray();
                    plans.Add(new(command,ordinals));
                    var matches = foreign.Columns.Select((column,index) => "p." + Quote(foreign.ParentColumns![index]) + "=c." + Quote(column));
                    var unchanged = ordinals.Select(index => "p." + Quote(assignments[index].Field.Name) + " IS $v" + index + " COLLATE " + SqlForeignSchema.Collation(assignments[index].Field));
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM " + Quote(table) + " p JOIN " + Quote(child) + " c ON " + string.Join(" AND ",matches) +
                        " WHERE p." + Quote(rowid) + "=$rrn AND NOT(" + string.Join(" AND ",unchanged) + "))";
                    command.Parameters.AddWithValue("$rrn",0L); foreach (var index in ordinals) command.Parameters.AddWithValue("$v" + index,DBNull.Value);
                }
            return plans;
        }
        catch { foreach (var plan in plans) plan.Command.Dispose(); throw; }
    }
    private static void CheckRestrictedForeignUpdate(IReadOnlyList<RestrictedForeignUpdate> plans, long number, IReadOnlyList<object?> values)
    {
        foreach (var plan in plans)
        {
            plan.Command.Parameters["$rrn"].Value = number;
            foreach (var index in plan.Ordinals) plan.Command.Parameters["$v" + index].Value = values[index] ?? DBNull.Value;
            if (Convert.ToInt32(plan.Command.ExecuteScalar()) != 0) throw new CpfException("SQL0531","Parent key update prevented by RESTRICT.",sqlState:"23001");
        }
    }
}
