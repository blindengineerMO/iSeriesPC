using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static void RequireReferentialClear(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file, string member)
    {
        if (!SqlForeignCatalog.IsParent(connection,transaction,file,enforcedOnly:true)) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM " + Quote(MemberTable(file.Library,file.Name.Value,member)) + ")";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new CpfException("CPF3134", "Cannot clear a nonempty parent member with an enabled referential constraint.");
    }
}
