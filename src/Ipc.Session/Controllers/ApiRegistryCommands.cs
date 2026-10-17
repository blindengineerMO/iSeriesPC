using System.Text.RegularExpressions;
using Ipc.Api;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ListRuntimeApis(CommandCall call)
    {
        var selector = CommandParser.Unquote(call.GetOption("API") ?? "*ALL").ToUpperInvariant();
        if (selector != "*ALL" && !Regex.IsMatch(selector, @"\A[A-Z_][A-Z0-9_]{0,127}\*?\z", RegexOptions.CultureInvariant))
            throw new CpfException("IPC0003", "API requires a name, trailing-star prefix or *ALL.");
        var names = BuiltinProgramService.Names;
        var contracts = ApiContractCatalog.All.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var rows = names.Where(n => selector == "*ALL" || (selector.EndsWith('*') ? n.StartsWith(selector[..^1], StringComparison.Ordinal) : n == selector)).Select(name =>
        {
            var status = "Available";
            try
            {
                var descriptor = _system.Objects.Get("QSYS", name, ObjectType.Program);
                if (descriptor is null) status = "Not installed";
                else { BuiltinProgramService.Validate(descriptor); _system.ObjectSigning.RequireExecutable(descriptor); }
            }
            catch (CpfException failure) { status = "Blocked " + failure.MessageId; }
            var contract = contracts[name];
            return new WorkRow(name, name.PadRight(10) + " " + status.PadRight(18) + " " + contract.Description,
                [new WorkAction("5", "Display contract", "", Details: new[] { "Availability: " + status }.Concat(contract.Describe()).ToArray())]);
        });
        return WorkResult("Runtime system APIs", "WRKAPILS API(" + selector + ")", rows);
    }
}
