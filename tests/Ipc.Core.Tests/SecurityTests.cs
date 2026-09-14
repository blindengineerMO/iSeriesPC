using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests.Security;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_and_verify_round_trip()
    {
        var hash = PasswordHasher.Hash("Pas$word1", 10_000);
        Assert.True(PasswordHasher.Verify("Pas$word1", hash));
        Assert.False(PasswordHasher.Verify("PAS$word1", hash));
    }

    [Fact]
    public void Hashes_are_unique_per_salt()
    {
        var a = PasswordHasher.Hash("PASSWORD1", 10_000);
        var b = PasswordHasher.Hash("PASSWORD1", 10_000);
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("PASSWORD1", a));
        Assert.True(PasswordHasher.Verify("PASSWORD1", b));
    }
}

public class PasswordPolicyTests
{
    private readonly SystemValueRegistry _registry = new();

    [Fact]
    public void Rejects_short_password()
    {
        var policy = new PasswordPolicy(_registry);
        Assert.Contains("at least 6", policy.Validate("abc")[0]);
    }

    [Fact]
    public void Rejects_missing_digit_when_required()
    {
        _registry.Get(SystemValueNames.PasswordRequiredDigit).Value = "*YES";
        var policy = new PasswordPolicy(_registry);
        Assert.Contains("digit", policy.Validate("ABCDEF")[0]);
    }

    [Fact]
    public void Rejects_repeated_characters_when_disallowed()
    {
        _registry.Get(SystemValueNames.PasswordRepeatedCharacters).Value = "*YES";
        var policy = new PasswordPolicy(_registry);
        Assert.Contains("repeated", policy.Validate("AABCDF")[0]);
    }

    [Fact]
    public void Accepts_valid_password()
    {
        var policy = new PasswordPolicy(_registry);
        Assert.True(policy.IsValid("Passw1"));
    }
}

public class UserProfileStoreTests : IDisposable
{
    private readonly IpcSystem _system;

    public UserProfileStoreTests()
    {
        _system = IpcSystem.Create(":memory:");
        _system.Start();
    }

    public void Dispose() => _system.Dispose();

    [Fact]
    public void Default_profiles_seeded()
    {
        var profiles = _system.Security.Profiles;
        Assert.True(profiles.Exists(ProfileNames.QSecOficer));
        Assert.True(profiles.Exists(ProfileNames.QSecurityAdministrator));
        Assert.True(profiles.Exists(ProfileNames.QUser));
        Assert.True(profiles.Exists(ProfileNames.QService));

        var secofr = profiles.Get(ProfileNames.QSecOficer);
        Assert.Equal(ProfileStatus.PasswordExpired, secofr.Status);
        Assert.True(profiles.VerifyPassword(ProfileNames.QSecOficer, "11111111"));
        Assert.Equal(SpecialAuthority.AllObject, secofr.SpecialAuthorities & SpecialAuthority.AllObject);
    }

    [Fact]
    public void Create_and_delete_profile()
    {
        var profiles = _system.Security.Profiles;
        profiles.Create(new UserProfile { Name = "MATT", Description = "human" });
        Assert.True(profiles.Exists("matt"));

        profiles.Delete("matt");
        Assert.False(profiles.Exists("matt"));
    }

    [Fact]
    public void System_profiles_cannot_be_deleted()
    {
        var profiles = _system.Security.Profiles;
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => profiles.Delete(ProfileNames.QSecOficer));
    }

    [Fact]
    public void Set_password_requires_policy_compliance()
    {
        var profiles = _system.Security.Profiles;
        var profile = profiles.Get(ProfileNames.QUser);
        Assert.Throws<Ipc.Core.Messages.CpfException>(() =>
            profiles.SetPassword(profile, "short"));
    }

    [Fact]
    public void Failed_attempts_disable_profile_at_limit()
    {
        var profiles = _system.Security.Profiles;

        profiles.RecordSignOnFailure(ProfileNames.QUser);
        profiles.RecordSignOnFailure(ProfileNames.QUser);
        profiles.RecordSignOnFailure(ProfileNames.QUser);

        Assert.Equal(ProfileStatus.Disabled, profiles.Get(ProfileNames.QUser).Status);
    }

    [Fact]
    public void Groups_expand_recursively()
    {
        var profiles = _system.Security.Profiles;
        profiles.Create(new UserProfile { Name = "GRP", Description = "parent group" });
        profiles.Create(new UserProfile { Name = "SUBGRP", GroupProfile = "GRP", Description = "child group" });
        profiles.Create(new UserProfile { Name = "MEMBER", GroupProfile = "SUBGRP", Description = "member" });

        var groups = profiles.EffectiveGroups("MEMBER");
        Assert.Equal(new[] { "SUBGRP", "GRP" }, groups);
    }
}

public class SecurityServiceTests : IDisposable
{
    private readonly IpcSystem _system;

    public SecurityServiceTests()
    {
        _system = IpcSystem.Create(":memory:");
        _system.Start();
    }

    public void Dispose() => _system.Dispose();

    [Fact]
    public void Sign_on_with_default_security_officer_password()
    {
        var result = _system.Security.Authenticate(ProfileNames.QSecOficer, "11111111");
        Assert.True(result.Success);
        Assert.True(result.MustChangePassword);
    }

