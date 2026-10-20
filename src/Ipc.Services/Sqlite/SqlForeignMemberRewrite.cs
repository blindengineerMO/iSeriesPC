using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

// Rebuild only the referential clauses. Preserve the physical layout, all row
// identities, indexes and hooks; copying must not run application/journal hooks.
internal static class SqlForeignMemberRewrite
{
    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName key, string member, FileDefinition original, FileDefinition next, CancellationToken token)
    {
        var before = original.SqlTable?.ForeignKeys ?? [];
        var after = next.SqlTable?.ForeignKeys ?? [];
        var changed = before.Concat(after).Select(item => item.Name!).Distinct().Where(name =>
            Clause(original, before.FirstOrDefault(item => item.Name == name)) !=
            Clause(next, after.FirstOrDefault(item => item.Name == name))).ToArray();
        if (changed.Length == 0) return;
        var table = key.Library + "." + key.Name.Value + "." + member;
        var schema = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name=$table AND type IN('index','trigger') AND sql IS NOT NULL ORDER BY type,name";
            read.Parameters.AddWithValue("$table", table); using var reader = read.ExecuteReader();
            while (reader.Read()) schema.Add(reader.GetString(0));
        }
        using var foreign = new SqlForeignDdlScope(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA ignore_check_constraints"; var checks = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "PRAGMA legacy_alter_table"; var legacy = Convert.ToInt64(command.ExecuteScalar());
        try
        {
            command.CommandText = "PRAGMA ignore_check_constraints=ON"; command.ExecuteNonQuery();
            command.CommandText = "PRAGMA legacy_alter_table=OFF"; command.ExecuteNonQuery();
            var staging = "ipc_foreign_" + Guid.NewGuid().ToString("N");
            SqliteCancellation.Run(connection, token, () => {
                new MemberTableCopy(connection, transaction).Copy([(table, staging)], sql => {
                    foreach (var name in changed)
                    {
                        var previous = before.FirstOrDefault(item => item.Name == name);
                        // The schema probe rewrites pre-existing self references.
                        var old = Clause(original, previous);
                        if (previous is not null && SqlForeignSchema.ParentTable(previous) == table)
                            old = old.Replace("REFERENCES " + SqlForeignSchema.Quote(table) + "(",
                                "REFERENCES " + SqlForeignSchema.Quote(staging) + "(", StringComparison.Ordinal);
                        var replacement = Clause(next, after.FirstOrDefault(item => item.Name == name));
                        if (old.Length == 0)
                        {
                            var end = sql.LastIndexOf(')');
                            if (end < 0) throw Invalid();
                            sql = sql[..end] + ", " + replacement + sql[end..]; continue;
                        }
                        var offset = sql.IndexOf(old, StringComparison.Ordinal);
                        if (offset < 0 || sql.IndexOf(old, offset + old.Length, StringComparison.Ordinal) >= 0) throw Invalid();
                        if (replacement.Length == 0)
                        {
                            var comma = offset - 1; while (comma >= 0 && char.IsWhiteSpace(sql[comma])) comma--;
                            if (comma < 0 || sql[comma] != ',') throw Invalid();
                            sql = sql[..comma] + sql[(offset + old.Length)..];
                        }
                        else sql = sql[..offset] + replacement + sql[(offset + old.Length)..];
                    }
                    // Self references must keep the final identity. The actual
                    // rename below deliberately leaves external hooks untouched.
                    return sql.Replace("REFERENCES " + SqlForeignSchema.Quote(staging) + "(",
                        "REFERENCES " + SqlForeignSchema.Quote(table) + "(", StringComparison.Ordinal);
                }, copySchemaObjects: false);
                // A catalog hook may refer to the temporarily absent old table.
                // No external identifier changes during this same-name rebuild.
                command.CommandText = "PRAGMA legacy_alter_table=ON"; command.ExecuteNonQuery();
                command.CommandText = "DROP TABLE " + SqlForeignSchema.Quote(table); command.ExecuteNonQuery();
                command.CommandText = "ALTER TABLE " + SqlForeignSchema.Quote(staging) + " RENAME TO " + SqlForeignSchema.Quote(table); command.ExecuteNonQuery();
                foreach (var sql in schema) { command.CommandText = sql; command.ExecuteNonQuery(); }
                return 0;
            });
        }
        finally
        {
            command.CommandText = "PRAGMA ignore_check_constraints=" + checks; command.ExecuteNonQuery();
            command.CommandText = "PRAGMA legacy_alter_table=" + legacy; command.ExecuteNonQuery();
        }
    }

    private static string Clause(FileDefinition definition, SqlForeignConstraint? foreign) =>
        foreign is not null && SqlForeignSchema.Enforced(foreign) ? SqlForeignSchema.Clause(definition.PrimaryFormat, foreign) : "";
    private static CpfException Invalid() => new("CPF32B2", "Constraint schema and metadata disagree during referential establishment.");
}
