using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class BatchQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-batch-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    public BatchQueueTests()
    {
        _system = IpcSystem.Create(_directory);
        _system.Start();
        _system.Subsystems.Ensure("ONE", "One active job", 1);
        _system.Subsystems.Start("ONE");
    }

    [Fact]
    public async Task Independent_claimants_respect_priority_fifo_and_subsystem_capacity()
    {
        var low = _system.Jobs.Submit("LOW", "", "ONE", priority: 9, command: "DSPJOB");
        var high = _system.Jobs.Submit("HIGH", "", "ONE", priority: 1, command: "DSPJOB");
        var high2 = _system.Jobs.Submit("HIGH2", "", "ONE", priority: 1, command: "DSPJOB");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var factory = new SqliteConnectionFactory(_directory);
            return new BatchQueue(factory, new JobService(factory)).ClaimNext();
        })));
        var winner = Assert.Single(attempts, a => a is not null)!.Value;
        Assert.Equal(high.Key, winner.Job.Key);
        var queue = new BatchQueue(_system.Connections, _system.Jobs);
        Assert.Null(queue.ClaimNext());
        _system.Jobs.Complete(high.Key);
        Assert.Equal(high2.Key, queue.ClaimNext()!.Value.Job.Key);
        _system.Jobs.Complete(high2.Key);
        Assert.Equal(low.Key, queue.ClaimNext()!.Value.Job.Key);
    }

    [Fact]
    public void Stopped_subsystems_and_held_jobs_are_not_dispatched_and_interrupted_jobs_are_not_retried()
    {
        var job = _system.Jobs.Submit("TEST", "", "ONE", command: "DSPJOB");
        var queue = new BatchQueue(_system.Connections, _system.Jobs);
        _system.Jobs.Hold(job.Key);
        Assert.Null(queue.ClaimNext());
        _system.Jobs.Release(job.Key);
        _system.Subsystems.End("ONE");
        Assert.Null(queue.ClaimNext());
        _system.Subsystems.Start("ONE");
        Assert.Equal(job.Key, queue.ClaimNext()!.Value.Job.Key);
        queue.RecoverInterrupted();
        Assert.Null(queue.ClaimNext());
        var recovered = _system.Jobs.GetRequired(job.Key);
        Assert.Equal(JobCompletion.Abnormal, recovered.CompletionCode);
        Assert.Contains("uncertain", recovered.CompletionMessage);
    }

    [Fact]
    public async Task Concurrent_log_writes_have_unique_contiguous_sequences()
    {
        var job = _system.Jobs.Submit("LOGTEST", "", "ONE", command: "DSPJOB");
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            using var factory = new SqliteConnectionFactory(_directory);
            new JobService(factory).WriteLog(job, "INFO", null, 0, $"Entry {i}");
        })));
        Assert.Equal(Enumerable.Range(1, 31), _system.Jobs.GetLog(job.Key).Select(e => e.Sequence));
    }

    public void Dispose()
    {
        _system.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
