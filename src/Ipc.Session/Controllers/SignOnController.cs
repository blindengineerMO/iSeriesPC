using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public enum SignOnState
{
    SignOn,
    ChangePassword,
    MfaChallenge,
    Token,
    SignedIn,
    Failed,
}

public sealed class SignOnController : ISessionController, IDisposable
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
    private readonly CancellationToken _cancellationToken;
    private readonly string? _unixAccount;
    private DisplayForm? _challenge;
    private FieldEditor? _challengeEditor;
    private string? _pendingPassword;
    private DateTimeOffset _challengeExpires;
    private Timer? _challengeTimer;

    public SignOnController(IpcSystem system, SystemNameProvider? systemNameProvider = null, CancellationToken cancellationToken = default, string? unixAccount = null)
    {
        _system = system;
        _cancellationToken = cancellationToken;
        _unixAccount = unixAccount;
        var provider = systemNameProvider ?? (() => system.Config.SystemName);
        _buffer = Screens.SignOn(provider);
        _signOn = new SignOnPanel(_buffer, system.Security.PasswordPolicy.MaximumLength);
        _change = new ChangePasswordPanel(_buffer, system.Security.PasswordPolicy.MaximumLength);
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
                return State switch
                {
                    SignOnState.SignOn => SubmitSignOn(), SignOnState.ChangePassword => SubmitChange(),
                    SignOnState.MfaChallenge => SubmitFactor(), SignOnState.Token => SubmitToken(),
                    _ => new SessionEvent(State.ToString()),
                };
            case AidKey.Pf3:
                State = SignOnState.Failed;
                _signOn.Form.ClearAll();
                _change.Form?.ClearAll();
                Dispose();
                return new SessionEvent(State.ToString(), null, "Sign-on cancelled.", EndSession: true);
            case AidKey.Pf8 when State == SignOnState.SignOn:
                Dispose();
                return new SessionEvent("AccountSecurity", Next: new AccountSecurityController(_system, _cancellationToken, _unixAccount));
            case AidKey.Pf9 when State == SignOnState.SignOn:
                _signOn.Form.ClearAll();
                State = SignOnState.Token;
                PaintChallenge("SSO token", 43);
                return new SessionEvent(State.ToString());
            case AidKey.Pf6 when State == SignOnState.SignOn:
                var profile = _signOn.Form.ReadValue(UserField).Trim().ToUpperInvariant();
                if (!Ipc.Core.Objects.ObjectName.IsValid(profile))
                {
                    Screens.Status(_buffer, "Enter a user profile before changing its password.", error: true);
                    return new SessionEvent(State.ToString());
                }
                _profileName = profile;
                _signOn.Form.WriteValue(PasswordField, "");
                return BeginPasswordChange();
            case AidKey.Clear:
                if (State is SignOnState.MfaChallenge or SignOnState.Token) _challenge?.ClearAll();
                else if (State == SignOnState.ChangePassword) _change.Form?.ClearAll();
                else _signOn.Form.ClearAll();
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
            ActiveEditor()?.ApplyEdit(edit);
        }
        else if (!char.IsControl(keyPress.Character) && keyPress.Character != '\0')
        {
            ActiveEditor()?.Apply(keyPress.Character);
        }
    }

    private FieldEditor? ActiveEditor() => State switch
    {
        SignOnState.SignOn => _signOnEditor, SignOnState.ChangePassword => _changeEditor, _ => _challengeEditor,
    };

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

        var result = _system.Security.OpenSession(user, password, unixAccount: _unixAccount, terminal: true);
        _signOn.Form.WriteValue(PasswordField, "");
        _profileName = user.ToUpperInvariant();
        if (result.MustVerifyMfa) return BeginFactor(password);

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
            return BeginPasswordChange();
        }

        return SignIn(result);
    }

    private SessionEvent BeginPasswordChange()
    {
        State = SignOnState.ChangePassword;
        _buffer.ClearScreen();
        _change.Paint(_buffer);
        _changeEditor = new FieldEditor(_change.Form!);
        Screens.Status(_buffer, "Change your profile password.");
        return new SessionEvent(State.ToString());
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
            _system.Security.ChangePassword(_profileName!, current, next, _change.Form!.ReadValue(3));
        }
        catch (CpfException ex)
        {
            Screens.Status(_buffer, ex.Message, error: true);
            _change.Form!.WriteValue(NewField, "");
            _change.Form!.WriteValue(VerifyField, "");
            _change.Form!.WriteValue(3, "");
            _changeEditor?.ApplyEdit(CursorEdit.CursorRight);
            return new SessionEvent(State.ToString());
        }

        _change.Form!.ClearAll();
        var authentication = _system.Security.OpenSession(_profileName!, next, unixAccount: _unixAccount, terminal: true);
        if (authentication.MustVerifyMfa) return BeginFactor(next);
        if (!authentication.Success || authentication.MustChangePassword)
        {
            State = SignOnState.Failed;
            Screens.Status(_buffer, authentication.Reason ?? "Sign on again after changing the password.", error: true);
            return new SessionEvent(State.ToString(), EndSession: true);
        }
        return SignIn(authentication);
    }

    private SessionEvent BeginFactor(string password)
    {
        _pendingPassword = password;
        _challengeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
        _challengeTimer?.Dispose();
        _challengeTimer = new Timer(_ => Interlocked.Exchange(ref _pendingPassword, null), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        State = SignOnState.MfaChallenge;
        PaintChallenge("Authenticator or recovery code", 32);
        Screens.Status(_buffer, "Enter your code within two minutes. F3=Exit");
        return new SessionEvent(State.ToString());
    }

    private void PaintChallenge(string label, int length)
    {
        _buffer.ClearScreen();
        _buffer.MoveCursor(3, 2); _buffer.Write(label);
        _buffer.MoveCursor(22, 2); _buffer.Write("F3=Exit");
        _challenge = new DisplayForm(_buffer, new[] { new InputField { Row = 5, Column = 2, Length = length, Hidden = true } });
        _challengeEditor = new FieldEditor(_challenge);
    }

    private SessionEvent SubmitFactor()
    {
        var code = _challenge!.ReadValue(0);
        _challenge.ClearAll();
        var password = _pendingPassword;
        if (password is null || DateTimeOffset.UtcNow >= _challengeExpires)
        {
            Dispose(); State = SignOnState.Failed;
            Screens.Status(_buffer, "Challenge expired. Sign on again.", error: true);
            return new SessionEvent(State.ToString(), EndSession: true);
        }
        var result = _system.Security.OpenSession(_profileName!, password, code, _unixAccount, terminal: true);
        if (!result.Success || result.MustChangePassword)
        {
            Dispose(); State = SignOnState.Failed;
            Screens.Status(_buffer, "Verification failed. Sign on again.", error: true);
            return new SessionEvent(State.ToString(), EndSession: true);
        }
        return SignIn(result);
    }

    private SessionEvent SubmitToken()
    {
        var result = _system.Security.ResumeSession(_challenge!.ReadValue(0), _unixAccount, terminal: true);
        _challenge.ClearAll();
        if (!result.Success)
        {
            State = SignOnState.Failed;
            Screens.Status(_buffer, "Session token rejected. Sign on again.", error: true);
            return new SessionEvent(State.ToString(), EndSession: true);
        }
        return SignIn(result);
    }

    private SessionEvent SignIn(Ipc.Services.Security.SignOnResult result)
    {
        Dispose();
        State = SignOnState.SignedIn;
        try
        {
            var menu = new MenuController(_system, result.Profile!, cancellationToken: _cancellationToken,
                authSessionId: result.Session!.Id, sessionToken: result.Session.Token);
            if (menu.StartupSignOff)
            {
                menu.Dispose(); Screens.Status(_buffer, "Initial program completed. Signed off.");
                return new SessionEvent(State.ToString(), result.Profile, EndSession: true);
            }
            return new SessionEvent(State.ToString(), result.Profile, Next: menu);
        }
        catch (Exception error)
        {
            _system.Security.RevokeSession(result.Session!.Token); State = SignOnState.Failed;
            if (error is not (Ipc.Core.Messages.CpfException or ArgumentException or FormatException)) throw;
            Screens.Status(_buffer, error.Message, error: true);
            return new SessionEvent(State.ToString(), EndSession: true);
        }
    }

    public void Dispose()
    {
        _pendingPassword = null;
        _challengeTimer?.Dispose(); _challengeTimer = null;
        _signOn.Form.ClearAll(); _change.Form?.ClearAll(); _challenge?.ClearAll();
    }
}

