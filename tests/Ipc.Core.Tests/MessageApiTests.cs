using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class MessageApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private MessageQueueStore Messages => new(_system.Connections);
    private static readonly QualifiedName Inbox = new("QGPL", "INBOX");
    private static readonly QualifiedName Replies = new("QGPL", "REPLIES");
    public MessageApiTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "MessageApiFixture27");
        Messages.Create(Inbox); Messages.Create(Replies);
    }
    private static string Name(string name = "INBOX", string library = "QGPL") => name.PadRight(10) + library.PadRight(10);
    private static ProgramBuffer Hex(string text, int ccsid = 37) => new(Convert.FromHexString(text), ccsid);
    private static ProgramBuffer Key(uint key, int ccsid = 37) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, key); return new(bytes, ccsid); }
    private MessageApi Api(int ccsid = 37, ExecutionSession? session = null, CancellationToken token = default) => new(_system.Connections,
        selector => _system.SearchLibraries(session?.Job, selector), ccsid, session is null ? null : _system.JobRuntime.Environment(session.Job.Key), session?.Job.Key, token);
    private sealed class Cell
    {
        public ProgramBuffer Buffer { get; private set; }
        public ProgramArgument Argument { get; }
        public byte[] Bytes => Buffer.ToArray();
        public Cell(int length, int ccsid = 37, Func<ProgramBuffer, bool>? accept = null)
        {
            Buffer = new(Enumerable.Repeat((byte)0xAA, length).ToArray(), ccsid);
            Argument = new(() => Buffer, value => Buffer = (ProgramBuffer)value!, value => value is ProgramBuffer b && b.Length == length && b.Ccsid == ccsid && (accept?.Invoke(b) ?? true)
                ? b : throw new CpfException("CPF3C3A", "Receiver rejects this value."), "*CHAR", length, buffer: () => Buffer);
        }
        public int Int(int offset) => BinaryPrimitives.ReadInt32BigEndian(Bytes.AsSpan(offset));
        public uint UInt(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(offset));
        public string Text(int offset, int length, int? ccsid = null) => new ProgramBuffer(Bytes.AsSpan(offset, length), ccsid ?? Buffer.Ccsid).ToText();
    }
    private static Cell Error(int ccsid = 37)
    {
        var cell = new Cell(20, ccsid); var bytes = cell.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); cell.Argument.Value = new ProgramBuffer(bytes, ccsid); return cell;
    }
    private uint Send(string text = "HI", int ccsid = 37)
    {
        Api(ccsid).Invoke("QMHSNDM", ["", "IGNORED", text, CodePage.ToBytes(ccsid, text).Length, "*INFO", Name(), 1, "IGNORED", "IGNORED", Hex("00000000", ccsid)]);
        return Messages.List(Inbox).Last().Key;
    }
    private Cell Receive(string type = "*ANY", object? key = null, string action = "*SAME", string format = "RCVM0100", int length = 80, int ccsid = 37, string? name = null)
    {
        var output = new Cell(length + 8, ccsid);
        Api(ccsid).Invoke("QMHRCVM", [output.Argument, length, format, name ?? Name(), type, key ?? "    ", 0, action, Hex("00000000", ccsid)]); return output;
    }

    [Theory]
    [InlineData(37, "40404040404040F0F4", "40404040404040", "00000025", "C8C9")]
    [InlineData(1208, "202020202020203034", "20202020202020", "000004B8", "4849")]
    public void Brief_receive_matches_independent_native_header_vectors(int ccsid, string idAndType, string reserved, string encoding, string text)
    {
        Assert.Equal(1U, Send(ccsid: ccsid));
        var output = Receive(ccsid: ccsid);
        var expected = "0000003200000032AAAAAAAA" + idAndType + "00000001" + reserved + "00000000" + encoding + "0000000200000002" + text;
        Assert.Equal(expected, Convert.ToHexString(output.Bytes.AsSpan(0, 50))); Assert.All(output.Bytes[50..], b => Assert.Equal(0xAA, b));
        Assert.False(Assert.Single(Messages.List(Inbox)).Seen);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(37)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    public void Truncated_receive_reports_full_availability_and_preserves_the_unused_tail(int length)
    {
        Send(); var output = Receive(length: length);
        Assert.Equal(length, output.Int(0)); Assert.Equal(50, output.Int(4)); Assert.All(output.Bytes[length..], b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void Empty_receive_changes_only_the_eight_byte_prefix()
    {
        var output = Receive(); Assert.Equal("0000000800000000", Convert.ToHexString(output.Bytes.AsSpan(0, 8)));
        Assert.All(output.Bytes[8..], b => Assert.Equal(0xAA, b));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Named_full_receiver_has_independent_sender_date_microsecond_and_actual_profile_offsets(int ccsid)
    {
        Send(ccsid: ccsid);
        using (var connection = _system.Connections.Open())
        using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE sys_message_entries SET sender='EXECUSER',sender_job=42,
                    sender_job_name='BATCHJOB',sender_job_user='ORIGINAL',sender_program='APP',
                    sent='2024-01-23T04:05:06.1234567Z',origin_sent='2024-01-23T04:05:06.1234567Z'
                """;
            update.ExecuteNonQuery();
        }
        var output = Receive(format: "RCVM0200", length: 200, ccsid: ccsid);
        Assert.Equal(178, output.Int(0)); Assert.Equal(178, output.Int(4));
        Assert.Equal("BATCHJOB  ORIGINAL  000042APP             1240123040506123456EXECUSER   ", output.Text(55, 72));
        Assert.Equal(0, output.Int(127)); Assert.Equal(0, output.Int(131));
        Assert.Equal("*NO      ", output.Text(135, 9)); Assert.Equal(ccsid, output.Int(148));
        Assert.Equal(2, output.Int(152)); Assert.Equal("HI", output.Text(176, 2));
    }

    [Theory]
    [InlineData("QMHSNDPM", 8)]
    [InlineData("QMHRCVPM", 11)]
    [InlineData("QMHSNDM", 12)]
    [InlineData("QMHRCVM", 12)]
    [InlineData("QMHSNDRM", 9)]
    [InlineData("QMHRTVM", 12)]
    public void Incomplete_native_parameter_groups_fail_before_interpreting_receivers(string name, int count)
    {
        Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Invoke(name, Enumerable.Repeat<object?>(null, count).ToArray())).MessageId);
        Assert.Empty(Messages.List(Inbox));
    }

    [Theory]
    [InlineData("RCVM0300", 80, "CPF3C21")]
    [InlineData("RCVM0100", 7, "CPF24A7")]
    public void Invalid_receive_layouts_report_errors_without_marking_or_overwriting_storage(string format, int length, string expected)
    {
        Send(); var output = new Cell(80); var error = Error();
        Api().Invoke("QMHRCVM", [output.Argument, length, format, Name(), "*ANY", "    ", 0, "*REMOVE", error.Argument]);
        Assert.Equal(expected, error.Text(8, 7)); Assert.Equal(16, error.Int(4)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
        Assert.False(Assert.Single(Messages.List(Inbox)).Seen);
    }

    [Fact]
    public void SAME_OLD_and_REMOVE_follow_native_new_old_and_key_semantics()
    {
        var key = Send(); Assert.Equal("HI", Receive().Text(48, 2)); Assert.Equal("HI", Receive().Text(48, 2));
        Receive(action: "*OLD"); Assert.True(Assert.Single(Messages.List(Inbox)).Seen); Assert.Equal(0, Receive().Int(4));
        Assert.Equal("HI", Receive(key: Key(key)).Text(48, 2));
        var removed = Receive(key: Key(key), action: "*REMOVE"); Assert.Equal("    ", removed.Text(21, 4)); Assert.Empty(Messages.List(Inbox));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Opaque_high_bit_keys_and_NEXT_PRV_TOP_are_never_decoded_as_text(int ccsid)
    {
        Send("FIRST", ccsid);
        using (var connection = _system.Connections.Open())
        using (var update = connection.CreateCommand())
        { update.CommandText = "UPDATE sys_message_entries SET key=2147548929"; update.ExecuteNonQuery(); }
        var key = Assert.Single(Messages.List(Inbox)).Key; Assert.Equal(0x8000ff01U, key);
        Assert.Equal(key, Receive(key: Key(key, ccsid), ccsid: ccsid).UInt(21));
        Assert.Equal(key, Receive("*NEXT", "*TOP", ccsid: ccsid).UInt(21));
        Assert.Equal(key, Receive("*PRV", Hex("00000000", ccsid), ccsid: ccsid).UInt(21));
        Assert.Equal(0, Receive("*NEXT", Key(key, ccsid), ccsid: ccsid).Int(4));
        Assert.Equal("CPF2410", Assert.Throws<CpfException>(() => Receive("*NEXT", Key(42, ccsid), ccsid: ccsid)).MessageId);
    }

    [Theory]
    [InlineData("*FIRST", "*TOP", "CPF24B2")]
    [InlineData("*LAST", "*TOP", "CPF24B2")]
    [InlineData("*COPY", "    ", "CPF24B1")]
    [InlineData("*NEXT", "    ", "CPF24B1")]
    [InlineData("*INFO", "*TOP", "CPF24B2")]
    public void Invalid_key_type_combinations_leave_the_queue_untouched(string type, string key, string error)
    {
        Send(); Assert.Equal(error, Assert.Throws<CpfException>(() => Receive(type, key, action: "*REMOVE")).MessageId);
        Assert.False(Assert.Single(Messages.List(Inbox)).Seen);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Failed_receiver_normalization_does_not_remove_an_inquiry_or_send_its_default_reply(int ccsid)
    {
        var key = Messages.Send([Inbox], new(CodePage.ToBytes(ccsid, "Proceed?"), ccsid), "INQ", Replies, new(CodePage.ToBytes(ccsid, "N"), ccsid)).Single();
        var output = new Cell(80, ccsid, accept: b => b.ToArray()[0] != 0); var error = Error(ccsid);
        Api(ccsid).Invoke("QMHRCVM", [output.Argument, 80, "RCVM0100", Name(), "*ANY", Key(key, ccsid), 0, "*REMOVE", error.Argument]);
        Assert.Equal("CPF3C3A", error.Text(8, 7)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
        var inquiry = Assert.Single(Messages.List(Inbox)); Assert.False(inquiry.Seen); Assert.False(inquiry.Replied); Assert.Empty(Messages.List(Replies));
    }

    [Fact]
    public void Immediate_payloads_accept_the_native_6000_byte_limit_and_reject_more_before_sending()
    {
        Send(new string('A', 6000)); Assert.Equal(6000, Assert.Single(Messages.List(Inbox)).Data.Length);
        Assert.Equal("CPF24B6", Assert.Throws<CpfException>(() => Send(new string('B', 6001))).MessageId); Assert.Single(Messages.List(Inbox));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Named_inquiry_returns_a_copy_key_and_reply_rules_remain_atomic(int ccsid)
    {
        var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QGPL", "ERRORS");
        descriptions.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Proceed?", "Choose yes or no", 30, [], 37, "N", ReplyRules: new("*CHAR", 1, Values: ["Y", "N"], SpecialValues: [new("yes", "Y")])));
        var copy = new Cell(4, ccsid);
        Api(ccsid).Invoke("QMHSNDM", ["ERR0001", Name("ERRORS"), "", 0, "*INQ", Name(), 1, Name("REPLIES"), copy.Argument, Hex("00000000", ccsid)]);
        var inquiry = Assert.Single(Messages.List(Inbox)); Assert.Equal("COPY", Assert.Single(Messages.List(Replies)).Kind);
        Assert.NotEqual(inquiry.Key, copy.UInt(0));
        var error = Error(ccsid); Api(ccsid).Invoke("QMHSNDRM", [Key(inquiry.Key, ccsid), Name(), "bad", 3, "*YES", error.Argument]);
        Assert.Equal("CPF2422", error.Text(8, 7)); Assert.False(Assert.Single(Messages.List(Inbox)).Replied); Assert.Single(Messages.List(Replies));
        Api(ccsid).Invoke("QMHSNDRM", [Key(inquiry.Key, ccsid), Name(), "yes", 3, "*YES", Hex("00000000", ccsid)]);
        Assert.Empty(Messages.List(Inbox));
        var reply = Receive("*RPY", copy.Buffer, name: Name("REPLIES"), ccsid: ccsid);
        Assert.Equal("22", reply.Text(19, 2)); Assert.Equal("Y", reply.Text(48, 1)); Assert.Equal(copy.UInt(0), reply.UInt(21));
        Assert.Equal("Y", Receive("*RPY", copy.Buffer, name: Name("REPLIES"), ccsid: ccsid, action: "*OLD").Text(48, 1));
        Assert.Equal("Y", Receive("*RPY", copy.Buffer, name: Name("REPLIES"), ccsid: ccsid, action: "*REMOVE").Text(48, 1)); Assert.Empty(Messages.List(Replies));
    }

    [Fact]
    public void Failed_copy_key_normalization_rolls_back_both_the_inquiry_and_the_sender_copy()
    {
        var copy = new Cell(4, accept: b => b.ToArray()[0] == 0xAA); var error = Error();
        Api().Invoke("QMHSNDM", ["", "", "Proceed?", 8, "*INQ", Name(), 1, Name("REPLIES"), copy.Argument, error.Argument]);
        Assert.Equal("CPF3C3A", error.Text(8, 7)); Assert.Empty(Messages.List(Inbox)); Assert.Empty(Messages.List(Replies));
    }

    [Fact]
    public void Native_multicast_is_atomic_when_a_later_destination_is_full()
    {
        using (var connection = _system.Connections.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<4096) INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent) SELECT 'QGPL','REPLIES','INFO',0,X'',37,'QSYS','2026-10-17T00:00:00Z' FROM n"; insert.ExecuteNonQuery();
        }
        Assert.Equal("CPF2460", Assert.Throws<CpfException>(() => Api().Invoke("QMHSNDM", ["", "", "HI", 2, "*INFO", Name() + Name("REPLIES"), 2, "", "", Hex("00000000")])).MessageId);
        Assert.Empty(Messages.List(Inbox));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Program_counters_skip_API_frames_and_SAME_does_not_handle_an_exception(int ccsid)
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var environment = _system.JobRuntime.Environment(session.Job.Key)!;
        using var caller = environment.EnterCall("CALLER"); var destination = environment.MessageQueue();
        using (environment.EnterCall("CHILD"))
        using (environment.EnterCall("QMHSNDPM", systemApi: true))
        {
            var key = new Cell(4, ccsid); Api(ccsid, session).Invoke("QMHSNDPM", ["", "", "HI", 2, "*DIAG", "*", 1, key.Argument, Hex("00000000", ccsid)]);
            Assert.Equal(key.UInt(0), Assert.Single(Messages.ListFrom(destination)).Key);
            Assert.Empty(Messages.ListFrom(environment.ApiMessageQueue("*", 0)));
        }
        var exception = Messages.SendTo([destination], new(CodePage.ToBytes(ccsid, "Escape"), ccsid), "ESCAPE", messageId: "USR0001").Single();
        var output = new Cell(80, ccsid);
        Api(ccsid, session).Invoke("QMHRCVPM", [output.Argument, 80, "RCVM0100", "*", 0, "*EXCP", "    ", 0, "*SAME", Hex("00000000", ccsid)]);
        Assert.Equal("17", output.Text(19, 2)); Assert.False(Messages.ListFrom(destination).Single(m => m.Key == exception).ExceptionHandled);
        Api(ccsid, session).Invoke("QMHRCVPM", [output.Argument, 80, "RCVM0100", "*", 0, "*ESCAPE", Key(exception, ccsid), 0, "*OLD", Hex("00000000", ccsid)]);
        Assert.True(Messages.ListFrom(destination).Single(m => m.Key == exception).ExceptionHandled);
        Assert.Equal("CPF24A3", Assert.Throws<CpfException>(() => environment.ApiMessageQueue("*", 1)).MessageId);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Full_receive_has_native_offsets_saved_replacement_text_and_help_after_file_deletion(int ccsid)
    {
        var store = new MessageDescriptionStore(_system.Connections); store.Create("QGPL", "ERRORS");
        store.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Amount &1", "Correct &1", 40, [new("*DEC", 5, 2)], 37));
        Api(ccsid).Invoke("QMHSNDM", ["ERR0001", Name("ERRORS"), Hex("01234D", ccsid), 3, "*DIAG", Name(), 1, "", "", Hex("00000000", ccsid)]);
        _system.Objects.Delete("QGPL", "ERRORS", ObjectType.MessageFile);
        var output = Receive(format: "RCVM0200", length: 256, ccsid: ccsid);
        Assert.Equal(40, output.Int(8)); Assert.Equal("ERR0001", output.Text(12, 7)); Assert.Equal("02", output.Text(19, 2));
        Assert.Equal("ERRORS    ", output.Text(25, 10)); Assert.Equal("QGPL      ", output.Text(35, 10)); Assert.Equal("          ", output.Text(45, 10));
        Assert.Equal(65535, output.Int(148)); Assert.Equal(3, output.Int(152)); Assert.Equal(3, output.Int(156)); Assert.Equal("01234D", Convert.ToHexString(output.Bytes.AsSpan(176, 3)));
        Assert.Equal("Amount -12.34", output.Text(179, output.Int(160))); Assert.Equal("Correct -12.34", output.Text(179 + output.Int(160), output.Int(168)));
    }

    [Fact]
    public void Receive_conversion_status_distinguishes_converted_truncated_hex_and_unconvertible_data()
    {
        Send("é", 37); var output = new Cell(80, 1208);
        Api(1208).Invoke("QMHRCVM", [output.Argument, 80, "RCVM0100", Name(), "*ANY", "    ", 0, "*SAME", Hex("00000000", 1208)]);
        Assert.Equal(3, output.Int(32)); Assert.Equal(1208, output.Int(36)); Assert.Equal("C3A9", Convert.ToHexString(output.Bytes.AsSpan(48, 2)));
        Api(1208).Invoke("QMHRCVM", [output.Argument, 49, "RCVM0100", Name(), "*ANY", "    ", 0, "*SAME", Hex("00000000", 1208)]);
        Assert.Equal(2, output.Int(32)); Assert.Equal(37, output.Int(36)); Assert.Equal(0x51, output.Bytes[48]);
        Api(1208).Invoke("QMHRCVM", [output.Argument, 80, "RCVM0100", Name(), "*ANY", "    ", 0, "*SAME", Hex("00000000", 1208), 65535]); Assert.Equal(1, output.Int(32)); Assert.Equal(37, output.Int(36));
        var ascii = new Cell(80, 367); Api(367).Invoke("QMHRCVM", [ascii.Argument, 80, "RCVM0100", Name(), "*ANY", "    ", 0, "*SAME", Hex("00000000", 367)]);
        Assert.Equal(-1, ascii.Int(32)); Assert.Equal(37, ascii.Int(36)); Assert.Equal(0x51, ascii.Bytes[48]);
    }

    [Theory]
    [InlineData("*YES", "Value ABC", "Details ABC")]
    [InlineData("*NO", "Value &1", "Details &1")]
    public void Retrieve_returns_formatted_or_unsubstituted_descriptions_without_queue_effects(string replace, string text, string help)
    {
        var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QGPL", "ERRORS");
        descriptions.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Value &1", "Details &1", 30, [new("*CHAR", 3)], 37));
        var output = new Cell(100);
        Api().Invoke("QMHRTVM", [output.Argument, 100, "RTVM0100", "ERR0001", Name("ERRORS"), "ABC", 3, replace, "*NO", Hex("00000000")]);
        Assert.Equal(24 + text.Length + help.Length, output.Int(4)); Assert.Equal(text, output.Text(24, output.Int(8)));
        Assert.Equal(help, output.Text(24 + output.Int(8), output.Int(16))); Assert.Empty(Messages.List(Inbox));
    }

    [Fact]
    public void Retrieve_FIRST_NEXT_use_identifier_order_and_return_blanks_at_end()
    {
        var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QGPL", "ERRORS");
        descriptions.Add("QGPL", "ERRORS", "ZZZ0002", new MessageDescription("LAST", "&PHelp&B", 0, [], 37));
        descriptions.Add("QGPL", "ERRORS", "AAA0001", new MessageDescription("FIRST", "&NHelp", 0, [], 37));
        var output = new Cell(80);
        void Retrieve(string id, string option, string controls = "*NO") => Api().Invoke("QMHRTVM",
            [output.Argument, 80, "RTVM0100", id, Name("ERRORS"), "", 0, "*NO", controls, Hex("00000000"), option, 0, 0]);
        Retrieve("IGNORED", "*FIRST"); Assert.Equal("FIRST", output.Text(24, output.Int(8))); Assert.Equal("Help", output.Text(29, output.Int(16)));
        Retrieve("AAA0001", "*NEXT", "*YES"); Assert.Equal("LAST", output.Text(24, output.Int(8))); Assert.Equal("&PHelp&B", output.Text(28, output.Int(16)));
        Retrieve("ZZZ0002", "*NEXT"); Assert.Equal(new string(' ', 80), output.Text(0, 80));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(14)]
    public void Unsupported_program_delivery_is_explicit_and_does_not_send_an_ordinary_message(int count)
    {
        var key = new Cell(4); var error = Error(); var p = new List<object?> { "USR0001", "", "HI", 2, "*STATUS", "*", 0, key.Argument, error.Argument };
        if (count >= 12) p.AddRange([1, Name("*NONE", "*NONE"), 0]); if (count == 14) p.AddRange(["*CHAR", 0]);
        Api().Invoke("QMHSNDPM", p); Assert.Equal("IPC0006", error.Text(8, 7)); Assert.All(key.Bytes, b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public async Task Waiting_receiver_excludes_other_jobs_reads_and_removals_allows_senders_and_releases_on_return()
    {
        using var owner = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var other = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var output = new Cell(100);
        var receive = Task.Run(() =>
        {
            using var identity = OperationIdentity.Enter("QSECOFR", owner.Job.Key);
            Api(session: owner, token: stop.Token).Invoke("QMHRCVM", [output.Argument, 100, "RCVM0100", Name(), "*ANY", "    ", -1, "*REMOVE", Hex("00000000")]);
        });
        try
        {
            while (ReceiverCount() == 0) await Task.Delay(10, stop.Token);
            using (OperationIdentity.Enter("QSECOFR", other.Job.Key))
            {
                Assert.Equal("CPF2451", Assert.Throws<CpfException>(() => Receive()).MessageId);
                Assert.Equal("CPF2451", Assert.Throws<CpfException>(() => Messages.Remove(Inbox, MessageClear.All)).MessageId);
                Send("READY");
            }
            await receive.WaitAsync(stop.Token); Assert.Equal("READY", output.Text(48, 5)); Assert.Equal(0, ReceiverCount());
            Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "INBOX", ObjectType.MessageQueue)), l => l.Lifetime == "MessageReceive");
        }
        finally { stop.Cancel(); try { await receive; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task Cancelled_native_receive_releases_its_queue_allocation()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var stop = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var output = new Cell(80);
        var receive = Task.Run(() =>
        {
            using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
            Api(session: session, token: stop.Token).Invoke("QMHRCVM", [output.Argument, 80, "RCVM0100", Name(), "*ANY", "    ", int.MaxValue, "*REMOVE", Hex("00000000")]);
        });
        try
        {
            while (ReceiverCount() == 0) await Task.Delay(10, timeout.Token); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await receive); Assert.Equal(0, ReceiverCount()); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
        }
        finally { stop.Cancel(); try { await receive; } catch (OperationCanceledException) { } }
    }
    private long ReceiverCount()
    {
        using var connection = _system.Connections.Open(); using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM sys_message_receivers"; return (long)count.ExecuteScalar()!;
    }
    public void Dispose() => _system.Dispose();
}
