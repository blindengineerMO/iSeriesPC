using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class CryptoApiIntegrationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public CryptoApiIntegrationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CryptoAdministrator29");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "CryptoCaller29");
    }
    [Theory]
    [InlineData(37, false)]
    [InlineData(1208, false)]
    [InlineData(37, true)]
    [InlineData(1208, true)]
    public void Compiled_CL_and_direct_RPG_call_all_six_crypto_adapters_and_share_key_contexts(int ccsid, bool rpg)
    {
        var sources = new SqliteFileStore(_system.Connections, _system.Objects); sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        sources.SaveSourceMember("QGPL", "SOURCE", "CRYPTO", rpg ? Ipc.Testing.CryptoProgramSources.RpgSource : Ipc.Testing.CryptoProgramSources.ClSource, null);
        var result = new CommandService(_system).Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/CRYPTO) SRCFILE(QGPL/SOURCE)"); Assert.False(result.IsError, result.Message);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER", ccsid: ccsid), CancellationToken.None);
        result = session.Execute("CALL QGPL/CRYPTO"); Assert.False(result.IsError, result.Message); Assert.Contains("NATIVE CRYPTO APIS ACCEPTED", result.Message);
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM sys_crypto_keys"; Assert.Equal(0L, query.ExecuteScalar());
    }
    [Fact]
    public void Live_API_authority_signatures_and_replaced_manifests_fail_before_hash_output()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        ProgramBuffer bytes = new(Enumerable.Repeat((byte)0xFE, 32).ToArray(), 37);
        var output = new ProgramArgument(() => bytes, v => bytes = (ProgramBuffer)v!, v => v, "*CHAR", 32, buffer: () => bytes);
        object?[] p = [new ProgramBuffer(new byte[] { 0x61, 0x62, 0x63 }, 37), 3, "DATA0100", 3, "ALGD0500", "1", "", output, new ProgramBuffer(new byte[4], 37)];
        var ca = _system.Certificates.CreateAuthority("CRYPTOCA", "Crypto signer"); _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("CRYPTOSIGN", "Crypto APIs", ca.Id, CertificatePurpose.ObjectSigning); _system.ObjectSigning.Sign("QSYS", "QC3CALHA", ObjectType.Program, signer.Id);
        Assert.False(session.ExecuteProgram("QSYS/QC3CALHA", p).IsError); var before = bytes.ToArray();
        _system.Security.Authority.Grant("QSYS", "QC3CALHA", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QC3CALHA", p).MessageId); Assert.Equal(before, bytes.ToArray());
        _system.Security.Authority.Grant("QSYS", "QC3CALHA", ObjectType.Program, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false); Assert.True(session.ExecuteProgram("QSYS/QC3CALHA", p).IsError); Assert.Equal(before, bytes.ToArray());
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        using var connection = _system.Connections.Open(); using var corrupt = connection.CreateCommand(); corrupt.CommandText = "UPDATE sys_objects SET source='bad crypto manifest' WHERE lib='QSYS' AND name='QC3CALHA' AND type='*PGM'"; corrupt.ExecuteNonQuery();
        Assert.True(session.ExecuteProgram("QSYS/QC3CALHA", p).IsError); Assert.Equal(before, bytes.ToArray());
    }
    public void Dispose() => _system.Dispose();
}
