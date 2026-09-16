using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class MessageSenderTests
{
    private static QueuedMessage Fixture()
    {
        var sent = DateTimeOffset.Parse("2024-01-23T04:05:06.1234567+00:00", global::System.Globalization.CultureInfo.InvariantCulture);
        return new(7, "INFO", new ProgramBuffer(new byte[] { 0xFF }, 1208), "", 0, "CURRENT", 42, sent, false, false, null,
            Origin: new("BATCHJOB", "ORIGINAL", "SENDER", "RECEIVER", sent));
    }

    [Theory]
    [InlineData(37, 80)]
    [InlineData(37, 86)]
    [InlineData(37, 87)]
    [InlineData(37, 100)]
    [InlineData(1208, 80)]
    [InlineData(1208, 86)]
    [InlineData(1208, 87)]
    [InlineData(1208, 100)]
    public void Short_sender_layout_uses_native_offsets_and_only_returns_complete_current_profile(int ccsid, int length)
    {
        const string common = "BATCHJOB  ORIGINAL  000042SENDER          0240123040506RECEIVER      00123456";
        Assert.Equal(77, common.Length);
        var expected = common + (length >= 87 ? "CURRENT   " : ""); expected = expected.PadRight(length);
        var buffer = MessageSenderLayout.Encode(Fixture(), length, false, ccsid);
        Assert.Equal(length, buffer.Length); Assert.Equal(expected, buffer.ToText());
        Assert.Equal(ccsid == 37 ? "F0F0F0F0F4F2" : "303030303432", Convert.ToHexString(buffer.ToArray().AsSpan(20, 6)));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Long_sender_layout_keeps_module_procedure_and_unavailable_instruction_fields_blank(int ccsid)
    {
        var text = MessageSenderLayout.Encode(Fixture(), 725, true, ccsid).ToText();
        Assert.Equal("BATCHJOB  ORIGINAL  000042024012304050600SENDER      ", text[..53]);
        Assert.Equal(new string(' ', 267), text[53..320]); Assert.Equal("0000", text[320..324]);
        Assert.Equal(new string(' ', 30), text[324..354]); Assert.Equal("RECEIVER  ", text[354..364]);
        Assert.Equal(new string(' ', 276), text[364..640]); Assert.Equal("0000", text[640..644]);
        Assert.Equal(new string(' ', 30), text[644..674]); Assert.Equal("123456CURRENT   ", text[674..690]);
        Assert.Equal(new string(' ', 35), text[690..]);
        var named = Fixture() with { Origin = Fixture().Origin! with { Recipient = "" } };
        Assert.Equal(new string(' ', 320), MessageSenderLayout.Encode(named, 720, true, ccsid).ToText()[354..674]);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_receives_stored_sender_after_program_return_job_end_and_catalog_changes(int ccsid)
    {
        using var system = System(); var store = new MessageQueueStore(system.Connections); var inbox = new QualifiedName("QGPL", "INBOX"); store.Create(inbox);
        ClExternalCallTests.Create(system, "SENDER", "CLP", "SNDPGMMSG MSG('PAYLOAD') TOMSGQ(QGPL/INBOX)");
        using var sender = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        Assert.False(sender.Execute("CALL QGPL/SENDER").IsError); var original = Assert.Single(store.List(inbox));
        Assert.Equal(sender.Job.Key.Name, original.Origin!.JobName); Assert.Equal(sender.Job.Key.User, original.Origin.JobUser); Assert.Equal("SENDER", original.Origin.Program);
        sender.End(JobCompletion.Normal, "done"); system.Objects.Delete("QGPL", "SENDER", ObjectType.Program);
        using (var connection = system.Connections.Open()) using (var edit = connection.CreateCommand())
        { edit.CommandText = "UPDATE sys_jobs SET name='RENAMED' WHERE number=$number"; edit.Parameters.AddWithValue("$number", sender.Job.Key.Number); edit.ExecuteNonQuery(); }
        ClExternalCallTests.Create(system, "RECEIVER", "CLP", """
            DCL &SENDER *CHAR LEN(87)
            DCL &TEXT *CHAR LEN(7)
            RCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT) SENDER(&SENDER)
            CHGDTAARA DTAARA(*LDA (1 87)) VALUE(&SENDER)
            IF (&TEXT *EQ 'PAYLOAD' *AND %SST(&SENDER 27 12) *EQ 'SENDER') THEN(SNDPGMMSG MSG('SENDER ACCEPTED'))
            """);
        using var receiver = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = receiver.Execute("CALL QGPL/RECEIVER"); Assert.False(result.IsError, result.Message); Assert.Contains("SENDER ACCEPTED", result.Message);
        var bytes = new JobDataAreaStore(system.Connections).Read(receiver.Job.Key, JobDataArea.Local).AsSpan(0, 87).ToArray();
        Assert.Equal(MessageSenderLayout.Encode(original, 87, false, ccsid).ToArray(), bytes); Assert.Empty(store.List(inbox));
    }

    [Fact]
    public void Nested_exception_propagation_preserves_original_sender_and_updates_destination()
    {
        using var system = System();
        ClExternalCallTests.Create(system, "INNER", "CLP", "DCL &N *DEC LEN(1 0)\nCHGVAR &N (1 / 0)");
        ClExternalCallTests.Create(system, "MIDDLE", "CLP", "CALL QGPL/INNER");
        ClExternalCallTests.Create(system, "OUTER", "CLP", """
            DCL &SENDER *CHAR LEN(720)
            CALL QGPL/MIDDLE
            MONMSG MCH1211 EXEC(DO)
              RCVMSG MSGTYPE(*EXCP) SENDER(&SENDER) SENDERFMT(*LONG)
              IF (%SST(&SENDER 42 12) *EQ 'INNER' *AND %SST(&SENDER 355 10) *EQ 'OUTER') THEN(SNDPGMMSG MSG('ORIGINAL SENDER ACCEPTED'))
            ENDDO
            """);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/OUTER"); Assert.False(result.IsError, result.Message); Assert.Contains("ORIGINAL SENDER ACCEPTED", result.Message);
        var messages = new MessageQueueStore(system.Connections).ListJobMessages(session.Job.Key).Where(m => m.MessageId == "MCH1211").ToArray();
        Assert.Equal(2, messages.Length); Assert.All(messages, message => Assert.Equal("INNER", message.Origin!.Program));
        Assert.Single(messages.Select(message => message.Origin!.Sent).Distinct());
    }

    [Fact]
    public void Invalid_sender_outputs_or_unrepresentable_job_numbers_do_not_consume_messages()
    {
        using var system = System(); var store = new MessageQueueStore(system.Connections); var inbox = new QualifiedName("QGPL", "INBOX"); store.Create(inbox);
        store.Send(new[] { inbox }, new ProgramBuffer(new byte[] { 0xC1 }, 37));
        ClExternalCallTests.Create(system, "BADOUT", "CLP", "DCL &TEXT *CHAR VALUE('KEEP')\nDCL &SHORT *CHAR LEN(80)\nRCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT) SENDER(&SHORT) SENDERFMT(*LONG)\nMONMSG IPC0003\nSNDPGMMSG MSG(&TEXT)");
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/BADOUT"); Assert.False(result.IsError, result.Message); Assert.Equal("KEEP", result.Message); Assert.False(Assert.Single(store.List(inbox)).Seen);
        using (var connection = system.Connections.Open()) using (var edit = connection.CreateCommand())
        { edit.CommandText = "UPDATE sys_message_entries SET sender_job=1000000 WHERE queue_name='INBOX'"; edit.ExecuteNonQuery(); }
        Assert.Throws<CpfException>(() => store.ReceiveFrom(new(inbox), senderLength: 87));
        Assert.False(Assert.Single(store.List(inbox)).Seen); Assert.NotNull(store.Receive(inbox));
    }

    [Fact]
    public void Schema_22_preserves_historical_bytes_keys_reply_codes_and_unknown_sender_fields()
    {
        using var factory = new SqliteConnectionFactory(":memory:"); new Migrator(factory, Migrator.Migrations.Take(21).ToArray()).MigrateToLatest();
        new SqliteObjectStore(factory).Create(new() { Key = new("QGPL", "INBOX"), ObjectType = ObjectType.MessageQueue });
        using (var connection = factory.Open()) using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sys_message_entries(key,queue_lib,queue_name,kind,severity,data,ccsid,sender,sender_job,sent,seen,reply_type_code)
                VALUES(7,'QGPL','INBOX','RPY',0,X'FF00',1208,'OLDUSER',42,'2024-01-23T04:05:06Z',1,'23');
                UPDATE sqlite_sequence SET seq=99 WHERE name='sys_message_entries';
                """; insert.ExecuteNonQuery();
        }
        new Migrator(factory).MigrateToLatest(); var store = new MessageQueueStore(factory); var inbox = new QualifiedName("QGPL", "INBOX");
        var message = Assert.Single(store.List(inbox)); Assert.Equal(7u, message.Key); Assert.True(message.Seen); Assert.Equal("23", message.ReturnType);
        Assert.Equal("FF00", Convert.ToHexString(message.Data.ToArray())); Assert.Equal("", message.Origin!.Program); Assert.Equal("", message.Origin.JobName);
        Assert.Equal("OLDUSER   ", MessageSenderLayout.Encode(message, 87, false, 37).ToText()[77..]);
        Assert.Equal(100u, Assert.Single(store.Send(new[] { inbox }, new ProgramBuffer(new byte[] { 1 }, 1208))));
    }

    [Theory]
    [InlineData("DCL &S *CHAR LEN(79)", "SENDER(&S)")]
    [InlineData("DCL &S *CHAR LEN(719)", "SENDER(&S) SENDERFMT(*LONG)")]
    [InlineData("DCL &S *CHAR LEN(87)", "SENDER(&S) SENDERFMT(*BOGUS)")]
    [InlineData("DCL &S *CHAR LEN(87)", "SENDERFMT(*SHORT)")]
    public void Invalid_sender_contracts_fail_before_receipt(string declaration, string arguments)
    {
        using var system = System(); var store = new MessageQueueStore(system.Connections); var inbox = new QualifiedName("QGPL", "INBOX"); store.Create(inbox);
        store.Send(new[] { inbox }, new ProgramBuffer(new byte[] { 0xC1 }, 37));
        ClExternalCallTests.Create(system, "BAD", "CLP", declaration + "\nRCVMSG MSGQ(QGPL/INBOX) " + arguments);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/BAD"); Assert.True(result.IsError); Assert.Equal("IPC0003", result.MessageId);
        Assert.False(Assert.Single(store.List(inbox)).Seen);
    }

    [Fact]
    public void Empty_receive_blanks_the_entire_sender_receiver()
    {
        using var system = System(); new MessageQueueStore(system.Connections).Create(new("QGPL", "EMPTY"));
        ClExternalCallTests.Create(system, "EMPTY", "CLP", "DCL &S *CHAR LEN(725) VALUE('ORIGINAL')\nRCVMSG MSGQ(QGPL/EMPTY) SENDER(&S) SENDERFMT(*LONG)\nIF (&S *EQ ' ') THEN(SNDPGMMSG MSG('EMPTY SENDER ACCEPTED'))");
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/EMPTY"); Assert.False(result.IsError, result.Message); Assert.Equal("EMPTY SENDER ACCEPTED", result.Message);
    }

    private static IpcSystem System()
    {
        var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "SenderFixture22"); return system;
    }
}
