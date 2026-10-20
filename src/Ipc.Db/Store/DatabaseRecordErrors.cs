using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

/// <summary>Distinguish record contention from object allocation without changing CPF identities.</summary>
public static class DatabaseRecordErrors
{
    private const string RecordLock = "Ipc.Db.RecordLock";
    public static bool IsRecordLockFailure(CpfException error) => error.MessageId == "CPF1002" && error.Data[RecordLock] is true;
    public static bool IsReferentialConstraintFailure(SqliteException error) => error.SqliteExtendedErrorCode == 787 ||
        error.SqliteExtendedErrorCode == 1811 && error.Message.Contains("FOREIGN KEY constraint failed", StringComparison.Ordinal);
    internal static void MarkRecordLockFailure(CpfException error) { if (error.MessageId == "CPF1002") error.Data[RecordLock] = true; }
}
