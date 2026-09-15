using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClMessageCommandTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ClMessageCommandTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ClMessageFixture22");
    }
    private ExecutionSession Session(int ccsid = 37) => new(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
    private static void Execute(ExecutionSession session, string command)
    { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
    private void Program(string name, string source) => ClExternalCallTests.Create(_system, name, "CLP", source);
    [Fact]
    public void Called_program_diagnostics_arrive_on_the_callers_queue_and_same_frame_keys_roundtrip()
    {
        Program("DIAGCHILD", "SNDPGMMSG MSG('child diagnostic') MSGTYPE(*DIAG)");
        Program("DIAGPARENT", """
            PGM
            DCL &TEXT *CHAR LEN(32)
            DCL &KEY *CHAR LEN(4)
            CALL QGPL/DIAGCHILD
            RCVMSG MSGTYPE(*DIAG) MSG(&TEXT)
            IF (&TEXT *NE 'child diagnostic') THEN(RETURN)
            SNDPGMMSG MSG('local') TOPGMQ(*SAME) KEYVAR(&KEY)
            RCVMSG MSGKEY(&KEY) MSG(&TEXT)
            IF (&TEXT *EQ 'local') THEN(SNDPGMMSG MSG('FRAME QUEUE ACCEPTED'))
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL QGPL/DIAGPARENT");
        Assert.False(result.IsError, result.Message); Assert.Contains("FRAME QUEUE ACCEPTED", result.Message);
    }
    [Fact]
    public async Task Program_inquiry_receives_another_jobs_reply_using_the_sender_copy_key()
    {
        using var sender = Session(1208); using var responder = Session(1208);
        Execute(responder, "CRTMSGQ QGPL/INBOX");
        Program("WAITREPLY", """
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &REPLY *CHAR LEN(8)
            SNDPGMMSG MSG('Proceed?') MSGTYPE(*INQ) TOMSGQ(QGPL/INBOX) KEYVAR(&KEY)
            RCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) WAIT(5) MSG(&REPLY)
            IF (&REPLY *EQ 'YES') THEN(SNDPGMMSG MSG('PROGRAM REPLY ACCEPTED'))
            ENDPGM
            """);
        var running = Task.Run(() => sender.Execute("CALL QGPL/WAITREPLY"));
        var store = new MessageQueueStore(_system.Connections); var inbox = new QualifiedName("QGPL", "INBOX");
        QueuedMessage? inquiry = null;
        for (var attempt = 0; attempt < 200 && inquiry is null && !running.IsCompleted; attempt++)
        { inquiry = store.List(inbox).SingleOrDefault(); if (inquiry is null) await Task.Delay(20); }
        Assert.NotNull(inquiry);
        using (Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", responder.Job.Key))
            store.Reply(inbox, inquiry.Key, new ProgramBuffer("YES"u8, 1208));
        var result = await running; Assert.False(result.IsError, result.Message); Assert.Contains("PROGRAM REPLY ACCEPTED", result.Message);
        Assert.Empty(store.List(inbox));
    }
    [Fact]
    public void Escape_sent_to_caller_is_available_to_its_monitor_and_unremoved_messages_remain_in_job_log()
    {
        Program("ESCCHILD", "SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('escape payload') MSGTYPE(*ESCAPE)");
        Program("ESCPARENT", """
            PGM
            DCL &TEXT *CHAR LEN(32)
            DCL &ID *CHAR LEN(7)
            CALL QGPL/ESCCHILD
            MONMSG CPF9898 EXEC(DO)
              RCVMSG MSGTYPE(*EXCP) MSG(&TEXT) MSGID(&ID)
              IF (&TEXT *EQ 'escape payload' *AND &ID *EQ 'CPF9898') THEN(SNDPGMMSG MSG('ESCAPE RECEIVED'))
            ENDDO
            SNDPGMMSG MSG('retained diagnostic') MSGTYPE(*DIAG) TOPGMQ(*SAME)
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL QGPL/ESCPARENT");
        Assert.False(result.IsError, result.Message); Assert.Contains("ESCAPE RECEIVED", result.Message);
        var log = session.Execute("DSPJOBLOG"); Assert.False(log.IsError, log.Message);
        Assert.Contains(log.Listing!, line => line.Contains("retained diagnostic"));
        Assert.DoesNotContain(new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key), message => message.Kind == "ESCAPE");
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void MONMSG_can_receive_arithmetic_and_command_errors_from_its_program_queue(int ccsid)
    {
        Program("ERRQUEUE", """
            PGM
            DCL &VALUE *DEC LEN(5 0)
            DCL &ID *CHAR LEN(7)
            DCL &TEXT *CHAR LEN(100)
            DCL &COUNT *DEC LEN(2 0)
            DCL &TYPE *CHAR LEN(2)
            CHGVAR &VALUE (1 / 0)
            MONMSG MCH1211 EXEC(DO)
              RCVMSG MSGTYPE(*EXCP) MSGID(&ID) MSG(&TEXT) RTNTYPE(&TYPE)
              IF (&ID *EQ 'MCH1211' *AND &TYPE *EQ '15') THEN(CHGVAR &COUNT (&COUNT + 1))
            ENDDO
            DLTMSGQ MSGQ(QGPL/MISSING)
            MONMSG CPF9802 EXEC(DO)
              RCVMSG MSGTYPE(*EXCP) MSGID(&ID) MSG(&TEXT)
              IF (&ID *EQ 'CPF9802') THEN(CHGVAR &COUNT (&COUNT + 1))
            ENDDO
            IF (&COUNT *EQ 2) THEN(SNDPGMMSG MSG('RUNTIME ERRORS RECEIVED'))
            ENDPGM
            """);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/ERRQUEUE");
        Assert.False(result.IsError, result.Message); Assert.Contains("RUNTIME ERRORS RECEIVED", result.Message);
        Assert.DoesNotContain(new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key), m => m.Kind == "ESCAPE");
    }
    [Fact]
    public void Nested_unmonitored_errors_are_delivered_once_per_frame_and_remain_in_job_log()
    {
        Program("ERRINNER", "PGM\nDCL &N *DEC LEN(2 0)\nCHGVAR &N (1 / 0)\nENDPGM");
        Program("ERRMIDDLE", "CALL QGPL/ERRINNER");
        Program("ERROUTER", """
            PGM
            DCL &ID *CHAR LEN(7)
            CALL QGPL/ERRMIDDLE
            MONMSG MCH1211 EXEC(DO)
              RCVMSG MSGTYPE(*EXCP) MSGID(&ID)
              IF (&ID *EQ 'MCH1211') THEN(SNDPGMMSG MSG('NESTED ERROR RECEIVED'))
              RCVMSG MSGTYPE(*EXCP) MSGID(&ID)
              IF (&ID *NE ' ') THEN(SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('duplicate error') MSGTYPE(*ESCAPE))
            ENDDO
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL QGPL/ERROUTER");
        Assert.False(result.IsError, result.Message); Assert.Contains("NESTED ERROR RECEIVED", result.Message);
        var errors = new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key).Where(m => m.Kind == "ESCAPE").ToArray();
        Assert.Equal(2, errors.Length); Assert.All(errors, m => Assert.Equal("MCH1211", m.MessageId));
        Assert.Equal(errors[0].Data.ToArray(), errors[1].Data.ToArray());
        Assert.Contains(session.Execute("DSPJOBLOG").Listing!, line => line.Contains("MCH1211"));
    }
    [Fact]
    public void Full_program_message_quota_reports_delivery_failure_without_recursive_error_reporting()
    {
        using var session = Session();
        var environment = _system.JobRuntime.Environment(session.Job.Key)!;
        var queue = environment.MessageQueue();
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<4096)
                INSERT INTO sys_message_entries(queue_type,program_queue,kind,severity,data,ccsid,sender,sent)
                SELECT NULL,$queue,'INFO',0,X'41',1208,'QSYS','2026-09-15T00:00:00Z' FROM n
                """;
            command.Parameters.AddWithValue("$queue", queue.ProgramQueue!); command.ExecuteNonQuery();
        }
        Program("FULLMSG", "PGM\nDCL &N *DEC LEN(2 0)\nCHGVAR &N (1 / 0)\nMONMSG CPF2460 EXEC(RETURN)\nENDPGM");
        Execute(session, "CALL QGPL/FULLMSG");
        Assert.Equal(4096, new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key, limit: 4096).Count);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Commands_send_receive_and_reply_through_binary_CL_keys(int ccsid)
    {
        using var session = Session(ccsid); Execute(session, "CRTMSGQ QGPL/INBOX TEXT('Requests')"); Execute(session, "CRTMSGQ QGPL/REPLIES");
        // Force a key whose final byte is invalid UTF-8, independent of the program ABI codec.
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE sqlite_sequence SET seq=240 WHERE name='sys_message_entries'"; command.ExecuteNonQuery(); }
        Program("ROUNDTRIP", """
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &TEXT *CHAR LEN(8)
            DCL &SIZE *DEC LEN(5 0)
            SNDMSG MSG('Proceed?') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) RPYMSGQ(QGPL/REPLIES)
            RCVMSG MSGQ(QGPL/INBOX) MSGTYPE(*INQ) RMV(*NO) KEYVAR(&KEY) MSG(&TEXT) MSGLEN(&SIZE)
            IF (&TEXT *NE 'Proceed?') THEN(SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('bad body') MSGTYPE(*ESCAPE))
            IF (&SIZE *NE 8) THEN(SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('bad length') MSGTYPE(*ESCAPE))
            SNDRPY MSGKEY(&KEY) MSGQ(QGPL/INBOX) RPY('YES')
            RCVMSG MSGQ(QGPL/REPLIES) MSGTYPE(*RPY) MSG(&TEXT) MSGLEN(&SIZE)
            IF (&TEXT *EQ 'YES' *AND &SIZE *EQ 3) THEN(SNDPGMMSG MSG('QUEUE ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/ROUNDTRIP"); Assert.False(result.IsError, result.Message); Assert.Contains("QUEUE ACCEPTED", result.Message);
        var store = new MessageQueueStore(_system.Connections); Assert.Empty(store.List(new("QGPL", "INBOX"))); Assert.Empty(store.List(new("QGPL", "REPLIES")));
        Execute(session, "SNDMSG 'Visible message' QGPL/INBOX");
        Assert.Contains(session.Execute("DSPMSG QGPL/INBOX").Listing!, line => line.Contains("Visible message"));
        Execute(session, "DLTMSGQ QGPL/INBOX"); Assert.False(_system.Objects.Exists("QGPL", "INBOX", ObjectType.MessageQueue));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void RMVMSG_uses_raw_CL_keys_and_rejects_invalid_hex_without_mutation(int ccsid)
    {
        using var session = Session(ccsid); Execute(session, "CRTMSGQ QGPL/INBOX");
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE sqlite_sequence SET seq=240 WHERE name='sys_message_entries'"; command.ExecuteNonQuery(); }
        Execute(session, "SNDMSG 'remove me' QGPL/INBOX");
        Assert.True(session.Execute("RMVMSG MSGQ(QGPL/INBOX) MSGKEY(X'000000FZ')").IsError);
        Assert.True(session.Execute("RMVMSG MSGQ(QGPL/INBOX) MSGKEY(X'00000000')").IsError);
        Assert.True(session.Execute("RMVMSG MSGQ(QGPL/INBOX) CLEAR(*ALL) MSGKEY(X'000000F1')").IsError);
        Program("REMOVE", """
            PGM
            DCL &KEY *CHAR LEN(4)
            RCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY)
            RMVMSG MSGQ(QGPL/INBOX) MSGKEY(&KEY)
            SNDPGMMSG MSG('program data') TOPGMQ(*SAME) KEYVAR(&KEY)
            RMVMSG MSGKEY(&KEY)
            SNDPGMMSG MSG('REMOVAL ACCEPTED')
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/REMOVE"); Assert.False(result.IsError, result.Message);
        Assert.Contains("REMOVAL ACCEPTED", result.Message);
        Assert.Empty(new MessageQueueStore(_system.Connections).List(new("QGPL", "INBOX")));
    }
    [Fact]
    public void RMVMSG_clears_returned_program_messages_but_preserves_caller_messages()
    {
        Program("CHILDLOG", "SNDPGMMSG MSG('child log') TOPGMQ(*SAME)");
        Program("CLEARLOG", """
            PGM
            SNDPGMMSG MSG('active log') TOPGMQ(*SAME)
            CALL QGPL/CHILDLOG
            RMVMSG PGMQ(*ALLINACT) CLEAR(*ALL) RMVEXCP(*NO)
            ENDPGM
            """);
        using var session = Session(); Execute(session, "CALL QGPL/CLEARLOG");
        var messages = new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key);
        Assert.Equal("active log", Assert.Single(messages).Data.ToText());
        Assert.True(session.Execute("RMVMSG PGMQ(*ALLINACT) CLEAR(*NEW)").IsError);
        Execute(session, "RMVMSG PGMQ(*ALLINACT) CLEAR(*ALL)");
        Assert.Empty(new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_receives_sender_copy_and_pending_or_delivered_reply_return_types(int ccsid)
    {
        using var session = Session(ccsid); Execute(session, "CRTMSGQ QGPL/INBOX");
        Program("COPYTYPE", """
            PGM
            DCL &COPY *CHAR LEN(4)
            DCL &KEY *CHAR LEN(4)
            DCL &TYPE *CHAR LEN(2)
            DCL &TEXT *CHAR LEN(12)
            SNDPGMMSG MSG('Proceed?') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) KEYVAR(&COPY)
            RCVMSG MSGTYPE(*COPY) MSGKEY(&COPY) RMV(*NO) MSG(&TEXT) RTNTYPE(&TYPE)
            IF (&TYPE *NE '06' *OR &TEXT *NE 'Proceed?') THEN(RETURN)
            RCVMSG MSGTYPE(*RPY) MSGKEY(&COPY) WAIT(0) RTNTYPE(&TYPE)
            IF (&TYPE *NE ' ') THEN(RETURN)
            RCVMSG MSGQ(QGPL/INBOX) MSGTYPE(*INQ) RMV(*NO) KEYVAR(&KEY)
            SNDRPY MSGQ(QGPL/INBOX) MSGKEY(&KEY) RPY('*DFT')
            RCVMSG MSGTYPE(*RPY) MSGKEY(&COPY) MSG(&TEXT) RTNTYPE(&TYPE)
            IF (&TYPE *EQ '21' *AND &TEXT *EQ '*DFT') THEN(SNDPGMMSG MSG('RETURN TYPES ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/COPYTYPE"); Assert.False(result.IsError, result.Message);
        Assert.Contains("RETURN TYPES ACCEPTED", result.Message);
        Assert.DoesNotContain(new MessageQueueStore(_system.Connections).ListJobMessages(session.Job.Key), m => m.Kind == "COPY");
    }
    [Fact]
    public void Invalid_return_type_output_leaves_message_available()
    {
        using var session = Session(); Execute(session, "CRTMSGQ QGPL/INBOX"); Execute(session, "SNDMSG 'keep' QGPL/INBOX");
        Program("BADTYPE", "PGM\nDCL &TYPE *CHAR LEN(3)\nRCVMSG MSGQ(QGPL/INBOX) RTNTYPE(&TYPE)\nENDPGM");
        Assert.True(session.Execute("CALL QGPL/BADTYPE").IsError);
        Assert.False(Assert.Single(new MessageQueueStore(_system.Connections).List(new("QGPL", "INBOX"))).Seen);
    }
    [Fact]
    public void Invalid_output_does_not_consume_message_or_change_earlier_variables()
    {
        using var session = Session(); Execute(session, "CRTMSGQ QGPL/INBOX"); Execute(session, "SNDMSG 'payload' QGPL/INBOX");
        Program("BADOUTPUT", """
            PGM
            DCL &TEXT *CHAR LEN(8) VALUE('ORIGINAL')
            DCL &SIZE *DEC LEN(4 0)
            RCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT) MSGLEN(&SIZE)
            MONMSG IPC0003
            SNDPGMMSG MSG(&TEXT)
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/BADOUTPUT"); Assert.False(result.IsError, result.Message); Assert.Equal("ORIGINAL", result.Message);
        Assert.False(Assert.Single(new MessageQueueStore(_system.Connections).List(new("QGPL", "INBOX"))).Seen);
    }
    [Fact]
    public void Empty_receive_blanks_and_zeroes_outputs_and_key_selection_can_read_old_messages()
    {
        using var session = Session(); Execute(session, "CRTMSGQ QGPL/INBOX");
        Program("EMPTY", """
            PGM
            DCL &TEXT *CHAR LEN(8) VALUE('ORIGINAL')
            DCL &SIZE *DEC LEN(5 0) VALUE(123)
            DCL &KEY *CHAR LEN(4)
            RCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT) MSGLEN(&SIZE)
            IF (&TEXT *EQ ' ' *AND &SIZE *EQ 0) THEN(SNDPGMMSG MSG('EMPTY'))
            SNDMSG MSG('one') TOMSGQ(QGPL/INBOX)
            RCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY)
            RCVMSG MSGQ(QGPL/INBOX) MSGKEY(&KEY) MSG(&TEXT)
            SNDPGMMSG MSG(&TEXT)
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/EMPTY"); Assert.False(result.IsError, result.Message); Assert.Equal("EMPTY\none", result.Message?.Replace("\r", "").TrimEnd());
    }
    [Fact]
    public void Failed_transcoding_preserves_message_and_hex_receive_copies_raw_bytes()
    {
        using var session = Session(); Execute(session, "CRTMSGQ QGPL/INBOX");
        new MessageQueueStore(_system.Connections).Send(new[] { new QualifiedName("QGPL", "INBOX") }, new ProgramBuffer(new byte[] { 255, 0 }, 1208));
        Program("RAWMSG", """
            PGM
            DCL &TEXT *CHAR LEN(2)
            RCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT)
            MONMSG IPC0136 EXEC(SNDPGMMSG MSG('CONVERSION HANDLED'))
            RCVMSG MSGQ(QGPL/INBOX) CCSID(*HEX) MSG(&TEXT)
            CRTDTAARA DTAARA(QGPL/RESULT) TYPE(*CHAR) VALUE(&TEXT)
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/RAWMSG"); Assert.False(result.IsError, result.Message); Assert.Contains("CONVERSION HANDLED", result.Message);
        var bytes = Assert.IsType<ProgramBuffer>(new Ipc.Services.Work.DataAreaStore(_system.Connections).Read("QGPL", "RESULT").Value).ToArray();
        Assert.Equal("FF00", Convert.ToHexString(bytes)); Assert.Empty(new MessageQueueStore(_system.Connections).List(new("QGPL", "INBOX")));
    }
    [Fact]
    public void Compiled_send_enforces_command_object_authority()
    {
        using var session = Session(); Execute(session, "CRTMSGQ QGPL/INBOX");
        Program("DENIED", "SNDMSG MSG('must not send') TOMSGQ(QGPL/INBOX)\nMONMSG CPF9802 EXEC(SNDPGMMSG MSG('DENIED'))");
        _system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QSYS", "SNDMSG", ObjectType.Command, "READER", AuthorityBit.None);
        using var reader = new ExecutionSession(_system, _system.Jobs.CreateInteractive("READER"), CancellationToken.None);
        var result = reader.Execute("CALL QGPL/DENIED"); Assert.False(result.IsError, result.Message); Assert.Equal("DENIED", result.Message);
        Assert.Empty(new MessageQueueStore(_system.Connections).List(new("QGPL", "INBOX")));
    }
    public void Dispose() => _system.Dispose();
}
