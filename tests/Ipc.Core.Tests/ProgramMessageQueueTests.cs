using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ProgramMessageQueueTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ProgramMessageQueueTests()
    { _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ProgramQueueFixture22"); }
    private ExecutionSession Session() => new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    private static ProgramBuffer Bytes() => new(new byte[] { 255, 0 }, 1208);
    [Fact]
    public void Frame_queues_are_distinct_and_only_the_owning_job_can_receive()
    {
        using var first = Session(); using var second = Session(); var store = new MessageQueueStore(_system.Connections);
        var environment = _system.JobRuntime.Environment(first.Job.Key)!;
        MessageQueueAddress parent, child;
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key))
        {
            parent = environment.MessageQueue();
            using (environment.EnterCall("CHILD"))
            {
                child = environment.MessageQueue(); Assert.NotEqual(parent, child); Assert.Equal(parent, environment.MessageQueue("*PRV"));
                store.SendTo(new[] { child }, Bytes()); Assert.Empty(store.ListFrom(parent));
                Assert.Equal("FF00", Convert.ToHexString(store.ReceiveFrom(child)!.Data.ToArray()));
            }
            Assert.Throws<CpfException>(() => store.ReceiveFrom(child));
            store.SendTo(new[] { parent }, Bytes());
        }
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key)) Assert.Throws<CpfException>(() => store.ReceiveFrom(parent));
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key)) Assert.NotNull(store.ReceiveFrom(parent));
    }
    [Fact]
    public void An_inquiry_grants_reply_delivery_without_granting_access_to_the_senders_frame()
    {
        using var sender = Session(); using var operatorJob = Session(); var store = new MessageQueueStore(_system.Connections);
        var inbox = new QualifiedName("QGPL", "INBOX"); store.Create(inbox);
        MessageQueueAddress replies; uint key;
        using (OperationIdentity.Enter("QSECOFR", sender.Job.Key))
        {
            replies = _system.JobRuntime.Environment(sender.Job.Key)!.MessageQueue();
            key = Assert.Single(store.SendTo(new[] { new MessageQueueAddress(inbox) }, Bytes(), "INQ", replies));
        }
        using (OperationIdentity.Enter("QSECOFR", operatorJob.Job.Key))
        {
            Assert.Throws<CpfException>(() => store.ListFrom(replies));
            store.Reply(inbox, key, Bytes()); Assert.Throws<CpfException>(() => store.ReceiveFrom(replies));
        }
        using (OperationIdentity.Enter("QSECOFR", sender.Job.Key)) Assert.Equal(key, store.ReceiveFrom(replies)!.CorrelationKey);
    }
    [Fact]
    public void Reply_after_frame_return_fails_without_removing_the_inquiry()
    {
        using var sender = Session(); var environment = _system.JobRuntime.Environment(sender.Job.Key)!; var store = new MessageQueueStore(_system.Connections);
        var inbox = new QualifiedName("QGPL", "INBOX"); store.Create(inbox); uint key;
        using (environment.EnterCall("CHILD"))
            key = Assert.Single(store.SendTo(new[] { new MessageQueueAddress(inbox) }, Bytes(), "INQ", environment.MessageQueue()));
        Assert.Throws<CpfException>(() => store.Reply(inbox, key, Bytes()));
        Assert.False(Assert.Single(store.List(inbox)).Replied);
    }
    [Fact]
    public void Clear_inactive_messages_preserves_active_frames_and_other_jobs()
    {
        using var first = Session(); using var second = Session(); var store = new MessageQueueStore(_system.Connections);
        var environment = _system.JobRuntime.Environment(first.Job.Key)!;
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key))
        {
            store.SendTo(new[] { environment.MessageQueue() }, Bytes());
            using (environment.EnterCall("PARENT"))
            using (environment.EnterCall("CHILD")) store.SendTo(new[] { environment.MessageQueue() }, Bytes());
            Assert.Equal(2, store.ListJobMessages(first.Job.Key).Count);
        }
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key))
        {
            var other = _system.JobRuntime.Environment(second.Job.Key)!;
            using (other.EnterCall("CHILD")) store.SendTo(new[] { other.MessageQueue() }, Bytes());
            Assert.Throws<CpfException>(() => store.RemoveInactiveJobMessages(first.Job.Key));
        }
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key))
        {
            Assert.Equal(1, store.RemoveInactiveJobMessages(first.Job.Key));
            Assert.Single(store.ListJobMessages(first.Job.Key)); Assert.Single(store.ListFrom(environment.MessageQueue()));
            Assert.Single(store.ListJobMessages(second.Job.Key)); Assert.Equal(0, store.RemoveInactiveJobMessages(first.Job.Key));
        }
        using var connection = _system.Connections.Open(); using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT count(*) FROM sys_program_message_queues WHERE job_number=$job AND active=0";
        inspect.Parameters.AddWithValue("$job", first.Job.Key.Number); Assert.Equal(0L, (long)inspect.ExecuteScalar()!);
    }
    [Fact]
    public void Exception_forwarding_reuses_delivery_and_rejects_other_job_references()
    {
        using var first = Session(); using var second = Session(); var store = new MessageQueueStore(_system.Connections);
        ProgramMessageReference reference;
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key))
        {
            var queue = _system.JobRuntime.Environment(first.Job.Key)!.MessageQueue();
            reference = store.DeliverException(queue, Bytes(), "CPF9898");
            Assert.Equal(reference, store.DeliverException(queue, Bytes(), "CPF9898", reference));
            Assert.Single(store.ListFrom(queue));
        }
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key))
        {
            var queue = _system.JobRuntime.Environment(second.Job.Key)!.MessageQueue();
            Assert.Throws<CpfException>(() => store.DeliverException(queue, Bytes(), "CPF9898", reference));
            Assert.Empty(store.ListFrom(queue));
        }
    }
    [Fact]
    public void Schema_21_preserves_existing_reply_bytes_and_classifies_known_default_data()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory, Migrator.Migrations.Take(20).ToArray()).MigrateToLatest();
        new SqliteObjectStore(factory).Create(new() { Key = new("QGPL", "INBOX"), ObjectType = ObjectType.MessageQueue });
        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent,default_reply)
                VALUES('QGPL','INBOX','INQ',0,X'FF00',1208,'QSYS','2026-09-15T00:00:00Z',X'44'),
                      ('QGPL','INBOX','RPY',0,X'52',1208,'QSYS','2026-09-15T00:00:00Z',X'');
                """; insert.ExecuteNonQuery();
        }
        new Migrator(factory).MigrateToLatest();
        var entries = new MessageQueueStore(factory).List(new("QGPL", "INBOX"));
        Assert.Equal(new[] { "05", "21" }, entries.Select(m => m.ReturnType));
        Assert.Equal("FF00", Convert.ToHexString(entries[0].Data.ToArray()));
        using var reopened = factory.Open(); using var inspect = reopened.CreateCommand();
        inspect.CommandText = "SELECT default_reply_code FROM sys_message_entries ORDER BY key";
        using var reader = inspect.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal("23", reader.GetString(0));
        Assert.True(reader.Read()); Assert.Equal("24", reader.GetString(0));
    }
    [Fact]
    public void Schema_20_preserves_deleted_message_key_high_water_mark()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory, Migrator.Migrations.Take(19).ToArray()).MigrateToLatest();
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "INSERT INTO sqlite_sequence(name,seq) VALUES('sys_message_entries',4000000000)"; command.ExecuteNonQuery(); }
        new Migrator(factory).MigrateToLatest();
        using var reopened = factory.Open(); using var inspect = reopened.CreateCommand();
        inspect.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='sys_message_entries'";
        Assert.Equal(4000000000L, (long)inspect.ExecuteScalar()!);
    }
    [Fact]
    public void Schema_20_preserves_named_queue_bytes_and_pending_reply_routes()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory, Migrator.Migrations.Take(19).ToArray()).MigrateToLatest();
        var objects = new SqliteObjectStore(factory);
        foreach (var name in new[] { "INBOX", "REPLIES" }) objects.Create(new() { Key = new("QGPL", name), ObjectType = ObjectType.MessageQueue });
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sys_message_entries(key,queue_lib,queue_name,kind,severity,data,ccsid,sender,sent,seen,reply_lib,reply_name,reply_type,default_reply)
                VALUES(7,'QGPL','INBOX','INQ',0,X'FF00',1208,'QSYS','2026-09-15T00:00:00Z',1,'QGPL','REPLIES','*MSGQ',X'44');
                UPDATE sqlite_sequence SET seq=99 WHERE name='sys_message_entries';
                """; command.ExecuteNonQuery();
        }
        new Migrator(factory).MigrateToLatest(); var store = new MessageQueueStore(factory);
        var inbox = new QualifiedName("QGPL", "INBOX"); var original = Assert.Single(store.List(inbox));
        Assert.Equal(7u, original.Key); Assert.True(original.Seen); Assert.Equal("FF00", Convert.ToHexString(original.Data.ToArray()));
        var key = store.Reply(inbox, 7); Assert.Equal(100u, key);
        Assert.Equal("44", Convert.ToHexString(store.Receive(new("QGPL", "REPLIES"))!.Data.ToArray()));
    }
    public void Dispose() => _system.Dispose();
}
