using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class MfaLifecycleTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly TestClock _clock = new();
    private readonly SecurityService _security;
    public MfaLifecycleTests()
    {
        _system.Start();
        _system.Security.Profiles.Create(new UserProfile { Name = "ALICE" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ALICE"), "Password2");
        _security = new(_system.Security.Profiles, _system.Security.PasswordPolicy, _system.Security.Authority,
            new SystemValueRegistry(), clock: _clock);
    }

    [Fact]
    public void Enrollment_challenge_replay_and_one_use_recovery_are_enforced()
    {
        var (enrollment, confirmation) = Enroll();
        Assert.Equal(8, confirmation.RecoveryCodes.Distinct().Count());
        Assert.False(_security.Authenticate("ALICE", "Password2").Success);
        Assert.True(_security.Authenticate("ALICE", "Password2").MustVerifyMfa);
        Assert.False(_security.OpenSession("ALICE", "Password2", Code(enrollment)).Success);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var signedIn = _security.OpenSession("ALICE", "Password2", Code(enrollment));
        Assert.True(signedIn.Success, signedIn.Reason);
        Assert.NotNull(signedIn.Session);
        Assert.False(_security.OpenSession("ALICE", "Password2", Code(enrollment)).Success);
        Assert.True(_security.OpenSession("ALICE", "Password2", confirmation.RecoveryCodes[0]).Success);
        Assert.False(_security.OpenSession("ALICE", "Password2", confirmation.RecoveryCodes[0]).Success);
        Assert.Throws<CpfException>(() => _security.ConfirmMfaEnrollment(enrollment.EnrollmentToken, Code(enrollment)));
        using var connection = _system.Connections.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT secret FROM sys_mfa";
        Assert.DoesNotContain(enrollment.SharedSecret, (string)read.ExecuteScalar()!);
        read.CommandText = "SELECT group_concat(code_hash) FROM sys_mfa_recovery";
        Assert.DoesNotContain(confirmation.RecoveryCodes[1], (string)read.ExecuteScalar()!);
        read.CommandText = "SELECT group_concat(token_hash) FROM sys_auth_sessions";
        Assert.DoesNotContain(signedIn.Session!.Token, (string)read.ExecuteScalar()!);
        read.CommandText = "SELECT group_concat(payload) FROM sys_events";
        var events = (string)read.ExecuteScalar()!;
        Assert.DoesNotContain("Password2", events);
        Assert.DoesNotContain(enrollment.SharedSecret, events);
        Assert.DoesNotContain(confirmation.RecoveryCodes[0], events);
        Assert.DoesNotContain(signedIn.Session.Token, events);
    }

    [Fact]
    public void Enrollment_expires_and_rejects_wrong_factor_after_five_attempts()
    {
        var expired = _security.BeginMfaEnrollment("ALICE", "Password2");
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Throws<CpfException>(() => _security.ConfirmMfaEnrollment(expired.EnrollmentToken, Code(expired)));
        var enrollment = _security.BeginMfaEnrollment("ALICE", "Password2");
        for (var i = 0; i < 5; i++)
            Assert.Throws<CpfException>(() => _security.ConfirmMfaEnrollment(enrollment.EnrollmentToken, "invalid"));
        Assert.Throws<CpfException>(() => _security.ConfirmMfaEnrollment(enrollment.EnrollmentToken, Code(enrollment)));
        Assert.True(_security.Authenticate("ALICE", "Password2").Success);
    }

    [Fact]
    public void Recovery_unlocks_mfa_without_disabling_the_factor()
    {
        var (enrollment, confirmation) = Enroll();
        for (var i = 0; i < 5; i++) Assert.False(_security.Authenticate("ALICE", "Password2", verificationCode: "invalid").Success);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(_security.Authenticate("ALICE", "Password2", verificationCode: Code(enrollment)).Success);
        Assert.True(_security.Authenticate("ALICE", "Password2", verificationCode: confirmation.RecoveryCodes[0]).Success);
        Assert.True(_security.Authenticate("ALICE", "Password2", verificationCode: Code(enrollment)).Success);
        Assert.True(_security.Authenticate("ALICE", "Password2").MustVerifyMfa);
    }

    [Fact]
    public void Password_changes_invalidate_enrollment_and_all_preexisting_sessions()
    {
        var ticket = _security.OpenSession("ALICE", "Password2").Session!;
        var enrollment = _security.BeginMfaEnrollment("ALICE", "Password2");
        _security.ChangePassword("ALICE", "Password2", "Changed3");
        Assert.False(_security.ResumeSession(ticket.Token).Success);
        Assert.Throws<CpfException>(() => _security.ConfirmMfaEnrollment(enrollment.EnrollmentToken, Code(enrollment)));
        Assert.True(_security.OpenSession("ALICE", "Changed3").Success);
    }

    [Fact]
    public void Enabling_and_disabling_mfa_revokes_sessions_and_requires_fresh_proof()
    {
        var old = _security.OpenSession("ALICE", "Password2").Session!;
        var (_, confirmation) = Enroll();
        Assert.False(_security.ResumeSession(old.Token).Success);
        Assert.Throws<CpfException>(() => _security.BeginMfaEnrollment("ALICE", "Password2"));
        Assert.Throws<CpfException>(() => _security.DisableMfa("ALICE", "Password2", "invalid"));
        var current = _security.OpenSession("ALICE", "Password2", confirmation.RecoveryCodes[0]).Session!;
        _security.DisableMfa("ALICE", "Password2", confirmation.RecoveryCodes[1]);
        Assert.False(_security.ResumeSession(current.Token).Success);
        Assert.True(_security.OpenSession("ALICE", "Password2").Success);
    }

    [Fact]
    public void Sessions_expire_on_idle_and_absolute_limits_and_explicit_revocation()
    {
        var first = _security.OpenSession("ALICE", "Password2").Session!;
        Assert.True(_security.ResumeSession(first.Token).Success);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(_security.ResumeSession(first.Token).Success);
        var active = _security.OpenSession("ALICE", "Password2").Session!;
        for (var i = 0; i < 47; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(10));
            Assert.True(_security.ResumeSession(active.Token).Success);
        }
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(_security.ResumeSession(active.Token).Success);
        var revoked = _security.OpenSession("ALICE", "Password2").Session!;
        _security.RevokeSession(revoked.Token);
        Assert.False(_security.ResumeSession(revoked.Token).Success);
        Assert.False(_security.ResumeSession("not-a-token").Success);
    }

    [Fact]
    public void Live_service_authority_rejects_a_revoked_session()
    {
        var ticket = _security.OpenSession("ALICE", "Password2").Session!;
        using var identity = OperationIdentity.Enter("ALICE", authSessionId: ticket.Id);
        var guard = new ServiceAuthorization(_system.Connections);
        guard.RequireRunAs("ALICE");
        _security.RevokeSession(ticket.Token);
        Assert.Throws<CpfException>(() => guard.RequireRunAs("ALICE"));
    }

    [Fact]
    public void Caller_cannot_change_another_profiles_factor_even_with_its_password()
    {
        using var identity = OperationIdentity.Enter("BOB");
        Assert.Throws<CpfException>(() => _security.BeginMfaEnrollment("ALICE", "Password2"));
    }

    private (MfaEnrollment, MfaConfirmation) Enroll()
    {
        var enrollment = _security.BeginMfaEnrollment("ALICE", "Password2");
        return (enrollment, _security.ConfirmMfaEnrollment(enrollment.EnrollmentToken, Code(enrollment)));
    }

    [Fact]
    public void Password_change_requires_the_enrolled_factor_and_preserves_mfa()
    {
        var (_, confirmation) = Enroll();
        Assert.Throws<CpfException>(() => _security.ChangePassword("ALICE", "Password2", "Changed3"));
        Assert.Throws<CpfException>(() => _system.Security.Profiles.ChangePassword("ALICE", "Password2", "Changed3"));
        _security.ChangePassword("ALICE", "Password2", "Changed3", confirmation.RecoveryCodes[0]);
        Assert.True(_security.Authenticate("ALICE", "Changed3").MustVerifyMfa);
        Assert.True(_security.OpenSession("ALICE", "Changed3", confirmation.RecoveryCodes[1]).Success);
    }

    [Fact]
    public async Task Concurrent_recovery_attempts_admit_only_one_session()
    {
        var (_, confirmation) = Enroll();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            _security.OpenSession("ALICE", "Password2", confirmation.RecoveryCodes[0]))));
        Assert.Single(results, r => r.Success);
    }

    [Fact]
    public void Offline_administrator_recovery_resets_a_lost_factor_and_requires_a_new_password()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-mfa-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var system = IpcSystem.Create(directory);
            system.Start();
            system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "Adminpass2");
            var enrollment = system.Security.BeginMfaEnrollment("QSECOFR", "Adminpass2");
            system.Security.ConfirmMfaEnrollment(enrollment.EnrollmentToken,
                TotpCode.Generate(Decode(enrollment.SharedSecret), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var path = system.Security.Profiles.ResetAdministratorCredential();
            var temporary = File.ReadAllText(path).Trim();
            Assert.True(system.Security.Authenticate("QSECOFR", temporary).MustChangePassword);
            system.Security.ChangePassword("QSECOFR", temporary, "Recovered3");
            Assert.NotNull(system.Security.OpenSession("QSECOFR", "Recovered3").Session);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private string Code(MfaEnrollment enrollment) => TotpCode.Generate(Decode(enrollment.SharedSecret), _clock.GetUtcNow().ToUnixTimeSeconds());
    internal static byte[] Decode(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var bits = 0; var value = 0;
        foreach (var ch in encoded)
        {
            value = (value << 5) | alphabet.IndexOf(ch); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); }
        }
        return bytes.ToArray();
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    public void Dispose() { _security.Dispose(); _system.Dispose(); }
}
