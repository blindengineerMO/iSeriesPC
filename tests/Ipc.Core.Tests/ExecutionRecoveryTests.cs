using System.Diagnostics;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class ExecutionRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-execution-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly List<Process> _processes = new();
    public ExecutionRecoveryTests() { _system = IpcSystem.Create(_directory); _system.Start(); }

    [Fact]
    public async Task Four_processes_allocate_unique_numbers_and_claim_each_request_once()
    {
        var writers = Enumerable.Range(0, 4).Select(_ => Start("allocate", 20)).ToArray();
        await Release(writers);
        var allocated = (await Task.WhenAll(writers.Select(Result))).SelectMany(n => n).ToArray();
        Assert.Equal(80, allocated.Length); Assert.Equal(80, allocated.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 80), allocated.Order());
        ExecuteSql("UPDATE sys_subsystems SET max_active=128 WHERE library='QSYS' AND name='QBATCH'");
        File.Delete(Path.Combine(_directory, "start"));
        var claimants = Enumerable.Range(0, 4).Select(_ => Start("claim", 80)).ToArray();
        await Release(claimants);
        var claimed = (await Task.WhenAll(claimants.Select(Result))).SelectMany(n => n).ToArray();
        Assert.Equal(allocated.Order(), claimed.Order());
        var queue = new BatchQueue(_system.Connections, _system.Jobs);
        var metadata = _system.Jobs.List().Select(j => queue.Inspect(j.Key)!).ToArray();
        Assert.All(metadata, m => { Assert.Equal(1, m.Attempts); Assert.Equal(JobExecutionState.Running, m.State); Assert.NotNull(m.HostId); Assert.NotNull(m.ClaimedAt); Assert.Null(m.FinishedAt); });
        Assert.Equal(80, metadata.Select(m => m.ClaimToken).Distinct().Count());
        Assert.Null(queue.ClaimNext());
    }

    [Fact]
    public async Task Killing_a_claimant_after_an_effect_marks_interruption_without_repeating_the_effect()
    {
        var interrupted = Submit("CRASH"); var pending = Submit("PENDING");
        var child = Start("effect-crash", 1); await Release(new[] { child });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(_directory, "effect"))) await Task.Delay(20, deadline.Token);
        child.Kill(entireProcessTree: true); await child.WaitForExitAsync(deadline.Token);
        using var connection = new SqliteConnectionFactory(_directory); var jobs = new JobService(connection); var queue = new BatchQueue(connection, jobs);
        Assert.Equal(JobExecutionState.Running, jobs.GetRequired(interrupted.Key).ExecutionState);
        queue.RecoverInterrupted();
        var info = queue.Inspect(interrupted.Key)!;
        Assert.Equal(JobExecutionState.Interrupted, info.State); Assert.Equal(1, info.Attempts); Assert.NotNull(info.ClaimToken); Assert.NotNull(info.FinishedAt);
        Assert.Contains("uncertain", jobs.GetRequired(interrupted.Key).CompletionMessage);
        Assert.Equal(pending.Key, queue.ClaimNext()!.Value.Job.Key);
        Assert.Null(queue.ClaimNext()); queue.RecoverInterrupted();
        Assert.Single(File.ReadAllLines(Path.Combine(_directory, "effect")));
        Assert.Single(jobs.GetLog(interrupted.Key), e => e.MessageType == "COMPLETION");
        var resubmission = Submit("REVIEWED");
        Assert.True(resubmission.Key.Number > pending.Key.Number);
        Assert.Equal(resubmission.Key, queue.ClaimNext()!.Value.Job.Key);
    }

    [Fact]
    public void Terminal_outcomes_survive_reopen_and_the_first_completion_wins()
    {
        var queue = new BatchQueue(_system.Connections, _system.Jobs);
        foreach (var state in new[] { JobExecutionState.Succeeded, JobExecutionState.Failed, JobExecutionState.Cancelled })
        {
            var job = Submit(state.ToString().ToUpperInvariant());
            Assert.Equal(job.Key, queue.ClaimNext()!.Value.Job.Key);
            _system.Jobs.Complete(job.Key, state == JobExecutionState.Succeeded ? JobCompletion.Normal : JobCompletion.Abnormal, "first outcome", state);
            _system.Jobs.Complete(job.Key, JobCompletion.Normal, "late success");
            using var factory = new SqliteConnectionFactory(_directory); var jobs = new JobService(factory);
            Assert.Equal(state, jobs.GetRequired(job.Key).ExecutionState);
            Assert.Equal("first outcome", jobs.GetRequired(job.Key).CompletionMessage);
            Assert.Single(jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        }
        queue.RecoverInterrupted(); Assert.Null(queue.ClaimNext());
    }

    [Fact]
    public void Completion_and_submission_roll_back_when_their_log_cannot_be_written()
    {
        var job = Submit("ATOMIC"); var queue = new BatchQueue(_system.Connections, _system.Jobs); queue.ClaimNext();
        ExecuteSql("CREATE TRIGGER reject_completion BEFORE INSERT ON sys_joblog WHEN new.message_type='COMPLETION' BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Throws<SqliteException>(() => _system.Jobs.Complete(job.Key));
        Assert.Equal(JobExecutionState.Running, _system.Jobs.GetRequired(job.Key).ExecutionState);
        Assert.Null(queue.Inspect(job.Key)!.FinishedAt);
        ExecuteSql("DROP TRIGGER reject_completion"); _system.Jobs.Complete(job.Key);
        ExecuteSql("CREATE TRIGGER reject_submit BEFORE INSERT ON sys_joblog WHEN new.message_id='CPC1228' BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Throws<SqliteException>(() => Submit("ROLLBACK"));
        Assert.DoesNotContain(_system.Jobs.List(), j => j.Key.Name == "ROLLBACK");
        ExecuteSql("DROP TRIGGER reject_submit");
        Assert.True(Submit("AFTER").Key.Number > job.Key.Number + 1); // Failed allocations are never recycled.
    }

    [Fact]
    public void Job_number_corruption_and_exhaustion_fail_closed()
    {
        Submit("FIRST"); ExecuteSql("UPDATE sys_meta SET value='invalid' WHERE key='last_job_number'");
        Assert.Throws<CpfException>(() => Submit("CORRUPT"));
        ExecuteSql("UPDATE sys_meta SET value='2147483647' WHERE key='last_job_number'");
        Assert.Throws<CpfException>(() => Submit("OVERFLOW"));
        Assert.Single(_system.Jobs.List());
    }

    private Job Submit(string name) => _system.Jobs.Submit(name, "", "QBATCH", profile: "QUSER", command: "DSPJOB");

    [Fact]
    public async Task Concurrent_completion_attempts_cannot_overwrite_the_winning_outcome()
    {
        var job = Submit("FINISH"); new BatchQueue(_system.Connections, _system.Jobs).ClaimNext();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            using var factory = new SqliteConnectionFactory(_directory);
            new JobService(factory).Complete(job.Key, index % 2 == 0 ? JobCompletion.Normal : JobCompletion.Abnormal, "winner " + index);
        })));
        var persisted = _system.Jobs.GetRequired(job.Key);
        var entry = Assert.Single(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        Assert.Equal(persisted.CompletionMessage, entry.Text);
        Assert.Equal(persisted.CompletionCode == JobCompletion.Normal ? JobExecutionState.Succeeded : JobExecutionState.Failed, persisted.ExecutionState);
    }
    private void ExecuteSql(string sql) { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private Process Start(string mode, int count)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, mode, _directory, count.ToString() }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!; _processes.Add(process); return process;
    }
    private async Task Release(Process[] processes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (processes.Any(p => !File.Exists(Path.Combine(_directory, $"ready-{p.Id}"))))
        {
            foreach (var process in processes) if (process.HasExited) Assert.Fail(await process.StandardError.ReadToEndAsync(timeout.Token));
            await Task.Delay(20, timeout.Token);
        }
        File.WriteAllText(Path.Combine(_directory, "start"), "start");
    }
    private static async Task<int[]> Result(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, await error);
        return JsonSerializer.Deserialize<int[]>(await output)!;
    }
    public void Dispose()
    {
        foreach (var process in _processes) { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } process.Dispose(); }
        _system.Dispose(); Directory.Delete(_directory, true);
    }
}
