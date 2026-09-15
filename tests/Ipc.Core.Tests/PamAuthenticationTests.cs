using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Configuration;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class PamAuthenticationTests
{
    [Fact]
    public void Configured_pam_defaults_to_verifying_both_linux_and_profile_credentials()
    {
        using var system = IpcSystem.Create(":memory:");
        system.Start();
        system.Security.Profiles.Create(new UserProfile { Name = "MAPPED", PasswordHashIterations = 10_000 });
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("MAPPED"), "Localpass1");
        var provider = new ResultProvider(CredentialStatus.Valid);
        var security = new SecurityService(system.Security.Profiles, system.Security.PasswordPolicy,
            system.Security.Authority, system.SystemValues, system.DurableEvents,
            new AuthenticationConfig { PamAccounts = new() { ["MAPPED"] = "linux-test" } }, provider);
        Assert.False(security.Authenticate("MAPPED", "Different1").Success);
        Assert.True(security.Authenticate("MAPPED", "Localpass1").Success);
        security.ChangePassword("MAPPED", "Localpass1", "Changed1");
        Assert.True(system.Security.Profiles.VerifyPassword("MAPPED", "Changed1"));
        Assert.Equal(3, provider.Calls);
    }

    [Theory]
    [InlineData(CredentialStatus.Invalid)]
    [InlineData(CredentialStatus.PasswordExpired)]
    [InlineData(CredentialStatus.AccountUnavailable)]
    [InlineData(CredentialStatus.ProviderUnavailable)]
    public void Provider_failure_never_falls_back_to_the_profile_password(CredentialStatus status)
    {
        using var system = IpcSystem.Create(":memory:");
        system.Start();
        system.Security.Profiles.Create(new UserProfile { Name = "MAPPED", PasswordHashIterations = 10_000 });
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("MAPPED"), "Localpass1");
        var provider = new ResultProvider(status);
        var security = new SecurityService(system.Security.Profiles, system.Security.PasswordPolicy,
            system.Security.Authority, system.SystemValues, system.DurableEvents,
            new AuthenticationConfig { PamRequireProfilePassword = false, PamAccounts = new() { ["MAPPED"] = "linux-test" } }, provider);
        Assert.False(security.Authenticate("MAPPED", "Localpass1").Success);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(status == CredentialStatus.Invalid ? 1 : 0, system.Security.Profiles.Get("MAPPED").SignOnAttempts);
    }

    [Fact]
    public void Terminal_mapping_requires_verified_unix_identity_and_disabled_profiles_stay_disabled()
    {
        using var system = IpcSystem.Create(":memory:");
        system.Start();
        system.Security.Profiles.Create(new UserProfile { Name = "MAPPED" });
        var provider = new ResultProvider(CredentialStatus.Valid);
        var security = new SecurityService(system.Security.Profiles, system.Security.PasswordPolicy,
            system.Security.Authority, system.SystemValues, system.DurableEvents,
            new AuthenticationConfig { PamRequireProfilePassword = false, PamAccounts = new() { ["MAPPED"] = "linux-test" } }, provider);
        Assert.False(security.Authenticate("MAPPED", "External1", "different-user", terminal: true).Success);
        Assert.False(security.Authenticate("MAPPED", "External1", terminal: true).Success);
        Assert.Equal(0, provider.Calls);
        Assert.True(security.Authenticate("MAPPED", "External1", "linux-test", terminal: true).Success);
        var profile = system.Security.Profiles.Get("MAPPED");
        profile.Status = ProfileStatus.Disabled;
        system.Security.Profiles.Update(profile);
        Assert.False(security.Authenticate("MAPPED", "External1", "linux-test", terminal: true).Success);
        Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain(system.DurableEvents.Read("audit", 1000).Events, e => e.Payload.Contains("External1"));
    }

    [Fact]
    public void Ambiguous_profile_or_linux_account_mappings_are_rejected()
    {
        using var system = IpcSystem.Create(":memory:");
        system.Start();
        foreach (var mappings in new[]
        {
            new Dictionary<string, string> { ["USER"] = "one", ["user"] = "two" },
            new Dictionary<string, string> { ["USER"] = "one", ["OTHER"] = "one" },
        })
            Assert.Throws<ArgumentException>(() => new SecurityService(system.Security.Profiles,
                system.Security.PasswordPolicy, system.Security.Authority, system.SystemValues,
                authentication: new AuthenticationConfig { PamAccounts = mappings }));
    }

    [Fact]
    public void Native_pam_checks_password_conversation_and_account_policy()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native PAM acceptance requires Linux.");
        var directory = Path.Combine(Path.GetTempPath(), "ipc-pam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var script = Path.Combine(directory, "verify.py");
            File.WriteAllText(script, "#!/usr/bin/python3\nimport os,sys\np = sys.stdin.buffer.read().rstrip(b'\\0')\nsys.exit(0 if os.environ.get('PAM_USER') == 'pam-test' and p == b'NativePassword1  ' else 1)\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var auth = $"auth [success=1 default=ignore] pam_exec.so quiet expose_authtok {script}\n" +
                "auth requisite pam_deny.so\nauth required pam_permit.so\n";
            File.WriteAllText(Path.Combine(directory, "allow"), auth + "account required pam_permit.so\n");
            File.WriteAllText(Path.Combine(directory, "deny"), auth + "account required pam_deny.so\n");
            File.SetUnixFileMode(Path.Combine(directory, "allow"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(directory, "deny"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var allow = new PamPasswordProvider("allow", TimeSpan.FromSeconds(5), directory);
            Assert.Equal(CredentialStatus.Valid, allow.Verify("pam-test", "NativePassword1  ").Status);
            Assert.Equal(CredentialStatus.Invalid, allow.Verify("pam-test", "NativePassword1").Status);
            Assert.NotEqual(CredentialStatus.Valid, new PamPasswordProvider("deny", TimeSpan.FromSeconds(5), directory)
                .Verify("pam-test", "NativePassword1  ").Status);
            Assert.Equal(CredentialStatus.ProviderUnavailable, new PamPasswordProvider("missing", TimeSpan.FromSeconds(5), directory)
                .Verify("pam-test", "NativePassword1  ").Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ResultProvider(CredentialStatus status) : IPasswordAuthenticationProvider
    {
        internal int Calls { get; private set; }
        public CredentialVerification Verify(string account, string password) { Calls++; return new(status); }
    }
}
