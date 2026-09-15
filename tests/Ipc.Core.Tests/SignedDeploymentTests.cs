using System.Text;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services;
using Ipc.Services.Security;

namespace Ipc.Core.Tests;

public sealed class SignedDeploymentTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly CertificateInfo _ca;
    private readonly CertificateInfo _signer;
    public SignedDeploymentTests()
    {
        _system.Start();
        _ca = _system.Certificates.CreateAuthority("ROOT", "Deployment test CA");
        foreach (var purpose in new[] { CertificatePurpose.ObjectSigning, CertificatePurpose.Restore, CertificatePurpose.Update, CertificatePurpose.ServiceDeployment })
            _system.Certificates.SetTrust(_ca.Id, purpose, true);
        _signer = _system.Certificates.Issue("SIGNER", "Deployment signer", _ca.Id, CertificatePurpose.ObjectSigning);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DEPLOY"), ObjectType = ObjectType.Program, Attribute = "CLP",
            Source = "PGM\nSNDPGMMSG MSG('original')\nENDPGM" });
        _system.ObjectSigning.Sign("QGPL", "DEPLOY", ObjectType.Program, _signer.Id);
    }

    [Theory]
    [InlineData(CertificatePurpose.Restore)]
    [InlineData(CertificatePurpose.Update)]
    [InlineData(CertificatePurpose.ServiceDeployment)]
    public void Tampered_packages_and_wrong_purpose_signatures_have_no_catalog_effect(CertificatePurpose purpose)
    {
        var content = Export(purpose);
        var signature = _system.ContentTrust.Sign(content, purpose, "test-package", _signer.Id);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("original", "tampered"));
        _system.Objects.Delete("QGPL", "DEPLOY", ObjectType.Program);
        Assert.Throws<CpfException>(() => Apply(tampered, signature, purpose));
        Assert.Throws<CpfException>(() => Apply(content, signature with { Value = new string('A', signature.Value.Length) }, purpose));
        Assert.False(_system.Objects.Exists("QGPL", "DEPLOY", ObjectType.Program));
        using var connection = _system.Connections.Open(); using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sys_verified_artifacts"; Assert.Equal(0L, count.ExecuteScalar());
        Apply(content, signature, purpose);
        Assert.Equal("VALID", _system.ObjectSigning.Check("QGPL", "DEPLOY", ObjectType.Program).Result);
        Assert.False(new CommandService(_system).Execute("CALL PGM(QGPL/DEPLOY)").IsError);
        if (purpose == CertificatePurpose.ServiceDeployment)
        {
            Assert.Equal("DEPLOY", _system.SignedDeployments.ResolveService("TESTSVC").Name);
            _system.Certificates.Revoke(_ca.Id);
            Assert.Throws<CpfException>(() => _system.SignedDeployments.ResolveService("TESTSVC"));
        }
    }

    [Fact]
    public void Signed_command_packages_validate_metadata_and_restore_program_dependencies_in_one_transaction()
    {
        var source = "CMD PROMPT('Deployment command')";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/DEPLOY");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "DEPLOYCMD", source, definition, "QSECOFR");
        _system.ObjectSigning.Sign("QGPL", "DEPLOYCMD", ObjectType.Command, _signer.Id);
        var content = _system.SignedDeployments.Export("command-package", new[] { new QualifiedObject("QGPL", "DEPLOYCMD", ObjectType.Command), new QualifiedObject("QGPL", "DEPLOY", ObjectType.Program) });
        var signature = _system.ContentTrust.Sign(content, CertificatePurpose.Restore, "command-package", _signer.Id);
        _system.Objects.Delete("QGPL", "DEPLOYCMD", ObjectType.Command); _system.Objects.Delete("QGPL", "DEPLOY", ObjectType.Program);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DeploymentPassword22");
        using (Ipc.Services.Events.OperationIdentity.Enter("QSECOFR")) _system.SignedDeployments.RestoreObjects(content, signature);
        Assert.Equal("original", new CommandService(_system).Execute("DEPLOYCMD").Message);
        Assert.Throws<CpfException>(() => _system.Objects.Delete("QGPL", "DEPLOY", ObjectType.Program));
        var command = _system.Objects.GetRequired("QGPL", "DEPLOYCMD", ObjectType.Command); command.Source = "CMD PROMPT('Changed')"; Assert.Throws<CpfException>(() => _system.Objects.Update(command));
        using (var connection = _system.Connections.Open())
        using (var tamper = connection.CreateCommand())
        { tamper.CommandText = "UPDATE sys_objects SET source=$source WHERE lib='QGPL' AND name='DEPLOYCMD' AND type='*CMD'"; tamper.Parameters.AddWithValue("$source", command.Source); tamper.ExecuteNonQuery(); }
        Assert.True(new CommandService(_system).Execute("DEPLOYCMD").IsError);
        _system.ObjectSigning.Sign("QGPL", "DEPLOYCMD", ObjectType.Command, _signer.Id);
        Assert.Throws<CpfException>(() => _system.SignedDeployments.Export("invalid-command", new[] { new QualifiedObject("QGPL", "DEPLOYCMD", ObjectType.Command) }));
    }

    [Fact]
    public void Signed_updates_replace_code_atomically_and_service_content_must_be_republished()
    {
        var original = Export(CertificatePurpose.ServiceDeployment);
        _system.SignedDeployments.DeployService(original, _system.ContentTrust.Sign(original, CertificatePurpose.ServiceDeployment, "test-package", _signer.Id));
        var descriptor = _system.Objects.GetRequired("QGPL", "DEPLOY", ObjectType.Program);
        descriptor.Source = "PGM\nSNDPGMMSG MSG('updated')\nENDPGM"; _system.Objects.Update(descriptor);
        _system.ObjectSigning.Sign("QGPL", "DEPLOY", ObjectType.Program, _signer.Id);
        Assert.Throws<CpfException>(() => _system.SignedDeployments.ResolveService("TESTSVC"));
        var update = Export(CertificatePurpose.Update);
        _system.SignedDeployments.ApplyObjectUpdate(update, _system.ContentTrust.Sign(update, CertificatePurpose.Update, "test-package", _signer.Id));
        Assert.Contains("updated", new CommandService(_system).Execute("CALL PGM(QGPL/DEPLOY)").Message);
        var republished = Export(CertificatePurpose.ServiceDeployment);
        _system.SignedDeployments.DeployService(republished, _system.ContentTrust.Sign(republished, CertificatePurpose.ServiceDeployment, "test-package", _signer.Id));
        Assert.Equal("DEPLOY", _system.SignedDeployments.ResolveService("TESTSVC").Name);
    }

    [Fact]
    public void Transaction_failure_rolls_back_objects_and_verified_artifact_receipt()
    {
        var content = Export(CertificatePurpose.Restore);
        var signature = _system.ContentTrust.Sign(content, CertificatePurpose.Restore, "test-package", _signer.Id);
        _system.Objects.Delete("QGPL", "DEPLOY", ObjectType.Program);
        using var connection = _system.Connections.Open(); using var inject = connection.CreateCommand();
        inject.CommandText = "CREATE TRIGGER fail_receipt BEFORE INSERT ON sys_verified_artifacts BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END";
        inject.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _system.SignedDeployments.RestoreObjects(content, signature));
        Assert.False(_system.Objects.Exists("QGPL", "DEPLOY", ObjectType.Program));
    }

    [Fact]
    public void Certificate_commands_check_integrity_and_reject_unsupported_parameters()
    {
        var commands = new CommandService(_system);
        Assert.False(commands.Execute("WRKCERT").IsError);
        Assert.False(commands.Execute("CHKOBJITG OBJ('/QSYS.LIB/QGPL.LIB/DEPLOY.PGM') CHKSIG(*ALL)").IsError);
        Assert.True(commands.Execute("CHKOBJITG OBJ(*SYSTEM) CHKLIC(*YES)").IsError);
        Assert.False(commands.Execute($"RVKCERT CERT({_signer.Id})").IsError);
        Assert.True(commands.Execute("CHKOBJITG OBJ('/QSYS.LIB/QGPL.LIB/DEPLOY.PGM') CHKSIG(*ALL)").IsError);
    }

    private byte[] Export(CertificatePurpose purpose) => _system.SignedDeployments.Export("test-package",
        new[] { new QualifiedObject("QGPL", "DEPLOY", ObjectType.Program) }, purpose == CertificatePurpose.ServiceDeployment ? "TESTSVC" : null);
    private SignedDeploymentResult Apply(byte[] content, ObjectSignature signature, CertificatePurpose purpose) => purpose switch
    {
        CertificatePurpose.Restore => _system.SignedDeployments.RestoreObjects(content, signature),
        CertificatePurpose.Update => _system.SignedDeployments.ApplyObjectUpdate(content, signature),
        _ => _system.SignedDeployments.DeployService(content, signature),
    };
    public void Dispose() => _system.Dispose();
}
