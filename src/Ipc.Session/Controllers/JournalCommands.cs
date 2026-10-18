using System.Globalization;
using System.Text.Json;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterJournalCommands()
    {
        foreach (var name in new[] { "CRTJRNRCV", "CRTJRN", "CHGJRN", "DLTJRN", "DLTJRNRCV", "STRJRNPF", "ENDJRNPF", "SNDJRNE", "DSPJRN", "APYJRNCHG", "RMVJRNCHG" })
        { var operation = name; _catalog.Register(name, call => ExecuteJournal(call, operation)); }
    }
    private CommandResult ExecuteJournal(CommandCall call, string operation)
    {
        var store = _system.Journals;
        string Text(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback);
        string Choice(string key, string fallback = "") => Text(key, fallback).ToUpperInvariant();
        QualifiedName Object(string key, string type, bool creating = false)
        {
            var (library, name) = ResolveWorkObject(Choice(key), type, creating); return new(library, name);
        }
        long Number(string key, string fallback) => long.TryParse(Text(key, fallback), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw JournalParameter(key + " requires a positive integer.");
        switch (operation)
        {
            case "APYJRNCHG":
            case "RMVJRNCHG": return ExecuteJournalRecovery(call, operation == "RMVJRNCHG");
            case "CRTJRNRCV":
                store.CreateReceiver(Object("JRNRCV", ObjectType.JournalReceiver, true), Choice("THRESHOLD", "1500000") == "*NONE" ? null : Number("THRESHOLD", "1500000"),
                    Text("TEXT"), Choice("AUT", "*LIBCRTAUT"), _cancellationToken); break;
            case "CRTJRN":
                // The native default is system management; require an explicit supported choice.
                if (Choice("MNGRCV", "*SYSTEM") != "*USER") throw JournalParameter("This build requires MNGRCV(*USER); automatic receiver management is unavailable.");
                if (Choice("DLTRCV", "*NO") != "*NO") throw JournalParameter("This build requires DLTRCV(*NO).");
                store.Create(Object("JRN", ObjectType.Journal, true), Object("JRNRCV", ObjectType.JournalReceiver), Text("TEXT"), Choice("AUT", "*LIBCRTAUT"), _cancellationToken); break;
            case "CHGJRN":
                var receiver = Choice("JRNRCV"); if (receiver.Length == 0) throw JournalParameter("JRNRCV requires a receiver name or *GEN.");
                var attached = store.Rotate(Object("JRN", ObjectType.Journal), receiver == "*GEN" ? null : Object("JRNRCV", ObjectType.JournalReceiver), cancellationToken: _cancellationToken);
                return CommandResult.Ok("Receiver " + attached + " attached.");
            case "DLTJRN": store.Delete(Object("JRN", ObjectType.Journal), _cancellationToken); break;
            case "DLTJRNRCV":
                var option = Choice("DLTOPT", "*NONE"); if (option is not ("*NONE" or "*IGNINQMSG")) throw JournalParameter("DLTOPT requires *NONE or *IGNINQMSG.");
                store.DeleteReceiver(Object("JRNRCV", ObjectType.JournalReceiver), option == "*IGNINQMSG", _cancellationToken); break;
            case "STRJRNPF": store.StartFile(Object("FILE", ObjectType.File), Object("JRN", ObjectType.Journal), Choice("IMAGES", "*AFTER"), _cancellationToken); break;
            case "ENDJRNPF": store.EndFile(Object("FILE", ObjectType.File), _cancellationToken); break;
            case "SNDJRNE":
                return SendJournalEntry(call);
            case "DSPJRN":
                var journal = Object("JRN", ObjectType.Journal);
                var from = Choice("FROMENT", "*FIRST") == "*FIRST" ? 1 : Number("FROMENT", "1");
                long? to = Choice("TOENT", "*LAST") == "*LAST" ? null : Number("TOENT", "1");
                var count = Number("NBRENT", "1000"); if (count is < 1 or > 4000) throw JournalParameter("NBRENT requires 1–4000 for the work-list display.");
                var entries = store.Read(journal, from, to, (int)count, _cancellationToken);
                var rows = entries.Select(entry => new WorkRow(entry.Sequence.ToString(CultureInfo.InvariantCulture),
                    $"{entry.Sequence,12} {entry.Code} {entry.Type} {entry.Timestamp:yyyy-MM-dd HH:mm:ss} {entry.ObjectLibrary}/{entry.ObjectName} {entry.Member} {entry.RecordNumber}",
                    [new WorkAction("5", "Display entry", "", Details: DescribeJournalEntry(entry))])).ToList();
                var next = entries.Count == 0 ? "" : entries[^1].Sequence == long.MaxValue ? " Journal sequence exhausted." :
                    " Resume with FROMENT(" + (entries[^1].Sequence + 1).ToString(CultureInfo.InvariantCulture) + ").";
                return new() { Message = entries.Count + " entries returned (bounded page)." + next, Listing = rows.Select(row => row.Text).ToArray(),
                    WorkList = new("Journal entries", rows, $"DSPJRN JRN({journal}) FROMENT({from}) TOENT({(to is null ? "*LAST" : to.Value.ToString(CultureInfo.InvariantCulture))}) NBRENT({count})") };
        }
        return CommandResult.Ok(operation + " completed.");
    }
    private CommandResult SendJournalEntry(CommandCall call, ClCommandContext? context = null)
    {
        string Value(string key, string fallback = "") => (context?.Resolve(call.GetOption(key) ?? fallback) ?? CommandParser.Unquote(call.GetOption(key) ?? fallback)).Trim().ToUpperInvariant();
        var (library, name) = ResolveWorkObject(Value("JRN"), ObjectType.Journal);
        var raw = call.GetOption("ENTDTA"); object input;
        if (context is not null && raw?.Trim().StartsWith('&') == true)
        {
            var variable = context.Variable(raw.Trim());
            if (variable.Type != "*CHAR") throw JournalParameter("ENTDTA requires CHAR storage.");
            input = variable.ToBuffer() ?? throw JournalParameter("ENTDTA has no byte storage.");
        }
        else input = raw is null || raw.Equals("*BLANK", StringComparison.OrdinalIgnoreCase) ? "" :
            raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase) ? DataAreaInput(raw, null) : CommandParser.Unquote(raw);
        var data = input is ProgramBuffer buffer ? buffer.ToArray() : StrictJobEncoding(_job?.Ccsid ?? _system.Config.Ccsid).GetBytes(Convert.ToString(input, CultureInfo.InvariantCulture) ?? "");
        return CommandResult.Ok("Journal entry " + _system.Journals.Send(new(library, name), Value("TYPE", "00"), data, _cancellationToken).ToString(CultureInfo.InvariantCulture) + " written.");
    }
    private static IReadOnlyList<string> DescribeJournalEntry(JournalEntry entry)
    {
        var details = new List<string> { "Sequence: " + entry.Sequence + "  Code/type: " + entry.Code + "/" + entry.Type,
            "Receiver: " + entry.Receiver, "Time: " + entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            "User: " + entry.Principal + "  Job: " + entry.Job, "Object: " + entry.ObjectLibrary + "/" + entry.ObjectName + "  Member: " + entry.Member + "  RRN: " + entry.RecordNumber,
            "Local entry format version: " + entry.Version, "File attachment ID: " + entry.BindingId };
        void Image(string label, IReadOnlyList<JournalCell>? cells)
        {
            if (cells is null) return; details.Add(label + ":");
            foreach (var cell in cells.Take(128))
            {
                var value = JsonSerializer.Serialize(cell.Value); if (value.Length > 512) value = value[..512] + " [display truncated]";
                details.Add(cell.Name + " (" + cell.StorageType + ") " + value);
            }
            if (cells.Count > 128) details.Add("[remaining fields omitted from display]");
        }
        Image("Before", entry.Before); Image("After", entry.After);
        if (entry.Data is { } data) details.Add("Entry data (hex): " + Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 256))) + (data.Length > 256 ? " [display truncated]" : ""));
        return details;
    }
    private static CpfException JournalParameter(string message) => new("IPC0003", message);
}
