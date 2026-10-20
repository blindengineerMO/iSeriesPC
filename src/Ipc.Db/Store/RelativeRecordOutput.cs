using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static void RequireRelativeOutputSlot(SqliteConnection connection, SqliteTransaction transaction,
        string library, string file, string member, string rowid, long number, CancellationToken cancellationToken)
    {
        if (number is < 1 or > DatabaseRecordCursor.MaximumRelativeRecordNumber) throw LogicalError("Invalid relative output record number.");
        var identity = RecordSlots.Identity(connection, transaction, library, file, member) ?? throw LogicalError("Relative output member is unavailable.");
        if (number > RecordSlots.HighWater(connection, transaction, identity)) throw LogicalError("Relative WRITE requires an already allocated record position; use INZPFM first.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM " + Quote(MemberTable(library, file, member)) + " WHERE " + Quote(rowid) + "=$rrn";
        command.Parameters.AddWithValue("$rrn", number);
        if (Convert.ToInt64(SqliteCancellation.Run(connection, cancellationToken, command.ExecuteScalar)) != 0)
            throw LogicalError("Relative WRITE requires a deleted record position.");
    }
}
