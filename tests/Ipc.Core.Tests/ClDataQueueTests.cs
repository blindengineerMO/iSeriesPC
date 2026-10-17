using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClDataQueueTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ClDataQueueTests()
    { _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DataQueueFixture24"); }
    private ExecutionSession Session(int ccsid = 37, string profile = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(profile, ccsid: ccsid), CancellationToken.None);
    private static void Execute(ExecutionSession session, string command) { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_sends_and_receives_raw_bytes_keys_lengths_peek_and_timeout(int ccsid)
    {
        using var session = Session(ccsid);
        Execute(session, "CRTDTAQ QGPL/BINARY 8 SEQ(*KEYED) KEYLEN(2) SENDERID(*YES)");
        ClExternalCallTests.Create(_system, "DQUEUE", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(8) VALUE(X'FF00FE0100000000')
            DCL &KEY *CHAR LEN(2) VALUE(X'00FF')
            DCL &RETURNED *CHAR LEN(2)
            DCL &SIZE *DEC LEN(5 0)
            SNDDTAQ DTAQ(QGPL/BINARY) DATA(&DATA) LEN(4) KEY(&KEY)
            RCVDTAQ DTAQ(QGPL/BINARY) KEY(&KEY) KEYORDER(*GE) RMV(*NO) RTNVAR(&DATA) LENVAR(&SIZE) KEYVAR(&RETURNED)
            IF (&SIZE *NE 4 *OR %SST(&DATA 1 4) *NE X'FF00FE01' *OR &RETURNED *NE &KEY) THEN(RETURN)
            RCVDTAQ DTAQ(QGPL/BINARY) KEY(&KEY) RTNVAR(&DATA)
            SNDDTAQ DTAQ(QGPL/BINARY) DATA('OTHER') KEY(&KEY)
            CLRDTAQ DTAQ(QGPL/BINARY) KEY(&KEY)
            RCVDTAQ DTAQ(QGPL/BINARY) KEY(&KEY) RTNVAR(&DATA) LENVAR(&SIZE) KEYVAR(&RETURNED)
            IF (&SIZE *EQ 0 *AND &DATA *EQ ' ' *AND &RETURNED *EQ ' ') THEN(SNDPGMMSG MSG('DATA QUEUE ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/DQUEUE"); Assert.False(result.IsError, result.Message); Assert.Contains("DATA QUEUE ACCEPTED", result.Message);
        Execute(session, "DLTDTAQ QGPL/BINARY"); Assert.False(_system.Objects.Exists("QGPL", "BINARY", ObjectType.DataQueue));
    }

    [Fact]
    public void Short_or_bad_return_variables_do_not_consume_or_change_outputs()
    {
        using var session = Session(); Execute(session, "CRTDTAQ QGPL/BINARY 8 SEQ(*KEYED) KEYLEN(2)");
        Execute(session, "SNDDTAQ QGPL/BINARY X'FF00FE01' KEY(X'0011')");
        ClExternalCallTests.Create(_system, "DQBAD", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(2) VALUE('OK')
            DCL &KEY *CHAR LEN(1) VALUE('K')
            DCL &LEN *DEC LEN(5 0) VALUE(99)
            RCVDTAQ QGPL/BINARY KEY(X'0011') RTNVAR(&DATA) LENVAR(&LEN)
            MONMSG CPF9506
            IF (&DATA *NE 'OK' *OR &LEN *NE 99) THEN(RETURN)
            RCVDTAQ QGPL/BINARY KEY(X'0011') LENVAR(&DATA)
            MONMSG IPC0003
            RCVDTAQ QGPL/BINARY KEY(X'0011') KEYVAR(&KEY)
            MONMSG CPF9506
            IF (&DATA *EQ 'OK' *AND &LEN *EQ 99 *AND &KEY *EQ 'K') THEN(SNDPGMMSG MSG('RECEIVE ATOMICITY ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/DQBAD"); Assert.False(result.IsError, result.Message); Assert.Contains("RECEIVE ATOMICITY ACCEPTED", result.Message);
        Assert.Equal(1, new DataQueueStore(_system.Connections).Inspect(new("QGPL", "BINARY")).Entries);
        Assert.True(session.Execute("RCVDTAQ QGPL/BINARY KEY(X'0011') RTNVAR(&DATA)").IsError);
    }

    [Theory]
    [InlineData("TYPE(*DDM)")]
    [InlineData("FORCE(*YES)")]
    [InlineData("SEQ(*KEYED)")]
    [InlineData("KEYLEN(2)")]
    [InlineData("MAXLEN(64513)")]
    public void Unimplemented_and_invalid_create_options_fail_without_objects(string options)
    {
        using var session = Session();
        var command = options.StartsWith("MAXLEN") ? "CRTDTAQ QGPL/BINARY " + options : "CRTDTAQ QGPL/BINARY MAXLEN(8) " + options;
        Assert.True(session.Execute(command).IsError);
        Assert.False(_system.Objects.Exists("QGPL", "BINARY", ObjectType.DataQueue));
    }

    [Fact]
    public void Library_list_resolution_does_not_require_read_authority_to_send()
    {
        using var admin = Session(); Execute(admin, "CRTDTAQ QGPL/BINARY 8");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DataQueueUserFixture24");
        _system.Security.Authority.Grant("QGPL", "BINARY", ObjectType.DataQueue, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var user = Session(profile: "QUSER"); Execute(user, "SNDDTAQ BINARY 'HELLO'");
        Assert.True(user.Execute("RCVDTAQ BINARY").IsError); Assert.True(user.Execute("CLRDTAQ BINARY").IsError);
        _system.Security.Authority.Grant("QSYS", "SNDDTAQ", ObjectType.Command, "QUSER", AuthorityBit.None);
        Assert.True(user.Execute("SNDDTAQ BINARY 'DENIED'").IsError);
        Assert.Equal(1, new DataQueueStore(_system.Connections).Inspect(new("QGPL", "BINARY")).Entries);
    }

    [Fact]
    public async Task Separate_jobs_send_to_a_waiting_compiled_CL_receiver()
    {
        using var reader = Session(); using var sender = Session(); Execute(sender, "CRTDTAQ QGPL/BINARY 8");
        ClExternalCallTests.Create(_system, "DQWAIT", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(8)
            DCL &LENGTH *DEC LEN(5 0)
            RCVDTAQ QGPL/BINARY WAIT(5) RTNVAR(&DATA) LENVAR(&LENGTH)
            IF (&DATA *EQ 'READY' *AND &LENGTH *EQ 5) THEN(SNDPGMMSG MSG('WAITED RECEIVE ACCEPTED'))
            ENDPGM
            """);
        var running = Task.Run(() => reader.Execute("CALL QGPL/DQWAIT"));
        await Task.Delay(100); Execute(sender, "SNDDTAQ QGPL/BINARY 'READY'");
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(result.IsError, result.Message); Assert.Contains("WAITED RECEIVE ACCEPTED", result.Message);
    }

    [Fact]
    public void Direct_receipt_returns_a_bounded_hex_listing_and_an_empty_queue_message()
    {
        using var session = Session(); Execute(session, "CRTDTAQ QGPL/BINARY 300");
        Execute(session, "SNDDTAQ QGPL/BINARY X'" + new string('A', 600) + "'");
        var result = session.Execute("RCVDTAQ QGPL/BINARY"); Assert.False(result.IsError, result.Message);
        Assert.Contains("first 256 bytes", result.Listing![0]); Assert.True(result.Listing[0].Length < 600);
        Assert.Equal("No data-queue entry available.", session.Execute("RCVDTAQ QGPL/BINARY").Message);
    }
    public void Dispose() => _system.Dispose();
}
