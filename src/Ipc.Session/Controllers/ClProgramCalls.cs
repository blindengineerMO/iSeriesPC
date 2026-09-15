using Ipc.Cl.Commands;
using Ipc.Core.Work;
using Ipc.Rpg.Runtime;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult CallFromCl(string library, string name, IReadOnlyList<ProgramArgument> arguments)
    {
        var loaded = LoadProgramObject(library, name);
        if (loaded.External is { } native)
        {
            // The process protocol exchanges values, so overlapping native reference
            // arguments need an explicit alias-capable protocol before they can run.
            if (arguments.Distinct().Count() != arguments.Count)
                return CommandResult.Error("Native process calls do not yet support aliased reference arguments.");
            using var scope = EnterProgram(native);
            var invocation = _system.ExternalPrograms.Execute(native,
                arguments.Select(argument => (object?)argument.ToBuffer() ?? argument.Value).ToArray(), _cancellationToken);
            // Validate the entire response before changing any caller cell. A valid
            // failure response still writes back before MONMSG handles the escape.
            var values = invocation.Parameters.Select((value, index) => arguments[index].Normalize(value)).ToArray();
            for (var index = 0; index < values.Length; index++) arguments[index].Value = values[index];
            if (invocation.Message.Length > 0) _messages.AppendLine(invocation.Message);
            return invocation.Success ? CommandResult.Ok(invocation.Message) : CommandResult.Error(invocation.Message);
        }
        if (loaded.Rpg is { } rpg)
        {
            using var scope = EnterProgram(rpg);
            try
            {
                var entry = RpgInterpreter.EntryParameterNames(rpg);
                if (entry.Count != arguments.Count || entry.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Count)
                    return CommandResult.Error("RPG entry parameter count or declarations do not match the caller.");
                RunRpg(rpg, arguments.Cast<object?>().ToArray()); return CommandResult.Ok();
            }
            catch (RpgException error) { return CommandResult.Error(error.Message); }
        }
        return CommandResult.Error($"Program {library}/{name} is not available for the external call.");
    }
}
