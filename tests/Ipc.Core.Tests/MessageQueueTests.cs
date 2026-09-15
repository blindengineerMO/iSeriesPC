using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed class MessageQueueTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly MessageQueueStore _store;
    private static readonly QualifiedName Inbox = new("QGPL", "INBOX"), Replies = new("QGPL", "REPLIES");
    public MessageQueueTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "MessageQueueFixture22");
        _store = new(_system.Connections); _store.Create(Inbox); _store.Create(Replies);
    }
    private static ProgramBuffer Bytes(string hex) => new(Convert.FromHexString(hex), 1208);
    private uint Send(string hex = "FF00") => Assert.Single(_store.Send(new[] { Inbox }, Bytes(hex)));
    [Fact]
    public void Receive_preserves_bytes_and_first_last_key_and_new_message_selection()
    {
        var first = Send(); var last = Send("0102");
        Assert.Equal(first, _store.Receive(Inbox, remove: false)!.Key);
        Assert.Equal(last, _store.Receive(Inbox, remove: false)!.Key); Assert.Null(_store.Receive(Inbox, remove: false));
        Assert.Equal(first, _store.Receive(Inbox, MessageSelection.First, remove: false)!.Key);
        Assert.Equal(last, _store.Receive(Inbox, MessageSelection.Last, remove: false)!.Key);
        var received = _store.Receive(Inbox, MessageSelection.Key, first)!;
        Assert.Equal("FF00", Convert.ToHexString(received.Data.ToArray())); Assert.True(received.Seen);
        Assert.Null(_store.Receive(Inbox, MessageSelection.Key, first));
        Assert.Equal(last, Assert.Single(_store.List(Inbox)).Key);
    }
    [Fact]
    public void Reply_is_atomic_correlated_and_cannot_be_sent_twice()
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("4142"), "INQ", Replies, Bytes("44")));
        Assert.Equal("00000001", Convert.ToHexString(_store.List(Inbox).Single().KeyBuffer(1208).ToArray()));
        var replyKey = _store.Reply(Inbox, key, Bytes("FF01"), remove: false);
        Assert.True(Assert.Single(_store.List(Inbox)).Replied);
        Assert.Throws<CpfException>(() => _store.Reply(Inbox, key));
        var reply = _store.Receive(Replies, MessageSelection.Reply, key)!;
        Assert.Equal(replyKey, reply.Key); Assert.Equal(key, reply.CorrelationKey); Assert.Equal("FF01", Convert.ToHexString(reply.Data.ToArray()));
        _store.Receive(Inbox, MessageSelection.Key, key); Assert.Empty(_store.List(Replies));
    }
    [Fact]
    public void Removing_unanswered_inquiry_sends_default_and_missing_reply_queue_rolls_back()
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), "INQ", Replies, Bytes("44")));
        _store.Receive(Inbox); Assert.Empty(_store.List(Inbox));
        Assert.Equal("44", Convert.ToHexString(_store.Receive(Replies, MessageSelection.Reply, key)!.Data.ToArray()));
        key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("42"), "INQ", Replies));
        _system.Objects.Delete(Replies.Library, Replies.Name.Value, ObjectType.MessageQueue);
        Assert.Throws<CpfException>(() => _store.Receive(Inbox));
        Assert.False(Assert.Single(_store.List(Inbox)).Seen); Assert.False(Assert.Single(_store.List(Inbox)).Replied);
        _store.Create(Replies); // Recreating the name must not redirect the old inquiry.
        Assert.Throws<CpfException>(() => _store.Reply(Inbox, key)); Assert.Empty(_store.List(Replies));
    }
    [Fact]
    public void Rename_follows_queue_contents_and_reply_routes_without_reusing_deleted_keys()
    {
        var inquiry = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), "INQ", Replies));
        var renamed = new Ipc.Console.Session.CommandService(_system).Execute("RNMOBJ OBJ(QGPL/REPLIES) OBJTYPE(*MSGQ) NEWOBJ(MOVED)");
        Assert.False(renamed.IsError, renamed.Message);
        var moved = new QualifiedName("QGPL", "MOVED"); _store.Reply(Inbox, inquiry, Bytes("52"));
        Assert.Equal(inquiry, Assert.Single(_store.List(moved)).CorrelationKey);
        _system.Objects.Delete(moved.Library, moved.Name.Value, ObjectType.MessageQueue); _store.Create(moved);
        Assert.True(Send() > inquiry); Assert.Empty(_store.List(moved));
    }
    [Fact]
    public void Sender_identity_is_server_established_and_authority_revocation_is_live()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "READER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using (OperationIdentity.Enter("READER")) { Send(); Assert.Throws<CpfException>(() => _store.List(Inbox)); }
        Assert.Equal("READER", Assert.Single(_store.List(Inbox)).Sender);
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "READER", Authorities.UseBits);
        using (OperationIdentity.Enter("READER"))
        {
            Assert.Throws<CpfException>(() => _store.Receive(Inbox));
            Assert.NotNull(_store.Receive(Inbox, remove: false)); Assert.Throws<CpfException>(() => Send());
        }
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "READER", AuthorityBit.None);
        using (OperationIdentity.Enter("READER")) Assert.Throws<CpfException>(() => _store.Receive(Inbox, remove: false));
        Assert.Single(_store.List(Inbox));
    }
    [Fact]
    public void Multiple_destinations_are_atomic_when_a_later_queue_is_full()
    {
        Fill(Replies); Assert.Throws<CpfException>(() => _store.Send(new[] { Inbox, Replies }, Bytes("41")));
        Assert.Empty(_store.List(Inbox)); Assert.Equal(1000, _store.List(Replies, limit: 1000).Count);
    }
    [Fact]
    public void Reply_to_full_queue_keeps_inquiry_unanswered_and_receive_unconsumed()
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), "INQ", Replies, Bytes("44")));
        Fill(Replies); Assert.Throws<CpfException>(() => _store.Reply(Inbox, key)); Assert.Throws<CpfException>(() => _store.Receive(Inbox));
        var inquiry = Assert.Single(_store.List(Inbox)); Assert.False(inquiry.Replied); Assert.False(inquiry.Seen);
    }
    [Fact]
    public async Task Concurrent_receivers_consume_each_message_once_and_wait_allows_senders()
    {
        var keys = Enumerable.Range(0, 20).Select(_ => Send()).ToHashSet();
        var received = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => _store.Receive(Inbox)!.Key)));
        Assert.Equal(keys.Order(), received.Order()); Assert.Empty(_store.List(Inbox));
        var waiting = Task.Run(() => _store.Receive(Inbox, wait: TimeSpan.FromSeconds(5)));
        await Task.Delay(100); var next = Send(); Assert.Equal(next, (await waiting)!.Key);
    }
    [Fact]
    public async Task Waiting_receive_observes_cancellation_without_consuming_future_messages()
    {
        using var stop = new CancellationTokenSource();
        var waiting = Task.Run(() => _store.Receive(Inbox, wait: Timeout.InfiniteTimeSpan, cancellationToken: stop.Token));
        await Task.Delay(100); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        var key = Send(); Assert.Equal(key, Assert.Single(_store.List(Inbox)).Key);
        Assert.Null(_store.Receive(Replies, wait: TimeSpan.FromMilliseconds(10)));
    }
    [Fact]
    public async Task Waiting_receive_rechecks_revoked_authority()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "READER", Authorities.UseBits);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = Task.Run(() => {
            using var identity = OperationIdentity.Enter("READER");
            return _store.Receive(Inbox, remove: false, wait: Timeout.InfiniteTimeSpan, cancellationToken: stop.Token);
        });
        await Task.Delay(100); _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "READER", AuthorityBit.None);
        var error = await Assert.ThrowsAsync<CpfException>(async () => await waiting); Assert.Equal("CPF9802", error.MessageId);
    }
    [Fact]
    public void Opaque_unsigned_keys_reach_the_four_byte_boundary_without_wraparound()
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE sqlite_sequence SET seq=4294967294 WHERE name='sys_message_entries'"; command.ExecuteNonQuery(); }
        var key = Send(); Assert.Equal(uint.MaxValue, key);
        Assert.Equal("FFFFFFFF", Convert.ToHexString(Assert.Single(_store.List(Inbox)).KeyBuffer(1208).ToArray()));
        Assert.Equal("CPF2460", Assert.Throws<CpfException>(() => Send()).MessageId);
        Assert.Single(_store.List(Inbox));
    }
    [Fact]
    public void Byte_quota_is_enforced_before_message_count_and_released_after_removal()
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<2048)
                INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
                SELECT 'QGPL','INBOX','INFO',0,zeroblob(4096),1208,'QSYS','2026-09-15T00:00:00Z' FROM n
                """; command.ExecuteNonQuery();
        }
        Assert.Equal("CPF2460", Assert.Throws<CpfException>(() => Send()).MessageId);
        Assert.NotNull(_store.Receive(Inbox)); Send();
    }
    [Fact]
    public void Queue_messages_and_inquiry_state_survive_catalog_reopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-msgq-" + Guid.NewGuid().ToString("N"));
        try
        {
            uint key;
            using (var first = IpcSystem.Create(directory))
            {
                first.Start(); var store = new MessageQueueStore(first.Connections); store.Create(Inbox); store.Create(Replies);
                key = Assert.Single(store.Send(new[] { Inbox }, Bytes("FF00"), "INQ", Replies, Bytes("44")));
                store.Receive(Inbox, remove: false);
            }
            using var second = IpcSystem.Create(directory); second.Start(); var reopened = new MessageQueueStore(second.Connections);
            Assert.Null(reopened.Receive(Inbox, remove: false)); Assert.True(Assert.Single(reopened.List(Inbox)).Seen);
            reopened.Reply(Inbox, key); Assert.Equal("44", Convert.ToHexString(reopened.Receive(Replies)!.Data.ToArray()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void Removal_selects_old_new_and_keeps_unanswered_inquiries()
    {
        var old = Send("41"); _store.Receive(Inbox, remove: false);
        var pending = Assert.Single(_store.Send(new[] { Inbox }, Bytes("42"), "INQ", Replies));
        var fresh = Send("43");
        Assert.Equal(1, _store.Remove(Inbox, MessageClear.Old));
        Assert.Equal(new[] { pending, fresh }, _store.List(Inbox).Select(m => m.Key));
        Assert.Equal(1, _store.Remove(Inbox, MessageClear.KeepUnanswered));
        Assert.Equal(pending, Assert.Single(_store.List(Inbox)).Key); Assert.Empty(_store.List(Replies));
        Assert.Equal(1, _store.Remove(Inbox, MessageClear.New));
        Assert.Equal(pending, Assert.Single(_store.List(Replies)).CorrelationKey);
        Assert.Equal("CPF2410", Assert.Throws<CpfException>(() => _store.Remove(Inbox, key: old)).MessageId);
    }
    [Fact]
    public void Bulk_removal_rolls_back_all_default_replies_when_a_later_route_fails()
    {
        var missing = new QualifiedName("QGPL", "MISSING"); _store.Create(missing);
        _store.Send(new[] { Inbox }, Bytes("41"), "INQ", Replies);
        _store.Send(new[] { Inbox }, Bytes("42"), "INQ", missing);
        _system.Objects.Delete("QGPL", "MISSING", ObjectType.MessageQueue);
        Assert.Throws<CpfException>(() => _store.Remove(Inbox, MessageClear.All));
        Assert.Equal(2, _store.List(Inbox).Count); Assert.All(_store.List(Inbox), m => Assert.False(m.Replied));
        Assert.Empty(_store.List(Replies));
    }
    [Fact]
    public void Removal_needs_operate_delete_without_granting_read_and_rechecks_revocation()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "REMOVER" });
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "REMOVER", AuthorityBit.ObjectOperate | AuthorityBit.Delete);
        var key = Send();
        using (OperationIdentity.Enter("REMOVER"))
        {
            Assert.Throws<CpfException>(() => _store.List(Inbox));
            Assert.Equal(1, _store.Remove(Inbox, key: key));
        }
        key = Send();
        _system.Security.Authority.Grant("QGPL", "INBOX", ObjectType.MessageQueue, "REMOVER", AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("REMOVER")) Assert.Throws<CpfException>(() => _store.Remove(Inbox, key: key));
        Assert.Equal(key, Assert.Single(_store.List(Inbox)).Key);
    }
    [Fact]
    public void Same_queue_default_reply_survives_bulk_removal_and_cancel_is_atomic()
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), "INQ", Inbox, Bytes("44")));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => _store.Remove(Inbox, MessageClear.All, cancellationToken: stop.Token));
        Assert.False(Assert.Single(_store.List(Inbox)).Replied);
        Assert.Equal(1, _store.Remove(Inbox, MessageClear.All));
        var reply = Assert.Single(_store.List(Inbox)); Assert.Equal("RPY", reply.Kind);
        Assert.Equal(key, reply.CorrelationKey); Assert.Equal("44", Convert.ToHexString(reply.Data.ToArray()));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Removing_sender_copy_or_its_reply_removes_the_pair(bool removeCopy)
    {
        var copyKey = Assert.Single(_store.SendTo(new[] { new MessageQueueAddress(Inbox) }, Bytes("41"), "INQ", new(Replies), senderCopy: true));
        var inquiry = Assert.Single(_store.List(Inbox));
        var replyKey = _store.Reply(Inbox, inquiry.Key, Bytes("52"));
        Assert.Equal(2, _store.List(Replies).Count);
        _store.Remove(Replies, key: removeCopy ? copyKey : replyKey); Assert.Empty(_store.List(Replies));
    }
    [Theory]
    [InlineData("COMP", "01")]
    [InlineData("DIAG", "02")]
    [InlineData("INFO", "04")]
    [InlineData("INQ", "05")]
    public void Message_return_types_match_independent_codes(string kind, string code)
    {
        _store.Send(new[] { Inbox }, Bytes("41"), kind, kind == "INQ" ? Replies : null);
        Assert.Equal(code, _store.Receive(Inbox, remove: false)!.ReturnType);
    }
    [Theory]
    [InlineData(false, false, "24")]
    [InlineData(true, false, "23")]
    [InlineData(true, true, "21")]
    public void Reply_return_type_distinguishes_entered_message_default_and_system_default(bool explicitDefault, bool entered, string code)
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), "INQ", Replies, explicitDefault ? Bytes("44") : null));
        _store.Reply(Inbox, key, entered ? Bytes("52") : null);
        Assert.Equal(code, Assert.Single(_store.List(Replies)).ReturnType);
    }
    [Theory]
    [InlineData("NOTIFY", "16", "14")]
    [InlineData("ESCAPE", "17", "15")]
    public void Keep_exception_preserves_new_unhandled_state_until_a_handling_receive(string kind, string unhandled, string handled)
    {
        var key = Assert.Single(_store.Send(new[] { Inbox }, Bytes("41"), kind));
        var queue = new MessageQueueAddress(Inbox);
        Assert.Equal(unhandled, _store.ReceiveFrom(queue, remove: false, keepException: true)!.ReturnType);
        Assert.Equal(unhandled, _store.ReceiveFrom(queue, remove: false, keepException: true)!.ReturnType);
        Assert.False(Assert.Single(_store.List(Inbox)).Seen);
        Assert.Equal(unhandled, _store.ReceiveFrom(queue, remove: false)!.ReturnType);
        Assert.True(Assert.Single(_store.List(Inbox)).Seen);
        Assert.Equal(handled, _store.ReceiveFrom(queue, MessageSelection.Key, key, remove: false, keepException: true)!.ReturnType);
    }
    [Fact]
    public void Unsupported_return_type_fails_before_consume_or_mark_old()
    {
        _store.Send(new[] { Inbox }, Bytes("41"), "STATUS");
        Assert.Throws<CpfException>(() => _store.ReceiveFrom(new(Inbox), requireReturnType: true));
        Assert.False(Assert.Single(_store.List(Inbox)).Seen);
    }
    private void Fill(QualifiedName queue)
    {
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<4096)
            INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
            SELECT $lib,$name,'INFO',0,X'41',1208,'QSYS','2026-09-15T00:00:00Z' FROM n
            """;
        command.Parameters.AddWithValue("$lib", queue.Library); command.Parameters.AddWithValue("$name", queue.Name.Value); command.ExecuteNonQuery();
    }
    public void Dispose() => _system.Dispose();
}
