using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteChangePhysicalFile(CommandCall call)
    {
        var (library, name) = ResolveFileReference(RequiredFile(call));
        var option = CommandParser.Unquote(call.GetOption("MAXMBRS") ?? "*SAME").ToUpperInvariant();
        int? maximum = option == "*SAME" ? null : option == "*NOMAX" ? 32767 : int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var result = _files.ChangePhysicalMemberLimit(library, name, maximum, ParseRecordWait(call.GetOption("WAITRCD"), change: true));
        return CommandResult.Ok($"Physical file {library}/{name} maximum members: {result}.");
    }
    private CommandResult ExecuteChangeLogicalFile(CommandCall call)
    {
        var (library, name) = ResolveFileReference(RequiredFile(call));
        _files.ChangeLogicalRecordWait(library, name, ParseRecordWait(call.GetOption("WAITRCD"), change: true));
        return CommandResult.Ok($"Logical file {library}/{name} record wait updated.");
    }
    private static int? ParseRecordWait(string? raw, bool change = false, bool overriding = false)
    {
        if (raw is null) return change || overriding ? null : 60;
        var text = CommandParser.Unquote(raw).ToUpperInvariant();
        if (change && text == "*SAME") return null;
        if (text == "*IMMED") return 0;
        if (text == "*NOMAX") return overriding ? -1 : 32767;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 32767) return seconds;
        throw new CpfException("IPC0138", "WAITRCD requires 1–32767, *IMMED or *NOMAX" + (change ? ", or *SAME." : "."));
    }
    private static string RecordWaitText(int value) => value switch { -1 => "*NOMAX", 0 => "*IMMED", _ => value.ToString(CultureInfo.InvariantCulture) };
}
