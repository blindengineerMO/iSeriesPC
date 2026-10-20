using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteChangePhysicalConstraint(CommandCall call)
    {
        string Required(string keyword, int position)
        {
            var option = call.GetOption(keyword);
            if (option is not null && call.Positional.Count > position) throw new CpfException("IPC0003", "Duplicate " + keyword + " value.");
            return option ?? (call.Positional.Count > position ? call.Positional[position] : throw new CpfException("IPC0003", "CHGPFCST requires " + keyword + "."));
        }
        var (library, name) = ResolveFileReference(CommandParser.Unquote(Required("FILE", 0)));
        var names = CommandParser.Tokenize(Required("CST", 1)).Select(CommandParser.Unquote).ToArray();
        bool? enabled = CommandParser.Unquote(call.GetOption("STATE") ?? "*SAME").ToUpperInvariant() switch {
            "*SAME" => null, "*ENABLED" => true, "*DISABLED" => false,
            _ => throw new CpfException("IPC0003", "STATE requires *SAME, *ENABLED or *DISABLED.") };
        var check = CommandParser.Unquote(call.GetOption("CHECK") ?? "*YES").ToUpperInvariant() switch {
            "*YES" => true, "*NO" => false, _ => throw new CpfException("IPC0003", "CHECK requires *YES or *NO.") };
        var results = _files.ChangeConstraints(library, name, names, enabled, check, _cancellationToken);
        var changed = results.Count(item => item.Changed);
        var diagnostics = results.Where(item => item.Error is not null).Select(item => item.Name + ": " + item.Error!.Message).ToArray();
        var message = changed + " constraints changed." + (diagnostics.Length == 0 ? "" : " " + string.Join(" ", diagnostics));
        return diagnostics.Length == 0 ? CommandResult.Ok(message) : CommandResult.Error(message, "CPF32B2");
    }
}
