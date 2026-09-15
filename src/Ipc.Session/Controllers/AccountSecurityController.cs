using Ipc.Core.Messages;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Terminal;

namespace Ipc.Console.Session;

/// <summary>Pre-job credential management; passwords and factor codes never enter CL command text.</summary>
public sealed class AccountSecurityController : ISessionController, IDisposable
{
    private readonly IpcSystem _system;
    private readonly CancellationToken _cancellationToken;
    private readonly string? _unixAccount;
    private readonly DisplayBuffer _buffer = new();
    private DisplayForm? _form;
    private FieldEditor? _editor;
    private MfaEnrollment? _enrollment;
    private string _stage = "Credentials";

    public AccountSecurityController(IpcSystem system, CancellationToken cancellationToken = default, string? unixAccount = null)
    {
        _system = system; _cancellationToken = cancellationToken; _unixAccount = unixAccount;
        PaintCredentials();
    }
    public DisplayBuffer Buffer => _buffer;
    // Exposes field editing, not authentication state, to terminal drivers and tests.
    public DisplayForm? Form => _form;

    public SessionEvent Handle(KeyPress key)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (key.Aid == AidKey.Pf3)
        {
            Dispose();
            return new("SignOn", Next: new SignOnController(_system, cancellationToken: _cancellationToken, unixAccount: _unixAccount));
        }
        if (key.Aid == AidKey.Clear) { _form?.ClearAll(); return new(_stage); }
        try
        {
            if (_stage == "Credentials" && key.Aid is AidKey.Enter or AidKey.Pf5 or AidKey.Pf6 or AidKey.Pf7)
            {
                var user = _form!.ReadValue(0).Trim();
                var password = _form.ReadValue(1);
                var code = _form.ReadValue(2);
                _form.WriteValue(1, ""); _form.WriteValue(2, "");
                if (key.Aid == AidKey.Pf6)
                {
                    _system.Security.DisableMfa(user, password, code, _unixAccount, terminal: true);
                    Screens.Status(_buffer, "Authenticator disabled. Existing sessions revoked.");
                }
                else if (key.Aid == AidKey.Pf7)
                {
                    var result = _system.Security.OpenSession(user, password, code, _unixAccount, terminal: true);
                    if (!result.Success || result.Session is null) throw new CpfException("IPC0102", "Password and enrolled factor are required.");
                    ShowSecret("SSO token", new[] { result.Session.Token, "Expires: " + result.Session.Expires.ToString("u"),
                        "Also expires after 30 idle minutes. Keep this token private.", "Use F9 on sign-on, or an API Bearer header." });
                }
                else
                {
                    _enrollment = _system.Security.BeginMfaEnrollment(user, password, code, _unixAccount, terminal: true);
                    _form.ClearAll(); _buffer.ClearScreen(); _stage = "Confirm";
                    Write(3, "Add this shared secret to your authenticator:");
                    Write(5, _enrollment.SharedSecret);
                    Write(7, "Enter its six-digit code. Enrollment expires in 10 minutes.");
                    Write(22, "Enter=Confirm  F3=Cancel");
                    _form = new DisplayForm(_buffer, new[] { new InputField { Row = 9, Column = 2, Length = 6, Hidden = true } });
                    _editor = new FieldEditor(_form);
                }
            }
            else if (_stage == "Confirm" && key.Aid == AidKey.Enter)
            {
                var code = _form!.ReadValue(0); _form.ClearAll();
                var confirmed = _system.Security.ConfirmMfaEnrollment(_enrollment!.EnrollmentToken, code);
                _enrollment = null;
                ShowSecret("Save these one-use recovery codes privately", confirmed.RecoveryCodes.Concat(new[] {
                    "Codes require your password and are shown only now.", "MFA is enabled. Existing sessions were revoked.", "Wait for the next authenticator code before signing on." }).ToArray());
            }
            else if (key.Edit is { } edit && edit != CursorEdit.None) _editor?.ApplyEdit(edit);
            else if (!char.IsControl(key.Character) && key.Character != '\0') _editor?.Apply(key.Character);
        }
        catch (Exception ex) when (ex is CpfException or System.Security.Cryptography.CryptographicException)
        { Screens.Status(_buffer, "Authentication or verification failed. Re-enter credentials or code.", error: true); }
        return new(_stage);
    }

    private void PaintCredentials()
    {
        Write(3, "Account security"); Write(5, "Profile:"); Write(6, "Password:"); Write(7, "Current MFA/recovery code:");
        Write(10, "Replacing or disabling MFA requires the current factor.");
        Write(22, "Enter/F5=Enroll  F6=Disable MFA  F7=Issue SSO token  F3=Back");
        _form = new DisplayForm(_buffer, new[] {
            new InputField { Row = 5, Column = 29, Length = 10 },
            new InputField { Row = 6, Column = 29, Length = _system.Security.PasswordPolicy.MaximumLength,
                DisplayLength = 50, TrimTrailingFill = false, Hidden = true, TabOrder = 1 },
            new InputField { Row = 7, Column = 29, Length = 32, Hidden = true, TabOrder = 2 },
        });
        _editor = new FieldEditor(_form);
    }
    private void ShowSecret(string title, IReadOnlyList<string> lines)
    {
        _form?.ClearAll(); _form = null; _editor = null; _stage = "Saved";
        _buffer.ClearScreen(); Write(2, title);
        for (var i = 0; i < lines.Count; i++) Write(4 + i, lines[i]);
        Write(22, "F3=I have saved this information; return to sign-on");
    }
    private void Write(int row, string text) { _buffer.MoveCursor(row, 2); _buffer.Write(text); }
    public void Dispose() { _form?.ClearAll(); _enrollment = null; _buffer.ClearScreen(); }
}
