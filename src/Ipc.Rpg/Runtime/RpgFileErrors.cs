using Ipc.Core.Messages;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Microsoft.Data.Sqlite;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private static bool IsFileOperation(RpgOpcode opcode) => opcode is RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Unlock or
        RpgOpcode.Chain or RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.ReadC or
        RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Delete or RpgOpcode.ExFmt;
    private static RpgRuntimeException FileFailure(int status, string message, string? file = null, Exception? service = null) =>
        new(message) { StatusCode = status, FileError = true, StatusFile = file, ServiceError = service };
    private T FileIo<T>(string file, Func<T> operation, int fallback = 1299)
    {
        try { return operation(); }
        catch (CpfException error) when (error.MessageId != "CPF1241")
        {
            var status = fallback == 1299 && DatabaseRecordErrors.IsRecordLockFailure(error) ? 1218 : fallback;
            throw FileFailure(status, error.Message, file, error);
        }
        catch (SqliteException error)
        {
            var status = fallback != 1299 ? fallback : error.SqliteExtendedErrorCode switch { 1555 or 2067 => 1021, 787 => 1022, _ => 1299 };
            throw FileFailure(status, error.Message, file, error);
        }
        catch (RpgRuntimeException error) when (!error.FileError && error.StatusCode is null)
        { throw FileFailure(fallback, error.Message, file, error.ServiceError); }
    }
    private void FileIo(string file, Action operation, int fallback = 1299) => FileIo(file, () => { operation(); return true; }, fallback);
    private RpgFileCursor IoCursor(RpgStatement statement) => FileIo(StatusFile(statement), () => GetCursor(FirstFile(statement)));
    private bool DeclaredFile(RpgStatement statement) => _context is { } context &&
        (DiskBinding(context, statement) is not null || context.Program.Workstations.Any(f => f.Request.Name == StatusFile(statement)));
    private T ImplicitFile<T>(string file, Func<T> operation)
    {
        try { var result = FileIo(file, operation, 1216); _context?.RecordStatus(0, file); return result; }
        catch (RpgRuntimeException error)
        { _context?.RecordStatus(error.StatusCode ?? 1216, file); _context?.RecordFeedbackError(error); throw; }
    }
    private bool HandleOperationError(RpgRuntimeException error, RpgStatement statement, RpgRuntimeContext context)
    {
        if (!error.FeedbackRecorded && error.StatusCode is { } code)
            context.RecordStatus(code, error.FileError ? error.StatusFile ?? (IsFileOperation(statement.Opcode) ? StatusFile(statement) : null) : null,
                errorExtender: error.FileError && statement.ErrorExtender);
        context.RecordFeedbackError(error);
        if ((statement.NativeOperation == "CALL" || statement.Opcode == RpgOpcode.CallP) && !error.CallArgumentError && error.StatusCode == 202 && (statement.ErrorExtender || statement.Indicator1 != 0))
        {
            if (statement.Indicator1 != 0) context.Indicators[statement.Indicator1] = true;
            if (statement.ErrorExtender) context.RecordStatus(202, errorExtender: true);
            return true;
        }
        if (!error.FileError || !IsFileOperation(statement.Opcode) || !DeclaredFile(statement)) return false;
        if (statement.Indicator2 != 0) { context.Indicators[statement.Indicator2] = true; return true; }
        return statement.ErrorExtender;
    }
    private static int MonitorHandler(IReadOnlyList<RpgStatement> statements, int pc, int status, RpgBlockState state, bool unwind = true)
    {
        for (var monitor = statements[pc].Monitor; monitor >= 0; monitor = statements[monitor].Monitor)
        {
            var opening = statements[monitor];
            foreach (var index in opening.ErrorHandlers)
            {
                if (!statements[index].ErrorStatuses.Any(filter => filter == status || filter == -1 && status is >= 100 and <= 9999 ||
                    filter == -2 && (status is >= 100 and <= 999 || status == 9999) || filter == -3 && status is >= 1000 and <= 2999)) continue;
                if (unwind)
                {
                    while (state.Loops.TryPeek(out var loop) && loop.Start > monitor && loop.End <= opening.End) state.Loops.Pop();
                    while (state.Selects.TryPeek(out var select) && select.Start > monitor && select.End <= opening.End) state.Selects.Pop();
                }
                return index + 1;
            }
        }
        return -1;
    }
}
