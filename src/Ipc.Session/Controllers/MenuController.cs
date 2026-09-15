using Ipc.Cl.Commands;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Terminal;
using Ipc.Session;

namespace Ipc.Console.Session;

public sealed partial class MenuController : ISessionController, IDisposable
{
    private const int SelectionField = 0;
    private const int CommandRow = 23;

    private readonly IpcSystem _system;
    private readonly UserProfile _profile;
    private readonly DisplayBuffer _buffer;
    private readonly DisplayForm _form;
    private readonly FieldEditor _editor;
    private readonly ExecutionSession _execution;
    private readonly CancellationTokenSource _terminalStop;
    private readonly TerminalGroup _terminalGroup;
    public bool StartupSignOff { get; private set; }
    private volatile bool _disconnected;
    private readonly Stack<string> _stack = new();

    private ApplicationMenu _current;
    private readonly Job _job;
    private volatile bool _disposed;
    private Ipc.Dsp.DisplayFileSession? _display;
    private PanelRequest? _displayRequest;
    private readonly string? _sessionToken;

    public MenuController(IpcSystem system, UserProfile profile, string? startMenu = null, CancellationToken cancellationToken = default,
        string? authSessionId = null, string? sessionToken = null)
        : this(system, profile, startMenu, cancellationToken, authSessionId, sessionToken, null, null) { }

    private MenuController(IpcSystem system, UserProfile profile, string? startMenu, CancellationToken cancellationToken,
        string? authSessionId, string? sessionToken, Job? attached, TerminalGroup? group)
    {
        _system = system;
        _profile = profile;
        _sessionToken = sessionToken;
        _buffer = new DisplayBuffer();
        _form = new DisplayForm(_buffer, new[]
        {
            new InputField
            {
                Row = CommandRow, Column = 12, Length = 32768, DisplayLength = 66, TabOrder = 0, Usage = FieldUsage.AlphaNumeric,
            },
        });
        _editor = new FieldEditor(_form);
        _terminalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try { _execution = attached is null ? new ExecutionSession(system, profile, _terminalStop.Token, authSessionId) : new ExecutionSession(system, attached, _terminalStop.Token); }
        catch { _terminalStop.Dispose(); throw; }
        _job = _execution.Job;
        try
        {
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(profile.Name, _job.Key, _job.AuthSessionId);
            using var locks = new Ipc.Services.Work.JobLockStore(system.Connections).EnterCommand(_job.Key, cancellationToken);
            if (attached is null) RunInitialProgram(profile.InitialProgram);
            StartupSignOff = attached is null && profile.InitialMenu?.Equals("*SIGNOFF", StringComparison.OrdinalIgnoreCase) == true;
            _current = StartupSignOff ? new ApplicationMenu { Name = "SIGNOFF", Title = "Initial program completed" } : ResolveStart(startMenu ?? profile.InitialMenu ?? MenuNames.Main);
        }
        catch { _execution.Dispose(); _terminalStop.Dispose(); throw; }
        _terminalGroup = group ?? new TerminalGroup(this);
        Paint();
    }

