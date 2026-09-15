using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class CertificateTrustTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public CertificateTrustTests() => _system.Start();

    [Fact]
    public void Instance_trust_is_explicit_purpose_scoped_and_revocation_is_live()
    {
        var ca = _system.Certificates.CreateAuthority("CA", "iSeriesPC test CA");
        var leaf = _system.Certificates.Issue("SIGNER", "Object signer", ca.Id, CertificatePurpose.ObjectSigning);
        var bytes = Encoding.UTF8.GetBytes("signed package");
        Assert.Throws<CpfException>(() => _system.ContentTrust.Sign(bytes, CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", leaf.Id));
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signature = _system.ContentTrust.Sign(bytes, CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", leaf.Id);
        _system.ContentTrust.Verify(bytes, CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", signature);
        Assert.Throws<CpfException>(() => _system.ContentTrust.Verify(Encoding.UTF8.GetBytes("altered package"), CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", signature));
        Assert.Throws<CpfException>(() => _system.ContentTrust.Verify(bytes, CertificatePurpose.ObjectSigning, "QGPL/OTHER:*PGM", signature));
        Assert.Throws<CpfException>(() => _system.ContentTrust.Verify(bytes, CertificatePurpose.Update, "QGPL/DEMO:*PGM", signature));
        Assert.Throws<CpfException>(() => _system.ContentTrust.Verify(bytes, CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", signature with { SignedAt = signature.SignedAt.AddSeconds(-1) }));
        _system.Certificates.Revoke(ca.Id);
        Assert.Throws<CpfException>(() => _system.ContentTrust.Verify(bytes, CertificatePurpose.ObjectSigning, "QGPL/DEMO:*PGM", signature));
    }

    [Fact]
    public void Renewal_rotates_keys_and_bindings_while_retired_signatures_remain_verifiable()
    {
        var (ca, leaf) = Signer();
        _system.Certificates.Bind("objects", leaf.Id, CertificatePurpose.ObjectSigning);
        var content = Encoding.UTF8.GetBytes("old version");
        var signature = _system.ContentTrust.Sign(content, CertificatePurpose.ObjectSigning, "version1", leaf.Id);
        var next = _system.Certificates.Renew(leaf.Id, ca.Id, CertificatePurpose.ObjectSigning);
        Assert.NotEqual(leaf.Id, next.Id);
        Assert.Equal(next.Id, Assert.Single(_system.Certificates.Bindings()).CertificateId);
        Assert.Equal("Retired", _system.Certificates.List().Single(c => c.Id == leaf.Id).State);
        _system.ContentTrust.Verify(content, CertificatePurpose.ObjectSigning, "version1", signature);
        Assert.Throws<CpfException>(() => _system.ContentTrust.Sign(content, CertificatePurpose.ObjectSigning, "version2", leaf.Id));
        Assert.NotNull(_system.ContentTrust.Sign(content, CertificatePurpose.ObjectSigning, "version2", next.Id));
    }

    [Fact]
    public void Failed_renewal_leaves_previous_bindings_and_certificate_active()
    {
        var (_, leaf) = Signer();
        _system.Certificates.Bind("objects", leaf.Id, CertificatePurpose.ObjectSigning);
        var untrusted = _system.Certificates.CreateAuthority("OTHERCA", "Untrusted CA");
        Assert.Throws<CpfException>(() => _system.Certificates.Renew(leaf.Id, untrusted.Id, CertificatePurpose.ObjectSigning));
        Assert.Equal(leaf.Id, Assert.Single(_system.Certificates.Bindings()).CertificateId);
        Assert.Equal("Active", _system.Certificates.List().Single(c => c.Id == leaf.Id).State);
    }

    [Fact]
    public void Expired_and_tls_only_certificates_cannot_sign_objects()
    {
        var (ca, leaf) = Signer();
        var tls = _system.Certificates.Issue("TLS", "localhost", ca.Id, CertificatePurpose.TlsServer, dnsNames: new[] { "localhost", "127.0.0.1" });
        Assert.Throws<CpfException>(() => _system.ContentTrust.Sign(new byte[] { 1 }, CertificatePurpose.ObjectSigning, "object", tls.Id));
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.TlsServer, true);
        _system.Certificates.Bind("web", tls.Id, CertificatePurpose.TlsServer);
        using var future = new CertificateService(_system.Connections, new FixedClock(DateTimeOffset.UtcNow.AddDays(366)));
        Assert.True(future.List().Single(c => c.Id == leaf.Id).DaysRemaining < 0);
        Assert.Throws<CpfException>(() => future.RequireTrusted(leaf.Id, CertificatePurpose.ObjectSigning));
    }

    [Fact]
    public void Object_signatures_bind_source_identity_adoption_and_attributes_and_prevent_stripping()
    {
        var (_, leaf) = Signer();
        var descriptor = Program("SIGNED", "PGM\nSNDPGMMSG MSG('signed version')\nENDPGM");
        _system.Objects.Create(descriptor);
        _system.ObjectSigning.Sign("QGPL", "SIGNED", ObjectType.Program, leaf.Id);
        Assert.Equal("VALID", _system.ObjectSigning.Check("QGPL", "SIGNED", ObjectType.Program).Result);
        Assert.False(new CommandService(_system).Execute("CALL PGM(QGPL/SIGNED)").IsError);
        var changed = _system.Objects.GetRequired("QGPL", "SIGNED", ObjectType.Program);
        changed.Source = "PGM\nSNDPGMMSG MSG('altered')\nENDPGM";
        _system.Objects.Update(changed);
        Assert.Equal("ALTERED", _system.ObjectSigning.Check("QGPL", "SIGNED", ObjectType.Program).Result);
        Assert.True(new CommandService(_system).Execute("CALL PGM(QGPL/SIGNED)").IsError);
        changed.Signature = null;
        _system.Objects.Update(changed);
        Assert.Equal("NOSIG", _system.ObjectSigning.Check("QGPL", "SIGNED", ObjectType.Program).Result);
        Assert.True(new CommandService(_system).Execute("CALL PGM(QGPL/SIGNED)").IsError);
        _system.ObjectSigning.Sign("QGPL", "SIGNED", ObjectType.Program, leaf.Id);
        Assert.False(new CommandService(_system).Execute("CALL PGM(QGPL/SIGNED)").IsError);
    }

    [Fact]
    public void Menu_signature_covers_separate_options_payload()
    {
        var (_, leaf) = Signer();
        _system.ObjectSigning.Sign("QSYS", "MAIN", ObjectType.Menu, leaf.Id);
        Assert.NotNull(_system.Menus.TryGet("MAIN", "QSYS"));
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_menu_options SET target='ALTERED' WHERE library='QSYS' AND menu='MAIN' AND ordinal=0";
        command.ExecuteNonQuery();
        Assert.Equal("ALTERED", _system.ObjectSigning.Check("QSYS", "MAIN", ObjectType.Menu).Result);
        Assert.Throws<CpfException>(() => _system.Menus.TryGet("MAIN", "QSYS"));
    }

    [Fact]
    public void Menu_option_policy_is_signed_and_signed_legacy_defaults_are_not_overwritten()
    {
        var (_, leaf) = Signer();
        _system.Menus.Register(Ipc.Core.Menu.SystemMenus.LegacyMain());
        _system.ObjectSigning.Sign("QSYS", "MAIN", ObjectType.Menu, leaf.Id);
        _system.Menus.SeedDefaults();
        Assert.Equal("M A I N", _system.Menus.Get("MAIN", "QSYS").Title);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_objects SET attrs=json_set(attrs,'$.\"ipc.menu.options\"','{\"1\":\"*JOBCTL\"}') WHERE lib='QSYS' AND name='MAIN' AND type='*MENU'";
        command.ExecuteNonQuery();
        Assert.Equal("ALTERED", _system.ObjectSigning.Check("QSYS", "MAIN", ObjectType.Menu).Result);
        Assert.Throws<CpfException>(() => _system.Menus.Get("MAIN", "QSYS"));
    }

    [Fact]
    public void Certificate_and_signing_management_require_non_adopted_security_authority()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "LIMITED" });
        using var identity = OperationIdentity.Enter("LIMITED");
        using var adoption = OperationIdentity.EnterProgram(new ObjectDescriptor { Key = new("QGPL", "ADOPT"), ObjectType = ObjectType.Program, Owner = "QSECOFR", AdoptsOwnerAuthority = true });
        Assert.Throws<CpfException>(() => _system.Certificates.List());
        Assert.Throws<CpfException>(() => _system.Certificates.CreateAuthority("DENIED", "Denied"));
    }

    private (CertificateInfo Ca, CertificateInfo Leaf) Signer()
    {
        var ca = _system.Certificates.CreateAuthority("CA", "Test certificate authority");
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        return (ca, _system.Certificates.Issue("SIGNER", "Test object signer", ca.Id, CertificatePurpose.ObjectSigning));
    }
    private static ObjectDescriptor Program(string name, string source) => new() { Key = new("QGPL", name), ObjectType = ObjectType.Program, Attribute = "CLP", Source = source };
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose() => _system.Dispose();
}
