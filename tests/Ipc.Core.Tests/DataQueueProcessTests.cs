using System.Diagnostics;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class DataQueueProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-data-queues-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = new();
    private readonly QualifiedName _queue = new("QGPL", "DATA");

    [Fact]
    public async Task Independent_processes_compete_without_duplicates_or_lost_entries()
    {
        using var system = IpcSystem.Create(_directory); system.Start();
        var queues = new DataQueueStore(system.Connections); queues.Create(_queue, 8);
        var consumers = new[] { Start("data-consume", 20), Start("data-consume", 20) };
        var producers = new[] { Start("data-produce", 20), Start("data-produce", 20) };
        foreach (var child in consumers.Concat(producers)) await Ready(child);
        File.WriteAllText(Path.Combine(_directory, "start"), "start");
        var sent = (await Task.WhenAll(producers.Select(Result))).SelectMany(values => values).Order().ToArray();
        var received = (await Task.WhenAll(consumers.Select(Result))).SelectMany(values => values).Order().ToArray();
        Assert.Equal(sent, received); Assert.Equal(40, received.Distinct().Count()); Assert.Equal(0, queues.Inspect(_queue).Entries);
    }

    [Fact]
    public async Task Killed_waiting_process_does_not_retain_a_transaction_or_consume_future_entries()
    {
        using var system = IpcSystem.Create(_directory); system.Start();
        var queues = new DataQueueStore(system.Connections); queues.Create(_queue, 8);
        var consumer = Start("data-consume", 1); await Ready(consumer);
        File.WriteAllText(Path.Combine(_directory, "start"), "start");
        await Task.Delay(100); consumer.Kill(true); await consumer.WaitForExitAsync();
        queues.Send(_queue, new(BitConverter.GetBytes(42L), 37));
        Assert.Equal(42L, BitConverter.ToInt64(queues.Receive(_queue)!.Data.ToArray()));
    }

    [Fact]
    public void Committed_entries_and_sender_snapshots_survive_reopening_the_catalog()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var queues = new DataQueueStore(system.Connections); queues.Create(_queue, 4, DataQueueSequence.Keyed, 1, includeSender: true);
            var job = system.Jobs.CreateInteractive("QUSER");
            using var identity = Ipc.Services.Events.OperationIdentity.Enter("QUSER", job.Key);
            queues.Send(_queue, new(new byte[] { 255, 0 }, 1208), new(new byte[] { 0x80 }, 37));
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        var store = new DataQueueStore(reopened.Connections); var entry = store.Receive(_queue, new(new byte[] { 0x80 }, 37))!;
        Assert.Equal("FF00", Convert.ToHexString(entry.Data.ToArray())); Assert.Equal(1208, entry.Data.Ccsid); Assert.Equal("QUSER", entry.Sender!.User);
        Assert.Equal("QUSER", entry.Sender.Job!.Value.User); Assert.Equal(0, store.Inspect(_queue).Entries);
    }

    [Fact]
    public void Schema_23_upgrade_preserves_existing_message_bytes_and_adds_data_queue_storage()
    {
        using var factory = new SqliteConnectionFactory(_directory);
        new Migrator(factory, Migrator.Migrations.Where(migration => migration.Version <= 23).ToArray()).MigrateToLatest();
        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority) VALUES('QGPL','INBOX','*MSGQ','QSYS','2026-10-16','2026-10-16',3);
                INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
                VALUES('QGPL','INBOX','INFO',0,X'FF00',37,'QSYS','2026-10-16T00:00:00Z');
                """;
            insert.ExecuteNonQuery();
        }
        new Migrator(factory).MigrateToLatest();
        new SqliteObjectStore(factory).Create(new ObjectDescriptor { Key = new("QSYS", "QGPL"), ObjectType = ObjectType.Library });
        Assert.Equal("FF00", Convert.ToHexString(new MessageQueueStore(factory).Receive(new("QGPL", "INBOX"))!.Data.ToArray()));
        var queues = new DataQueueStore(factory); queues.Create(_queue, 2); queues.Send(_queue, new(new byte[] { 1, 2 }, 37));
        Assert.Equal("0102", Convert.ToHexString(queues.Receive(_queue)!.Data.ToArray()));
    }

    [Fact]
    public async Task Cancellation_interrupts_SQLite_write_lock_contention_before_an_entry_is_inserted()
    {
        using var system = IpcSystem.Create(_directory); system.Start();
        var queues = new DataQueueStore(system.Connections); queues.Create(_queue, 2);
        using var heldConnection = system.Connections.Open(); using var held = heldConnection.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource();
        var sending = Task.Run(() => queues.Send(_queue, new(new byte[] { 1 }, 37), cancellationToken: stop.Token));
        await Task.Delay(100); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        held.Rollback(); Assert.Equal(0, queues.Inspect(_queue).Entries);
    }

    private Process Start(string mode, int count)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { typeof(WorkFixture.Program).Assembly.Location, mode, _directory, count.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start)!; _children.Add(process); return process;
    }
    private async Task Ready(Process process)
    {
        await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "ready-" + process.Id)) || process.HasExited);
        if (process.HasExited) Assert.Fail(await process.StandardError.ReadToEndAsync());
    }
    private static async Task<long[]> Result(Process process)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        return JsonSerializer.Deserialize<long[]>(await process.StandardOutput.ReadToEndAsync())!;
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
