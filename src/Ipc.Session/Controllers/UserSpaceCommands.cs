using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterUserSpaceCommands()
    {
        _catalog.Register("CRTUSRSPC", CreateUserSpace);
        _catalog.Register("DLTUSRSPC", DeleteUserSpace);
    }
    private CommandResult DeleteUserSpace(CommandCall call)
    {
        var (library, name) = SplitQualified(CommandParser.Unquote(call.GetOption("USRSPC") ?? "").ToUpperInvariant(), "*LIBL");
        var count = new UserSpaceStore(_system.Connections).Delete(name, library, SearchLibraries, _cancellationToken);
        return CommandResult.Ok($"{count} user space(s) deleted.");
    }
    private CommandResult CreateUserSpace(CommandCall call)
    {
        string Text(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback);
        string Choice(string key, string fallback) => Text(key, fallback).Trim().ToUpperInvariant();
        int Number(string key, string fallback) => int.TryParse(Text(key, fallback), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw Invalid("Invalid " + key + ".");
        bool Flag(string key, string fallback) => Choice(key, fallback) switch { "*YES" => true, "*NO" => false, _ => throw Invalid(key + " requires *YES or *NO.") };
        var (library, name) = ResolveWorkObject(Text("USRSPC").ToUpperInvariant(), ObjectType.UserSpace, creating: true);
        var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        byte initial = 0;
        if (call.GetOption("VALUE") is { } value)
        {
            var input = DataAreaInput(value, null);
            var bytes = input is ProgramBuffer buffer ? buffer.ToArray() : StrictJobEncoding(ccsid).GetBytes(Convert.ToString(input, CultureInfo.InvariantCulture) ?? "");
            if (bytes.Length != 1) throw new CpfException("IPC0003", "VALUE must contain exactly one byte.");
            initial = bytes[0];
        }
        new UserSpaceStore(_system.Connections).Create(new(library, name), Number("SIZE", "1"), initial, Text("ATTR"), Text("TEXT"),
            Choice("AUT", "*LIBCRTAUT"), Flag("REPLACE", "*NO"), Choice("DOMAIN", "*DEFAULT"), Number("TRFSIZ", "0"),
            Flag("ALIGN", "*YES"), Flag("AUTOEXT", "*NO"), ccsid, _cancellationToken);
        return CommandResult.Ok("User space " + library + "/" + name + " created.");
    }
}
