using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class DateTimeApiIntegrationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    public DateTimeApiIntegrationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DateApiAdministrator");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DateApiCaller");
        _sources = new(_system.Connections, _system.Objects); _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    [Theory]
    [InlineData(37, false)]
    [InlineData(1208, false)]
    [InlineData(37, true)]
    [InlineData(1208, true)]
    public void Compiled_CL_and_direct_RPG_round_trip_raw_native_timestamps(int ccsid, bool rpg)
    {
        var source = rpg ? """
            **free
            dcl-s RAW char(8) ccsid(*hex);
            dcl-s OUT char(20) ccsid(*hex);
            callp QWCCVTDT('*YYMD':'20000101000000123456':'*DTS':RAW:X'00000000':'*UTC':'*UTC':'':0:'1');
            callp QWCCVTDT('*DTS':RAW:'*YYMD':OUT:X'00000000':'*UTC':'*UTC':'':0:'1');
            if RAW = X'800000001E240000' and OUT = '20000101000000123456';
              sndpgmmsg 'NATIVE DATE API ACCEPTED';
            endif;
            seton LR;
            """ : """
            PGM
            DCL &RAW *CHAR LEN(8)
            DCL &OUT *CHAR LEN(17)
            CALL QSYS/QWCCVTDT PARM('*YYMD' '20000101000000000' '*DTS' &RAW X'00000000')
            CALL QSYS/QWCCVTDT PARM('*DTS' &RAW '*YYMD' &OUT X'00000000')
            IF (&OUT *EQ '20000101000000000') THEN(SNDPGMMSG MSG('NATIVE DATE API ACCEPTED'))
            ENDPGM
            """;
        _sources.SaveSourceMember("QGPL", "SOURCE", "DATEAPI", source, null);
        var commands = new CommandService(_system);
        var result = commands.Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/DATEAPI) SRCFILE(QGPL/SOURCE)"); Assert.False(result.IsError, result.Message);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER", ccsid: ccsid), CancellationToken.None);
        result = session.Execute("CALL QGPL/DATEAPI"); Assert.False(result.IsError, result.Message); Assert.Contains("NATIVE DATE API ACCEPTED", result.Message);
    }
    [Fact]
    public void Live_authority_signing_and_manifest_checks_precede_conversion_and_output_writes()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        ProgramBuffer bytes = new(Enumerable.Repeat((byte)0xFE, 24).ToArray(), 37);
        var output = new ProgramArgument(() => bytes, v => bytes = (ProgramBuffer)v!, v => v, "*CHAR", 24, buffer: () => bytes);
        object?[] parameters = ["*YYMD", "20000101000000000", "*DTS", output, new ProgramBuffer(new byte[4], 37)];
        var ca = _system.Certificates.CreateAuthority("DATECA", "Date signer"); _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("DATESIGN", "Date conversion", ca.Id, CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QWCCVTDT", ObjectType.Program, signer.Id);
        Assert.False(session.ExecuteProgram("QSYS/QWCCVTDT", parameters).IsError); var before = bytes.ToArray();
        _system.Security.Authority.Grant("QSYS", "QWCCVTDT", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QWCCVTDT", parameters).MessageId); Assert.Equal(before, bytes.ToArray());
        _system.Security.Authority.Grant("QSYS", "QWCCVTDT", ObjectType.Program, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false); Assert.True(session.ExecuteProgram("QSYS/QWCCVTDT", parameters).IsError); Assert.Equal(before, bytes.ToArray());
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        using var connection = _system.Connections.Open(); using var corrupt = connection.CreateCommand(); corrupt.CommandText = "UPDATE sys_objects SET source='bad date manifest' WHERE lib='QSYS' AND name='QWCCVTDT' AND type='*PGM'"; corrupt.ExecuteNonQuery();
        Assert.True(session.ExecuteProgram("QSYS/QWCCVTDT", parameters).IsError); Assert.Equal(before, bytes.ToArray());
    }
    public void Dispose() => _system.Dispose();
}