public sealed class SignOnPanel
{
    public SignOnPanel(DisplayBuffer buffer, int passwordLength = 128)
    {
        buffer.MoveCursor(4, 1);
        buffer.Write("User . . . . . . . . . . . . :");
        buffer.MoveCursor(5, 1);
        buffer.Write("Password . . . . . . . . . . :");
        buffer.MoveCursor(21, 1);
        buffer.Write("Copyright iSeriesPC. An AS/400 experience.");
        buffer.MoveCursor(22, 1);
        buffer.Write("F3=Exit  F6=Change password  F8=Account security  F9=SSO token");

        Form = new DisplayForm(buffer, new[]
        {
            new InputField { Row = 4, Column = 29, Length = 10, TabOrder = 0, Usage = FieldUsage.AlphaNumeric },
            new InputField { Row = 5, Column = 29, Length = passwordLength, DisplayLength = 50, TrimTrailingFill = false, TabOrder = 1, Usage = FieldUsage.AlphaNumeric, Hidden = true },
        });
    }

    public DisplayForm Form { get; }
}

public sealed class ChangePasswordPanel
{
    private readonly int _passwordLength;
    public ChangePasswordPanel(DisplayBuffer buffer, int passwordLength = 128)
    {
        _passwordLength = passwordLength;
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
        buffer.MoveCursor(8, 1);
        buffer.Write("MFA/recovery code (if set) . :");
        Form = new DisplayForm(buffer, new[]
        {
            new InputField { Row = 5, Column = 29, Length = _passwordLength, DisplayLength = 50, TrimTrailingFill = false, TabOrder = 0, Hidden = true },
            new InputField { Row = 6, Column = 29, Length = _passwordLength, DisplayLength = 50, TrimTrailingFill = false, TabOrder = 1, Hidden = true },
            new InputField { Row = 7, Column = 29, Length = _passwordLength, DisplayLength = 50, TrimTrailingFill = false, TabOrder = 2, Hidden = true },
            new InputField { Row = 8, Column = 29, Length = 32, TabOrder = 3, Hidden = true },
        });
    }
}