    private void RunInitialProgram(string? program)
    {
        program = program?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(program) || program is "*NONE" or "QCMD" or "QSYS/QCMD") return;
        var name = Ipc.Core.Objects.QualifiedName.Parse(program, "*LIBL");
        var result = _execution.Execute("CALL PGM(" + name + ")");
        if (result.IsError) throw new CpfException("CPF1164", "Initial program failed: " + result.Message);
    }

    private DisplayBuffer OwnBuffer => _help?.Buffer ?? _commandPrompt?.Buffer ?? _designer?.Buffer ?? _display?.Buffer ?? _work?.Buffer ?? _buffer;
    public DisplayBuffer Buffer => _terminalGroup.Buffer;

    public string CurrentMenu => $"{_terminalGroup.Active._current.Library}/{_terminalGroup.Active._current.Name}";

    public JobKey JobKey => _terminalGroup.Active._job.Key;
    public CancellationToken CancellationToken => _terminalGroup.CancellationToken;

    public void RequestDisconnect()
    {
        _terminalGroup.Disconnect();
    }

    public void Dispose() => EndSession(_disconnected ? JobCompletion.Abnormal : JobCompletion.Normal,
        _disconnected ? "Terminal disconnected." : "Interactive session ended.");

    public void EndSession(JobCompletion completion, string message)
    {
        if (ReferenceEquals(_terminalGroup.Root, this)) { _terminalGroup.End(completion, message); return; }
        EndOwn(completion, message);
    }
    private void EndOwn(JobCompletion completion, string message)
    {
        if (_disposed) return;
        _disposed = true;
        try { _execution.End(completion, message); }
        finally
        {
            _terminalStop.Dispose();
        }
    }

    public SessionEvent Handle(KeyPress keyPress) => _terminalGroup.Handle(keyPress);

    private SessionEvent HandleOwn(KeyPress keyPress)
    {
        using var identity = Ipc.Services.Events.OperationIdentity.Enter(_profile.Name, _job.Key, _job.AuthSessionId);
        if (_job.AuthSessionId is { } session && !_system.Security.IsSessionActive(session, _profile.Name))
        {
            Screens.Status(_buffer, "Session expired or revoked. Sign on again.", error: true);
            return new SessionEvent("Failed", EndSession: true);
        }
        if (_help is not null) return HandleHelp(keyPress);
        if (_commandPrompt is not null) return HandleCommandPrompt(keyPress);
        if (_designer is not null) return HandleDesigner(keyPress);
        if (_display is not null)
        {
            try
            {
                using var locks = new Ipc.Services.Work.JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
                _ = _system.Objects.GetRequired(_displayRequest!.Library, _displayRequest.File, Ipc.Core.Objects.ObjectType.File);
                var response = _display.Handle(keyPress);
                if (response.Help is { } help) return OpenHelp(help);
                if (response.HelpId is { } helpId) return OpenBuiltinHelp(helpId, new[] { "Display help context: " + helpId, "This record has no panel-group binding. Add HLPPNLGRP and HLPARA to provide application help." });
                if (response.Accepted)
                {
                    _display = null; _displayRequest = null;
                    return ReturnToWorkOrMenu("Panel returned " + response.Aid + ".");
                }
                return new SessionEvent("DisplayPanel", Message: response.Error);
            }
            catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
            {
                _display = null; _displayRequest = null;
                return ReturnToWorkOrMenu(error.Message, error: true);
            }
        }
        if (_work is not null) return HandleWorkWith(keyPress);
        try
        {
            var live = _system.Menus.Get(_current.Name, _current.Library);
            if (MenuDefinition.Serialize(live) != MenuDefinition.Serialize(_current))
            {
                var pending = _form.ReadValue(SelectionField); var cursor = _buffer.Cursor;
                _current = live; Paint(); _form.WriteValue(SelectionField, pending); _buffer.MoveCursor(cursor.Row, cursor.Column);
            }
        }
        catch (CpfException error)
        {
            _buffer.ClearScreen(); Screens.Status(_buffer, error.Message, error: true);
            return new("Failed", EndSession: true, Message: error.Message);
        }
        switch (keyPress.Aid)
        {
            case AidKey.Pf1:
                return OpenContextHelp();
            case AidKey.Pf4:
                return OpenCommandPrompt(_form.ReadValue(SelectionField));
            case AidKey.Enter:
                return Submit();
            case AidKey.Clear:
                _form.ClearAll();
                Screens.Status(_buffer, "Selection cleared.");
                return new SessionEvent("Menu");
            case AidKey.Pf3:
            case AidKey.Pf12:
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
        name = name.Trim().ToUpperInvariant();
        if (name == "*MAIN") name = MenuNames.Main;
        var menu = _system.FindMenu(name, _job);
        if (menu is not null)
        {
            return menu;
        }

        throw new CpfException("CPF9824", $"Menu {name} not found.");
    }

    private SessionEvent Submit()
    {
        var value = _form.ReadValue(SelectionField).Trim();
        _form.WriteValue(SelectionField, "");

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

    private SessionEvent Select(MenuOption option, ApplicationMenu? source = null)
    {
        var required = Ipc.Core.Security.SpecialAuthorities.Parse(option.RequiredAuthority);
        if (required != Ipc.Core.Security.SpecialAuthority.None)
            new Ipc.Services.Security.ServiceAuthorization(_system.Connections).RequireSpecial(required, allowAdopted: false);
        switch (option.Kind)
        {
            case MenuOptionKind.SignOff:
            case MenuOptionKind.Exit:
                Screens.Status(_buffer, $"Signed off. Profile: {_profile.Name}.");
                return new SessionEvent("Menu", Profile: _profile, EndSession: true, Message: "Sign off.");
            case MenuOptionKind.Prompt:
                var response = OpenCommandPrompt(option.Target == "DSPMENU" ? "GO" : option.Target);
                if (_commandPrompt is not null)
                {
                    source ??= _current;
                    _promptOption = (source.Library ?? "QSYS", source.Name, option.Number, option.Target);
                }
                return response;
            case MenuOptionKind.SubMenu:
            {
                var target = _system.FindMenu(option.Target, _job);
                if (target is null)
                {
                    Screens.Status(_buffer, $"{option.Text} is not available.", error: true);
                    return new SessionEvent("Menu");
                }

                if (_stack.Count >= 64) throw new CpfException("IPC0135", "Menu nesting limit reached; return to an earlier menu.");
                ClearWorkLists(); _stack.Push(CurrentMenu);
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

    private SessionEvent RunCommand(string line) => ApplyCommandResult(_execution.Execute(line));

    private SessionEvent ApplyCommandResult(CommandResult result)
    {
        if (result.Outcome == CommandOutcome.Error)
        {
            if (_work is not null) _work.Error(result.Message ?? "Command failed.");
            else Screens.Status(_buffer, result.Message ?? "Command failed.", error: true);
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

        if (result.Outcome == CommandOutcome.GroupJob && result.GroupJob is { } group) return _terminalGroup.Command(group);

        if (result.Outcome == CommandOutcome.ScreenDesigner && result.Designer is { } designer) return OpenDesigner(designer);

        if (result.Outcome == CommandOutcome.DisplayHelp && result.Help is { } help) return OpenHelp(help);

        if (result.Outcome == CommandOutcome.DisplayPanel && result.Panel is { } request)
        {
            var definition = new Ipc.Dsp.DisplayFileStore(_system.Connections).Load(request.Library, request.File);
            var panel = new Ipc.Dsp.DisplayFileSession(definition, (id, file) =>
            {
                var key = Ipc.Core.Objects.QualifiedName.Parse(file, "*LIBL");
                var library = _system.SearchLibraries(_job, key.Library).FirstOrDefault(l => _system.Objects.Exists(l, key.Name.Value, Ipc.Core.Objects.ObjectType.MessageFile))
                    ?? throw new CpfException("CPF2401", "Display message file not found.");
                return new Ipc.Services.Messages.MessageDescriptionStore(_system.Connections).Get(library, key.Name.Value, id);
            });
            panel.Write(request.Record, new Dictionary<string, object?>());
            _display = panel; _displayRequest = request;
            return new SessionEvent("DisplayPanel");
        }

        if (result.WorkList is { } workList) return OpenWorkList(workList);
        if (result.Listing is { } rows)
            return OpenWorkList(new(result.Message ?? "Command output", OutputRows(rows)));
        if (_work is not null)
        {
            _work.Completed(result.Message); RefreshWork(); return new("WorkWith");
        }
        Screens.Status(_buffer, result.Message ?? "Command completed.");
        return new SessionEvent("Menu");
    }

    private SessionEvent ShowMenu(string? name, string? library)
    {
        if (name is null)
        {
            Screens.Status(_buffer, "No menu name was supplied.", error: true);
            return new SessionEvent("Menu");
        }

        var target = _system.FindMenu(name, _job, library);
        if (target is null)
        {
            Screens.Status(_buffer, $"Menu {name} not found.", error: true);
            return new SessionEvent("Menu");
        }

        if (_stack.Count >= 64) throw new CpfException("IPC0135", "Menu nesting limit reached; return to an earlier menu.");
        ClearWorkLists(); _stack.Push(CurrentMenu);
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
        var menu = _system.FindMenu(name, _job, library);
        if (menu is not null)
        {
            _current = menu;
        }

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
            if (_editor.Apply(keyPress.Character) is EditingResult.Full or EditingResult.Rejected)
                Screens.Status(_buffer, "Command is full or the character is not supported.", error: true);
        }
    }

    private void Paint()
    {
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
