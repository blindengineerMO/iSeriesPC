using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    internal CommandResult ExecuteTerminalControl(string line)
    {
        // The program remains paused. Only immutable, verified built-in group
        // and job-information controls may run here; a shadowing CPP must never re-enter its interpreter.
        try
        {
            var call = CommandParser.Parse(line);
            var key = ResolveCommandObject(call.Name);
            var definition = new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
            if (definition.Builtin is not ("CHGGRPA" or "TFRGRPJOB" or "ENDGRPJOB" or "TFRSECJOB" or "DSPJOB"))
                return CommandResult.Error("IPC0123: A program is waiting for display input.");
            var canonical = new CommandCall { Name = definition.Builtin, Keywords = call.Keywords, Positional = call.Positional };
            if (Ipc.Cl.Compatibility.BuiltinContract.Validate(canonical) is { } error) return CommandResult.Error(error);
            var bound = Ipc.Cl.Definitions.CommandBinder.Bind(definition, canonical);
            if (definition.Builtin == "DSPJOB") return _catalog.Execute(bound.Call);
            return ExecuteGroupCommand(bound.Call);
        }
        catch (CpfException error) { return CommandResult.Error(error.Message); }
        catch (Ipc.Cl.Parsing.ClParseException error) { return CommandResult.Error(error.Message); }
        catch (InvalidDataException error) { return CommandResult.Error("IPC0006: " + error.Message); }
    }

    private void RegisterGroupCommands()
    {
        _catalog.Register("CHGGRPA", ExecuteGroupCommand);
        _catalog.Register("TFRGRPJOB", ExecuteGroupCommand);
        _catalog.Register("ENDGRPJOB", ExecuteGroupCommand);
        _catalog.Register("TFRSECJOB", ExecuteGroupCommand);
    }
    private CommandResult ExecuteGroupCommand(CommandCall call)
    {
        if (_job?.Type != JobType.Interactive || OperationIdentity.Current?.CallStack.Count > 0)
            throw new CpfException("IPC0131", "Group transfer commands require the interactive command line.");
        var name = CommandParser.Unquote(call.GetOption("GRPJOB") ?? (call.Name == "ENDGRPJOB" ? "*CURRENT" : "*SELECT")).ToUpperInvariant();
        var program = CommandParser.Unquote(call.GetOption("INLGRPPGM") ?? "QCMD").ToUpperInvariant();
        if (program != "QCMD") _ = QualifiedName.Parse(program, "*LIBL");
        if (name is not ("*SELECT" or "*CURRENT" or "*PRV") && !ObjectName.IsValid(name)) throw new CpfException("IPC0131", "Invalid group job name.");
        if (call.Name == "CHGGRPA" && !ObjectName.IsValid(name)) throw new CpfException("IPC0131", "CHGGRPA requires a group name.");
        return new() { Outcome = CommandOutcome.GroupJob, GroupJob = new(call.Name, name, program, CommandParser.Unquote(call.GetOption("TEXT"))) };
    }
}
