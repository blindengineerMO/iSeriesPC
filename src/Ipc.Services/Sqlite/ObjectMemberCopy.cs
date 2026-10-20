using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed partial class ObjectCatalogOperations
{
    private static void CopyMembers(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName source, QualifiedName target, IReadOnlyList<(string Source, string Target)> tables,
        string? definition, bool copyData, bool copyConstraints, CancellationToken token)
    {
        if (tables.Count == 0) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA ignore_check_constraints"; var original = Convert.ToInt64(command.ExecuteScalar());
        // A pending CHECK remains pending in the duplicate, including its old
        // invalid rows. Preserve the state without relaxing later ordinary I/O.
        command.CommandText = "SELECT count(*) FROM sys_file_defs d,json_each(d.def,'$.sqlTable.checks') c WHERE d.lib=$lib AND d.name=$name AND json_extract(c.value,'$.checkPending')=1";
        command.Parameters.AddWithValue("$lib", source.Library); command.Parameters.AddWithValue("$name", source.Name.Value);
        var pending = copyData && copyConstraints && Convert.ToInt64(command.ExecuteScalar()) != 0;
        try
        {
            if (pending) { command.CommandText = "PRAGMA ignore_check_constraints=ON"; command.ExecuteNonQuery(); }
            SqliteCancellation.Run(connection, token, () => {
                new MemberTableCopy(connection, transaction).Copy(tables,
                    SqlConstraintCatalog.CheckCopySql(connection, transaction, source, target, definition), copyData: copyData,
                    includeIndex: copyConstraints ? null : name => !SqlConstraintCatalog.IsConstraintIndex(name));
                return 0;
            });
        }
        finally { if (pending) { command.CommandText = "PRAGMA ignore_check_constraints=" + original; command.ExecuteNonQuery(); } }
    }
}
