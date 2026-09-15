using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterLockCommands()
    {
        _catalog.Register("ALCOBJ", ExecuteLocks);
        _catalog.Register("DLCOBJ", ExecuteLocks);
        _catalog.Register("WRKOBJLCK", ExecuteLocks);
    }
    private CommandResult ExecuteLocks(CommandCall call)
    {
        if (_job is null) throw new CpfException("CPF1241", "Lock commands require an executing job.");
        var store = new JobLockStore(_system.Connections);
        if (call.Name.Equals("WRKOBJLCK", StringComparison.OrdinalIgnoreCase))
        {
            var resource = ResolveLockResource(CommandParser.Unquote(call.GetOption("OBJ")), CommandParser.Unquote(call.GetOption("OBJTYPE")));
            var rows = store.Inspect(resource).Select(x => $"{x.Job} {(x.Waiting ? "WAIT" : "HELD")} {LockModeName(x.Mode)} {x.Lifetime} " +
                (x.Resource.IsRecord ? $"MBR({x.Resource.Member}) RRN({x.Resource.RowNumber})" : "OBJECT")).ToArray();
            return new CommandResult { Listing = rows, Message = $"{rows.Length} lock holder/waiter(s)." };
        }
        var tuples = call.Split("OBJ");
        if (tuples.Count is < 1 or > 64) throw new CpfException("IPC0125", "OBJ requires 1–64 (object type lock-state) tuples.");
        var requests = tuples.Select(tuple =>
        {
            if (!tuple.StartsWith('(') || !tuple.EndsWith(')')) throw new CpfException("IPC0125", "OBJ requires (object type lock-state) tuples.");
            var parts = CommandParser.Tokenize(tuple[1..^1]);
            if (parts.Count != 3) throw new CpfException("IPC0125", "Each OBJ tuple requires object, type and lock state.");
            var mode = parts[2].ToUpperInvariant() switch {
                "*EXCL" => JobLockMode.Exclusive, "*SHRUPD" => JobLockMode.SharedUpdate,
                "*SHRRD" => JobLockMode.SharedRead, "*SHRNUP" => JobLockMode.SharedNoUpdate,
                _ => throw new CpfException("IPC0125", "Unsupported lock state.") };
            return (Resource: ResolveLockResource(CommandParser.Unquote(parts[0]), parts[1]), Mode: mode);
        }).ToArray();
        if (call.Name.Equals("DLCOBJ", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var request in requests) store.Release(_job.Key, request.Resource, request.Mode);
        }
        else
        {
            var seconds = ParseWorkInteger(call, "WAIT", 0);
            if (seconds is < 0 or > 300) throw new CpfException("IPC0125", "WAIT must be 0–300 seconds.");
            var acquired = new List<IDisposable>(); var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                foreach (var request in requests)
                {
                    var remaining = TimeSpan.FromSeconds(seconds) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    acquired.Add(store.Acquire(_job.Key, request.Resource, request.Mode, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _cancellationToken));
                }
            }
            catch { foreach (var held in acquired) held.Dispose(); throw; }
        }
        return new CommandResult { Message = $"{requests.Length} object allocation(s) {(call.Name.Equals("DLCOBJ", StringComparison.OrdinalIgnoreCase) ? "released" : "acquired")}." };
    }
    private JobLockResource ResolveLockResource(string value, string type)
    {
        type = type.ToUpperInvariant();
        var (library, name) = SplitQualified(value.ToUpperInvariant(), "*LIBL");
        var objects = new SqliteObjectStore(_system.Connections);
        if (library is "*LIBL" or "*CURLIB") library = SearchLibraries(library).FirstOrDefault(l => objects.GetForAuthorization(l, name, type) is not null)
            ?? throw new CpfException("CPF9801", "Lock object not found in the job library list.");
        if (objects.GetForAuthorization(library, name, type) is null) throw new CpfException("CPF9801", "Lock object not found.");
        return new(library, name, type);
    }
    private static string LockModeName(JobLockMode mode) => mode switch {
        JobLockMode.Exclusive => "*EXCL", JobLockMode.SharedUpdate => "*SHRUPD", JobLockMode.SharedRead => "*SHRRD", _ => "*SHRNUP" };
}
