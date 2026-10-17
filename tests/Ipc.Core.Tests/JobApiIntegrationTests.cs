using System.Buffers.Binary;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobApiIntegrationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    public JobApiIntegrationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "NativeJobIntegration28");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "NativeJobReader28");
        _sources = new(_system.Connections, _system.Objects); _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Compile(string source, bool rpg = false)
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", "JOBAPIS", source, null);
        var compiled = new CommandService(_system).Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/JOBAPIS) SRCFILE(QGPL/SOURCE)");
        Assert.False(compiled.IsError, compiled.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_lists_current_job_in_QTEMP_then_retrieves_by_opaque_ID_and_receives_its_library_list(int ccsid)
    {
        Compile("""
            PGM
            DCL &LIST *CHAR LEN(512)
            DCL &INFO *CHAR LEN(512)
            DCL &ID *CHAR LEN(16)
            CALL QSYS/QUSCRTUS PARM('JOBS      QTEMP     ' '' (512 (*INT 4)) X'FE' '*CHANGE' ('Native job list' (*CHAR 50)))
            CALL QSYS/QUSLJOB PARM('JOBS      QTEMP     ' 'JOBL0100' '*' '*ACTIVE' X'00000000')
            CALL QSYS/QUSRTVUS PARM('JOBS      QTEMP     ' (1 (*INT 4)) (512 (*INT 4)) &LIST X'00000000')
            IF (%BIN(&LIST 133 4) *NE 1 *OR %BIN(&LIST 125 4) *NE 388) THEN(RETURN)
            CHGVAR &ID %SST(&LIST 415 16)
            CALL QSYS/QUSRJOBI PARM(&INFO (86 (*INT 4)) 'JOBI0100' '*INT' &ID X'00000000')
            IF (%BIN(&INFO 1 4) *NE 86 *OR %SST(&INFO 35 16) *NE &ID *OR %SST(&INFO 61 1) *NE 'I') THEN(RETURN)
            CALL QSYS/QUSRJOBI PARM(&INFO (512 (*INT 4)) 'JOBI0700' '*' '' X'00000000')
            IF (%BIN(&INFO 65 4) *EQ 5 *AND %BIN(&INFO 77 4) *EQ 2) THEN(SNDPGMMSG MSG('CL NATIVE JOB APIS ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/JOBAPIS"); Assert.False(result.IsError, result.Message); Assert.Contains("CL NATIVE JOB APIS ACCEPTED", result.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Direct_RPG_calls_list_user_space_and_retrieval_without_a_CL_wrapper(int ccsid)
    {
        Compile("""
            **free
            dcl-s LIST char(512) ccsid(*hex);
            dcl-s INFO char(128) ccsid(*hex);
            dcl-s ID char(16) ccsid(*hex);
            dcl-s SIZE int(10) inz(128);
            callp QUSCRTUS('JOBS      QTEMP     ':'':512:X'FE':'*CHANGE':'Native RPG job APIs');
            callp QUSLJOB('JOBS      QTEMP     ':'JOBL0100':'*':'*ACTIVE':X'00000000');
            callp QUSRTVUS('JOBS      QTEMP     ':1:512:LIST:X'00000000');
            eval ID = %subst(LIST:415:16);
            callp QUSRJOBI(INFO:SIZE:'JOBI0100':'*INT':ID:X'00000000');
            if %subst(INFO:1:4) = X'00000056' and %subst(INFO:35:16) = ID and %subst(INFO:61:1) = 'I';
              sndpgmmsg 'RPG NATIVE JOB APIS ACCEPTED';
            endif;
            seton LR;
            """, rpg: true);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/JOBAPIS"); Assert.False(result.IsError, result.Message); Assert.Contains("RPG NATIVE JOB APIS ACCEPTED", result.Message);
    }
    [Fact]
    public void API_authority_trust_and_manifests_are_checked_before_any_receiver_or_user_space_write()
    {
        var spaces = new UserSpaceStore(_system.Connections); spaces.Create(new("QGPL", "JOBS"), 512, 0xFE);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        var ca = _system.Certificates.CreateAuthority("JOBROOT", "Job API signer"); _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("JOBSIGN", "Native jobs", ca.Id, CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QUSLJOB", ObjectType.Program, signer.Id);
        object?[] parameters = ["JOBS      QGPL      ", "JOBL0100", "*", "*ACTIVE", new ProgramBuffer(new byte[4], 37)];
        Assert.False(session.ExecuteProgram("QSYS/QUSLJOB", parameters).IsError); var before = spaces.Read(new("QGPL", "JOBS"), 1, 512);
        _system.Security.Authority.Grant("QSYS", "QUSLJOB", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QUSLJOB", parameters).MessageId); Assert.Equal(before, spaces.Read(new("QGPL", "JOBS"), 1, 512));
        _system.Security.Authority.Grant("QSYS", "QUSLJOB", ObjectType.Program, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false); Assert.True(session.ExecuteProgram("QSYS/QUSLJOB", parameters).IsError);
        Assert.Equal(before, spaces.Read(new("QGPL", "JOBS"), 1, 512));
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        using var connection = _system.Connections.Open(); using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE sys_objects SET source='invalid job API manifest' WHERE lib='QSYS' AND name='QUSLJOB' AND type='*PGM'"; corrupt.ExecuteNonQuery();
        Assert.True(session.ExecuteProgram("QSYS/QUSLJOB", parameters).IsError); Assert.Equal(before, spaces.Read(new("QGPL", "JOBS"), 1, 512));
    }
    [Fact]
    public void Native_list_generation_obeys_target_space_authority_and_object_allocations()
    {
        var spaces = new UserSpaceStore(_system.Connections); var name = new QualifiedName("QGPL", "JOBS"); spaces.Create(name, 512, 0xFE, authority: "*EXCLUDE");
        using var owner = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var other = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        object?[] parameters = ["JOBS      QGPL      ", "JOBL0100", "*", "*ACTIVE", new ProgramBuffer(new byte[4], 37)];
        Assert.Equal("CPF9802", other.ExecuteProgram("QSYS/QUSLJOB", parameters).MessageId);
        _system.Security.Authority.Grant("QGPL", "JOBS", ObjectType.UserSpace, "QUSER", Authorities.ChangeBits);
        Assert.False(owner.Execute("ALCOBJ OBJ((QGPL/JOBS *USRSPC *EXCL))").IsError);
        Assert.True(other.ExecuteProgram("QSYS/QUSLJOB", parameters).IsError); Assert.All(spaces.Read(name, 1, 512), b => Assert.Equal(0xFE, b));
        Assert.False(owner.Execute("DLCOBJ OBJ((QGPL/JOBS *USRSPC *EXCL))").IsError);
        Assert.False(other.ExecuteProgram("QSYS/QUSLJOB", parameters).IsError);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(spaces.Read(name, 133, 4)));
    }
    [Fact]
    public void Retrieval_checks_live_API_authority_and_signing_before_writing_its_receiver()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        ProgramBuffer value = new(Enumerable.Repeat((byte)0xFE, 128).ToArray(), 37);
        var receiver = new ProgramArgument(() => value, v => value = (ProgramBuffer)v!, v => v, "*CHAR", 128, buffer: () => value);
        object?[] parameters = [receiver, 128, "JOBI0100", "*", "", new ProgramBuffer(new byte[4], 37)];
        var ca = _system.Certificates.CreateAuthority("READROOT", "Job retrieval signer"); _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("READSIGN", "Retrieve native job", ca.Id, CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QUSRJOBI", ObjectType.Program, signer.Id);
        Assert.False(session.ExecuteProgram("QSYS/QUSRJOBI", parameters).IsError); var before = value.ToArray();
        _system.Security.Authority.Grant("QSYS", "QUSRJOBI", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QUSRJOBI", parameters).MessageId); Assert.Equal(before, value.ToArray());
        _system.Security.Authority.Grant("QSYS", "QUSRJOBI", ObjectType.Program, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        Assert.True(session.ExecuteProgram("QSYS/QUSRJOBI", parameters).IsError); Assert.Equal(before, value.ToArray());
    }
    public void Dispose() => _system.Dispose();
}
