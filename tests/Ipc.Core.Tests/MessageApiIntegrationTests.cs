using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class MessageApiIntegrationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
    public MessageApiIntegrationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "NativeMessages27");
        _sources = new(_system.Connections, _system.Objects); _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Compile(string text, string name = "APP", bool rpg = false)
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", name, text, null);
        var result = new CommandService(_system).Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + $" PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE)");
        Assert.False(result.IsError, result.Message);
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), _timeout.Token);

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_uses_its_own_program_queue_and_native_sender_information(int ccsid)
    {
        Compile("PGM\nDCL &KEY *CHAR LEN(4)\nDCL &RECEIVER *CHAR LEN(256)\n" +
            "CALL QSYS/QMHSNDPM PARM('' '' 'OK' (2 (*INT 4)) '*INFO' '*' (0 (*INT 4)) &KEY X'00000000')\n" +
            "CALL QSYS/QMHRCVPM PARM(&RECEIVER (256 (*INT 4)) 'RCVM0200' '*' (0 (*INT 4)) '*INFO' &KEY (0 (*INT 4)) '*SAME' X'00000000')\n" +
            "IF (%BIN(&RECEIVER 1 4) *EQ 178 *AND %SST(&RECEIVER 82 12) *EQ 'APP' *AND %SST(&RECEIVER 111 10) *EQ 'APP' *AND %SST(&RECEIVER 177 2) *EQ 'OK') THEN(SNDPGMMSG MSG('CL MESSAGE API ACCEPTED'))\nENDPGM");
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/APP");
        Assert.False(result.IsError, result.Message); Assert.Contains("CL MESSAGE API ACCEPTED", result.Message);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var message = Assert.Single(new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key), m => m.Data.ToText() == "OK");
        Assert.Equal("APP", message.Origin!.Program); Assert.False(message.Seen);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_RPG_calls_native_send_receive_reply_and_retrieve_without_a_CL_wrapper(int ccsid)
    {
        var messages = new MessageQueueStore(_system.Connections); messages.Create(new("QGPL", "INBOX")); messages.Create(new("QGPL", "REPLIES"));
        var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QGPL", "ERRORS");
        descriptions.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Value &1", "Correct &1", 30, [new("*CHAR", 3)], 37));
        Compile("**free\ndcl-s KEY char(4) ccsid(*hex);\ndcl-s INFO char(128) ccsid(65535);\ndcl-s SIZE int(10) inz(128);\ndcl-s ZERO int(10);\n" +
            "callp QMHSNDPM('':'':'SELF':4:'*INFO':'*':0:KEY:X'00000000');\n" +
            "callp QMHRCVPM(INFO:SIZE:'RCVM0100':'*':ZERO:'*INFO':KEY:ZERO:'*SAME':X'00000000');\n" +
            "if %subst(INFO:49:4) <> 'SELF';\nreturn;\nendif;\n" +
            "callp QMHSNDM('':'':'Proceed?':8:'*INQ':'INBOX     QGPL      ':1:'REPLIES   QGPL      ':KEY:X'00000000');\n" +
            "callp QMHRCVM(INFO:128:'RCVM0100':'INBOX     QGPL      ':'*INQ':'    ':0:'*SAME':X'00000000');\n" +
            "callp QMHSNDRM(%subst(INFO:22:4):'INBOX     QGPL      ':'Y':1:'*YES':X'00000000');\n" +
            "callp QMHRCVM(INFO:128:'RCVM0100':'REPLIES   QGPL      ':'*RPY':KEY:0:'*REMOVE':X'00000000');\n" +
            "if %subst(INFO:49:1) <> 'Y';\nreturn;\nendif;\n" +
            "callp QMHRTVM(INFO:SIZE:'RTVM0100':'ERR0001':'ERRORS    QGPL      ':'ABC':3:'*YES':'*NO':X'00000000');\n" +
            "if %subst(INFO:25:9) = 'Value ABC';\nsndpgmmsg 'DIRECT RPG MESSAGE APIS ACCEPTED';\nendif;\nseton LR;", rpg: true);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/APP");
        Assert.False(result.IsError, result.Message); Assert.Contains("DIRECT RPG MESSAGE APIS ACCEPTED", result.Message);
        Assert.Empty(messages.List(new("QGPL", "INBOX"))); Assert.Empty(messages.List(new("QGPL", "REPLIES")));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_RPG_message_production_drives_an_explicit_key_message_subfile(int ccsid)
    {
        new DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", Dds(keywords: "CA03(03)") +
            Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") + Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ") +
            Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL"));
        Compile("**free\ndcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc) sfile(ROWS:RRN);\ndcl-s RRN packed(4:0);\n" +
            "callp QMHSNDPM('':'':'NATIVE PROGRAM MESSAGE':22:'*INFO':'*':0:KEY:X'00000000');\n" +
            "eval QUEUE = '*';\neval RRN = 1;\nwrite ROWS;\nexfmt CTL;\nsndpgmmsg 'NATIVE SUBFILE ACCEPTED';\nseton LR;", rpg: true);
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        using var menu = new MenuController(_system, profile, cancellationToken: _timeout.Token);
        foreach (var ch in "CALL QGPL/APP") menu.Handle(new(AidKey.None, Character: ch));
        Assert.Equal("ProgramDisplay", menu.Handle(new(AidKey.Enter)).State); Assert.Contains("NATIVE PROGRAM MESSAGE", menu.Buffer.RowText(4));
        Assert.Equal("Menu", menu.Handle(new(AidKey.Pf3)).State); Assert.Contains("NATIVE SUBFILE ACCEPTED", menu.Buffer.RowText(24));
    }

    [Fact]
    public void Live_API_authority_and_manifest_checks_precede_message_mutation()
    {
        Compile("PGM\nDCL &KEY *CHAR LEN(4)\nCALL QSYS/QMHSNDPM PARM('' '' 'UNREACHED' (9 (*INT 4)) '*INFO' '*' (0 (*INT 4)) &KEY X'00000000')\n" +
            "MONMSG CPF9802 EXEC(SNDPGMMSG MSG('API AUTHORITY MONITORED'))\nENDPGM");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READER"), "NativeMessageReader27");
        using var session = Session(user: "READER");
        _system.Security.Authority.Grant("QSYS", "QMHSNDPM", ObjectType.Program, "READER", AuthorityBit.None);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("API AUTHORITY MONITORED", result.Message);
        var descriptor = _system.Objects.GetRequired("QSYS", "QMHSNDPM", ObjectType.Program); descriptor.Source = "{\"api\":\"QMHSNDPM\",\"version\":2}";
        Assert.Equal("IPC0136", Assert.Throws<CpfException>(() => _system.Objects.Update(descriptor)).MessageId);
        using (var connection = _system.Connections.Open())
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE sys_objects SET source=$source WHERE lib='QSYS' AND name='QMHSNDPM' AND type='*PGM'";
            corrupt.Parameters.AddWithValue("$source", descriptor.Source); corrupt.ExecuteNonQuery();
        }
        using var admin = Session(); Assert.True(admin.Execute("CALL QGPL/APP").IsError);
        using var identity = OperationIdentity.Enter("QSECOFR", admin.Job.Key);
        Assert.DoesNotContain(new MessageQueueStore(_system.Connections).ListJobMessages(admin.Job.Key), m => m.Data.ToText() == "UNREACHED");
    }

    [Fact]
    public void Native_APIs_resolve_each_jobs_private_queue_and_return_logical_message_file_metadata()
    {
        using var first = Session(1208); using var second = Session();
        foreach (var (session, text) in new[] { (first, "First &1"), (second, "Second &1") })
        {
            using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
            new MessageQueueStore(_system.Connections).Create(new("QTEMP", "INBOX"));
            var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QTEMP", "ERRORS");
            descriptions.Add("QTEMP", "ERRORS", "ERR0001", new MessageDescription(text, "Private &1", 30, [new("*CHAR", 3)], 37));
        }
        Compile("PGM\nDCL &INFO *CHAR LEN(256)\n" +
            "CALL QSYS/QMHSNDM PARM('ERR0001' 'ERRORS    QTEMP     ' 'ABC' (3 (*INT 4)) '*DIAG' 'INBOX     QTEMP     ' (1 (*INT 4)) '' '' X'00000000')\n" +
            "CALL QSYS/QMHRCVM PARM(&INFO (256 (*INT 4)) 'RCVM0200' 'INBOX     QTEMP     ' '*ANY' '' (0 (*INT 4)) '*SAME' X'00000000')\n" +
            "IF (%SST(&INFO 36 10) *EQ 'QTEMP' *AND %SST(&INFO 46 10) *EQ 'QTEMP') THEN(SNDPGMMSG MSG('PRIVATE MESSAGE APIS ACCEPTED'))\nENDPGM");
        foreach (var (session, expected) in new[] { (first, "First ABC"), (second, "Second ABC") })
        {
            var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("PRIVATE MESSAGE APIS ACCEPTED", result.Message);
            using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
            var found = Assert.Single(new MessageQueueStore(_system.Connections).List(new("QTEMP", "INBOX")));
            Assert.Equal(expected, found.Data.ToText()); Assert.Equal("QTEMP", found.Predefined!.Library); Assert.False(found.Seen);
        }
    }

    [Fact]
    public async Task ENDJOB_cancels_a_real_RPG_native_receive_before_job_resource_cleanup()
    {
        new MessageQueueStore(_system.Connections).Create(new("QGPL", "INBOX"));
        Compile("**free\ndcl-s INFO char(128) ccsid(*hex);\n" +
            "callp QMHRCVM(INFO:128:'RCVM0100':'INBOX     QGPL      ':'*ANY':'    ':-1:'*REMOVE':X'00000000');\n" +
            "sndpgmmsg 'UNREACHED';\nseton LR;", rpg: true);
        using var session = Session(); var running = Task.Run(() => session.Execute("CALL QGPL/APP"));
        try
        {
            while (ReceiverCount() == 0) await Task.Delay(10, _timeout.Token);
            _system.Jobs.RequestEnd(session.Job.Key);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(_timeout.Token));
            Assert.Equal(0, ReceiverCount());
        }
        finally { _timeout.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }
    private long ReceiverCount()
    {
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM sys_message_receivers"; return (long)query.ExecuteScalar()!;
    }
    public void Dispose() { _timeout.Dispose(); _system.Dispose(); }
}
