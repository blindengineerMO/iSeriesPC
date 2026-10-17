using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class DataQueueTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly DataQueueStore _queues;
    private readonly QualifiedName _queue = new("QGPL", "DATA");
    public DataQueueTests() { _system.Start(); _queues = new(_system.Connections); }
    private static ProgramBuffer Bytes(string hex) => new(Convert.FromHexString(hex), 37);

    [Theory]
    [InlineData(DataQueueSequence.Fifo, "010203")]
    [InlineData(DataQueueSequence.Lifo, "030201")]
    public void Sequences_preserve_opaque_bytes_without_transcoding(DataQueueSequence sequence, string expected)
    {
        _queues.Create(_queue, 3, sequence);
        foreach (var value in new[] { "01", "02", "03" }) _queues.Send(_queue, Bytes(value));
        var actual = Enumerable.Range(0, 3).SelectMany(_ => _queues.Receive(_queue)!.Data.ToArray()).ToArray();
        Assert.Equal(expected, Convert.ToHexString(actual)); Assert.Null(_queues.Receive(_queue));
        _queues.Send(_queue, new(new byte[] { 255, 0, 254 }, 1208));
        var raw = _queues.Receive(_queue)!; Assert.Equal(1208, raw.Data.Ccsid); Assert.Equal("FF00FE", Convert.ToHexString(raw.Data.ToArray()));
    }

    [Theory]
    [InlineData(DataQueueKeyOrder.Equal, "F0", "30")]
    [InlineData(DataQueueKeyOrder.NotEqual, "C1", "20")]
    [InlineData(DataQueueKeyOrder.Greater, "C1", "20")]
    [InlineData(DataQueueKeyOrder.GreaterOrEqual, "C1", "10")]
    [InlineData(DataQueueKeyOrder.Less, "F0", "10")]
    [InlineData(DataQueueKeyOrder.LessOrEqual, "F0", "10")]
    public void Keyed_selection_uses_ascending_unsigned_bytes_and_FIFO_duplicates(DataQueueKeyOrder order, string search, string expected)
    {
        _queues.Create(_queue, 2, DataQueueSequence.Keyed, 1);
        _queues.Send(_queue, Bytes("30"), Bytes("F0"));
        _queues.Send(_queue, Bytes("20"), Bytes("C2"));
        _queues.Send(_queue, Bytes("10"), Bytes("C1"));
        _queues.Send(_queue, Bytes("11"), Bytes("C1"));
        var entry = _queues.Receive(_queue, Bytes(search), order)!;
        Assert.Equal(expected, Convert.ToHexString(entry.Data.ToArray()));
        if (expected == "10") Assert.Equal("11", Convert.ToHexString(_queues.Receive(_queue, Bytes("C1"))!.Data.ToArray()));
    }

    [Theory]
    [InlineData(DataQueueSequence.Fifo, 0, 0)]
    [InlineData(DataQueueSequence.Fifo, 64513, 0)]
    [InlineData(DataQueueSequence.Fifo, 1, 1)]
    [InlineData(DataQueueSequence.Keyed, 1, 0)]
    [InlineData(DataQueueSequence.Keyed, 1, 257)]
    [InlineData((DataQueueSequence)99, 1, 0)]
    public void Invalid_definitions_do_not_create_objects(DataQueueSequence sequence, int length, int keyLength)
    {
        Assert.Throws<CpfException>(() => _queues.Create(_queue, length, sequence, keyLength));
        Assert.False(_system.Objects.Exists("QGPL", "DATA", ObjectType.DataQueue));
    }

    [Fact]
    public void Keys_and_lengths_are_checked_before_mutation()
    {
        _queues.Create(_queue, 2, DataQueueSequence.Keyed, 2);
        Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("010203"), Bytes("0000")));
        Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01")));
        Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes(""), Bytes("0000")));
        Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01"), Bytes("00")));
        Assert.Throws<CpfException>(() => _queues.Receive(_queue));
        Assert.Throws<CpfException>(() => _queues.Receive(_queue, Bytes("0000"), (DataQueueKeyOrder)99));
        Assert.Equal(0, _queues.Inspect(_queue).Entries);
        _queues.Send(_queue, Bytes("FF00"), Bytes("0011"));
        Assert.Throws<CpfException>(() => _queues.Receive(_queue, Bytes("0011"), maximumDataLength: 1));
        Assert.Throws<CpfException>(() => _queues.Receive(_queue, Bytes("0011"), maximumKeyLength: 1));
        Assert.Equal(1, _queues.Inspect(_queue).Entries);
        var peek = _queues.Receive(_queue, Bytes("0011"), remove: false)!;
        Assert.Equal(peek.Id, _queues.Receive(_queue, Bytes("0011"))!.Id);
        Assert.Equal(0, _queues.Inspect(_queue).Bytes);
    }

    [Fact]
    public void Exact_maximum_entry_and_key_lengths_round_trip()
    {
        _queues.Create(_queue, 64512, DataQueueSequence.Keyed, 256);
        var data = new ProgramBuffer(Enumerable.Repeat((byte)255, 64512).ToArray(), 37);
        var key = new ProgramBuffer(Enumerable.Range(0, 256).Select(n => (byte)n).ToArray(), 37);
        _queues.Send(_queue, data, key);
        var entry = _queues.Receive(_queue, key)!; Assert.Equal(data.ToArray(), entry.Data.ToArray()); Assert.Equal(key.ToArray(), entry.Key.ToArray());
    }

    [Fact]
    public void Clear_filters_keyed_entries_atomically_and_keeps_others()
    {
        _queues.Create(_queue, 1, DataQueueSequence.Keyed, 1);
        foreach (var key in new[] { "00", "11", "11", "22" }) _queues.Send(_queue, Bytes("FF"), Bytes(key));
        Assert.Equal(3, _queues.Clear(_queue, Bytes("11"), DataQueueKeyOrder.LessOrEqual));
        Assert.Equal("22", Convert.ToHexString(_queues.Receive(_queue, Bytes("00"), DataQueueKeyOrder.Greater)!.Key.ToArray()));
        Assert.Equal(0, _queues.Clear(_queue));
    }

    [Fact]
    public void Count_capacity_and_byte_capacity_fail_without_losing_existing_data()
    {
        _queues.Create(_queue, 64512);
        using (var connection = _system.Connections.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<4096)
                INSERT INTO sys_data_queue_entries(queue_lib,queue_name,queue_identity,key_data,data,ccsid,sent)
                SELECT 'QGPL','DATA',json_extract(source,'$.Definition.Identity'),X'',X'01',37,'2026-10-16T00:00:00Z'
                FROM n,sys_objects WHERE lib='QGPL' AND name='DATA' AND type='*DTAQ'
                """;
            insert.ExecuteNonQuery();
        }
        Assert.Equal("CPF9504", Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("02"))).MessageId);
        Assert.Equal(4096, _queues.Inspect(_queue).Entries); _queues.Clear(_queue);
        var data = new ProgramBuffer(new byte[64512], 37);
        for (var count = 0; count < DataQueueStore.MaximumBytes / data.Length; count++) _queues.Send(_queue, data);
        Assert.Throws<CpfException>(() => _queues.Send(_queue, data));
        Assert.Equal(260, _queues.Inspect(_queue).Entries);
        _queues.Receive(_queue); _queues.Send(_queue, data); Assert.Equal(260, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public async Task Waiting_receive_observes_a_producer_without_holding_a_transaction()
    {
        _queues.Create(_queue, 3);
        var waiting = Task.Run(() => _queues.Receive(_queue, wait: TimeSpan.FromSeconds(5)));
        await Task.Delay(100); Assert.False(waiting.IsCompleted);
        _queues.Send(_queue, Bytes("FF00"));
        var entry = await waiting.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal("FF00", Convert.ToHexString(entry!.Data.ToArray()));
        Assert.Equal(0, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public async Task Empty_timeout_and_cancellation_leave_queues_usable()
    {
        _queues.Create(_queue, 1);
        var stopwatch = Stopwatch.StartNew(); Assert.Null(_queues.Receive(_queue, wait: TimeSpan.FromMilliseconds(80)));
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(60));
        using var stop = new CancellationTokenSource();
        var waiting = Task.Run(() => _queues.Receive(_queue, wait: Timeout.InfiniteTimeSpan, cancellationToken: stop.Token));
        await Task.Delay(80); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        _queues.Send(_queue, Bytes("01")); Assert.NotNull(_queues.Receive(_queue));
    }

    [Fact]
    public async Task Concurrent_producers_and_consumers_deliver_each_entry_once()
    {
        _queues.Create(_queue, 4);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Enumerable.Range(0, 20).Select(_ =>
            BitConverter.ToInt32(_queues.Receive(_queue, wait: TimeSpan.FromSeconds(10), cancellationToken: stop.Token)!.Data.ToArray())).ToArray())).ToArray();
        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() => {
            for (var index = 0; index < 20; index++) _queues.Send(_queue, new(BitConverter.GetBytes(producer * 20 + index), 37), cancellationToken: stop.Token);
        })).ToArray();
        await Task.WhenAll(producers); var results = (await Task.WhenAll(consumers)).SelectMany(values => values).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 80), results); Assert.Equal(0, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public async Task A_waiter_rejects_a_deleted_and_recreated_queue()
    {
        _queues.Create(_queue, 1);
        var waiting = Task.Run(() => _queues.Receive(_queue, wait: TimeSpan.FromSeconds(4)));
        await Task.Delay(150);
        _system.Objects.Delete("QGPL", "DATA", ObjectType.DataQueue); _queues.Create(_queue, 1); _queues.Send(_queue, Bytes("01"));
        await Assert.ThrowsAsync<CpfException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public void Live_authority_and_sender_identity_apply_at_the_service_boundary()
    {
        _queues.Create(_queue, 1, includeSender: true);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.DataQueue, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        var job = _system.Jobs.CreateInteractive("QUSER");
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            _queues.Send(_queue, Bytes("FF"));
            Assert.Throws<CpfException>(() => _queues.Receive(_queue));
            Assert.Throws<CpfException>(() => _queues.Clear(_queue));
        }
        var entry = _queues.Receive(_queue, remove: false)!;
        Assert.Equal("QUSER", entry.Sender!.User); Assert.Equal(job.Key, entry.Sender.Job);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.DataQueue, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER", job.Key)) Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01")));
        Assert.Equal(1, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public async Task Authority_revocation_interrupts_an_existing_wait_without_consuming_data()
    {
        _queues.Create(_queue, 1);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.DataQueue, "QUSER", Authorities.ChangeBits);
        var waiting = Task.Run(() => { using var identity = OperationIdentity.Enter("QUSER"); return _queues.Receive(_queue, wait: TimeSpan.FromSeconds(4)); });
        await Task.Delay(150); _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.DataQueue, "QUSER", AuthorityBit.None);
        await Assert.ThrowsAsync<CpfException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Relocation_cascades_entries_copy_starts_empty_and_delete_cleans_up()
    {
        _queues.Create(_queue, 1); _queues.Send(_queue, Bytes("FF")); var original = _queues.Inspect(_queue).Definition.Identity;
        var operations = new ObjectCatalogOperations(_system.Connections);
        operations.Relocate(_queue, ObjectType.DataQueue, new("QGPL", "COPY"), copy: true);
        Assert.Equal(0, _queues.Inspect(new("QGPL", "COPY")).Entries);
        Assert.NotEqual(original, _queues.Inspect(new("QGPL", "COPY")).Definition.Identity);
        operations.Relocate(_queue, ObjectType.DataQueue, new("QGPL", "MOVED"));
        Assert.Equal("FF", Convert.ToHexString(_queues.Receive(new("QGPL", "MOVED"))!.Data.ToArray()));
        _queues.Send(new("QGPL", "MOVED"), Bytes("01")); _system.Objects.Delete("QGPL", "MOVED", ObjectType.DataQueue);
        using var connection = _system.Connections.Open(); using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sys_data_queue_entries"; Assert.Equal(0L, count.ExecuteScalar());
    }

    [Theory]
    [InlineData("{\"Version\":99,\"Definition\":{}}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Definition\":{}}")]
    [InlineData("{\"Version\":1,\"Definition\":null}")]
    public void Corrupt_definitions_fail_before_accessing_entries(string source)
    {
        _queues.Create(_queue, 1); _queues.Send(_queue, Bytes("FF"));
        var descriptor = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.DataQueue); descriptor.Source = source; _system.Objects.Update(descriptor);
        Assert.Throws<CpfException>(() => _queues.Receive(_queue)); Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01")));
        using var connection = _system.Connections.Open(); using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sys_data_queue_entries"; Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public void Explicit_object_allocation_blocks_other_jobs_queue_mutations()
    {
        _queues.Create(_queue, 1);
        var holder = _system.Jobs.CreateInteractive("QUSER"); var contender = _system.Jobs.CreateInteractive("QUSER");
        var locks = new Ipc.Services.Work.JobLockStore(_system.Connections);
        using (locks.Acquire(holder.Key, new("QGPL", "DATA", ObjectType.DataQueue), Ipc.Services.Work.JobLockMode.Exclusive, TimeSpan.Zero))
        using (OperationIdentity.Enter("QUSER", contender.Key))
        using (locks.EnterCommand(contender.Key, CancellationToken.None))
            Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01")));
        Assert.Equal(0, _queues.Inspect(_queue).Entries);
        using (OperationIdentity.Enter("QUSER", contender.Key))
        using (locks.EnterCommand(contender.Key, CancellationToken.None)) _queues.Send(_queue, Bytes("01"));
        Assert.Equal(1, _queues.Inspect(_queue).Entries);
    }

    [Fact]
    public void Creation_honors_library_default_authority_and_authorization_lists()
    {
        var library = _system.Objects.GetRequired("QSYS", "QGPL", ObjectType.Library);
        library.ExtendedAttributes = new Dictionary<string, string> { ["ipc.library.createAuthority"] = "*EXCLUDE" }; _system.Objects.Update(library);
        _queues.Create(_queue, 1);
        Assert.Equal(AuthorityBit.None, _system.Objects.GetRequired("QGPL", "DATA", ObjectType.DataQueue).PublicAuthority);
        using (OperationIdentity.Enter("QUSER")) Assert.Throws<CpfException>(() => _queues.Send(_queue, Bytes("01")));
        _system.Security.Authority.AddAuthLMember("DQACCESS", "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        _queues.Create(new("QGPL", "LISTED"), 1, authority: "DQACCESS");
        using (OperationIdentity.Enter("QUSER")) _queues.Send(new("QGPL", "LISTED"), Bytes("FF"));
        Assert.Equal(1, _queues.Inspect(new("QGPL", "LISTED")).Entries);
    }
    public void Dispose() => _system.Dispose();
}
