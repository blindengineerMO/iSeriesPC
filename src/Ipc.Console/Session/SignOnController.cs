using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public enum SignOnState
{
    SignOn,
    ChangePassword,
    SignedIn,
    Failed,
}

public sealed class SignOnController : ISessionController
{
    private const int UserField = 0;
    private const int PasswordField = 1;
    private const int CurrentField = 0;
    private const int NewField = 1;
    private const int VerifyField = 2;

    private readonly IpcSystem _system;
    private readonly DisplayBuffer _buffer;
    private readonly SignOnPanel _signOn;
    private readonly ChangePasswordPanel _change;
    private readonly FieldEditor _signOnEditor;
    private FieldEditor? _changeEditor;
    private string? _profileName;

    public SignOnController(IpcSystem system, SystemNameProvider? systemNameProvider = null)
    {
        _system = system;
        var provider = systemNameProvider ?? (() => system.Config.SystemName);
        _buffer = Screens.SignOn(provider);
        _signOn = new SignOnPanel(_buffer);
        _change = new ChangePasswordPanel(_buffer);
        _signOnEditor = new FieldEditor(_signOn.Form);
        _changeEditor = null;
    }

    public DisplayBuffer Buffer => _buffer;

    public SignOnState State { get; private set; } = SignOnState.SignOn;

    public SignOnPanel SignOn => _signOn;

    public SessionEvent Handle(KeyPress keyPress)
    {
        if (State is SignOnState.SignedIn or SignOnState.Failed)
        {
            return new SessionEvent(State.ToString());
        }

        switch (keyPress.Aid)
        {
            case AidKey.Enter:
                return State == SignOnState.SignOn ? SubmitSignOn() : SubmitChange();
            case AidKey.Pf3:
                State = SignOnState.Failed;
                return new SessionEvent(State.ToString(), null, "Sign-on cancelled.", EndSession: true);
            case AidKey.Clear:
                _signOn.Form.ClearAll();
                Screens.Status(_buffer, "Ready.");
                return new SessionEvent(State.ToString());
            default:
                ApplyEdit(keyPress);
                return new SessionEvent(State.ToString());
        }
    }

    private void ApplyEdit(KeyPress keyPress)
    {
        if (keyPress.Edit is { } edit && edit != CursorEdit.None)
        {
            (State == SignOnState.SignOn ? (FieldEditor?)_signOnEditor : _changeEditor)?.ApplyEdit(edit);
        }
        else if (!char.IsControl(keyPress.Character) && keyPress.Character != '\0')
        {
            (State == SignOnState.SignOn ? (FieldEditor?)_signOnEditor : _changeEditor)?.Apply(keyPress.Character);
        }
    }

    private SessionEvent SubmitSignOn()
    {
        var user = _signOn.Form.ReadValue(UserField).Trim();
        var password = _signOn.Form.ReadValue(PasswordField);
        if (user.Length == 0)
        {
            Screens.Status(_buffer, "User profile is required.", error: true);
            return new SessionEvent(State.ToString());
        }

        if (password.Length == 0)
        {
            Screens.Status(_buffer, "Password is required.", error: true);
            return new SessionEvent(State.ToString());
        }

        var result = _system.Security.Authenticate(user, password);
        _signOn.Form.WriteValue(PasswordField, "");

        if (!result.Success)
        {
            State = SignOnState.Failed;
            Screens.Status(_buffer, result.Reason ?? "Sign-on failed.", error: true);
            _signOnEditor.ApplyEdit(CursorEdit.NextField);
            return new SessionEvent(State.ToString(), null, result.Reason, EndSession: true);
        }

        _profileName = user.ToUpperInvariant();

        if (result.MustChangePassword)
        {
            State = SignOnState.ChangePassword;
            _buffer.ClearScreen();
            _change.Paint(_buffer);
            _changeEditor = new FieldEditor(_change.Form!);
            Screens.Status(_buffer, "Change your password.");
            return new SessionEvent(State.ToString(), result.Profile);
        }

        return SignIn(result.Profile);
    }

    private SessionEvent SubmitChange()
    {
        var current = _change.Form!.ReadValue(CurrentField);
        var next = _change.Form!.ReadValue(NewField);
        var verify = _change.Form!.ReadValue(VerifyField);

        if (next != verify)
        {
            Screens.Status(_buffer, "New passwords do not match.", error: true);
            _changeEditor?.ApplyEdit(CursorEdit.NextField);
            return new SessionEvent(State.ToString());
        }

        try
        {
            _system.Security.ChangePassword(_profileName!, current, next);
        }
        catch (CpfException ex)
        {
            Screens.Status(_buffer, ex.Message, error: true);
            _change.Form!.WriteValue(NewField, "");
            _change.Form!.WriteValue(VerifyField, "");
            _changeEditor?.ApplyEdit(CursorEdit.CursorRight);
            return new SessionEvent(State.ToString());
        }

        var profile = _system.Security.Profiles.TryGet(_profileName!);
        return SignIn(profile);
    }

    private SessionEvent SignIn(UserProfile? profile)
    {
        State = SignOnState.SignedIn;
        var menu = new MenuController(_system, profile!);
        return new SessionEvent(State.ToString(), profile, Next: menu);
    }
}

public sealed class SignOnPanel
{
    public SignOnPanel(DisplayBuffer buffer)
    {
        buffer.MoveCursor(4, 1);
        buffer.Write("User . . . . . . . . . . . . :");
        buffer.MoveCursor(5, 1);
        buffer.Write("Password . . . . . . . . . . :");
        buffer.MoveCursor(21, 1);
        buffer.Write("Copyright iSeriesPC. An AS/400 experience.");

        Form = new DisplayForm(buffer, new[]
        {
            new InputField { Row = 4, Column = 29, Length = 10, TabOrder = 0, Usage = FieldUsage.AlphaNumeric },
            new InputField { Row = 5, Column = 29, Length = 10, TabOrder = 1, Usage = FieldUsage.AlphaNumeric, Hidden = true },
        });
    }

    public DisplayForm Form { get; }
}

public sealed class ChangePasswordPanel
{
    public ChangePasswordPanel(DisplayBuffer buffer)
    {
    }

    public DisplayForm? Form { get; private set; }

    public void Paint(DisplayBuffer buffer)
    {
        buffer.MoveCursor(3, 1);
        buffer.Write("Change Password");
        buffer.MoveCursor(5, 1);
        buffer.Write("Current password . . . . . . :");
        buffer.MoveCursor(6, 1);
        buffer.Write("New password . . . . . . . . :");
        buffer.MoveCursor(7, 1);
        buffer.Write("Verify new password . . . . . :");
        Form = new DisplayForm(buffer, new[]
        {
            new InputField { Row = 5, Column = 29, Length = 10, TabOrder = 0, Usage = FieldUsage.AlphaNumeric, Hidden = true },
            new InputField { Row = 6, Column = 29, Length = 10, TabOrder = 1, Usage = FieldUsage.AlphaNumeric, Hidden = true },
            new InputField { Row = 7, Column = 29, Length = 10, TabOrder = 2, Usage = FieldUsage.AlphaNumeric, Hidden = true },
        });
    }
}