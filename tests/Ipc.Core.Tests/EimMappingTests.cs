using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class EimMappingTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly Probe _probe = new();
    private readonly EimService _identities;
    private readonly SecurityService _security;
    private readonly Provider _provider = new();
    private static EimMapping Mapping(string profile = "MAPPED") => new(profile, "directory", "uid=alice,dc=ipc,dc=test", "entry-1", "alice@IPC.TEST", "alice");

    public EimMappingTests()
    {
        _system.Start();
        _system.Security.Profiles.Create(new UserProfile { Name = "MAPPED", PasswordHashIterations = 10_000 });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("MAPPED"), "Localpass1");
        _identities = new(_system.Connections, _probe);
        _security = new(_system.Security.Profiles, _system.Security.PasswordPolicy,
            _system.Security.Authority, _system.SystemValues, _system.DurableEvents, eim: _identities, sssd: _provider);
    }

    [Fact]
    public void Mapping_requires_a_verified_directory_identity_and_replaces_local_credentials()
    {
        _identities.Mappings.Add(Mapping());
        Assert.Null(_system.Security.Profiles.Get("MAPPED").PasswordHash);
        Assert.False(_identities.Mappings.Get("MAPPED")!.AllowsExecution(DateTimeOffset.UtcNow));
        _probe.State = DirectoryIdentityState.IdentityMismatch;
        Assert.False(_security.Authenticate("MAPPED", "External1").Success);
        Assert.Equal(0, _provider.Calls);
        _probe.State = DirectoryIdentityState.Active;
        Assert.True(_security.Authenticate("MAPPED", "External1").Success);
        Assert.Equal(1, _provider.Calls);
        Assert.True(_identities.Mappings.Get("MAPPED")!.AllowsExecution(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(DirectoryIdentityState.Disabled)]
    [InlineData(DirectoryIdentityState.Missing)]
    [InlineData(DirectoryIdentityState.IdentityMismatch)]
    [InlineData(DirectoryIdentityState.Unavailable)]
    public void Directory_revocation_denies_live_service_operations_and_new_signons(DirectoryIdentityState state)
    {
        _identities.Mappings.Add(Mapping());
        Assert.True(_security.Authenticate("MAPPED", "External1").Success);
        using (OperationIdentity.Enter("MAPPED"))
            Assert.NotNull(_system.Objects.Get("QSYS", "MAIN", Ipc.Core.Objects.ObjectType.Menu));
        _probe.State = state;
        Assert.Equal(state, _identities.Refresh("MAPPED"));
        using (OperationIdentity.Enter("MAPPED"))
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _system.Objects.Get("QSYS", "MAIN", Ipc.Core.Objects.ObjectType.Menu));
        Assert.False(_security.Authenticate("MAPPED", "External1").Success);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public void Unavailable_kerberos_provider_cannot_use_a_local_password_or_create_a_session()
    {
        _identities.Mappings.Add(Mapping());
        _provider.Status = CredentialStatus.ProviderUnavailable;
        Assert.False(_security.Authenticate("MAPPED", "Localpass1").Success);
        Assert.Empty(_system.Jobs.List());
    }

    [Fact]
    public void Conflicting_principal_entry_or_account_mappings_rollback_without_resetting_the_other_profile()
    {
        _identities.Mappings.Add(Mapping());
        _system.Security.Profiles.Create(new UserProfile { Name = "OTHER", PasswordHashIterations = 10_000 });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("OTHER"), "Otherpass1");
        foreach (var duplicate in new[]
        {
            Mapping("OTHER") with { EntryId = "entry-2", LinuxAccount = "bob", DistinguishedName = "uid=bob,dc=ipc,dc=test" },
            Mapping("OTHER") with { Principal = "bob@IPC.TEST", LinuxAccount = "bob", DistinguishedName = "uid=bob,dc=ipc,dc=test" },
            Mapping("OTHER") with { Principal = "bob@IPC.TEST", EntryId = "entry-2", DistinguishedName = "uid=bob,dc=ipc,dc=test" },
        })
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _identities.Mappings.Add(duplicate));
        Assert.True(_system.Security.Profiles.VerifyPassword("OTHER", "Otherpass1"));
        Assert.Single(_identities.Mappings.List());
    }

    [Fact]
    public void Stale_probes_and_removed_mappings_cannot_reenable_access()
    {
        _identities.Mappings.Add(Mapping());
        var old = _identities.Mappings.Get("MAPPED")!;
        _identities.Mappings.RecordProbe(old, DirectoryIdentityState.Active, DateTimeOffset.UtcNow.AddMinutes(-2));
        Assert.False(_identities.Mappings.Get("MAPPED")!.AllowsExecution(DateTimeOffset.UtcNow));
        _identities.Mappings.Remove("MAPPED");
        Assert.Equal(ProfileStatus.Disabled, _system.Security.Profiles.Get("MAPPED").Status);
        _identities.Mappings.Add(Mapping());
        Assert.False(_identities.Mappings.RecordProbe(old, DirectoryIdentityState.Active, DateTimeOffset.UtcNow));
        Assert.False(_identities.Mappings.Get("MAPPED")!.AllowsExecution(DateTimeOffset.UtcNow));
        Assert.False(_security.Authenticate("MAPPED", "External1").Success);
    }

    [Fact]
    public void Nonadministrators_cannot_add_remove_or_refresh_mappings_and_recovery_profiles_cannot_be_mapped()
    {
        using (OperationIdentity.Enter("MAPPED"))
        {
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _identities.Mappings.Add(Mapping()));
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _identities.Mappings.Remove("MAPPED"));
            Assert.Throws<Ipc.Core.Messages.CpfException>(() => _identities.Refresh("MAPPED"));
        }
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => _identities.Mappings.Add(Mapping("QSECOFR")));
    }

    [Fact]
    public void An_older_directory_response_cannot_overwrite_a_newer_revocation()
    {
        _identities.Mappings.Add(Mapping());
        var snapshot = _identities.Mappings.Get("MAPPED")!;
        var now = DateTimeOffset.UtcNow;
        Assert.True(_identities.Mappings.RecordProbe(snapshot, DirectoryIdentityState.Disabled, now));
        Assert.False(_identities.Mappings.RecordProbe(snapshot, DirectoryIdentityState.Active, now.AddSeconds(-1)));
        Assert.Equal(DirectoryIdentityState.Disabled, _identities.Mappings.Get("MAPPED")!.DirectoryState);
    }

    [Fact]
    public void Ldap_configuration_requires_tls_explicit_account_state_and_unique_directory_names()
    {
        Assert.Throws<ArgumentException>(() => new LdapIdentityProbe(new[] { new Ipc.Services.Configuration.LdapDirectoryConfig { Name = "test", Uri = "ldap://example.test" } }));
        Assert.Throws<ArgumentException>(() => new LdapIdentityProbe(new[] { new Ipc.Services.Configuration.LdapDirectoryConfig { Name = "test", Uri = "ldap://example.test", AllowLoopbackPlaintext = true } }));
        Assert.Throws<ArgumentException>(() => new LdapIdentityProbe(new[] { new Ipc.Services.Configuration.LdapDirectoryConfig { Name = "test", Uri = "ldaps://example.test", EnabledValue = "" } }));
        Assert.Equal(DirectoryIdentityState.Unavailable, new LdapIdentityProbe(Array.Empty<Ipc.Services.Configuration.LdapDirectoryConfig>()).Check(Mapping()));
    }

    private sealed class Probe : IDirectoryIdentityProbe
    {
        internal DirectoryIdentityState State { get; set; } = DirectoryIdentityState.Active;
        public DirectoryIdentityState Check(EimMapping mapping) => State;
    }
    private sealed class Provider : IPasswordAuthenticationProvider
    {
        internal int Calls { get; private set; }
        internal CredentialStatus Status { get; set; } = CredentialStatus.Valid;
        public CredentialVerification Verify(string account, string password) { Calls++; return new(Status); }
    }
    public void Dispose() => _system.Dispose();
}
