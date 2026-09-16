using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class QcmdexcTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public QcmdexcTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "QcmdexcFixture22");
    }
    private ExecutionSession Session(int ccsid = 37) => new(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
    private void Program(string name, string source) => ClExternalCallTests.Create(_system, name, "CLP", source);

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Direct_and_compiled_calls_change_the_current_job_and_preserve_input_storage(int ccsid)
    {
        using var session = Session(ccsid);
        var direct = session.Execute("CALL QSYS/QCMDEXC PARM('CHGCURLIB QUSRSYS' 17 'IGC')");
        Assert.False(direct.IsError, direct.Message); Assert.Equal("QUSRSYS", session.Job.CurrentLibrary);
        Program("DYNAMIC", """
            PGM
            DCL &CMD *CHAR LEN(40) VALUE('CHGCURLIB QGPL')
            DCL &LEN *DEC LEN(15 5) VALUE(14)
            CALL QSYS/QCMDEXC PARM(&CMD &LEN)
            IF (&CMD *NE 'CHGCURLIB QGPL' *OR &LEN *NE 14) THEN(SNDPGMMSG MSG('INPUT CHANGED'))
            SNDPGMMSG MSG('DYNAMIC ACCEPTED')
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/DYNAMIC");
        Assert.False(result.IsError, result.Message); Assert.DoesNotContain("INPUT CHANGED", result.Message);
        Assert.Contains("DYNAMIC ACCEPTED", result.Message); Assert.Equal("QGPL", session.Job.CurrentLibrary);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("14.5")]
    [InlineData("33")]
    [InlineData("32703")]
    [InlineData("(14 (*INT 4))")]
    [InlineData("(14 (*DEC 5 0))")]
    [InlineData("'14'")]
    [InlineData("X'000000001A00000C'")]
    public void Invalid_lengths_fail_before_dispatch(string length)
    {
        using var session = Session();
        var result = session.Execute("CALL QSYS/QCMDEXC PARM('CHGCURLIB QUSRSYS' " + length + ")");
        Assert.True(result.IsError); Assert.Equal("QGPL", session.Job.CurrentLibrary);
    }

    [Theory]
    [InlineData("PARM('CHGCURLIB QUSRSYS')")]
    [InlineData("PARM('CHGCURLIB QUSRSYS' 17 'igc')")]
    [InlineData("PARM('CHGCURLIB QUSRSYS' 17 X'C9C7')")]
    [InlineData("PARM('CHGCURLIB QUSRSYS' 17 'IGC' 'EXTRA')")]
    [InlineData("PARM(123 3)")]
    public void Invalid_argument_groups_fail_before_dispatch(string arguments)
    {
        using var session = Session(); Assert.True(session.Execute("CALL QCMDEXC " + arguments).IsError);
        Assert.Equal("QGPL", session.Job.CurrentLibrary);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Packed_length_fixture_selects_only_command_bytes_and_ignores_invalid_trailing_storage(int ccsid)
    {
        using var session = Session(ccsid);
        var command = new ProgramBuffer(CodePage.ToBytes(ccsid, "CHGCURLIB QGPL").Concat(new byte[] { 255, 0 }).ToArray(), ccsid);
        var length = new ProgramBuffer(Convert.FromHexString("000000001400000C"), ccsid);
        var result = session.ExecuteProgram("QSYS/QCMDEXC", new object?[] { command, length });
        Assert.False(result.IsError, result.Message); Assert.Equal("QGPL", session.Job.CurrentLibrary);
        Assert.Equal(16, command.Length); Assert.Equal("000000001400000C", Convert.ToHexString(length.ToArray()));
    }

    [Fact]
    public void Utf8_split_and_mismatched_ccsid_are_rejected_before_execution()
    {
        using var session = Session(1208);
        Assert.True(session.ExecuteProgram("QCMDEXC", new object?[] { new ProgramBuffer(new byte[] { 195, 169 }, 1208), 1m }).IsError);
        Assert.True(session.ExecuteProgram("QCMDEXC", new object?[] { new ProgramBuffer(CodePage.ToBytes(37, "CHGCURLIB QGPL"), 37), 14m }).IsError);
    }

    [Fact]
    public void Command_escape_keeps_its_message_id_and_can_be_monitored_by_the_caller()
    {
        Program("MONAPI", """
            PGM
            DCL &ID *CHAR LEN(7)
            CALL QCMDEXC PARM('CHGCURLIB MISSING' 17)
            MONMSG CPF9801 EXEC(DO)
            RCVMSG MSGTYPE(*EXCP) MSGID(&ID)
            IF (&ID *EQ 'CPF9801') THEN(SNDPGMMSG MSG('API ERROR MONITORED'))
            ENDDO
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL QGPL/MONAPI");
        Assert.False(result.IsError, result.Message); Assert.Contains("API ERROR MONITORED", result.Message);
    }

    [Fact]
    public void Api_and_command_authority_are_checked_on_each_call()
    {
        var service = new CommandService(_system);
        using (OperationIdentity.Enter("QUSER")) { var first = service.Execute("CALL QCMDEXC PARM('DSPCMD CMD(DSPJOB)' 18)"); Assert.False(first.IsError, first.Message); }
        _system.Security.Authority.Grant("QSYS", "DSPCMD", ObjectType.Command, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF9802", service.Execute("CALL QCMDEXC PARM('DSPCMD CMD(DSPJOB)' 18)").MessageId);
        _system.Security.Authority.Grant("QSYS", "QCMDEXC", ObjectType.Program, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF9802", service.Execute("CALL QCMDEXC PARM('DSPCMD CMD(DSPJOB)' 18)").MessageId);
    }

    [Fact]
    public void Adapter_payload_cannot_be_tampered_or_relocated_and_existing_programs_are_not_reseeded()
    {
        var descriptor = _system.Objects.GetRequired("QSYS", "QCMDEXC", ObjectType.Program);
        descriptor.Source = "{}"; Assert.Throws<CpfException>(() => _system.Objects.Update(descriptor));
        Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "QCMDEXC"), ObjectType = ObjectType.Program, Attribute = BuiltinProgramService.Attribute, Source = "{}" }));
        descriptor.Attribute = "CLP"; descriptor.Source = "SNDPGMMSG MSG('REPLACED')"; _system.Objects.Update(descriptor);
        BuiltinProgramService.SeedDefaults(_system.Connections);
        Assert.Equal("REPLACED", new CommandService(_system).Execute("CALL QSYS/QCMDEXC").Message);
    }

    [Fact]
    public void Rpg_semantic_adapter_runs_the_same_command_in_the_same_job()
    {
        var character = ClExternalCallTests.Declaration("CMD", "A", "32").ToCharArray(); character[40] = ' ';
        var declaration = new string(character) + ClExternalCallTests.Declaration("LEN", "P", "15");
        var source = declaration + ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("CMD", "PARM") +
            ClExternalCallTests.Calculation("LEN", "PARM") + ClExternalCallTests.Calculation("PL1", "PLIST") +
            ClExternalCallTests.Calculation("CMD", "PARM") + ClExternalCallTests.Calculation("LEN", "PARM") + ClExternalCallTests.Calculation("'QCMDEXC'", "CALL", "PL1");
        ClExternalCallTests.Create(_system, "RPGAPI", "RPG", source);
        using var session = Session();
        var result = session.ExecuteProgram("QGPL/RPGAPI", new object?[] { "CHGCURLIB QUSRSYS", 17m });
        Assert.False(result.IsError, result.Message); Assert.Equal("QUSRSYS", session.Job.CurrentLibrary);
    }

    [Fact]
    public void Recursive_dynamic_calls_are_bounded_and_a_following_command_still_runs()
    {
        Program("RECURAPI", "CALL QCMDEXC PARM('CALL QGPL/RECURAPI' 18)");
        using var session = Session(); var result = session.Execute("CALL QGPL/RECURAPI");
        Assert.True(result.IsError);
        Assert.False(session.Execute("CALL QCMDEXC PARM('CHGCURLIB QUSRSYS' 17)").IsError);
        Assert.Equal("QUSRSYS", session.Job.CurrentLibrary);
    }

    [Fact]
    public void Maximum_command_storage_executes_and_one_more_byte_fails()
    {
        using var session = Session();
        var command = "CHGCURLIB QUSRSYS".PadRight(32703);
        var rejected = session.ExecuteProgram("QCMDEXC", new object?[] { command, 32703m });
        Assert.True(rejected.IsError); Assert.Equal("QGPL", session.Job.CurrentLibrary);
        var result = session.ExecuteProgram("QCMDEXC", new object?[] { command, 32702m });
        Assert.False(result.IsError, result.Message); Assert.Equal("QUSRSYS", session.Job.CurrentLibrary);
    }

    [Fact]
    public void Revoked_signing_trust_prevents_api_execution()
    {
        var ca = _system.Certificates.CreateAuthority("APIROOT", "API test authority");
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("APISIGNER", "API signer", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QCMDEXC", ObjectType.Program, signer.Id);
        using var session = Session();
        Assert.False(session.Execute("CALL QCMDEXC PARM('CHGCURLIB QGPL' 14)").IsError);
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, false);
        Assert.True(session.Execute("CALL QCMDEXC PARM('CHGCURLIB QUSRSYS' 17)").IsError);
        Assert.Equal("QGPL", session.Job.CurrentLibrary);
    }

    [Fact]
    public void Cancellation_interrupts_a_command_called_through_the_api()
    {
        Program("WAITAPI", "DOWHILE COND('1')\nENDDO");
        using var cancellation = new CancellationTokenSource();
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        Assert.ThrowsAny<OperationCanceledException>(() => session.Execute("CALL QCMDEXC PARM('CALL QGPL/WAITAPI' 17)"));
    }

    public void Dispose() => _system.Dispose();
}
