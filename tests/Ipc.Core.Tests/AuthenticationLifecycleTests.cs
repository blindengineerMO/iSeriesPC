using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class AuthenticationLifecycleTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public AuthenticationLifecycleTests()
    {
        _system.Start();
        _system.Security.Profiles.Create(new UserProfile { Name = "LOGIN", PasswordHashIterations = 10_000 });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("LOGIN"), "Correct1");
    }

    [Fact]
    public async Task Concurrent_failed_signons_lock_once_without_losing_attempts()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _system.Security.Authenticate("LOGIN", "Wrong1"))));
        var profile = _system.Security.Profiles.Get("LOGIN");
        Assert.Equal(ProfileStatus.Disabled, profile.Status);
        Assert.Equal(3, profile.SignOnAttempts);
        Assert.False(_system.Security.Authenticate("LOGIN", "Correct1").Success);
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.Security.ChangePassword("LOGIN", "Correct1", "Newpass1"));
        _system.Security.Profiles.SetPassword(profile, "Recovered1");
        Assert.True(_system.Security.Authenticate("LOGIN", "Recovered1").Success);
        Assert.Equal(0, _system.Security.Profiles.Get("LOGIN").SignOnAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_verification_snapshot_cannot_undo_a_concurrent_disable_or_reset(bool disable)
    {
        var snapshot = _system.Security.Profiles.Get("LOGIN");
        var current = _system.Security.Profiles.Get("LOGIN");
        if (disable) { current.Status = ProfileStatus.Disabled; _system.Security.Profiles.Update(current); }
        else _system.Security.Profiles.SetPassword(current, "Newpass1");
        Assert.False(_system.Security.Profiles.RecordVerifiedSignOn(snapshot));
        Assert.Equal(0, _system.Security.Profiles.Get("LOGIN").DaysUsed);
    }

    [Fact]
    public void Password_reset_changes_credentials_without_overwriting_newer_profile_configuration()
    {
        var stale = _system.Security.Profiles.Get("LOGIN");
        var latest = _system.Security.Profiles.Get("LOGIN");
        latest.Description = "Newer administrator edit";
        _system.Security.Profiles.Update(latest);
        _system.Security.Profiles.SetPassword(stale, "Changed1");
        Assert.Equal(latest.Description, _system.Security.Profiles.Get("LOGIN").Description);
    }

    [Fact]
    public void Reauthentication_failure_preserves_the_executing_audit_identity()
    {
        using (Ipc.Services.Events.OperationIdentity.Enter("LOGIN"))
            Assert.False(_system.Security.Authenticate("LOGIN", "Wrong1").Success);
        var entry = _system.DurableEvents.Read("reauthentication", 1000).Events.Last(e => e.Kind == "security.authentication");
        Assert.Equal("LOGIN", entry.Principal);
        Assert.DoesNotContain("Wrong1", entry.Payload);
    }

    [Fact]
    public void Password_change_failures_count_toward_lockout_and_expired_accounts_cannot_sign_on()
    {
        for (var i = 0; i < 3; i++)
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.Security.ChangePassword("LOGIN", "Wrong1", "Changed1"));
        Assert.Equal(ProfileStatus.Disabled, _system.Security.Profiles.Get("LOGIN").Status);
        var profile = _system.Security.Profiles.Get("LOGIN");
        profile.Status = ProfileStatus.Expired;
        _system.Security.Profiles.Update(profile);
        Assert.False(_system.Security.Authenticate("LOGIN", "Correct1").Success);
    }

    [Fact]
    public void Long_passwords_and_trailing_spaces_survive_terminal_editing_and_authentication()
    {
        var password = new string('z', 124) + "12  ";
        var buffer = new DisplayBuffer();
        var panel = new Ipc.Console.Session.SignOnPanel(buffer, _system.Security.PasswordPolicy.MaximumLength);
        var editor = new FieldEditor(panel.Form);
        editor.ApplyEdit(CursorEdit.Tab);
        foreach (var character in password) Assert.Equal(EditingResult.Accepted, editor.Apply(character));
        Assert.Equal(EditingResult.Full, editor.Apply('X'));
        Assert.Equal(password, panel.Form.ReadValue(1));
        Assert.DoesNotContain('z', buffer.RowText(5));
        editor.ApplyEdit(CursorEdit.FieldBackspace);
        Assert.Equal(password[..^1], panel.Form.ReadValue(1));
        editor.Apply(' ');
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("LOGIN"), password);
        Assert.True(_system.Security.Authenticate("LOGIN", panel.Form.ReadValue(1)).Success);
        Assert.False(_system.Security.Authenticate("LOGIN", password.TrimEnd()).Success);
        panel.Form.ClearAll();
        Assert.Equal("", panel.Form.ReadValue(1));
    }

    [Fact]
    public void Password_levels_and_expiration_policy_are_enforced_consistently()
    {
        _system.SetSystemValue(SystemValueNames.PasswordSystemLevel, "0");
        Assert.Equal(10, _system.Security.PasswordPolicy.MaximumLength);
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("LOGIN"), new string('a', 11)));
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.SetSystemValue(SystemValueNames.PasswordMinimumLength, "11"));
        _system.SetSystemValue(SystemValueNames.PasswordSystemLevel, "3");
        _system.SetSystemValue(SystemValueNames.PasswordMinimumLength, "20");
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.SetSystemValue(SystemValueNames.PasswordSystemLevel, "1"));
        _system.SetSystemValue(SystemValueNames.PasswordExpirationInterval, "0");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("LOGIN"), new string('a', 20));
        Assert.Null(_system.Security.Profiles.Get("LOGIN").PasswordExpires);
    }

    [Fact]
    public void Generated_bootstrap_is_private_persistent_consumed_and_recoverable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-enroll-" + Guid.NewGuid().ToString("N"));
        try
        {
            string initial;
            var path = Path.Combine(directory, "system.db.initial-password");
            using (var system = IpcSystem.Create(directory))
            {
                system.Start();
                initial = File.ReadAllText(path).TrimEnd('\n', '\r');
                Assert.NotEqual("11111111", initial);
                Assert.True(initial.Length >= 24);
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                var result = system.Security.Authenticate("QSECOFR", initial);
                Assert.True(result.Success);
                Assert.True(result.MustChangePassword);
            }
            using var restarted = IpcSystem.Create(directory);
            restarted.Start();
            Assert.Equal(initial, File.ReadAllText(path).TrimEnd('\n', '\r'));
            restarted.Security.ChangePassword("QSECOFR", initial, "LongerPermanentPassword1");
            Assert.False(File.Exists(path));
            var recoveryPath = restarted.Security.Profiles.ResetAdministratorCredential();
            var recovery = File.ReadAllText(recoveryPath).TrimEnd('\n', '\r');
            Assert.NotEqual(initial, recovery);
            Assert.False(restarted.Security.Authenticate("QSECOFR", "LongerPermanentPassword1").Success);
            Assert.True(restarted.Security.Authenticate("QSECOFR", recovery).MustChangePassword);
            Assert.DoesNotContain(restarted.DurableEvents.Read("test-audit", 1000).Events,
                e => e.Payload.Contains(initial) || e.Payload.Contains(recovery) || e.Payload.Contains("LongerPermanentPassword1"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("1$AAAA$AAAA")]
    [InlineData("2147483647$AAAA$AAAA")]
    [InlineData("210000$not-base64$bad")]
    public void Invalid_hash_formats_fail_closed(string hash) => Assert.False(PasswordHasher.Verify("Correct1", hash));

    public void Dispose() => _system.Dispose();
}