    [Fact]
    public void Unknown_profile_is_rejected()
    {
        var result = _system.Security.Authenticate("NOBODY", "*ANYTHING");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Reason);
    }

    [Fact]
    public void Disabled_profile_is_rejected()
    {
        var result = _system.Security.Authenticate(ProfileNames.QService, "whatever");
        Assert.False(result.Success);
        Assert.Contains("disabled", result.Reason);
    }

    [Fact]
    public void Wrong_password_is_rejected_and_counts()
    {
        var profiles = _system.Security.Profiles;
        profiles.SetPassword(profiles.Get(ProfileNames.QUser), "PASS102938");

        var result = _system.Security.Authenticate(ProfileNames.QUser, "WRONG");
        Assert.False(result.Success);
        Assert.Equal(1, profiles.Get(ProfileNames.QUser).SignOnAttempts);
    }

    [Fact]
    public void Change_password_flow()
    {
        var security = _system.Security;
        security.Profiles.SetPassword(security.Profiles.Get(ProfileNames.QUser), "PASS102938");
        security.ChangePassword(ProfileNames.QUser, "PASS102938", "NEWPASS1");

        Assert.True(security.Profiles.VerifyPassword(ProfileNames.QUser, "NEWPASS1"));
        Assert.False(security.Profiles.VerifyPassword(ProfileNames.QUser, "PASS102938"));

        var result = security.Authenticate(ProfileNames.QUser, "NEWPASS1");
        Assert.True(result.Success);
        Assert.False(result.MustChangePassword);
    }
}

public class AuthorityEngineTests : IDisposable
{
    private readonly IpcSystem _system;

    public AuthorityEngineTests()
    {
        _system = IpcSystem.Create(":memory:");
        _system.Start();
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("MYLIB", "PAYROLL"),
            ObjectType = ObjectType.File,
            Owner = ProfileNames.QSecOficer,
        });
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("MYLIB", "PUBLIC"),
            ObjectType = ObjectType.File,
            Owner = ProfileNames.QSecOficer,
            PublicAuthority = Authorities.ChangeBits,
        });
        var profiles = _system.Security.Profiles;
        profiles.Create(new UserProfile { Name = "GRP1", Description = "dept group" });
        profiles.Create(new UserProfile { Name = "ALICE", GroupProfile = "GRP1", Description = "dept member" });
    }

    public void Dispose() => _system.Dispose();

    private UserProfile User(string name) => _system.Security.Profiles.Get(name);

    private IReadOnlyList<string> Groups(string name) =>
        _system.Security.Profiles.EffectiveGroups(name);

    [Fact]
    public void Public_authority_applies_when_no_private_grant()
    {
        var bits = _system.Security.Authority.EffectiveFor(User("ALICE"), Groups("ALICE"),
            "MYLIB", "PAYROLL", "*FILE");
        Assert.Equal(Authorities.UseBits, bits);
    }

    [Fact]
    public void Public_change_authority_applies()
    {
        var bits = _system.Security.Authority.EffectiveFor(User("ALICE"), Groups("ALICE"),
            "MYLIB", "PUBLIC", "*FILE");
        Assert.Equal(Authorities.ChangeBits, bits);
    }

    [Fact]
    public void Private_grant_overrides_public()
    {
        var authority = _system.Security.Authority;
        authority.Grant("MYLIB", "PAYROLL", "*FILE", "ALICE", Authorities.AllBits);

        var bits = authority.EffectiveFor(User("ALICE"), Groups("ALICE"), "MYLIB", "PAYROLL", "*FILE");
        Assert.Equal(Authorities.AllBits, bits);
    }

    [Fact]
    public void Group_grants_to_member()
    {
        var authority = _system.Security.Authority;
        authority.Grant("MYLIB", "PAYROLL", "*FILE", "GRP1", Authorities.ChangeBits);

        var bits = authority.EffectiveFor(User("ALICE"), Groups("ALICE"), "MYLIB", "PAYROLL", "*FILE");
        Assert.Equal(Authorities.ChangeBits, bits);
    }

    [Fact]
    public void AuthL_membership_grants_to_member()
    {
        var authority = _system.Security.Authority;
        authority.GrantAuthL("MYLIB", "PAYROLL", "*FILE", "PAYROLLAUTL", Authorities.ChangeBits);
        authority.AddAuthLMember("PAYROLLAUTL", "GRP1", Authorities.ChangeBits);

        var bits = authority.EffectiveFor(User("ALICE"), Groups("ALICE"), "MYLIB", "PAYROLL", "*FILE");
        Assert.Equal(Authorities.ChangeBits, bits);
    }

    [Fact]
    public void AllObject_special_authority_bypasses_everything()
    {
        var authority = _system.Security.Authority;
        authority.SetPublicAuthority("MYLIB", "PAYROLL", "*FILE", AuthorityBit.None);

        var secofr = User(ProfileNames.QSecOficer);
        var bits = authority.EffectiveFor(secofr, Groups(ProfileNames.QSecOficer), "MYLIB", "PAYROLL", "*FILE");
        Assert.True((bits & AuthorityBit.ObjectExist) != 0);
    }

    [Fact]
    public void Security_level_10_allows_everything()
    {
        var store = new SystemValueStore(_system.Connections);
        store.Set(SystemValueNames.SecurityLevel, "10");
        var authority = _system.Security.Authority;
        authority.SetPublicAuthority("MYLIB", "PAYROLL", "*FILE", AuthorityBit.None);

        var bits = authority.EffectiveFor(User("ALICE"), Groups("ALICE"), "MYLIB", "PAYROLL", "*FILE");
        Assert.Equal(Authorities.AllBits, bits);
    }
}