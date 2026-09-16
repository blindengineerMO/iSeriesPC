using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    internal CommandResult RunProgram(string target, IReadOnlyList<object?> parameters)
    {
        using var identity = EnterJobIdentity(); _messages.Clear();
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            // The batch routing adapter shares CALL authority and program execution,
            // while keeping already-typed arguments out of command-string parsing.
            _ = new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Load("QSYS", "CALL");
            return ExecuteProgram(target, parameters);
        }
        catch (Ipc.Core.Messages.CpfException error) { return CommandResult.Error(error.Message); }
        catch (ClRuntimeException error) { return CommandResult.Error(error.Message, error.MessageId); }
        catch (ClCompileException error) { return CommandResult.Error(error.Message); }
    }

    private CommandResult CallFromCl(string library, string name, IReadOnlyList<ProgramArgument> arguments)
    {
        var loaded = LoadProgramObject(library, name);
        if (loaded.Api is not null) return ExecuteBuiltinProgram(loaded.Api, arguments.Cast<object?>().ToArray());
        if (loaded.External is { } native)
        {
            // The process protocol exchanges values, so overlapping native reference
            // arguments need an explicit alias-capable protocol before they can run.
            if (arguments.Distinct().Count() != arguments.Count)
                return CommandResult.Error("Native process calls do not yet support aliased reference arguments.");
            using var scope = EnterProgram(native);
            var invocation = _system.ExternalPrograms.Execute(native,
                arguments.Select(argument => (object?)argument.Constant ?? argument.ToBuffer() ?? argument.Value).ToArray(), _cancellationToken);
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
                RunRpg(rpg, BindRpgConstants(rpg, arguments.Select(argument => (object?)argument.Constant ?? argument).ToArray())); return CommandResult.Ok();
            }
            catch (RpgException error) { return CommandResult.Error(error.Message); }
        }
        return CommandResult.Error($"Program {library}/{name} is not available for the external call.");
    }

    private static IReadOnlyList<object?> BindRpgConstants(RpgProgram program, IReadOnlyList<object?> parameters)
    {
        if (!parameters.Any(value => value is ProgramConstant)) return parameters;
        var entry = RpgInterpreter.EntryParameterNames(program);
        if (entry.Count != parameters.Count) throw new ClRuntimeException("RPG entry parameter count does not match CALL.");
        return parameters.Select((value, index) =>
        {
            if (value is not ProgramConstant constant) return value;
            var field = program.FindField(entry[index]) ?? throw new ClRuntimeException("RPG entry parameter is not declared.");
            var (type, length) = field.Kind switch {
                RpgFieldKind.Character => ("*CHAR", field.Length), RpgFieldKind.Packed or RpgFieldKind.Decimal => ("*DEC", field.Length),
                RpgFieldKind.Integer or RpgFieldKind.Binary => ("*INT", field.Length <= 5 ? 2 : field.Length <= 10 ? 4 : 8),
                RpgFieldKind.Float => ("*FLT", field.Length),
                RpgFieldKind.Indicator => ("*LGL", 1), _ => throw new ClRuntimeException("Unsupported RPG CALL constant receiver layout.") };
            return (object?)ClCallArgument.Bind(constant, type, length, field.Decimals);
        }).ToArray();
    }
}
