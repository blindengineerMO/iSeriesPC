using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteChangePhysicalFile(CommandCall call)
    {
        var (library, name) = ResolveFileReference(RequiredFile(call));
        var option = CommandParser.Unquote(call.GetOption("MAXMBRS") ?? "*SAME").ToUpperInvariant();
        int? maximum = option == "*SAME" ? null : option == "*NOMAX" ? 32767 : int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var result = _files.ChangePhysicalMemberLimit(library, name, maximum);
        return CommandResult.Ok($"Physical file {library}/{name} maximum members: {result}.");
    }
}
