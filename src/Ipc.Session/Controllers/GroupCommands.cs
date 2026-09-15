using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
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
