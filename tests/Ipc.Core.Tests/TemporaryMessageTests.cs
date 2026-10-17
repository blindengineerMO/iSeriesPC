using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class TemporaryMessageTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private MessageQueueStore Queues => new(_system.Connections);
    private MessageDescriptionStore Files => new(_system.Connections);
    private static readonly QualifiedName Inbox = new("QTEMP", "INBOX"), Replies = new("QTEMP", "REPLIES"), Operator = new("QGPL", "OPERATOR");
    public TemporaryMessageTests()
    {
        _system.Start(); _system.Security.Profiles.Create(new UserProfile { Name = "REPLIER" });
        foreach (var user in new[] { "QUSER", "QSECOFR", "REPLIER" }) _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(user), "TemporaryMessageFixture26");
        Queues.Create(Operator);
    }
    private Job Job(string user = "QUSER", int ccsid = 37) => _system.Jobs.CreateInteractive(user, ccsid: ccsid);
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private string Backing(Job job) { using var identity = Enter(job); return new JobTemporaryLibrary(_system.Connections).ResolveLibrary("QTEMP"); }
    private static ProgramBuffer Text(string text, int ccsid = 37) => new(CodePage.ToBytes(ccsid, text), ccsid);
    private static ProgramBuffer Bytes(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
    private static void Ok(Ipc.Cl.Commands.CommandResult result) => Assert.False(result.IsError, result.Message);

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Same_named_private_queues_and_message_files_keep_job_storage_separate(int ccsid)
    {
        var first = Job(ccsid: ccsid); var second = Job(ccsid: ccsid);
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => Queues.Create(Inbox)).MessageId);
        foreach (var (job, value) in new[] { (first, "First"), (second, "Second") })
        using (Enter(job))
        {
            Queues.Create(Inbox); Files.Create("QTEMP", "TEXTS", ccsid: ccsid);
            Files.Add("QTEMP", "TEXTS", "USR0001", value); Queues.Send([Inbox], Text(value, ccsid));
        }
        using (Enter(first))
        {
            Assert.Equal("First", Queues.Receive(Inbox)!.Data.ToText()); Assert.Equal("First", Files.Get("QTEMP", "TEXTS", "USR0001"));
            Files.Change("QTEMP", "TEXTS", "USR0001", text: "Changed"); Files.Remove("QTEMP", "TEXTS", "USR0001");
            Assert.Empty(Files.List("QTEMP", "TEXTS")); Assert.Empty(Queues.List(Inbox));
        }
        using (Enter(second))
        {
            Assert.Equal("Second", Queues.Receive(Inbox)!.Data.ToText()); Assert.Equal("Second", Files.Get("QTEMP", "TEXTS", "USR0001"));
        }
        Assert.False(_system.Objects.Exists("QGPL", "INBOX", ObjectType.MessageQueue));
    }

    [Theory]
    [InlineData("QUSER")]
    [InlineData("QSECOFR")]
    public void Known_backing_names_do_not_grant_foreign_message_or_description_access(string user)
    {
        var owner = Job(); var other = Job(user); var physical = Backing(owner); uint key;
        using (Enter(owner))
        {
            Queues.Create(Inbox, authority: "*ALL"); Queues.Create(Replies, authority: "*ALL");
            Files.Create("QTEMP", "TEXTS"); Files.Add("QTEMP", "TEXTS", "USR0001", "Private text");
            key = Queues.Send([Inbox], Text("Question"), "INQ", Replies).Single();
        }
        using (Enter(other))
        {
            var queue = new QualifiedName(physical, "INBOX");
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Queues.List(queue)).MessageId);
            Assert.Throws<CpfException>(() => Queues.Send([queue], Text("Bad")));
            Assert.Throws<CpfException>(() => Queues.Receive(queue)); Assert.Throws<CpfException>(() => Queues.Reply(queue, key));
            Assert.Throws<CpfException>(() => Queues.Remove(queue, MessageClear.All)); Assert.Throws<CpfException>(() => Queues.Create(new(physical, "NEW")));
            Assert.Throws<CpfException>(() => Files.Get(physical, "TEXTS", "USR0001"));
            Assert.Throws<CpfException>(() => Files.Change(physical, "TEXTS", "USR0001", text: "Bad"));
            Assert.Throws<CpfException>(() => Files.Snapshot(physical, "TEXTS", "USR0001", physical, Text("")));
        }
        using (Enter(owner)) Assert.Equal("Private text", Files.Get("QTEMP", "TEXTS", "USR0001"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Authorized_replies_deliver_to_the_senders_private_queue_without_granting_read_access(bool defaultReply)
    {
        var owner = Job(); var replier = Job("REPLIER"); var physical = Backing(owner); uint senderCopy;
        using (Enter(owner))
        {
            Queues.Create(Replies, authority: "*EXCLUDE");
            senderCopy = Queues.SendTo([new(Operator)], Text("Question"), "INQ", new(Replies), Text("D"), senderCopy: true).Single();
        }
        var inquiry = Queues.List(Operator).Single(); var locks = new JobLockStore(_system.Connections);
        using (Enter(replier))
        using (locks.EnterCommand(replier.Key))
        {
            Assert.Throws<CpfException>(() => Queues.List(new(physical, "REPLIES")));
            if (defaultReply) Queues.Remove(Operator, MessageClear.All);
            else Queues.Reply(Operator, inquiry.Key, Text("Y"));
            Assert.Throws<CpfException>(() => Queues.List(new(physical, "REPLIES")));
            Assert.Throws<CpfException>(() => new JobTemporaryLibrary(_system.Connections).ResolveLibrary(physical));
            Assert.Throws<CpfException>(() => _system.Objects.GetRequired("QSYS", physical, ObjectType.Library));
            using (Enter(owner)) Assert.Contains(locks.Inspect(new("QTEMP", "REPLIES", ObjectType.MessageQueue)), row => row.Job == replier.Key && row.Mode == JobLockMode.SharedUpdate);
        }
        using (Enter(owner))
        {
            var reply = Queues.Receive(Replies, MessageSelection.Reply, senderCopy)!;
            Assert.Equal(defaultReply ? "D" : "Y", reply.Data.ToText()); Assert.Equal(defaultReply ? "23" : "21", reply.ReturnType);
            Assert.Equal("REPLIER", reply.Sender); Assert.Equal(replier.Key.Number, reply.SenderJob); Assert.Empty(Queues.List(Replies));
        }
    }

    [Fact]
    public void Private_reply_delivery_respects_the_senders_exclusive_allocation_and_scope_expires_on_error()
    {
        var owner = Job(); var replier = Job("REPLIER"); var physical = Backing(owner); uint inquiry;
        using (Enter(owner)) { Queues.Create(Replies); inquiry = Queues.Send([Operator], Text("Q"), "INQ", Replies).Single(); }
        var locks = new JobLockStore(_system.Connections);
        IDisposable held; using (Enter(owner)) held = locks.Acquire(owner.Key, new("QTEMP", "REPLIES", ObjectType.MessageQueue), JobLockMode.Exclusive, TimeSpan.Zero);
        try
        {
            using var identity = Enter(replier); using var command = locks.EnterCommand(replier.Key);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry, Text("Y"))).MessageId);
            Assert.False(Queues.List(Operator).Single().Replied);
            Assert.Throws<CpfException>(() => Queues.Send([new(physical, "REPLIES")], Text("Bad")));
            Assert.Throws<CpfException>(() => _system.Objects.GetRequired(physical, "REPLIES", ObjectType.MessageQueue));
        }
        finally { held.Dispose(); }
        using (Enter(replier)) Queues.Reply(Operator, inquiry, Text("Y"));
        using (Enter(owner)) Assert.Equal("Y", Queues.Receive(Replies)!.Data.ToText());
    }

    [Fact]
    public void Sending_to_both_logical_and_backing_aliases_is_rejected_before_any_enqueue()
    {
        var owner = Job(); var physical = Backing(owner);
        using var identity = Enter(owner); Queues.Create(Inbox);
        Assert.Throws<CpfException>(() => Queues.Send([Inbox, new(physical, "INBOX")], Text("Duplicate")));
        Assert.Empty(Queues.List(Inbox));
    }

    [Fact]
    public void Private_object_authority_errors_report_logical_names()
    {
        var owner = Job(); var physical = Backing(owner);
        using (Enter(owner)) Queues.Create(Inbox, authority: "*EXCLUDE");
        var descriptor = _system.Objects.GetRequired(physical, "INBOX", ObjectType.MessageQueue); descriptor.Owner = "QSECOFR"; _system.Objects.Update(descriptor);
        using (Enter(owner))
        {
            var failure = Assert.Throws<CpfException>(() => Queues.List(Inbox));
            Assert.Equal("CPF9802", failure.MessageId); Assert.Contains("QTEMP/INBOX", failure.Message); Assert.DoesNotContain(physical, failure.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Private_reply_routes_follow_rename_and_never_redirect_to_a_recreated_queue(bool recreate)
    {
        var owner = Job(); var replier = Job("REPLIER"); uint inquiry;
        var renamed = new QualifiedName("QTEMP", "RENAMED");
        using (Enter(owner))
        {
            Queues.Create(Replies); inquiry = Queues.Send([Operator], Text("Q"), "INQ", Replies).Single();
            if (recreate) { _system.Objects.Delete("QTEMP", "REPLIES", ObjectType.MessageQueue); Queues.Create(Replies); }
            else new ObjectCatalogOperations(_system.Connections).Relocate(Replies, ObjectType.MessageQueue, renamed);
        }
        using (Enter(replier))
        {
            if (recreate) Assert.Equal("CPF2469", Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry, Text("Y"))).MessageId);
            else Queues.Reply(Operator, inquiry, Text("Y"));
        }
        using (Enter(owner))
        {
            if (recreate) Assert.Empty(Queues.List(Replies));
            else Assert.Equal("Y", Queues.Receive(renamed)!.Data.ToText());
        }
    }

    [Fact]
    public void Private_reply_permission_is_bound_to_the_persisted_sending_job()
    {
        var owner = Job(); var replier = Job("REPLIER"); uint inquiry;
        using (Enter(owner)) { Queues.Create(Replies); inquiry = Queues.Send([Operator], Text("Q"), "INQ", Replies).Single(); }
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "UPDATE sys_message_entries SET sender_job=$wrong WHERE key=$key";
        query.Parameters.AddWithValue("$wrong", replier.Key.Number); query.Parameters.AddWithValue("$key", (long)inquiry); query.ExecuteNonQuery();
        using (Enter(replier)) Assert.Equal("CPF2469", Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry, Text("Y"))).MessageId);
        Assert.False(Queues.List(Operator).Single().Replied);
        using (Enter(owner)) Assert.Empty(Queues.List(Replies));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_private_reply_rules_reject_bad_replies_atomically_after_the_message_file_is_deleted(int ccsid)
    {
        var owner = Job(ccsid: ccsid); var replier = Job("REPLIER", ccsid == 37 ? 1208 : 37); var physical = Backing(owner); uint copy;
        using (Enter(owner))
        {
            Queues.Create(Replies); Files.Create("QTEMP", "TEXTS", ccsid: ccsid);
            Files.Add("QTEMP", "TEXTS", "USR0001", new MessageDescription("Question", "Help", 0, [], ccsid, "N",
                ReplyRules: new("*ALPHA", 1, Values: ["Y", "N"])));
            var snapshot = Files.Snapshot("QTEMP", "TEXTS", "USR0001", "QTEMP", Text("", ccsid));
            copy = Queues.SendTo([new(Operator)], Text("Question", ccsid), "INQ", new(Replies), messageId: "USR0001", predefined: snapshot, senderCopy: true).Single();
            Files.Change("QTEMP", "TEXTS", "USR0001", text: "Changed"); _system.Objects.Delete("QTEMP", "TEXTS", ObjectType.MessageFile);
        }
        var inquiry = Queues.List(Operator).Single();
        using (Enter(replier))
        {
            Assert.Equal("CPF2422", Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry.Key, Text("BAD", replier.Ccsid))).MessageId);
            Assert.False(Queues.List(Operator).Single().Replied);
            Assert.Throws<CpfException>(() => Queues.List(new(physical, "REPLIES")));
        }
        using (Enter(owner)) Assert.False(Queues.List(Replies).Single().Replied);
        using (Enter(replier)) Queues.Reply(Operator, inquiry.Key, Text("Y", replier.Ccsid));
        using (Enter(owner))
        {
            var reply = Queues.Receive(Replies, MessageSelection.Reply, copy, receiveCcsid: ccsid)!;
            Assert.Equal("22", reply.ReturnType); Assert.Equal("Y", reply.Data.ToText()); Assert.Empty(Queues.List(Replies));
        }
    }

    [Fact]
    public void Batch_send_rolls_back_earlier_private_delivery_when_a_later_queue_is_full()
    {
        var owner = Job(); using (Enter(owner)) Queues.Create(Inbox);
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = """
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<4096)
            INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
            SELECT 'QGPL','OPERATOR','INFO',0,X'',37,'QSYS','2026-10-17T00:00:00Z' FROM n
            """;
        query.ExecuteNonQuery();
        using (Enter(owner))
        {
            Assert.Equal("CPF2460", Assert.Throws<CpfException>(() => Queues.Send([Inbox, Operator], Text("Atomic"))).MessageId);
            Assert.Empty(Queues.List(Inbox));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_private_snapshot_versions_remain_private_after_the_namespace_is_cleaned(int version)
    {
        var owner = Job(); var physical = Backing(owner); uint key;
        using (Enter(owner))
        {
            Files.Create("QTEMP", "TEXTS"); Files.Add("QTEMP", "TEXTS", "USR0001", "Legacy private");
            var snapshot = Files.Snapshot("QTEMP", "TEXTS", "USR0001", "QTEMP", Text(""));
            key = Queues.SendTo([new(Operator)], Text("Legacy private"), messageId: "USR0001", predefined: snapshot).Single();
        }
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "UPDATE sys_message_entries SET predefined=json_remove(json_set(predefined,'$.Version',$version,'$.Library',$library,'$.RequestedLibrary',$library),'$.JobTemporary') WHERE key=$key";
        query.Parameters.AddWithValue("$version", version); query.Parameters.AddWithValue("$library", physical); query.Parameters.AddWithValue("$key", (long)key); query.ExecuteNonQuery();
        Assert.DoesNotContain(physical, Queues.List(Operator).Single().Predefined!.Serialize());
        _system.Jobs.Complete(owner.Key);
        var message = Queues.Receive(Operator)!; Assert.Equal("Legacy private", message.Data.ToText());
        Assert.Equal("QTEMP", message.Predefined!.Library); Assert.Equal("QTEMP", message.Predefined.RequestedLibrary); Assert.True(message.Predefined.JobTemporary);
        Assert.DoesNotContain(physical, message.Predefined.Serialize()); Assert.Equal("", message.ActualMessageFileLibrary);
    }

    [Fact]
    public void Program_queue_and_job_history_lists_sanitize_legacy_private_snapshots()
    {
        var owner = Job(); var physical = Backing(owner); MessageQueueAddress external; uint key;
        using (Enter(owner))
        {
            Files.Create("QTEMP", "TEXTS"); Files.Add("QTEMP", "TEXTS", "USR0001", "Private history");
            external = Queues.OpenProgramQueue(owner.Key, "*EXT");
            var snapshot = Files.Snapshot("QTEMP", "TEXTS", "USR0001", "QTEMP", Text(""));
            key = Queues.SendTo([external], Text("Private history"), messageId: "USR0001", predefined: snapshot).Single();
        }
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "UPDATE sys_message_entries SET predefined=json_remove(json_set(predefined,'$.Version',2,'$.Library',$library,'$.RequestedLibrary',$library),'$.JobTemporary') WHERE key=$key";
        query.Parameters.AddWithValue("$library", physical); query.Parameters.AddWithValue("$key", (long)key); query.ExecuteNonQuery();
        using (Enter(owner))
        {
            Assert.DoesNotContain(physical, Queues.ListFrom(external).Single().Predefined!.Serialize());
            Assert.DoesNotContain(physical, Queues.ListJobMessages(owner.Key).Single().Predefined!.Serialize());
        }
        _system.Jobs.Complete(owner.Key);
        var retained = Queues.ListJobMessages(owner.Key).Single(); Assert.Equal("QTEMP", retained.Predefined!.Library);
        Assert.DoesNotContain(physical, retained.Predefined.Serialize());
    }

    [Theory]
    [InlineData(JobCompletion.Normal)]
    [InlineData(JobCompletion.Abnormal)]
    public void Job_end_cascades_private_messages_and_invalidates_permanent_inquiry_reply_routes(JobCompletion completion)
    {
        var owner = Job(); var physical = Backing(owner); uint inquiry;
        using (Enter(owner))
        {
            Queues.Create(Inbox); Queues.Create(Replies); Queues.Send([Inbox], Bytes("FF00"));
            Files.Create("QTEMP", "TEXTS"); Files.Add("QTEMP", "TEXTS", "USR0001", "Private");
            inquiry = Queues.Send([Operator], Text("Q"), "INQ", Replies).Single();
        }
        _system.Jobs.Complete(owner.Key, completion);
        Assert.False(_system.Objects.Exists("QSYS", physical, ObjectType.Library));
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sys_message_entries WHERE queue_lib=$lib OR reply_lib=$lib"; query.Parameters.AddWithValue("$lib", physical);
        Assert.Equal(0L, query.ExecuteScalar());
        Assert.Equal("CPF2469", Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry, Text("Y"))).MessageId);
        var replacement = Job(); using (Enter(replacement)) Queues.Create(Replies);
        Assert.Throws<CpfException>(() => Queues.Reply(Operator, inquiry, Text("Y")));
        using (Enter(replacement)) Assert.Empty(Queues.List(Replies));
    }

    [Fact]
    public void Queue_move_rename_and_copy_preserve_routing_and_keep_copies_empty()
    {
        var owner = Job(); var catalog = new ObjectCatalogOperations(_system.Connections); uint inquiry;
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.ChangeBits);
        using (Enter(owner))
        {
            Queues.Create(Replies); inquiry = Queues.Send([Operator], Text("Q"), "INQ", Replies).Single();
            Queues.Send([Replies], Text("Before"));
            catalog.Relocate(Replies, ObjectType.MessageQueue, new("QTEMP", "RENAMED"));
            catalog.Relocate(new("QTEMP", "RENAMED"), ObjectType.MessageQueue, new("QGPL", "COPY"), copy: true);
            Assert.Empty(Queues.List(new("QGPL", "COPY")));
            catalog.Relocate(new("QTEMP", "RENAMED"), ObjectType.MessageQueue, new("QGPL", "EXPORTED"));
        }
        Queues.Reply(Operator, inquiry, Text("Y")); Assert.Equal(2, Queues.List(new("QGPL", "EXPORTED")).Count);
        _system.Jobs.Complete(owner.Key); Assert.Equal("Before", Queues.Receive(new("QGPL", "EXPORTED"))!.Data.ToText());
        Assert.Equal("Y", Queues.Receive(new("QGPL", "EXPORTED"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Private_predefined_snapshots_survive_source_changes_and_job_end_without_exposing_backing_names(int ccsid)
    {
        var owner = Job(ccsid: ccsid); var replier = Job("REPLIER", ccsid); var physical = Backing(owner); uint key;
        using (Enter(owner))
        {
            Files.Create("QTEMP", "TEXTS", ccsid: ccsid);
            Files.Add("QTEMP", "TEXTS", "USR0001", new MessageDescription("Private &1", "Help &1", 30, [new("*CHAR", 3)], ccsid));
            var snapshot = Files.Snapshot("QTEMP", "TEXTS", "USR0001", "*LIBL", Text("ABC", ccsid));
            Assert.Equal("QTEMP", snapshot.Library); Assert.True(snapshot.JobTemporary);
            var formatted = MessageDescriptionFormat.Format(snapshot.Description, snapshot.Replacement, ccsid);
            key = Queues.SendTo([new(Operator)], formatted.Text, messageId: "USR0001", severity: 30, predefined: snapshot).Single();
            Files.Change("QTEMP", "TEXTS", "USR0001", text: "Changed &1");
        }
        using (Enter(replier))
        {
            var listed = Queues.List(Operator).Single(); Assert.DoesNotContain(physical, listed.Predefined!.Serialize());
            var received = Queues.Receive(Operator, MessageSelection.Key, key, remove: false, receiveCcsid: ccsid)!;
            Assert.Equal("Private ABC", received.Data.ToText()); Assert.Equal("Help ABC", received.SecondLevel!.ToText());
            Assert.Equal("QTEMP", received.ActualMessageFileLibrary); Assert.Equal("QTEMP", received.Predefined!.Library);
            Assert.Equal("*LIBL", received.Predefined.RequestedLibrary); Assert.DoesNotContain(physical, received.Predefined.Serialize());
        }
        _system.Jobs.Complete(owner.Key);
        using (Enter(replier))
        {
            var received = Queues.Receive(Operator, MessageSelection.Key, key)!;
            Assert.Equal("Private ABC", received.Data.ToText()); Assert.Equal("", received.ActualMessageFileLibrary);
            Assert.Equal("QTEMP", received.Predefined!.Library); Assert.DoesNotContain(physical, received.Predefined.Serialize());
        }
    }

    [Fact]
    public void Message_file_edits_and_exported_definitions_survive_cleanup()
    {
        var owner = Job();
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.ChangeBits);
        using (Enter(owner))
        {
            Files.Create("QTEMP", "TEXTS"); Files.Add("QTEMP", "TEXTS", "USR0001", "Original");
            Files.Change("QTEMP", "TEXTS", "USR0001", text: "Changed");
            new ObjectCatalogOperations(_system.Connections).Relocate(new("QTEMP", "TEXTS"), ObjectType.MessageFile, new("QGPL", "EXPORTED"), copy: true);
        }
        _system.Jobs.Complete(owner.Key); Assert.Equal("Changed", Files.Get("QGPL", "EXPORTED", "USR0001"));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_private_message_workflow_uses_logical_queue_and_file_names(int ccsid)
    {
        var owner = Job("QSECOFR", ccsid); using var session = new ExecutionSession(_system, owner, CancellationToken.None);
        Ok(session.Execute("CRTMSGQ QTEMP/INBOX AUT(*EXCLUDE)")); Ok(session.Execute("CRTMSGF QTEMP/TEXTS"));
        Ok(session.Execute("ADDMSGD USR0001 QTEMP/TEXTS 'Private &1' SECLVL('Help &1') FMT((*CHAR 3))"));
        using (Enter(owner))
            _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "PRIVMSG"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = """
                PGM
                DCL &DATA *CHAR LEN(20)
                DCL &LIB *CHAR LEN(10)
                DCL &ACTUAL *CHAR LEN(10)
                SNDPGMMSG MSGID(USR0001) MSGF(QTEMP/TEXTS) MSGDTA('ABC') TOMSGQ(QTEMP/INBOX)
                RCVMSG MSGQ(QTEMP/INBOX) MSG(&DATA) MSGFLIB(&LIB) SNDMSGFLIB(&ACTUAL)
                IF (&DATA *EQ 'Private ABC' *AND &LIB *EQ 'QTEMP' *AND &ACTUAL *EQ 'QTEMP') THEN(SNDPGMMSG MSG('PRIVATE MESSAGE ACCEPTED'))
                ENDPGM
                """ });
        var result = session.Execute("CALL QTEMP/PRIVMSG"); Ok(result); Assert.Contains("PRIVATE MESSAGE ACCEPTED", result.Message);
        Ok(session.Execute("SNDMSG 'Visible' QTEMP/INBOX")); result = session.Execute("DSPMSG QTEMP/INBOX"); Ok(result);
        Assert.Contains("QTEMP/INBOX", result.WorkList!.Title); Assert.DoesNotContain("@T", JsonSerializer.Serialize(result.WorkList));
        Ok(session.Execute("RMVMSG MSGQ(QTEMP/INBOX) CLEAR(*ALL)")); Ok(session.Execute("DLTMSGQ QTEMP/INBOX")); Ok(session.Execute("DLTMSGF QTEMP/TEXTS"));
    }

    public void Dispose() => _system.Dispose();
}
