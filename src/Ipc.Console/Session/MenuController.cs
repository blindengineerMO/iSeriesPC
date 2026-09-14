using Ipc.Cl.Commands;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed class MenuController : ISessionController
{
    private const int SelectionField = 0;
    private const int CommandRow = 23;

    private readonly IpcSystem _system;
    private readonly UserProfile _profile;
    private readonly DisplayBuffer _buffer;
    private readonly DisplayForm _form;
    private readonly FieldEditor _editor;
    private readonly CommandService _commands;
    private readonly Stack<string> _stack = new();

    private ApplicationMenu _current;
    private bool _prompting;
    private IReadOnlyList<string>? _listing;

    public MenuController(IpcSystem system, UserProfile profile, string? startMenu = null)
    {
        _system = system;
        _profile = profile;
        _buffer = new DisplayBuffer();
        _form = new DisplayForm(_buffer, new[]
        {
            new InputField
            {
                Row = CommandRow, Column = 12, Length = 66, TabOrder = 0, Usage = FieldUsage.AlphaNumeric,
            },
        });
        _editor = new FieldEditor(_form);
        _commands = new CommandService(system);
        _current = ResolveStart(startMenu ?? profile.InitialMenu ?? MenuNames.Main);
        Paint();
    }

    public DisplayBuffer Buffer => _buffer;

    public string CurrentMenu => $"{_current.Library}/{_current.Name}";

    public SessionEvent Handle(KeyPress keyPress)
    {
        switch (keyPress.Aid)
        {
            case AidKey.Enter:
                return Submit();
            case AidKey.Clear:
                _form.ClearAll();
                Screens.Status(_buffer, "Selection cleared.");
                return new SessionEvent("Menu");
            case AidKey.Pf3:
            case AidKey.Pa1:
            case AidKey.Pa2:
            case AidKey.Pa3:
                return Back();
            default:
                ApplyEdit(keyPress);
                return new SessionEvent("Menu");
        }
    }

    private ApplicationMenu ResolveStart(string name)
    {
        var menu = _system.Menus.TryGet(name);
        if (menu is not null)
        {
            return menu;
        }

        var fallback = _system.Menus.TryGet(MenuNames.Main);
        return fallback ?? throw new CpfException("CPF9824", $"Menu {name} not found.");
    }

    private SessionEvent Submit()
    {
        var value = _form.ReadValue(SelectionField).Trim();
        _form.WriteValue(SelectionField, "");

        if (_prompting)
        {
            return CompletePrompt(value);
        }

        if (value.Length == 0)
        {
            Screens.Status(_buffer, "Enter a selection.");
            return new SessionEvent("Menu");
        }

        if (!int.TryParse(value, out _))
        {
            return RunCommand(value);
        }

        var option = _current.Find(value);
        if (option is null)
        {
            Screens.Status(_buffer, $"Option {value} is not valid.", error: true);
            return new SessionEvent("Menu");
        }

        return Select(option);
    }

    private SessionEvent CompletePrompt(string value)
    {
        _prompting = false;
        if (value.Length == 0)
        {
            Screens.Status(_buffer, "Enter a menu name.");
            return new SessionEvent("Menu");
        }

        var target = _system.Menus.TryGet(value.ToUpperInvariant());
        if (target is null)
        {
            Screens.Status(_buffer, $"Menu {value} not found.", error: true);
            return new SessionEvent("Menu");
        }

        _stack.Push(CurrentMenu);
        _current = target;
        Paint();
        return new SessionEvent("Menu");
    }

    private SessionEvent Select(MenuOption option)
    {
        switch (option.Kind)
        {
            case MenuOptionKind.SignOff:
            case MenuOptionKind.Exit:
                Screens.Status(_buffer, $"Signed off. Profile: {_profile.Name}.");
                return new SessionEvent("Menu", Profile: _profile, EndSession: true, Message: "Sign off.");
            case MenuOptionKind.Prompt:
                _prompting = true;
                Screens.Status(_buffer, "Type the name of a menu to display and press Enter.");
                return new SessionEvent("Menu");
            case MenuOptionKind.SubMenu:
            {
                var target = _system.Menus.TryGet(option.Target.ToUpperInvariant());
                if (target is null)
                {
                    Screens.Status(_buffer, $"{option.Text} is not available.", error: true);
                    return new SessionEvent("Menu");
                }

                _stack.Push(CurrentMenu);
                _current = target;
                Paint();
                return new SessionEvent("Menu");
            }
            case MenuOptionKind.Command:
                return RunCommand(option.Target);
            default:
                return new SessionEvent("Menu");
        }
    }

    private SessionEvent RunCommand(string line)
    {
        var result = _commands.Execute(line);
        if (result.Outcome == CommandOutcome.Error)
        {
            Screens.Status(_buffer, result.Message ?? "Command failed.", error: true);
            return new SessionEvent("Menu");
        }

        if (result.Outcome == CommandOutcome.SignOff)
        {
            Screens.Status(_buffer, $"Signed off. Profile: {_profile.Name}.");
            return new SessionEvent("Menu", Profile: _profile, EndSession: true, Message: "Sign off.");
        }

        if (result.Outcome == CommandOutcome.GoMenu)
        {
            return ShowMenu(result.MenuName, result.MenuLibrary);
        }

        Screens.Status(_buffer, result.Message ?? "Command completed.");
        if (result.Listing is { Count: > 0 } listing)
        {
            _listing = listing;
            PaintListing();
        }
        else if (_listing is not null)
        {
            _listing = null;
            _buffer.ClearRegion(5, 1, ListingRows, _buffer.Columns);
        }

        return new SessionEvent("Menu");
    }

    private const int ListingRows = 15;

    private void PaintListing()
    {
        _buffer.ClearRegion(5, 1, ListingRows, _buffer.Columns);
        var row = 5;
        foreach (var line in _listing!)
        {
            if (row > 5 + ListingRows - 1)
            {
                break;
            }

            _buffer.MoveCursor(row, 1);
            _buffer.Write(line.Length > _buffer.Columns - 2 ? line[..(_buffer.Columns - 2)] : line);
            row++;
        }
    }

    private SessionEvent ShowMenu(string? name, string? library)
    {
        if (name is null)
        {
            Screens.Status(_buffer, "No menu name was supplied.", error: true);
            return new SessionEvent("Menu");
        }

        var target = _system.Menus.TryGet(name, library);
        if (target is null)
        {
            Screens.Status(_buffer, $"Menu {name} not found.", error: true);
            return new SessionEvent("Menu");
        }

        _stack.Push(CurrentMenu);
        _current = target;
        Paint();
        return new SessionEvent("Menu");
    }

    private SessionEvent Back()
    {
        if (_stack.Count == 0)
        {
            Screens.Status(_buffer, $"Signed off. Profile: {_profile.Name}.");
            return new SessionEvent("Menu", Profile: _profile, EndSession: true, Message: "Sign off.");
        }

        var location = _stack.Pop();
        var parts = location.Split('/', 2);
        var library = parts[0];
        var name = parts[1];
        var menu = _system.Menus.TryGet(name, library);
        if (menu is not null)
        {
            _current = menu;
        }

        _prompting = false;
        Paint();
        return new SessionEvent("Menu");
    }

    private void ApplyEdit(KeyPress keyPress)
    {
        if (keyPress.Edit is { } edit && edit != CursorEdit.None)
        {
            _editor.ApplyEdit(edit);
        }
        else if (!char.IsControl(keyPress.Character) && keyPress.Character != '\0')
        {
            _editor.Apply(keyPress.Character);
        }
    }

    private void Paint()
    {
        _listing = null;
        _buffer.ClearScreen();

        if (_current.Title.Length > 0)
        {
            var left = Math.Max(1, (_buffer.Columns - _current.Title.Length + 1) / 2);
            _buffer.MoveCursor(1, left);
            _buffer.Write(_current.Title, DisplayAttribute.HighIntensity);
        }

        _buffer.MoveCursor(3, 1);
        _buffer.WriteLine("Select one of the following:");

        var row = 4;
        foreach (var option in _current.Options)
        {
            if (row > 20)
            {
                break;
            }

            _buffer.MoveCursor(row, 1);
            _buffer.Write($"  {option.Number.PadLeft(2)}. {option.Text}");
            row++;
        }

        _buffer.MoveCursor(CommandRow, 1);
        _buffer.Write("Selection:");
        Screens.Status(_buffer, "Enter an option number. F3=sign off  F12=cancel");
    }
}