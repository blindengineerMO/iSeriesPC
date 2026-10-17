using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class MessageReplyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly MessageDescriptionStore _descriptions;
    private readonly MessageQueueStore _queues;
    private readonly QualifiedName _inbox = new("QGPL", "INBOX");
    private readonly QualifiedName _replies = new("QGPL", "REPLIES");
    public MessageReplyTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ReplyFixture24");
        _descriptions = new(_system.Connections); _descriptions.Create("QGPL", "TEXTS");
        _queues = new(_system.Connections); _queues.Create(_inbox); _queues.Create(_replies);
    }
    private static ProgramBuffer Bytes(string text, int ccsid = 37) => new(CodePage.ToBytes(ccsid, text), ccsid);
    private static MessageDescription Description(MessageReplyRules rules, string? defaultReply = null, int ccsid = 37)
        => new("Continue?", "", 0, Array.Empty<MessageDataField>(), ccsid, defaultReply, ReplyRules: rules);
    private uint Send(MessageReplyRules rules, string? defaultReply = null)
    {
        _descriptions.Add("QGPL", "TEXTS", "USR0001", Description(rules, defaultReply));
        var snapshot = _descriptions.Snapshot("QGPL", "TEXTS", "USR0001", "QGPL", Bytes(""));
        return Assert.Single(_queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, Bytes("Continue?"), "INQ", new(_replies),
            messageId: "USR0001", predefined: snapshot, senderCopy: true));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Rejected_reply_preserves_inquiry_and_sender_copy_then_special_value_maps(int replyCcsid)
    {
        var copy = Send(new("*ALPHA", 1, Values: new[] { "Y", "N" }, SpecialValues: new[] { new MessageReplyValue("yes", "Y") }));
        var key = Assert.Single(_queues.List(_inbox)).Key;
        Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => _queues.Reply(_inbox, key, Bytes("bad", replyCcsid))).MessageId);
        var unchanged = Assert.Single(_queues.List(_inbox)); Assert.False(unchanged.Seen); Assert.False(unchanged.Replied);
        Assert.False(Assert.Single(_queues.List(_replies)).Replied);
        _queues.Reply(_inbox, key, Bytes("yes  ", replyCcsid));
        Assert.Empty(_queues.List(_inbox));
        var reply = _queues.Receive(_replies, MessageSelection.Reply, copy, receiveCcsid: 37)!;
        Assert.Equal("Y", reply.Data.ToText()); Assert.Equal("22", reply.ReturnType); Assert.Equal(copy, reply.CorrelationKey);
        Assert.Empty(_queues.List(_replies));
    }

    [Theory]
    [InlineData("*CHAR", 3, 0, "A B", "A B")]
    [InlineData("*ALPHA", 3, 0, "Yes", "Yes")]
    [InlineData("*NAME", 10, 0, "alpha", "ALPHA")]
    [InlineData("*NAME", 10, 0, "alpha1", "alpha1")]
    [InlineData("*DEC", 5, 2, "+0012.30", "+0012.30")]
    [InlineData("*DEC", 15, 0, "999999999999999", "999999999999999")]
    [InlineData("*DEC", 5, 2, "-.25", "-.25")]
    public void Independent_type_and_length_fixtures(string type, int length, int decimals, string input, string expected)
    {
        var rules = new MessageReplyRules(type, length, decimals); rules.Validate(37, null);
        Assert.Equal(expected, rules.Check(Bytes(input), 37, null).ToText());
    }

    [Theory]
    [InlineData("*ALPHA", 3, 0, "A B")]
    [InlineData("*ALPHA", 3, 0, "A1")]
    [InlineData("*NAME", 10, 0, "1ABC")]
    [InlineData("*NAME", 10, 0, "A_B")]
    [InlineData("*NAME", 10, 0, "ABCDEFGHIJK")]
    [InlineData("*DEC", 5, 2, "1000.00")]
    [InlineData("*DEC", 5, 2, "1.001")]
    [InlineData("*DEC", 5, 2, "1e2")]
    [InlineData("*DEC", 5, 2, "1,23")]
    [InlineData("*CHAR", 2, 0, "ABC")]
    public void Invalid_type_length_and_numeric_syntax_are_rejected(string type, int length, int decimals, string input)
        => Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => new MessageReplyRules(type, length, decimals).Check(Bytes(input), 37, null)).MessageId);

    [Fact]
    public void Decimal_range_is_numeric_and_exact_and_special_values_bypass_range()
    {
        var rules = new MessageReplyRules("*DEC", 15, Range: new("999999999999990", "999999999999999"),
            SpecialValues: new[] { new MessageReplyValue("*CANCEL", "-1") }); rules.Validate(37, null);
        Assert.Equal("999999999999991", rules.Check(Bytes("999999999999991"), 37, null).ToText());
        Assert.Throws<CpfException>(() => rules.Check(Bytes("999999999999989"), 37, null));
        Assert.Equal("-1", rules.Check(Bytes("*CANCEL"), 37, null).ToText());
        var values = new MessageReplyRules("*DEC", 5, 2, Values: new[] { "12.3" });
        Assert.Equal("0012.30", values.Check(Bytes("0012.30"), 37, null).ToText());
    }

    [Theory]
    [InlineData("*EQ", "A", true)]
    [InlineData("*NE", "A", false)]
    [InlineData("*LT", "9", true)]
    [InlineData("*LE", "A", true)]
    [InlineData("*GT", "9", false)]
    [InlineData("*GE", "A", true)]
    [InlineData("*NL", "9", false)]
    [InlineData("*NG", "A", true)]
    public void Relations_use_message_file_EBCDIC_collation(string op, string boundary, bool accepted)
    {
        // Independent CCSID 37 fixture: A=C1, 9=F9, so A sorts before 9.
        var rules = new MessageReplyRules("*CHAR", 3, Relation: new(op, boundary));
        if (accepted) Assert.Equal("ABC", rules.Check(Bytes("ABC", 1208), 37, null).ToText());
        else Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => rules.Check(Bytes("ABC", 1208), 37, null)).MessageId);
    }

    [Fact]
    public void Character_range_pads_boundaries_and_truncates_only_for_comparison()
    {
        var rules = new MessageReplyRules("*CHAR", 3, Range: new("A", "B")); rules.Validate(37, null);
        Assert.Equal("ABC", rules.Check(Bytes("ABC"), 37, null).ToText());
        Assert.Equal("BCD", rules.Check(Bytes("BCD"), 37, null).ToText());
        Assert.Throws<CpfException>(() => rules.Check(Bytes("C"), 37, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Defaults_bypass_value_selection_and_automatic_removal_uses_snapshot(bool automatic)
    {
        var copy = Send(new("*ALPHA", 1, Values: new[] { "Y" }), "N");
        _descriptions.Change("QGPL", "TEXTS", "USR0001", defaultReply: "Y", replaceDefault: true);
        if (automatic) _queues.Remove(_inbox, MessageClear.All);
        else _queues.Reply(_inbox, Assert.Single(_queues.List(_inbox)).Key);
        var reply = _queues.Receive(_replies, MessageSelection.Reply, copy)!;
        Assert.Equal("N", reply.Data.ToText()); Assert.Equal("23", reply.ReturnType);
    }

    [Fact]
    public void Missing_default_uses_system_default_and_NONE_preserves_opaque_bytes()
    {
        var rules = new MessageReplyRules("*NONE", -1); rules.Validate(37, null);
        var data = new ProgramBuffer(new byte[] { 255, 0 }, 1208);
        Assert.Equal(data.ToArray(), rules.Check(data, 37, null).ToArray());
        Send(new("*DEC", 3)); _queues.Remove(_inbox, MessageClear.All);
        var reply = _queues.Receive(_replies)!; Assert.Equal("24", reply.ReturnType); Assert.Equal(0, reply.Data.Length);
    }

    [Fact]
    public void Definitions_and_snapshots_do_not_expose_mutable_lists()
    {
        var values = new[] { "Y" }; var special = new[] { new MessageReplyValue("YES", "Y") };
        Send(new("*ALPHA", 1, Values: values, SpecialValues: special));
        values[0] = "N"; special[0] = new("NO", "N");
        var description = _descriptions.GetDescription("QGPL", "TEXTS", "USR0001");
        Assert.Equal("Y", Assert.Single(description.ReplyRules!.Values!));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)description.ReplyRules.Values!)[0] = "N");
        var snapshot = Assert.Single(_queues.List(_inbox)).Predefined!;
        Assert.Throws<NotSupportedException>(() => ((IList<MessageReplyValue>)snapshot.Description.ReplyRules!.SpecialValues!)[0] = new("NO", "N"));
    }

    [Theory]
    [InlineData("TYPE(*NONE)")]
    [InlineData("TYPE(*CHAR) LEN(*NONE)")]
    [InlineData("TYPE(*DEC) LEN(16)")]
    [InlineData("TYPE(*DEC) LEN(5 6)")]
    [InlineData("TYPE(*CHAR) LEN(0)")]
    [InlineData("TYPE(*ALPHA) LEN(2) VALUES(A1)")]
    [InlineData("TYPE(*CHAR) LEN(1) DFT(YES)")]
    [InlineData("TYPE(*CHAR) LEN(1) VALUES(Y) RANGE(A Z)")]
    [InlineData("TYPE(*CHAR) LEN(1) SPCVAL((YES NO))")]
    [InlineData("TYPE(*CHAR) LEN(1) SPCVAL((Y Y) (Y N))")]
    [InlineData("TYPE(*CHAR) LEN(1) REL(*BAD Y)")]
    [InlineData("TYPE(*DEC) LEN(2) RANGE(99 10)")]
    public void Invalid_command_rules_fail_before_creating_a_description(string options)
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("ADDMSGD USR0001 QGPL/TEXTS 'Continue?' " + options);
        Assert.True(result.IsError); Assert.Empty(_descriptions.List("QGPL", "TEXTS"));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_can_monitor_bad_reply_then_map_a_valid_one_and_receive_checked_code(int ccsid)
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var add = session.Execute("ADDMSGD USR0001 QGPL/TEXTS 'Continue?' TYPE(*ALPHA) LEN(1) VALUES(Y N) SPCVAL((yes Y) (no N)) DFT(N)");
        Assert.False(add.IsError, add.Message);
        ClExternalCallTests.Create(_system, "REPLY", "CLP", """
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &RPY *CHAR LEN(3)
            DCL &TYPE *CHAR LEN(2)
            RCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY)
            SNDRPY MSGQ(QGPL/INBOX) MSGKEY(&KEY) RPY('bad')
            MONMSG CPF2422 EXEC(GOTO RECOVER)
            RETURN
            RECOVER: SNDRPY MSGQ(QGPL/INBOX) MSGKEY(&KEY) RPY('yes')
            ENDPGM
            """);
        ClExternalCallTests.Create(_system, "INQUIRE", "CLP", """
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &RPY *CHAR LEN(3)
            DCL &TYPE *CHAR LEN(2)
            SNDPGMMSG MSGID(USR0001) MSGF(QGPL/TEXTS) MSGTYPE(*INQ) TOMSGQ(QGPL/INBOX) KEYVAR(&KEY)
            CALL QGPL/REPLY
            RCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) MSG(&RPY) RTNTYPE(&TYPE)
            IF (&RPY *EQ 'Y' *AND &TYPE *EQ '22') THEN(SNDPGMMSG MSG('VALIDATED REPLY ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/INQUIRE"); Assert.False(result.IsError, result.Message); Assert.Contains("VALIDATED REPLY ACCEPTED", result.Message);
    }

    [Fact]
    public void Changes_are_atomic_preserve_unspecified_rules_and_enforce_live_authority()
    {
        Send(new("*DEC", 4, Values: new[] { "0816", "0727" }));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        Assert.True(session.Execute("CHGMSGD USR0001 QGPL/TEXTS LEN(2) MSG('Must roll back')").IsError);
        Assert.Equal("Continue?", _descriptions.Get("QGPL", "TEXTS", "USR0001"));
        Assert.False(session.Execute("CHGMSGD USR0001 QGPL/TEXTS VALUES(*NONE) RANGE(0300 8900)").IsError);
        var changed = _descriptions.GetDescription("QGPL", "TEXTS", "USR0001").ReplyRules!;
        Assert.Empty(changed.Values!); Assert.Equal("*DEC", changed.Type); Assert.Equal(4, changed.Length);
        Assert.Equal(new MessageReplyRange("0300", "8900"), changed.Range);
        Assert.True(session.Execute("CHGMSGD USR0001 QGPL/TEXTS TYPE(*CHAR)").IsError);
        Assert.False(session.Execute("CHGMSGD USR0001 QGPL/TEXTS TYPE(*NONE) LEN(*NONE) RANGE(*NONE)").IsError);
        _system.Security.Authority.Grant("QGPL", "TEXTS", ObjectType.MessageFile, "QUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER"))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _descriptions.Change("QGPL", "TEXTS", "USR0001", replyUpdate: new(Length: 1))).MessageId);
    }

    [Fact]
    public void Legacy_version_two_descriptions_remain_unchecked_and_upgrade_on_mutation()
    {
        var original = new { Version = 2, Messages = new Dictionary<string, object> { ["USR0001"] = new {
            Text = "Continue?", SecondLevel = "", Severity = 0, Fields = Array.Empty<MessageDataField>(), Ccsid = 37,
            DefaultReply = (string?)null, LegacyLiteral = false } } };
        var file = _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile); file.Source = JsonSerializer.Serialize(original); _system.Objects.Update(file);
        Assert.Null(_descriptions.GetDescription("QGPL", "TEXTS", "USR0001").ReplyRules);
        Assert.Contains("\"Version\":2", _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile).Source);
        _descriptions.Change("QGPL", "TEXTS", "USR0001", severity: 10);
        Assert.Contains("\"Version\":3", _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile).Source);
    }

    [Fact]
    public void Reply_rules_survive_disk_restart_and_original_file_deletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-reply-" + Guid.NewGuid().ToString("N"));
        uint key;
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); var descriptions = new MessageDescriptionStore(system.Connections); descriptions.Create("QGPL", "TEXTS");
                descriptions.Add("QGPL", "TEXTS", "USR0001", Description(new("*ALPHA", 1, Values: new[] { "Y" })));
                var queues = new MessageQueueStore(system.Connections); queues.Create(_inbox); queues.Create(_replies);
                var snapshot = descriptions.Snapshot("QGPL", "TEXTS", "USR0001", "QGPL", Bytes(""));
                key = Assert.Single(queues.SendTo(new[] { new MessageQueueAddress(_inbox) }, Bytes("Continue?"), "INQ", new(_replies), messageId: "USR0001", predefined: snapshot));
                descriptions.Change("QGPL", "TEXTS", "USR0001", replyUpdate: new(Values: new[] { "N" }));
                system.Objects.Delete("QGPL", "TEXTS", ObjectType.MessageFile);
            }
            using var reopened = IpcSystem.Create(directory); reopened.Start(); var store = new MessageQueueStore(reopened.Connections);
            Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => store.Reply(_inbox, key, Bytes("N"))).MessageId);
            store.Reply(_inbox, key, Bytes("Y"));
            var reply = store.Receive(_replies)!; Assert.Equal("Y", reply.Data.ToText()); Assert.Equal("22", reply.ReturnType);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Invalid_encoding_cannot_pass_checked_rules_and_leaves_message_unchanged()
    {
        Send(new("*CHAR", 1)); var key = Assert.Single(_queues.List(_inbox)).Key;
        Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => _queues.Reply(_inbox, key, new(new byte[] { 255 }, 1208))).MessageId);
        Assert.False(Assert.Single(_queues.List(_inbox)).Replied);
        Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => _queues.Reply(_inbox, key, Bytes("😀", 1208))).MessageId);
        Assert.False(Assert.Single(_queues.List(_replies)).Replied);
    }

    [Fact]
    public void Compact_special_lists_preserve_quoted_parentheses_and_escaped_apostrophes()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("ADDMSGD USR0001 QGPL/TEXTS 'Continue?' TYPE(*ALPHA) LEN(1) VALUES(Y N) SPCVAL(('it''s (yes)' Y)(no N))");
        Assert.False(result.IsError, result.Message);
        var rules = _descriptions.GetDescription("QGPL", "TEXTS", "USR0001").ReplyRules!;
        Assert.Equal("Y", rules.Check(Bytes("it's (yes)"), 37, null).ToText());
        Assert.Equal("N", rules.Check(Bytes("no"), 37, null).ToText());
    }
    public void Dispose() => _system.Dispose();
}
