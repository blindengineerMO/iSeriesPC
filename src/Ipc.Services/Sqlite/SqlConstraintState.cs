using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class SqlConstraintState
{
    internal const AuthorityBit Data = AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete;

    internal static void RequireAccess(SqliteConnection connection, SqliteTransaction? transaction,
        string library, string name, AuthorityBit required)
    {
        if ((required & Data) == 0) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT json_extract(c.value,'$.name'),1 FROM sys_file_defs d,json_each(d.def,'$.sqlTable.foreignKeys') c
            WHERE coalesce(json_extract(c.value,'$.defined'),0)=0 AND coalesce(json_extract(c.value,'$.disabled'),0)=0 AND json_extract(c.value,'$.checkPending')=1
              AND ((d.lib=$lib AND d.name=$name) OR ($parentWrite AND json_extract(c.value,'$.parent.schema')=$lib AND json_extract(c.value,'$.parent.name')=$name))
            UNION ALL
            SELECT json_extract(c.value,'$.name'),3 FROM sys_file_defs d,json_each(d.def,'$.sqlTable.checks') c
            WHERE d.lib=$lib AND d.name=$name AND $read AND coalesce(json_extract(c.value,'$.disabled'),0)=0 AND json_extract(c.value,'$.checkPending')=1
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$parentWrite", (required & (AuthorityBit.Update | AuthorityBit.Delete)) != 0);
        command.Parameters.AddWithValue("$read", (required & AuthorityBit.Read) != 0);
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new CpfException("SQL0971", "Constraint " + (reader.IsDBNull(0) ? "" : reader.GetString(0)) +
            " is check pending for " + library + "/" + name + " (reason " + reader.GetInt32(1) + ").", sqlState: "57011");
    }
}
