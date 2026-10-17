using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterDataQueueCommands()
    {
        foreach (var name in new[] { "CRTDTAQ", "DLTDTAQ", "SNDDTAQ", "RCVDTAQ", "CLRDTAQ" })
            _catalog.Register(name, call => ExecuteDataQueueCommand(call, null));
    }
    private CommandResult ExecuteDataQueueCommand(CommandCall call, ClCommandContext? context)
    {
        string Text(string keyword, string fallback = "") => context?.Resolve(call.GetOption(keyword) ?? fallback) ?? CommandParser.Unquote(call.GetOption(keyword) ?? fallback);
        string Choice(string keyword, string fallback = "") => Text(keyword, fallback).Trim().ToUpperInvariant();
        int Number(string keyword, string fallback) => int.TryParse(Text(keyword, fallback).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw Invalid(keyword + " requires a nonnegative integer.");
        bool Flag(string keyword, string fallback) => Choice(keyword, fallback) switch { "*YES" => true, "*NO" => false, _ => throw Invalid(keyword + " requires *YES or *NO.") };
        var create = call.Name == "CRTDTAQ";
        var (library, name) = create ? ResolveWorkObject(Choice("DTAQ"), ObjectType.DataQueue, creating: true) : SplitQualified(Choice("DTAQ"), "*LIBL");
        if (!create && library is "*LIBL" or "*CURLIB")
            library = SearchLibraries(library).FirstOrDefault(candidate => new Ipc.Services.Sqlite.SqliteObjectStore(_system.Connections).GetForAuthorization(candidate, name, ObjectType.DataQueue) is not null)
                ?? throw new CpfException("CPF9801", "Data queue not found in the job library list.");
        var queue = new QualifiedName(library, name); var store = new DataQueueStore(_system.Connections);
        var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        if (create)
        {
            if (Choice("TYPE", "*STD") != "*STD") throw Invalid("Only standard local data queues are supported.");
            var sequence = Choice("SEQ", "*FIFO") switch { "*FIFO" => DataQueueSequence.Fifo, "*LIFO" => DataQueueSequence.Lifo, "*KEYED" => DataQueueSequence.Keyed, _ => throw Invalid("Unsupported SEQ.") };
            store.Create(queue, Number("MAXLEN", "0"), sequence, Number("KEYLEN", "0"), Flag("SENDERID", "*NO"), Text("TEXT"), Choice("AUT", "*LIBCRTAUT"), ccsid);
            return CommandResult.Ok("Data queue " + queue + " created.");
        }
        if (call.Name == "DLTDTAQ")
        {
            _system.Objects.Delete(library, name, ObjectType.DataQueue); return CommandResult.Ok("Data queue " + queue + " deleted.");
        }
        ProgramBuffer Input(string keyword)
        {
            var raw = call.GetOption(keyword)?.Trim() ?? throw Invalid(keyword + " is required.");
            if (context is not null && raw.StartsWith('&'))
            {
                var variable = context.Variable(raw);
                if (variable.Type != "*CHAR") throw Invalid(keyword + " requires CHAR storage.");
                return variable.ToBuffer() ?? throw Invalid("Missing byte storage.");
            }
            if (raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase))
                return (ProgramBuffer)ClExpression.Compile(raw).Evaluate(_ => throw Invalid("Expected hexadecimal bytes."), ccsid);
            return new(StrictJobEncoding(ccsid).GetBytes(CommandParser.Unquote(raw)), ccsid);
        }
        var key = call.GetOption("KEY") is null ? null : Input("KEY");
        if (call.Name == "SNDDTAQ")
        {
            var data = Input("DATA"); var length = Number("LEN", data.Length.ToString(CultureInfo.InvariantCulture));
            if (length < 1 || length > data.Length) throw Invalid("LEN must select bytes within DATA.");
            store.Send(queue, new(data.ToArray().AsSpan(0, length), data.Ccsid), key, _cancellationToken);
            return CommandResult.Ok("Data-queue entry sent.");
        }
        var order = call.GetOption("KEYORDER") is null ? (DataQueueKeyOrder?)null : Choice("KEYORDER") switch {
            "*EQ" or "EQ" => DataQueueKeyOrder.Equal, "*NE" or "NE" => DataQueueKeyOrder.NotEqual,
            "*GT" or "GT" => DataQueueKeyOrder.Greater, "*GE" or "GE" => DataQueueKeyOrder.GreaterOrEqual,
            "*LT" or "LT" => DataQueueKeyOrder.Less, "*LE" or "LE" => DataQueueKeyOrder.LessOrEqual, _ => throw Invalid("Invalid KEYORDER.") };
        if (key is null && order is not null) throw Invalid("KEYORDER requires KEY.");
        if (call.Name == "CLRDTAQ")
            return CommandResult.Ok(store.Clear(queue, key, order, _cancellationToken).ToString(CultureInfo.InvariantCulture) + " data-queue entries cleared.");
        var wait = Choice("WAIT", "0") == "*MAX" ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Number("WAIT", "0"));
        if (wait != Timeout.InfiniteTimeSpan && wait > TimeSpan.FromDays(7)) throw Invalid("WAIT exceeds seven days.");
        var remove = Flag("RMV", "*YES");
        var targets = new Dictionary<string, ProgramArgument>();
        foreach (var keyword in new[] { "RTNVAR", "LENVAR", "KEYVAR" })
        {
            if (call.GetOption(keyword) is not { } raw) continue;
            if (context is null || !raw.Trim().StartsWith('&')) throw Invalid(keyword + " requires a compiled CL return variable.");
            var target = context.Variable(raw.Trim());
            if (keyword == "LENVAR" ? target.Type != "*DEC" || target.Length != 5 || target.Decimals != 0 : target.Type != "*CHAR" || target.Length < 1)
                throw Invalid("Invalid return variable for " + keyword + ".");
            targets.Add(keyword, target);
        }
        var received = store.Receive(queue, key, order, wait, remove, _cancellationToken,
            targets.TryGetValue("RTNVAR", out var receiver) ? Math.Min(receiver.Length, DataQueueStore.MaximumEntryLength) : DataQueueStore.MaximumEntryLength,
            targets.TryGetValue("KEYVAR", out var keyReceiver) ? Math.Min(keyReceiver.Length, 256) : 256);
        foreach (var (keyword, target) in targets)
        {
            if (keyword == "LENVAR") { target.Value = (decimal)(received?.Data.Length ?? 0); continue; }
            var value = keyword == "RTNVAR" ? received?.Data.ToArray() : received?.Key.ToArray();
            var padded = Enumerable.Repeat(StrictJobEncoding(ccsid).GetBytes(" ")[0], target.Length).ToArray();
            value?.CopyTo(padded, 0); target.Value = new ProgramBuffer(padded, ccsid);
        }
        if (targets.Count > 0) return CommandResult.Ok();
        return received is null ? CommandResult.Ok("No data-queue entry available.") : new CommandResult {
            Message = "Data-queue entry: " + received.Data.Length.ToString(CultureInfo.InvariantCulture) + " bytes.",
            Listing = new[] { "Data: X'" + Convert.ToHexString(received.Data.ToArray().AsSpan(0, Math.Min(256, received.Data.Length))) + "'" + (received.Data.Length > 256 ? " (first 256 bytes)" : ""),
                "Key: X'" + Convert.ToHexString(received.Key.ToArray()) + "'", "Sender: " + (received.Sender?.User ?? "not retained") } };
    }
    private static CpfException Invalid(string message) => new("IPC0003", message);
}
