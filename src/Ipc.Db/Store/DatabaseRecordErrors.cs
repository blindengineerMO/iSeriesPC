using Ipc.Core.Messages;

namespace Ipc.Db.Store;

/// <summary>Distinguish record contention from object allocation without changing CPF identities.</summary>
public static class DatabaseRecordErrors
{
    private const string RecordLock = "Ipc.Db.RecordLock";
    public static bool IsRecordLockFailure(CpfException error) => error.MessageId == "CPF1002" && error.Data[RecordLock] is true;
    internal static void MarkRecordLockFailure(CpfException error) { if (error.MessageId == "CPF1002") error.Data[RecordLock] = true; }
}
