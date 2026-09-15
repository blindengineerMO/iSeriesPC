using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Dsp;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class MenuController
{
    private HelpSession? _help;
    private SessionEvent HandleHelp(KeyPress key)
    {
        try
        {
            using var locks = new JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
            if (_help!.Handle(key)) { _help = null; return new(_commandPrompt is not null ? "CommandPrompt" : _designer is not null ? "ScreenDesigner" : _display is not null ? "DisplayPanel" : _work is not null ? "WorkWith" : "Menu"); }
            return new("Help");
        }
        catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
        {
            _help = null; Screens.Status(Buffer, error.Message, error: true); return new(_commandPrompt is not null ? "CommandPrompt" : _designer is not null ? "ScreenDesigner" : _display is not null ? "DisplayPanel" : _work is not null ? "WorkWith" : "Menu", Message: error.Message);
        }
    }
    private HelpDefinition LoadHelp(string group)
    {
        var key = QualifiedName.Parse(group, "*LIBL");
        var library = _system.SearchLibraries(_job, key.Library).FirstOrDefault(l => _system.Objects.Exists(l, key.Name.Value, ObjectType.PanelGroup))
            ?? throw new CpfException("CPF9801", "Help panel group not found: " + group);
        return new HelpPanelStore(_system.Connections, _system.ObjectSigning).Load(library, key.Name.Value);
    }
    private SessionEvent OpenHelp(HelpRequest request)
    {
        try
        {
            using var locks = new JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
            var buffer = Buffer;
            _help = new(request.PanelGroup, request.Module, LoadHelp, buffer.Rows, buffer.Columns); return new("Help");
        }
        catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
        { Screens.Status(Buffer, error.Message, error: true); return new(_commandPrompt is not null ? "CommandPrompt" : _designer is not null ? "ScreenDesigner" : _display is not null ? "DisplayPanel" : _work is not null ? "WorkWith" : "Menu", Message: error.Message); }
    }
    private SessionEvent OpenBuiltinHelp(string title, IReadOnlyList<string> paragraphs)
    {
        var definition = new HelpDefinition { Name = "IPCHELP", Modules = new() { new HelpModule { Name = "CONTEXT", Title = title,
            Blocks = paragraphs.Select(p => new HelpBlock { Spans = new() { new(p) } }).ToList() } } };
        var buffer = Buffer; _help = new("*BUILTIN", "CONTEXT", _ => definition, buffer.Rows, buffer.Columns); return new("Help");
    }
    private SessionEvent OpenContextHelp()
    {
        using var locks = new JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
        var entry = _form.ReadValue(SelectionField).Trim();
        var context = _current.Name; string? command = null;
        if (entry.Length > 0 && !int.TryParse(entry, out _)) { command = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant(); context = command; }
        if (command is not null && _execution.CommandMetadata(command).Help is { } boundHelp) return OpenHelp(boundHelp);
        var group = command is null ? _current.Library + "/" + context : context;
        if (ObjectName.IsValid(context))
        {
            var key = QualifiedName.Parse(group, "*LIBL");
            var library = _system.SearchLibraries(_job, key.Library).FirstOrDefault(l => _system.Objects.Exists(l, key.Name.Value, ObjectType.PanelGroup));
            if (library is not null) return OpenHelp(new(library + "/" + context, context));
        }
        if (command is not null) return OpenBuiltinHelp(command + " help", _execution.DescribeCommand(command));
        var paragraphs = new List<string> { "Enter an option number or a CL command. F1 opens help for the command currently typed without executing it. F3 returns to the previous menu." };
        paragraphs.AddRange(_current.Options.Select(o => o.Number + ". " + o.Text));
        return OpenBuiltinHelp(_current.Title + " help", paragraphs);
    }
}
