using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteLibraryList(CommandCall call)
    {
        if (_job is null) throw new CpfException("CPF1241", "A job context is required.");
        var current = _job.CurrentLibrary ?? "*CRTDFT";
        var libraries = (_job.LibraryList ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string Value(string keyword, string fallback = "") => CommandParser.Unquote(call.GetOption(keyword) ?? fallback).Trim().ToUpperInvariant();
        switch (call.Name.ToUpperInvariant())
        {
            case "CHGLIBL":
                var list = (call.GetOption("LIBL") ?? "*SAME").Trim().ToUpperInvariant();
                if (list != "*SAME") libraries = list == "*NONE" ? new() : CommandParser.Tokenize(list.StartsWith('(') && list.EndsWith(')') ? list[1..^1] : list).Select(CommandParser.Unquote).ToList();
                var changed = Value("CURLIB", "*SAME"); if (changed != "*SAME") current = changed;
                break;
            case "CHGCURLIB": current = Value("CURLIB", "*CRTDFT"); break;
            case "ADDLIBLE":
                var library = Value("LIB");
                if (libraries.Contains(library)) throw new CpfException("CPF2103", "Library already exists in the user library list.");
                var position = CommandParser.Tokenize(Value("POSITION", "*FIRST").Trim('(', ')')).Select(CommandParser.Unquote).ToArray();
                if (position.Length == 1 && position[0] is "*FIRST" or "*LAST") libraries.Insert(position[0] == "*FIRST" ? 0 : libraries.Count, library);
                else if (position.Length == 2 && position[0] is "*BEFORE" or "*AFTER" or "*REPLACE")
                {
                    var index = libraries.IndexOf(position[1]);
                    if (index < 0) throw new CpfException("CPF2149", "Reference library is not in the user library list.");
                    if (position[0] == "*REPLACE") libraries[index] = library;
                    else libraries.Insert(index + (position[0] == "*AFTER" ? 1 : 0), library);
                }
                else throw new CpfException("IPC0003", "POSITION requires *FIRST, *LAST or (*BEFORE/*AFTER/*REPLACE reference-library).");
                break;
            case "RMVLIBLE":
                if (!libraries.Remove(Value("LIB"))) throw new CpfException("CPF2149", "Library is not in the user library list.");
                break;
        }
        if (libraries.Count > 250 || libraries.Distinct().Count() != libraries.Count || libraries.Any(l => !ObjectName.IsValid(l)) ||
            current != "*CRTDFT" && !ObjectName.IsValid(current)) throw new CpfException("IPC0003", "Invalid current library or user library list.");
        _system.Jobs.SetLibraries(_job, current, string.Join(' ', libraries));
        return CommandResult.Ok("Job library list changed.");
    }
}
