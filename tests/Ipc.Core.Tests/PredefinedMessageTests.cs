using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class PredefinedMessageTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly MessageDescriptionStore _descriptions;
    private readonly MessageQueueStore _queues;
    private readonly QualifiedName _inbox = new("QGPL", "INBOX");
    public PredefinedMessageTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PredefinedFixture23");
        _descriptions = new(_system.Connections); _descriptions.Create("QGPL", "TEXTS");
        _descriptions.Add("QGPL", "TEXTS", "USR0001", new MessageDescription("Failure &1", "Correct data &1", 75, MessageDescriptionFormat.Parse("*HEX 2"), 37, "YES"));
        _queues = new(_system.Connections); _queues.Create(_inbox);
    }
    private ExecutionSession Session(int ccsid = 37) => new(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
    private void Program(string name, string source) => ClExternalCallTests.Create(_system, name, "CLP", source);

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_sends_predefined_messages_and_receives_independent_metadata_and_raw_data(int ccsid)
    {
        Program("PREDEF", """
            PGM
            DCL &ID *CHAR LEN(7) VALUE('USR0001')
            DCL &FILE *CHAR LEN(20) VALUE('QGPL/TEXTS')
            DCL &TEXT *CHAR LEN(30)
            DCL &HELP *CHAR LEN(40)
            DCL &RAW *CHAR LEN(2)
            DCL &MSGF *CHAR LEN(10)
            DCL &LIB *CHAR LEN(10)
            DCL &ACTUAL *CHAR LEN(10)
            DCL &SEV *DEC LEN(2 0)
            DCL &DLEN *DEC LEN(5 0)
            DCL &DCCSID *DEC LEN(5 0)
            SNDPGMMSG MSGID(&ID) MSGF(&FILE) MSGDTA(X'FF00') TOPGMQ(*SAME)
            RCVMSG MSG(&TEXT) SECLVL(&HELP) MSGDTA(&RAW) MSGF(&MSGF) MSGFLIB(&LIB) SNDMSGFLIB(&ACTUAL) SEV(&SEV) MSGDTALEN(&DLEN) DTACCSID(&DCCSID)
            IF (&TEXT *NE 'Failure X''FF00''' *OR &HELP *NE 'Correct data X''FF00''' *OR &RAW *NE X'FF00') THEN(RETURN)
            IF (&MSGF *EQ 'TEXTS' *AND &LIB *EQ 'QGPL' *AND &ACTUAL *EQ 'QGPL' *AND &SEV *EQ 75 *AND &DLEN *EQ 2 *AND &DCCSID *EQ 65535) THEN(SNDPGMMSG MSG('PREDEFINED ACCEPTED'))
            ENDPGM
            """);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/PREDEF");
        Assert.False(result.IsError, result.Message); Assert.Contains("PREDEFINED ACCEPTED", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Definitions_and_original_file_identity_are_snapshotted(bool recreate)
    {
        Program("SENDDEF", "SNDPGMMSG MSGID(USR0001) MSGF(TEXTS) MSGDTA(X'FF00') TOMSGQ(QGPL/INBOX)");
        using (var session = Session()) Assert.False(session.Execute("CALL QGPL/SENDDEF").IsError);
        _descriptions.Change("QGPL", "TEXTS", "USR0001", text: "Changed &1", secondLevel: "Changed help", severity: 1);
        var unchanged = _queues.Receive(_inbox, remove: false)!;
        Assert.Equal("Failure X'FF00'", unchanged.Data.ToText()); Assert.Equal("Correct data X'FF00'", unchanged.SecondLevel!.ToText()); Assert.Equal(75, unchanged.Severity);
        _system.Objects.Delete("QGPL", "TEXTS", ObjectType.MessageFile);
        if (recreate) _descriptions.Create("QGPL", "TEXTS");
        var received = _queues.Receive(_inbox, MessageSelection.First)!;
        Assert.Equal("Failure X'FF00'", received.Data.ToText()); Assert.Equal("*LIBL", received.Predefined!.RequestedLibrary);
        Assert.Equal("", received.ActualMessageFileLibrary); Assert.Equal("FF00", Convert.ToHexString(received.ReplacementData!.ToArray()));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Nested_escape_monitors_compare_raw_replacement_bytes_and_keep_definition_metadata(int ccsid)
    {
        Program("FAILDEF", "SNDPGMMSG MSGID(USR0001) MSGF(QGPL/TEXTS) MSGDTA(X'FF00') MSGTYPE(*ESCAPE)");
        Program("MIDDEF", "CALL QGPL/FAILDEF");
        Program("MONDEF", """
            PGM
            DCL &RAW *CHAR LEN(2)
            DCL &SEV *DEC LEN(2 0)
            CALL QGPL/MIDDEF
            MONMSG USR0001 CMPDTA(X'FF00') EXEC(DO)
            RCVMSG MSGTYPE(*EXCP) MSGDTA(&RAW) SEV(&SEV)
            IF (&RAW *EQ X'FF00' *AND &SEV *EQ 75) THEN(SNDPGMMSG MSG('RAW MONITOR ACCEPTED'))
            ENDDO
            ENDPGM
            """);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/MONDEF");
        Assert.False(result.IsError, result.Message); Assert.Contains("RAW MONITOR ACCEPTED", result.Message);
        var exceptions = _queues.ListJobMessages(session.Job.Key).Where(message => message.MessageId == "USR0001").ToArray();
        Assert.NotEmpty(exceptions); Assert.All(exceptions, message => { Assert.Equal("TEXTS", message.Predefined!.File); Assert.Equal("FAILDEF", message.Origin!.Program); Assert.Equal(75, message.Severity); });
    }

    [Fact]
    public void Character_comparison_matches_data_instead_of_formatted_message_prefix()
    {
        _descriptions.Add("QGPL", "TEXTS", "USR0002", new MessageDescription("Display &1", "", 40, MessageDescriptionFormat.Parse("*CHAR 4"), 37));
        Program("STRFAIL", "SNDPGMMSG MSGID(USR0002) MSGF(QGPL/TEXTS) MSGDTA('DATA') MSGTYPE(*ESCAPE)");
        Program("STRMON", "CALL QGPL/STRFAIL\nMONMSG USR0002 CMPDTA('DATA') EXEC(SNDPGMMSG MSG('DATA PREFIX ACCEPTED'))");
        using var session = Session(); var result = session.Execute("CALL QGPL/STRMON"); Assert.False(result.IsError, result.Message); Assert.Contains("DATA PREFIX ACCEPTED", result.Message);
    }

    [Fact]
    public void CCHAR_receive_converts_only_convertible_fields_and_updates_varying_prefixes()
    {
        _descriptions.Add("QGPL", "TEXTS", "USR0002", new MessageDescription("&1/&2", "Help &2", 0, MessageDescriptionFormat.Parse("(*CHAR 1) (*CCHAR *VARY 2)"), 37));
        var data = new ProgramBuffer(new byte[] { 0xC1, 0, 1, 0x51 }, 37); // A / EBCDIC e-acute
        var snapshot = _descriptions.Snapshot("QGPL", "TEXTS", "USR0002", "QGPL", data);
        var formatted = MessageDescriptionFormat.Format(snapshot.Description, data, 37);
        _queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, formatted.Text, messageId: "USR0002", predefined: snapshot);
        var result = _queues.Receive(_inbox, receiveCcsid: 1208)!;
        Assert.Equal("C12FC3A9", Convert.ToHexString(result.Data.ToArray()));
        Assert.Equal("C10002C3A9", Convert.ToHexString(result.ReplacementData!.ToArray()));
        Assert.Equal("Help é", result.SecondLevel!.ToText());
    }

    [Fact]
    public void Inquiry_uses_the_snapshotted_default_reply_and_sender_copy_correlation()
    {
        Program("DEFRESP", "RCVMSG MSGQ(QGPL/INBOX)");
        Program("DEFINQ", """
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &RPY *CHAR LEN(3)
            DCL &TYPE *CHAR LEN(2)
            SNDPGMMSG MSGID(USR0001) MSGF(QGPL/TEXTS) MSGDTA(X'FF00') MSGTYPE(*INQ) TOMSGQ(QGPL/INBOX) KEYVAR(&KEY)
            CHGMSGD USR0001 QGPL/TEXTS DFT('NO')
            CALL QGPL/DEFRESP
            RCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) MSG(&RPY) RTNTYPE(&TYPE)
            IF (&RPY *EQ 'YES' *AND &TYPE *EQ '23') THEN(SNDPGMMSG MSG('DEFAULT SNAPSHOT ACCEPTED'))
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL QGPL/DEFINQ"); Assert.False(result.IsError, result.Message); Assert.Contains("DEFAULT SNAPSHOT ACCEPTED", result.Message);
    }

    [Theory]
    [InlineData("MSGF(&BAD)")]
    [InlineData("SECLVLLEN(&BAD)")]
    [InlineData("MSGDTALEN(&BAD)")]
    [InlineData("DTACCSID(&BAD)")]
    public void Invalid_return_layout_leaves_predefined_message_unseen(string output)
    {
        Program("SENDDEF", "SNDPGMMSG MSGID(USR0001) MSGF(QGPL/TEXTS) MSGDTA(X'FF00') TOMSGQ(QGPL/INBOX)");
        Program("BADDEF", "DCL &BAD *CHAR LEN(2)\nRCVMSG MSGQ(QGPL/INBOX) " + output);
        using var session = Session(); Assert.False(session.Execute("CALL QGPL/SENDDEF").IsError);
        Assert.True(session.Execute("CALL QGPL/BADDEF").IsError); Assert.False(Assert.Single(_queues.List(_inbox)).Seen);
    }

    [Fact]
    public void Predefined_snapshot_bytes_count_toward_queue_capacity()
    {
        _descriptions.Add("QGPL", "TEXTS", "USR0002", new MessageDescription("A", new string('Ä', 3000), 0, Array.Empty<MessageDataField>(), 37));
        using (var connection = _system.Connections.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<2047)
                INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
                SELECT 'QGPL','INBOX','INFO',0,zeroblob(4096),37,'QSYS','2026-09-16T00:00:00Z' FROM n
                """;
            insert.ExecuteNonQuery();
        }
        var snapshot = _descriptions.Snapshot("QGPL", "TEXTS", "USR0002", "QGPL", new(Array.Empty<byte>(), 37));
        Assert.Throws<CpfException>(() => _queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, new(CodePage.ToBytes(37, "A"), 37), messageId: "USR0002", predefined: snapshot));
        for (var key = 1u; key <= 5; key++) _queues.RemoveFrom(new(_inbox), MessageClear.ByKey, key);
        Assert.Single(_queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, new(CodePage.ToBytes(37, "A"), 37), messageId: "USR0002", predefined: snapshot));
    }

    [Fact]
    public void Schema_22_upgrade_preserves_existing_message_bytes_state_and_key_sequence()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory, Migrator.Migrations.Where(migration => migration.Version <= 22).ToArray()).MigrateToLatest();
        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority) VALUES('QGPL','INBOX','*MSGQ','QSYS','2026-09-16','2026-09-16',3);
                INSERT INTO sys_message_entries(key,queue_lib,queue_name,kind,severity,data,ccsid,sender,sent,seen,reply_type_code)
                  VALUES(7,'QGPL','INBOX','RPY',0,X'FF00',37,'QSYS','2026-09-16T00:00:00Z',1,'23');
                UPDATE sqlite_sequence SET seq=99 WHERE name='sys_message_entries';
                """;
            insert.ExecuteNonQuery();
        }
        new Migrator(factory).MigrateToLatest(); var store = new MessageQueueStore(factory);
        var message = Assert.Single(store.List(_inbox)); Assert.Null(message.Predefined); Assert.Equal(7u, message.Key); Assert.True(message.Seen);
        Assert.Equal("FF00", Convert.ToHexString(message.Data.ToArray())); Assert.Equal("23", message.ReturnType);
        Assert.Equal(100u, Assert.Single(store.Send(new[] { _inbox }, new(new byte[] { 1 }, 37))));
    }

    [Fact]
    public void A_durable_queue_retains_its_definition_after_restart_and_message_file_deletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-predefined-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); var descriptions = new MessageDescriptionStore(system.Connections); descriptions.Create("QGPL", "TEXTS");
                descriptions.Add("QGPL", "TEXTS", "USR0001", new MessageDescription("Raw &1", "Help &1", 40, MessageDescriptionFormat.Parse("*HEX 2"), 37));
                var queues = new MessageQueueStore(system.Connections); queues.Create(_inbox);
                var snapshot = descriptions.Snapshot("QGPL", "TEXTS", "USR0001", "*LIBL", new(new byte[] { 255, 0 }, 37));
                queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, MessageDescriptionFormat.Format(snapshot.Description, snapshot.Replacement, 37).Text,
                    messageId: "USR0001", severity: 40, predefined: snapshot);
                system.Objects.Delete("QGPL", "TEXTS", ObjectType.MessageFile);
            }
            using var reopened = IpcSystem.Create(directory); reopened.Start();
            var received = new MessageQueueStore(reopened.Connections).Receive(_inbox, receiveCcsid: 1208)!;
            Assert.Equal("Raw X'FF00'", received.Data.ToText()); Assert.Equal("Help X'FF00'", received.SecondLevel!.ToText());
            Assert.Equal("FF00", Convert.ToHexString(received.ReplacementData!.ToArray())); Assert.Equal("", received.ActualMessageFileLibrary);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Conversion_failure_leaves_the_message_unseen_and_a_raw_receive_can_follow()
    {
        _descriptions.Create("QGPL", "UTFTEXT", ccsid: 1208);
        _descriptions.Add("QGPL", "UTFTEXT", "USR0001", new MessageDescription("&1", "Help &1", 0, MessageDescriptionFormat.Parse("*CCHAR *VARY 2"), 1208));
        var data = new ProgramBuffer(Convert.FromHexString("0004F09F9880"), 1208);
        var snapshot = _descriptions.Snapshot("QGPL", "UTFTEXT", "USR0001", "QGPL", data);
        _queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, MessageDescriptionFormat.Format(snapshot.Description, data, 1208).Text, messageId: "USR0001", predefined: snapshot);
        Assert.Throws<CpfException>(() => _queues.Receive(_inbox, receiveCcsid: 37));
        Assert.False(Assert.Single(_queues.List(_inbox)).Seen);
        Assert.Equal("F09F9880", Convert.ToHexString(_queues.Receive(_inbox)!.Data.ToArray()));
    }

    [Fact]
    public void Revoking_message_file_use_blocks_new_sends_but_preserves_already_delivered_messages()
    {
        _system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = "MSGUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("MSGUSER"), "MessageUserFixture23");
        Program("SENDDEF", "SNDPGMMSG MSGID(USR0001) MSGF(QGPL/TEXTS) MSGDTA(X'FF00') TOMSGQ(QGPL/INBOX)");
        Program("READDEF", "DCL &TEXT *CHAR LEN(30)\nRCVMSG MSGQ(QGPL/INBOX) MSG(&TEXT)\nSNDPGMMSG MSG(&TEXT)");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("MSGUSER"), CancellationToken.None);
        Assert.False(session.Execute("CALL QGPL/SENDDEF").IsError);
        _system.Security.Authority.Grant("QGPL", "TEXTS", ObjectType.MessageFile, "MSGUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.Execute("CALL QGPL/SENDDEF").MessageId);
        Assert.Single(_queues.List(_inbox));
        var received = session.Execute("CALL QGPL/READDEF"); Assert.False(received.IsError, received.Message); Assert.Contains("Failure X'FF00'", received.Message);
        _system.Security.Authority.Grant("QSYS", "SNDPGMMSG", ObjectType.Command, "MSGUSER", AuthorityBit.None);
        using (Ipc.Services.Events.OperationIdentity.Enter("MSGUSER"))
            Assert.Equal("CPF9802", new Ipc.Console.Session.CommandService(_system).Execute("CALL QGPL/SENDDEF").MessageId);
    }

    [Theory]
    [InlineData("X'F'")]
    [InlineData("X'GG'")]
    [InlineData("X'0000000000000000000000000000000000000000000000000000000000FF'")]
    public void Invalid_or_oversized_binary_monitor_comparisons_fail_compilation(string value)
        => Assert.Throws<Ipc.Cl.Interpreter.ClCompileException>(() => new Ipc.Cl.Interpreter.ClCompiler().Compile("BAD", "QGPL", "CALL MISSING\nMONMSG USR0001 CMPDTA(" + value + ")"));

    public void Dispose() => _system.Dispose();
}
