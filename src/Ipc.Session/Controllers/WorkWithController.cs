using Ipc.Cl.Commands;
using Ipc.Core.Menu;
using Ipc.Dsp;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class MenuController
{
    private WorkWithSession? _work;
    private readonly Stack<WorkWithSession> _workStack = new();
    private SessionEvent ReturnToWorkOrMenu(string? message = null, bool error = false)
    {
        if (_work is not null)
        {
            if (error) _work.Error(message ?? "Action failed."); else { _work.Completed(message); RefreshWork(); }
            return new("WorkWith", Message: message);
        }
        if (message is not null) Screens.Status(_buffer, message, error: error);
        return new("Menu", Message: message);
    }
    private static IReadOnlyList<WorkRow> OutputRows(IReadOnlyList<string> lines)
    {
        var rows = new List<WorkRow>();
        foreach (var line in lines)
            for (var offset = 0; offset < Math.Max(1, line.Length); offset += 74)
            {
                if (rows.Count == 9999)
                {
                    rows.Add(new("limit", "Output exceeds 9999 screen rows; narrow the command filter.", Array.Empty<WorkAction>()));
                    return rows;
                }
                rows.Add(new(rows.Count.ToString(), line.Substring(offset, Math.Min(74, line.Length - offset)), Array.Empty<WorkAction>()));
            }
        return rows;
    }
    private SessionEvent OpenWorkList(WorkList list)
    {
        if (_workStack.Count >= 16) { Screens.Status(OwnBuffer, "Close a work screen before opening another.", error: true); return new("WorkWith"); }
        var next = new WorkWithSession(list);
        if (_work is not null) { _work.Completed(null); _workStack.Push(_work); }
        _work = next; return new("WorkWith");
    }
    private SessionEvent OpenCommandChooser() => OpenWorkList(new("Select a command",
        _execution.AvailableCommands.Select(name => new WorkRow(name, name, new[] { new WorkAction("1", "Prompt", name, Prompt: true) })).ToArray()));
    private SessionEvent HandleWorkWith(KeyPress key)
    {
        var response = _work!.Handle(key);
        if (response.Close)
        {
            _work = _workStack.TryPop(out var previous) ? previous : null;
            if (_work is not null) RefreshWork(); return new(_work is null ? "Menu" : "WorkWith");
        }
        if (response.Help) return OpenBuiltinHelp(_work.Definition.Title + " help", new[] {
            "Enter an option beside one row and press Enter. Page keys move through the complete list. F5 reloads lists that support refresh.",
            "F9 selects the command line. F4 prompts its command; a blank command opens command selection. F3 or F12 returns to the previous screen.",
            "Destructive row actions show a confirmation screen. Commands and changes use your current job's authority." });
        if (response.Refresh) { RefreshWork(); return new("WorkWith"); }
        if (response.Action is { } action)
        {
            if (action.Details is { } details) return OpenWorkList(new(action.Text, OutputRows(details)));
            return action.Prompt ? OpenCommandPrompt(action.Command) : RunCommand(action.Command);
        }
        return new("WorkWith");
    }
    private void RefreshWork()
    {
        if (_work?.Definition.RefreshCommand is not { } command) return;
        var result = _execution.Execute(command);
        if (result.IsError) { _work.Error(result.Message ?? "Refresh failed."); return; }
        if (result.WorkList is { } list) _work.Replace(list);
        else _work.Error("The command did not return a refreshable work list.");
    }
    private void ClearWorkLists() { _work = null; _workStack.Clear(); }
}
