using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Dsp;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class MenuController
{
    private CommandPromptSession? _commandPrompt;
    private (string Library, string Menu, string Number, string Target)? _promptOption;
    private SessionEvent OpenCommandPrompt(string line)
    {
        try
        {
            _promptOption = null;
            if (string.IsNullOrWhiteSpace(line)) return OpenCommandChooser();
            var initial = CommandParser.Parse(line);
            _commandPrompt = new(_execution.CommandMetadata(initial.Name), initial);
            return new("CommandPrompt");
        }
        catch (Exception error) when (error is CpfException or ClParseException or ArgumentException)
        { Screens.Status(OwnBuffer, error.Message, error: true); return new("Menu"); }
    }
    private SessionEvent HandleCommandPrompt(KeyPress key)
    {
        var response = _commandPrompt!.Handle(key);
        if (response.Help) return _execution.CommandMetadata(_commandPrompt.Name).Help is { } help ? OpenHelp(help) : OpenBuiltinHelp(_commandPrompt.Name + " help", _execution.DescribeCommand(_commandPrompt.Name));
        if (response.Cancelled) { _promptOption = null; _commandPrompt = null; return new(_work is null ? "Menu" : "WorkWith"); }
        if (response.Command is null) return new("CommandPrompt");
        if (_promptOption is { } origin)
        {
            try
            {
                var option = _system.Menus.Get(origin.Menu, origin.Library).Find(origin.Number);
                if (option is null || option.Target != origin.Target || option.Kind != Ipc.Core.Menu.MenuOptionKind.Prompt)
                    throw new CpfException("IPC0135", "Menu option changed; cancel and open its new prompt.");
                var required = Ipc.Core.Security.SpecialAuthorities.Parse(option.RequiredAuthority);
                if (required != Ipc.Core.Security.SpecialAuthority.None)
                    new Ipc.Services.Security.ServiceAuthorization(_system.Connections).RequireSpecial(required, allowAdopted: false);
            }
            catch (CpfException error) { _commandPrompt.Error(error.Message); return new("CommandPrompt"); }
        }
        try
        {
            if (_commandPrompt.Revision is { } revision && _execution.CommandMetadata(_commandPrompt.Name).Revision != revision)
            { _commandPrompt.Error("Command definition changed; cancel and open its new prompt."); return new("CommandPrompt"); }
        }
        catch (CpfException error) { _commandPrompt.Error(error.Message); return new("CommandPrompt"); }
        var result = _execution.Execute(response.Command);
        if (result.IsError) { _commandPrompt.Error(result.Message ?? "Command failed."); return new("CommandPrompt"); }
        _promptOption = null; _commandPrompt = null; _form.ClearAll(); return ApplyCommandResult(result);
    }
}
