using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteDuplicateObject(CommandCall call)
    {
        string Value(string keyword, string fallback = "") => CommandParser.Unquote(call.GetOption(keyword) ?? fallback).ToUpperInvariant();
        bool Choice(string keyword, bool fallback) => Value(keyword, fallback ? "*YES" : "*NO") switch {
            "*YES" => true, "*NO" => false, _ => throw new CpfException("IPC0003", keyword + " requires *YES or *NO.") };
        var name = Value("OBJ"); var from = Value("FROMLIB"); var type = Value("OBJTYPE");
        if (!ObjectName.IsValid(name) || from.Length == 0 || !ObjectType.IsKnown(type))
            throw new CpfException("IPC0003", "CRTDUPOBJ requires OBJ(name), FROMLIB and a supported OBJTYPE.");
        var data = Choice("DATA", false); var constraints = Choice("CST", true);
        if (data && type != ObjectType.File) throw new CpfException("CPF2116", "DATA(*YES) requires OBJTYPE(*FILE).");
        var library = SearchLibraries(from).FirstOrDefault(candidate => _system.Objects.Exists(candidate, name, type))
            ?? throw new CpfException("CPF9801", "Source object not found in the specified library or library list.");
        var to = Value("TOLIB", "*FROMLIB");
        if (to is "*FROMLIB" or "*SAME") to = library;
        else if (to == "*CURLIB") to = _job?.CurrentLibrary is { Length: > 0 } current && current != "*CRTDFT" ? current : "QGPL";
        var target = Value("NEWOBJ", "*OBJ"); if (target is "*OBJ" or "*SAME") target = name;
        var source = new QualifiedName(library, name);
        _system.ObjectOperations.Relocate(source, type, new(to, target), copy: true,
            newOwner: Ipc.Services.Events.OperationIdentity.Current?.Principal ?? _job?.UserProfile ?? "QSECOFR",
            copyData: data, copyConstraints: constraints, cancellationToken: _cancellationToken);
        return CommandResult.Ok($"CRTDUPOBJ completed for {source} {type}.");
    }
}
