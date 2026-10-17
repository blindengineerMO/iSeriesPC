using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class MessageSubfileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private MessageQueueStore Messages => new(_system.Connections);
    public MessageSubfileTests()
    {
        _system.Start();
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SubfileFixture22");
    }
    private ExecutionSession Session() => new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    private JobProgramMessageSource Source(ExecutionSession session) => new(Messages, _system.JobRuntime.Environment(session.Job.Key)!);
    private static string Key(uint key) => Encoding.Latin1.GetString(new QueuedMessage(key, "INFO", new([], 37), "", 0, "", null,
        DateTimeOffset.UtcNow, false, false, null).KeyBuffer(37).ToArray());
    private static string DdsSource(bool initialize = false, bool wide = false) => Dds(keywords: (wide ? "DSPSIZ(27 132) " : "") + "HELP CA03(03)") +
        Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") + Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ(10)") +
        Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLEND(*MORE)") +
        (initialize ? Dds(keywords: "SFLINZ", indicators: " 21") + Dds("PGMQ", keywords: "SFLPGMQ") : "");
    private static DisplayFileSession Open(IProgramMessageSource source, bool initialize = false, bool wide = false)
        => new(new DisplayDdsCompiler().Compile("MESSAGES", DdsSource(initialize, wide)), programMessageSource: source);
    private uint Send(MessageQueueAddress queue, string text, int ccsid = 37, string kind = "INFO")
        => Assert.Single(Messages.SendTo([queue], new(Encoding.GetEncoding(ccsid == 1208 ? 65001 : ccsid).GetBytes(text), ccsid), kind));
    private static void Write(DisplayFileSession file, int rrn, uint key, string queue = "*")
        => file.Subfile("ROWS").Write(rrn, new Dictionary<string, object?> { ["KEY"] = Key(key), ["QUEUE"] = queue });

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Live_message_output_captures_predefined_text_and_second_level_without_receiving(int ccsid)
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var environment = _system.JobRuntime.Environment(session.Job.Key)!; using var frame = environment.EnterCall("VALIDATOR");
        var queue = environment.MessageQueue(); var files = new MessageDescriptionStore(_system.Connections);
        files.Create("QTEMP", "ERRORS", "", ccsid: ccsid);
        files.Add("QTEMP", "ERRORS", "ERR0001", new MessageDescription("Invalid &1", "Correct &1 then retry.", 30, [new("*CHAR", 3)], ccsid));
        var replacement = new ProgramBuffer(Encoding.GetEncoding(ccsid == 1208 ? 65001 : ccsid).GetBytes("ABC"), ccsid);
        var snapshot = files.Snapshot("QTEMP", "ERRORS", "ERR0001", "QTEMP", replacement);
        var data = MessageDescriptionFormat.Format(snapshot.Description, replacement, ccsid).Text;
        var key = Assert.Single(Messages.SendTo([queue], data, "ESCAPE", messageId: "ERR0001", severity: 30, predefined: snapshot));
        var file = Open(Source(session)); Write(file, 1, key, "VALIDATOR ");
        files.Change("QTEMP", "ERRORS", "ERR0001", "changed", "changed help");
        file.Write("CTL", new Dictionary<string, object?>());
        Assert.Contains("Invalid ABC", file.Buffer.RowText(4));
        var help = file.Handle(new(AidKey.Pf1)).MessageHelp!;
        Assert.Equal("ERR0001", help.Title); Assert.Equal("Invalid ABC", help.Text); Assert.Equal("Correct ABC then retry.", help.SecondLevel);
        var stored = Assert.Single(Messages.ListFrom(queue)); Assert.False(stored.Seen); Assert.False(stored.ExceptionHandled);
        Assert.Null(file.Subfile("ROWS").ReadNextChanged());
        Messages.RemoveFrom(queue, key: key);
        _system.Objects.Delete("QTEMP", "ERRORS", ObjectType.MessageFile);
        file.Write("CTL", new Dictionary<string, object?>());
        Assert.Equal(help, file.Handle(new(AidKey.Pf1)).MessageHelp); Assert.Contains("Invalid ABC", file.Buffer.RowText(4));
    }

    [Fact]
    public void Keys_are_opaque_big_endian_bytes_and_missing_output_does_not_overwrite_a_row()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); Send(queue, "Opaque key");
        const uint highKey = 0x8000ff01;
        using (var connection = _system.Connections.Open())
        using (var update = connection.CreateCommand())
        { update.CommandText = "UPDATE sys_message_entries SET key=$key WHERE program_queue=$queue"; update.Parameters.AddWithValue("$key", (long)highKey); update.Parameters.AddWithValue("$queue", queue.ProgramQueue); update.ExecuteNonQuery(); }
        var file = Open(Source(session)); Write(file, 1, highKey);
        Assert.Equal(new byte[] { 0x80, 0, 0xff, 1 }, Encoding.Latin1.GetBytes((string)file.Subfile("ROWS").Read(1).Values["KEY"]!));
        Assert.Throws<InvalidOperationException>(() => file.Subfile("ROWS").Update(1, new Dictionary<string, object?> { ["KEY"] = Key(highKey + 1), ["QUEUE"] = "*" }));
        file.Write("CTL", new Dictionary<string, object?>()); Assert.Contains("Opaque key", file.Buffer.RowText(4));
        Assert.Throws<CpfException>(() => Source(session).Read("*", "\0\0\0\0"));
        Assert.Throws<CpfException>(() => Source(session).Read("*", "\u0100\0\0\0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_output_initializes_all_messages_extends_size_and_pages_frozen_rows(bool wide)
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue();
        for (var n = 1; n <= 5; n++) Send(queue, $"Message {n} " + new string('X', 140));
        var file = Open(Source(session), initialize: true, wide: wide); var flags = new bool[100]; flags[21] = true;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*EXT      " }, flags);
        Assert.Equal(5, file.Subfile("ROWS").Count); Assert.Equal(5, file.Subfile("ROWS").Capacity);
        Assert.Contains("Message 1", file.Buffer.RowText(4));
        Assert.Equal(wide ? 128 : 76, file.Buffer.RowText(4).Trim().Length);
        Assert.All(Messages.ListFrom(queue), message => Assert.False(message.Seen));
        Messages.RemoveFrom(queue, MessageClear.All);
        file.Handle(new(AidKey.RollUp)); Assert.Contains("Message 3", file.Buffer.RowText(4));
        file.Handle(new(AidKey.RollUp)); Assert.Contains("Message 5", file.Buffer.RowText(4));
        Assert.Contains("Message 5", file.Handle(new(AidKey.Pf1)).MessageHelp!.Text);
        flags[21] = false;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "MISSING" }, flags);
        Assert.Contains("Message 5", file.Buffer.RowText(4));
        flags[21] = true;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*EXT" }, flags);
        Assert.Equal(0, file.Subfile("ROWS").Count); Assert.DoesNotContain("Message", file.Buffer.RowText(4));
    }

    [Fact]
    public void Snapshot_initialization_reads_beyond_the_ordinary_message_page_limit()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue();
        using (var connection = _system.Connections.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<1003)
                INSERT INTO sys_message_entries(program_queue,queue_type,kind,severity,data,ccsid,sender,sent,default_reply)
                SELECT $queue,NULL,'INFO',0,X'C1',37,'QSECOFR','2026-10-17T00:00:00Z',X'' FROM numbers
                """;
            insert.Parameters.AddWithValue("$queue", queue.ProgramQueue); insert.ExecuteNonQuery();
        }
        var source = Source(session); Assert.Equal(1003, source.ReadAll("*").Count);
        var file = Open(source, initialize: true); var flags = new bool[100]; flags[21] = true;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*" }, flags);
        Assert.Equal(1003, file.Subfile("ROWS").Count); Assert.Equal("A", file.Subfile("ROWS").Read(1003).Message!.Text);
    }

    [Fact]
    public void Program_names_select_the_nearest_active_recursive_frame_and_external_is_distinct()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var environment = _system.JobRuntime.Environment(session.Job.Key)!; var source = Source(session);
        var external = Send(environment.MessageQueue("*EXT"), "external"); uint outer;
        using (environment.EnterCall("WORKER"))
        {
            outer = Send(environment.MessageQueue(), "outer");
            using (environment.EnterCall("WORKER"))
            {
                var inner = Send(environment.MessageQueue(), "inner");
                Assert.Equal("inner", source.Read("WORKER", Key(inner))!.Text); Assert.Null(source.Read("WORKER", Key(outer)));
                Assert.Equal("external", source.Read("*EXT", Key(external))!.Text);
            }
            Assert.Equal("outer", source.Read("WORKER", Key(outer))!.Text);
        }
        Assert.Equal("CPF2479", Assert.Throws<CpfException>(() => source.Read("WORKER", Key(outer))).MessageId);
    }

    [Fact]
    public void Even_a_privileged_foreign_job_cannot_use_retained_sources_or_program_addresses()
    {
        using var owner = Session(); using var other = Session(); JobProgramMessageSource source; MessageQueueAddress queue;
        using (OperationIdentity.Enter("QSECOFR", owner.Job.Key))
        { queue = _system.JobRuntime.Environment(owner.Job.Key)!.MessageQueue(); Send(queue, "private"); source = Source(owner); }
        using (OperationIdentity.Enter("QSECOFR", other.Job.Key))
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => source.ReadAll("*")).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Messages.SnapshotProgramMessages(queue)).MessageId);
        }
        using (OperationIdentity.Enter("QSECOFR", owner.Job.Key)) Assert.Single(source.ReadAll("*"));
    }

    [Fact]
    public void Inquiry_display_never_replies_and_exception_display_never_handles()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var environment = _system.JobRuntime.Environment(session.Job.Key)!; var root = environment.MessageQueue();
        using var frame = environment.EnterCall("CHILD"); var queue = environment.MessageQueue();
        Messages.SendTo([queue], new(Encoding.ASCII.GetBytes("question"), 1208), "INQ", replyQueue: root);
        Send(queue, "notify", kind: "NOTIFY");
        Assert.Equal(2, Source(session).ReadAll("*").Count); Assert.Empty(Messages.ListFrom(root));
        Assert.All(Messages.ListFrom(queue), message => { Assert.False(message.Seen); Assert.False(message.Replied); Assert.False(message.ExceptionHandled); });
    }

    [Fact]
    public void Failed_control_initialization_preserves_existing_rows_page_and_input()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); Send(queue, "keep this");
        var file = Open(Source(session), initialize: true); var flags = new bool[100]; flags[21] = true;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*" }, flags);
        var rendered = new AnsiRenderer().Render(file.Buffer);
        Assert.Throws<CpfException>(() => file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "MISSING" }, flags));
        Assert.Equal(rendered, new AnsiRenderer().Render(file.Buffer)); Assert.Equal(1, file.Subfile("ROWS").Count);
        Assert.Equal("keep this", file.Handle(new(AidKey.Pf1)).MessageHelp!.Text);
    }

    [Fact]
    public void Failed_control_composition_rolls_back_the_loaded_rows_and_capacity()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); Send(queue, "original");
        var definition = new DisplayDdsCompiler().Compile("MESSAGES", DdsSource(initialize: true) + Dds("VALUE", 5, row: 1, column: 20, usage: 'B'));
        var file = new DisplayFileSession(definition, programMessageSource: Source(session));
        var flags = new bool[100]; flags[21] = true;
        file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*", ["VALUE"] = "kept" }, flags);
        file.Handle(new(AidKey.None, Character: 'X'));
        var before = new AnsiRenderer().Render(file.Buffer); var cursor = file.Buffer.Cursor;
        Messages.RemoveFrom(queue, MessageClear.All);
        for (var n = 0; n < 3; n++) Send(queue, "replacement " + n);
        Assert.Throws<ArgumentException>(() => file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*", ["VALUE"] = new object() }, flags));
        Assert.Equal(1, file.Subfile("ROWS").Count); Assert.Equal(2, file.Subfile("ROWS").Capacity);
        Assert.Equal("original", file.Subfile("ROWS").Read(1).Message!.Text);
        Assert.Equal(before, new AnsiRenderer().Render(file.Buffer)); Assert.Equal(cursor, file.Buffer.Cursor);
    }

    [Fact]
    public void Clearing_a_message_subfile_leaves_live_queue_entries_unseen()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); var key = Send(queue, "still queued");
        var definition = new DisplayDdsCompiler().Compile("MESSAGES", DdsSource() + Dds(keywords: "SFLCLR", indicators: " 22"));
        var file = new DisplayFileSession(definition, programMessageSource: Source(session)); Write(file, 1, key);
        file.Write("CTL", new Dictionary<string, object?>());
        var flags = new bool[100]; flags[22] = true;
        file.Write("CTL", new Dictionary<string, object?>(), flags);
        Assert.Equal(0, file.Subfile("ROWS").Count); Assert.False(Assert.Single(Messages.ListFrom(queue)).Seen);
    }

    [Fact]
    public void Live_store_open_binds_the_active_job_message_source()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var displays = new DisplayFileStore(_system.Connections); displays.Create("QGPL", "MESSAGES", DdsSource());
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); var key = Send(queue, "live open");
        var file = displays.Open("QGPL", "MESSAGES", _system.JobRuntime.Environment(session.Job.Key)!);
        Write(file, 1, key); file.Write("CTL", new Dictionary<string, object?>());
        Assert.Contains("live open", file.Buffer.RowText(4));
    }

    [Theory]
    [InlineData("*SAME")]
    [InlineData("*PRV")]
    [InlineData("QGPL/NAME")]
    [InlineData(" name")]
    public void Display_queue_fields_reject_non_native_relationship_and_qualified_names(string queue)
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Assert.Equal("CPF2479", Assert.Throws<CpfException>(() => Source(session).ReadAll(queue)).MessageId);
    }

    [Fact]
    public void Unsafe_message_bytes_are_rendered_as_cells_and_do_not_escape_the_terminal()
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue(); Send(queue, "hello\u001b[2J\nworld", 1208);
        var message = Assert.Single(Source(session).ReadAll("*"));
        Assert.Equal("hello?[2J?world", message.Message.Text);
    }

    [Fact]
    public void Message_help_requires_the_HELP_keyword_and_does_not_transfer_inputs()
    {
        var definition = new DisplayDdsCompiler().Compile("MESSAGES", DdsSource());
        definition.Keywords.RemoveAll(k => k.Name == "HELP");
        var file = new DisplayFileSession(definition, programMessages: (_, _) => new("No help", Details: new("title", "text", "more")));
        Write(file, 1, 1); file.Write("CTL", new Dictionary<string, object?>());
        var response = file.Handle(new(AidKey.Pf1)); Assert.Null(response.MessageHelp); Assert.False(response.Accepted);
    }

    [Theory]
    [InlineData("SFLPGMQ(276)")]
    [InlineData("SFLPGMQ(11)")]
    [InlineData("SFLPGMQ DFT('*')")]
    [InlineData("SFLPGMQ TEXT('not native')")]
    public void Predefined_field_diagnostics_reject_unsupported_layouts_and_extra_keywords(string keyword)
    {
        Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("MESSAGES", DdsSource().Replace("SFLPGMQ(10)", keyword)));
    }

    [Fact]
    public void Initializer_requires_exactly_one_control_queue_field_and_a_message_subfile()
    {
        var source = DdsSource(initialize: true);
        Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("MESSAGES", source.Replace(Dds("PGMQ", keywords: "SFLPGMQ"), "")));
        Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("MESSAGES", source + Dds("OTHER", keywords: "SFLPGMQ")));
        Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("MESSAGES", source.Replace(Dds(keywords: "SFLINZ", indicators: " 21"), "")));
    }

    public void Dispose() => _system.Dispose();
}
