using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class AuthorityPrecedenceTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly UserProfile _reader = new() { Name = "READER", GroupProfile = "GROUP" };
    public AuthorityPrecedenceTests()
    {
        _system.Start();
        _system.Security.Profiles.Create(_reader);
        _system.Security.Profiles.Create(new UserProfile { Name = "GROUP" });
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DATA"), ObjectType = ObjectType.File, PublicAuthority = Authorities.AllBits });
    }
    private AuthorityBit Effective() => _system.Security.Authority.EffectiveFor(_reader, new[] { "GROUP" }, "QGPL", "DATA", ObjectType.File);

    [Fact]
    public void Explicit_private_exclusion_overrides_public_group_and_authorization_list_grants()
    {
        var authority = _system.Security.Authority;
        authority.Grant("QGPL", "DATA", ObjectType.File, "GROUP", Authorities.AllBits);
        authority.GrantAuthL("QGPL", "DATA", ObjectType.File, "ACCESS", Authorities.AllBits);
        authority.AddAuthLMember("ACCESS", "READER", Authorities.AllBits);
        authority.Grant("QGPL", "DATA", ObjectType.File, "READER", AuthorityBit.None);
        Assert.Equal(AuthorityBit.None, Effective());
        authority.Revoke("QGPL", "DATA", ObjectType.File, "READER");
        Assert.Equal(Authorities.AllBits, Effective());
    }

    [Fact]
    public void Insufficient_private_authority_is_not_augmented_by_public_or_group_authority()
    {
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "GROUP", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", Authorities.UseBits);
        Assert.Equal(Authorities.UseBits, Effective());
    }

    [Fact]
    public void Individual_authorization_list_exclusion_precedes_group_grants()
    {
        _system.Security.Authority.GrantAuthL("QGPL", "DATA", ObjectType.File, "ACCESS", Authorities.AllBits);
        _system.Security.Authority.AddAuthLMember("ACCESS", "READER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "GROUP", Authorities.AllBits);
        Assert.Equal(AuthorityBit.None, Effective());
    }

    [Fact]
    public void Group_exclusion_suppresses_public_but_other_group_grants_can_supply_authority()
    {
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "GROUP", AuthorityBit.None);
        Assert.Equal(AuthorityBit.None, Effective());
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SECOND", Authorities.UseBits);
        Assert.Equal(Authorities.UseBits, _system.Security.Authority.EffectiveFor(_reader, new[] { "GROUP", "SECOND" }, "QGPL", "DATA", ObjectType.File));
    }

    [Fact]
    public void Owner_and_group_all_object_authority_are_recognized_without_public_permissions()
    {
        var descriptor = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File);
        descriptor.Owner = "READER";
        descriptor.PublicAuthority = AuthorityBit.None;
        _system.Objects.Update(descriptor);
        Assert.Equal(Authorities.AllBits, Effective());
        descriptor.Owner = "QSECOFR";
        _system.Objects.Update(descriptor);
        var group = _system.Security.Profiles.Get("GROUP");
        group.SpecialAuthorities = SpecialAuthority.AllObject;
        _system.Security.Profiles.Update(group);
        Assert.Equal(Authorities.AllBits, Effective());
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", AuthorityBit.None);
        Assert.Equal(AuthorityBit.None, Effective());
    }

    [Fact]
    public void Authorization_list_public_mode_uses_list_permissions_with_attachment_mask()
    {
        _system.Security.Authority.GrantAuthL("QGPL", "DATA", ObjectType.File, "ACCESS", Authorities.UseBits);
        _system.Security.Authority.SetPublicAuthority("QSYS", "ACCESS", ObjectType.AuthorizationList, Authorities.AllBits);
        var descriptor = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File);
        descriptor.UseAuthorizationListPublicAuthority = true;
        _system.Objects.Update(descriptor);
        Assert.Equal(Authorities.UseBits, Effective());
    }

    [Fact]
    public void Level_twenty_allows_object_access_after_authentication()
    {
        _system.SetSystemValue("QSECURITY", "20");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", AuthorityBit.None);
        Assert.Equal(Authorities.AllBits, Effective());
    }
    [Theory]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(30, false)]
    [InlineData(40, false)]
    [InlineData(50, false)]
    public void Supported_security_levels_apply_their_documented_object_authority_gate(int level, bool bypass)
    {
        _system.SetSystemValue("QSECURITY", level.ToString());
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", AuthorityBit.None);
        Assert.Equal(bypass ? Authorities.AllBits : AuthorityBit.None, Effective());
        Assert.False(_system.Security.Authority.CanExecute(_reader, new[] { "GROUP" }, "QGPL", "MISSING", ObjectType.File, Authorities.UseBits));
    }

    public void Dispose() => _system.Dispose();
}
