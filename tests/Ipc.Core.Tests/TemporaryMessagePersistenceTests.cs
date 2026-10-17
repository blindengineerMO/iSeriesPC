using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class TemporaryMessagePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-private-messages-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = new();
    private static readonly QualifiedName Operator = new("QGPL", "OPERATOR"), Queue = new("QTEMP", "PROCESS"), Replies = new("QTEMP", "REPLIES");
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private IpcSystem System()
    {
        var system = IpcSystem.Create(_directory); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "PrivateMessagesFixture26"); return system;
    }
    private Process Start(string mode, Job job, int count, int tag = 0)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { typeof(WorkFixture.Program).Assembly.Location, mode, _directory, count.ToString(CultureInfo.InvariantCulture), job.Key.Number.ToString(CultureInfo.InvariantCulture), job.Key.Name, job.Key.User, tag.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
        var process = Process.Start(start)!; _children.Add(process); return process;
    }
    private async Task<long[][]> Run(params Process[] processes)
    {
        var errors = processes.Select(p => p.StandardError.ReadToEndAsync()).ToArray(); var outputs = processes.Select(p => p.StandardOutput.ReadToEndAsync()).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (processes.Any(p => !File.Exists(Path.Combine(_directory, "ready-" + p.Id)))) await Task.Delay(10, stop.Token);
            File.WriteAllText(Path.Combine(_directory, "start"), "start");
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(stop.Token)));
            var values = new List<long[]>();
            for (var index = 0; index < processes.Length; index++)
            {
                Assert.True(processes[index].ExitCode == 0, await errors[index]);
                values.Add(JsonSerializer.Deserialize<long[]>(await outputs[index])!);
            }
            return values.ToArray();
        }
        finally
        {
            stop.Cancel(); foreach (var process in processes) if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task Independent_producers_and_consumers_deliver_exactly_once_within_each_private_namespace()
    {
        using var system = System(); var queues = new MessageQueueStore(system.Connections);
        var first = system.Jobs.CreateInteractive("QUSER"); var second = system.Jobs.CreateInteractive("QUSER");
        using (Enter(first)) queues.Create(Queue); using (Enter(second)) queues.Create(Queue);
        var result = await Run(Start("temp-message-send", first, 16, 101), Start("temp-message-consume", first, 16),
            Start("temp-message-send", second, 16, 201), Start("temp-message-consume", second, 16));
        Assert.Equal(result[0], result[1]); Assert.Equal(result[2], result[3]); Assert.Empty(result[1].Intersect(result[3]));
        Assert.Equal(16, result[1].Distinct().Count()); Assert.Equal(16, result[3].Distinct().Count());
        using (Enter(first)) Assert.Empty(queues.List(Queue)); using (Enter(second)) Assert.Empty(queues.List(Queue));
    }

    [Fact]
    public async Task An_independent_job_can_reply_to_private_queues_of_multiple_owners()
    {
        using var system = System(); var queues = new MessageQueueStore(system.Connections); queues.Create(Operator);
        var first = system.Jobs.CreateInteractive("QUSER"); var second = system.Jobs.CreateInteractive("QUSER");
        var replier = system.Jobs.CreateInteractive("QUSER");
        foreach (var (owner, tag) in new[] { (first, 101), (second, 201) })
        using (Enter(owner))
        {
            queues.Create(Replies, authority: "*EXCLUDE");
            for (var index = 0; index < 8; index++) queues.Send([Operator], new(BitConverter.GetBytes(((long)tag << 32) | (uint)index), 37), "INQ", Replies);
        }
        var replied = (await Run(Start("temp-message-reply", replier, 16))).Single(); Assert.Equal(16, replied.Distinct().Count());
        foreach (var (owner, tag) in new[] { (first, 101), (second, 201) })
        using (Enter(owner))
        {
            var received = Enumerable.Range(0, 8).Select(_ => queues.Receive(Replies)!).ToArray();
            Assert.Equal(Enumerable.Range(0, 8).Select(i => ((long)tag << 32) | (uint)i), received.Select(m => BitConverter.ToInt64(m.Data.ToArray())));
            Assert.All(received, message => Assert.Equal(replier.Key.Number, message.SenderJob)); Assert.Empty(queues.List(Replies));
        }
        Assert.Empty(queues.List(Operator));
    }

    [Fact]
    public async Task Reopen_preserves_private_messages_and_exclusive_server_recovery_cleans_them_and_their_reply_routes()
    {
        Job owner; string physical; uint inquiry;
        using (var system = System())
        {
            owner = system.Jobs.CreateInteractive("QUSER"); var queues = new MessageQueueStore(system.Connections); queues.Create(Operator);
            using (Enter(owner))
            {
                physical = new JobTemporaryLibrary(system.Connections).ResolveLibrary("QTEMP");
                queues.Create(Queue); queues.Create(Replies); queues.Send([Queue], new(new byte[] { 255, 0, 254, 1 }, 37));
                var files = new MessageDescriptionStore(system.Connections); files.Create("QTEMP", "TEXTS"); files.Add("QTEMP", "TEXTS", "USR0001", "Saved private");
                inquiry = queues.Send([Operator], new(new byte[] { 1 }, 37), "INQ", Replies).Single();
            }
        }
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); using var identity = Enter(owner);
            Assert.Equal("FF00FE01", Convert.ToHexString(new MessageQueueStore(system.Connections).List(Queue).Single().Data.ToArray()));
            Assert.Equal("Saved private", new MessageDescriptionStore(system.Connections).Get("QTEMP", "TEXTS", "USR0001"));
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token); using var factory = new SqliteConnectionFactory(_directory);
            Assert.Equal(JobExecutionState.Interrupted, new JobService(factory).GetRequired(owner.Key).ExecutionState);
            Assert.False(new SqliteObjectStore(factory).Exists("QSYS", physical, ObjectType.Library));
            using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM sys_message_entries WHERE queue_lib=$lib OR reply_lib=$lib"; query.Parameters.AddWithValue("$lib", physical); Assert.Equal(0L, query.ExecuteScalar());
            Assert.Equal("CPF2469", Assert.Throws<CpfException>(() => new MessageQueueStore(factory).Reply(Operator, inquiry, new(new byte[] { 2 }, 37))).MessageId);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void Failed_cleanup_restores_private_payloads_and_the_permanent_inquirys_original_route()
    {
        using var system = System(); var queues = new MessageQueueStore(system.Connections); queues.Create(Operator);
        var owner = system.Jobs.CreateInteractive("QUSER"); string physical; uint inquiry;
        using (Enter(owner))
        {
            physical = new JobTemporaryLibrary(system.Connections).ResolveLibrary("QTEMP");
            queues.Create(Queue); queues.Create(Replies); queues.Send([Queue], new(new byte[] { 255 }, 37));
            var files = new MessageDescriptionStore(system.Connections); files.Create("QTEMP", "AFILE"); files.Add("QTEMP", "AFILE", "USR0001", "Restored");
            inquiry = queues.Send([Operator], new(new byte[] { 1 }, 37), "INQ", Replies).Single();
        }
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "CREATE TRIGGER reject_private_message_cleanup BEFORE DELETE ON sys_objects WHEN OLD.name='AFILE' AND OLD.type='*MSGF' BEGIN SELECT RAISE(ABORT,'injected cleanup failure'); END"; query.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => system.Jobs.Complete(owner.Key));
        Assert.Equal(JobExecutionState.Running, system.Jobs.GetRequired(owner.Key).ExecutionState);
        query.CommandText = "SELECT reply_lib FROM sys_message_entries WHERE key=$key"; query.Parameters.AddWithValue("$key", (long)inquiry); Assert.Equal(physical, query.ExecuteScalar());
        using (Enter(owner))
        {
            Assert.Equal(255, queues.List(Queue).Single().Data.ToArray()[0]);
            Assert.Equal("Restored", new MessageDescriptionStore(system.Connections).Get("QTEMP", "AFILE", "USR0001"));
        }
        query.CommandText = "DROP TRIGGER reject_private_message_cleanup"; query.ExecuteNonQuery();
        queues.Reply(Operator, inquiry, new(new byte[] { 2 }, 37));
        using (Enter(owner)) Assert.Equal(2, queues.Receive(Replies)!.Data.ToArray()[0]);
        system.Jobs.Complete(owner.Key); Assert.False(system.Objects.Exists("QSYS", physical, ObjectType.Library));
    }

    [Fact]
    public async Task Waiting_receive_rejects_an_atomically_deleted_and_recreated_private_queue()
    {
        using var system = System(); var owner = system.Jobs.CreateInteractive("QUSER"); string physical;
        using (Enter(owner)) { new MessageQueueStore(system.Connections).Create(Queue); physical = new JobTemporaryLibrary(system.Connections).ResolveLibrary("QTEMP"); }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = Task.Factory.StartNew(() => {
            using var identity = Enter(owner); ready.TrySetResult();
            return new MessageQueueStore(system.Connections).Receive(Queue, wait: Timeout.InfiniteTimeSpan, cancellationToken: stop.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "CREATE TEMP TABLE saved_queue AS SELECT * FROM sys_objects WHERE lib=$lib AND name='PROCESS' AND type='*MSGQ'"; query.Parameters.AddWithValue("$lib", physical); query.ExecuteNonQuery();
        try
        {
            await ready.Task.WaitAsync(stop.Token);
            while (!receiving.IsCompleted)
            {
                stop.Token.ThrowIfCancellationRequested();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    query.Transaction = transaction; query.CommandText = "DELETE FROM sys_objects WHERE lib=$lib AND name='PROCESS' AND type='*MSGQ'; INSERT INTO sys_objects SELECT * FROM saved_queue; UPDATE sys_objects SET created=$created WHERE lib=$lib AND name='PROCESS' AND type='*MSGQ'";
                    if (!query.Parameters.Contains("$created")) query.Parameters.AddWithValue("$created", "");
                    query.Parameters["$created"].Value = DateTimeOffset.UtcNow.ToString("O"); query.ExecuteNonQuery(); transaction.Commit(); query.Transaction = null;
                }
                await Task.WhenAny(receiving, Task.Delay(50, stop.Token));
            }
            var failure = await Assert.ThrowsAsync<CpfException>(async () => await receiving); Assert.Equal("CPF9801", failure.MessageId); Assert.Contains("replaced", failure.Message);
        }
        finally { stop.Cancel(); try { await receiving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) when (error is CpfException or OperationCanceledException) { } }
        using (Enter(owner)) Assert.Empty(new MessageQueueStore(system.Connections).List(Queue));
    }

    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); } child.Dispose(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
