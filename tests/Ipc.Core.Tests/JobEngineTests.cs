using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;

namespace Ipc.Core.Tests.Work;

public class JobEngineTests : IDisposable
{
    private readonly IpcSystem _system;

    public JobEngineTests()
    {
        _system = IpcSystem.Create(":memory:");
        _system.Start();
    }

    public void Dispose() => _system.Dispose();

    [Fact]
    public void Interactive_job_starts_active()
    {
        var job = _system.Jobs.CreateInteractive(ProfileNames.QUser);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(JobType.Interactive, job.Type);
        Assert.Equal(JobKeys.InteractiveSubsystem, job.Subsystem);
        Assert.True(_system.Jobs.GetLog(job.Key).Count >= 1);
    }

    [Fact]
    public void Submit_places_job_on_queue()
    {
        var job = _system.Jobs.Submit("PAYROLL", "Nightly payroll", JobKeys.BatchSubsystem, priority: 5);
        Assert.Equal(JobStatus.JobQueue, job.Status);
        Assert.Equal(5, job.Priority);
        Assert.Single(_system.Jobs.List(JobStatus.JobQueue));
    }

    [Fact]
    public void Start_next_runs_queued_job_in_batch_subsystem()
    {
        var job = _system.Jobs.Submit("INVENTORY", "Stock check", JobKeys.BatchSubsystem, priority: 9);

        var started = _system.Jobs.StartNext(JobKeys.BatchSubsystem);

        Assert.Equal(1, started);
        var active = _system.Jobs.GetRequired(job.Key);
        Assert.Equal(JobStatus.Active, active.Status);
        Assert.Equal(JobKeys.BatchSubsystem, active.Subsystem);
        Assert.NotNull(active.StartedAt);
    }

    [Fact]
    public void Higher_priority_starts_first()
    {
        _system.Subsystems.Ensure("PRIOTEST", "priority test", 1);
        _system.Subsystems.Start("PRIOTEST");
        _system.Jobs.Submit("LOWPRIO", "low", "PRIOTEST", priority: 9);
        var high = _system.Jobs.Submit("HIGHPRIO", "high", "PRIOTEST", priority: 1);

        var started = _system.Jobs.StartNext("PRIOTEST");
        Assert.Equal(1, started);

        var running = _system.Jobs.List(JobStatus.Active, "PRIOTEST");
        var key = Assert.Single(running);
        Assert.Equal(high.Key, key.Key);
    }

    [Fact]
    public void Max_active_jobs_are_not_exceeded()
    {
        _system.Subsystems.Ensure("QBATCHMAX", "batch with max 1", 1);
        _system.Subsystems.Start("QBATCHMAX");

        _system.Jobs.Submit("JOB1", "one", "QBATCHMAX", priority: 5);
        _system.Jobs.Submit("JOB2", "two", "QBATCHMAX", priority: 5);

        var started = _system.Jobs.StartNext("QBATCHMAX");
        Assert.Equal(1, started);

        var activeJobs = _system.Jobs.List(JobStatus.Active, "QBATCHMAX");
        Assert.Single(activeJobs);

        var elected = _system.Jobs.StartNext("QBATCHMAX");
        Assert.Equal(0, elected);

        var remaining = _system.Jobs.List(JobStatus.JobQueue, "QBATCHMAX");
        Assert.Single(remaining);
    }

    [Fact]
    public void Hold_and_release_preserves_queue_position()
    {
        var jobs = _system.Jobs;
        var first = jobs.Submit("FIRST", "first", JobKeys.BatchSubsystem, priority: 5);
        var second = jobs.Submit("SECOND", "second", JobKeys.BatchSubsystem, priority: 5);

        jobs.Hold(second.Key);
        Assert.Equal(JobStatus.Held, jobs.GetRequired(second.Key).Status);
        Assert.Equal(2, jobs.List().Count(j => j.Status == JobStatus.Held || j.Status == JobStatus.JobQueue));

        jobs.Release(second.Key);
        Assert.Equal(JobStatus.JobQueue, jobs.GetRequired(second.Key).Status);
    }

    [Fact]
    public void Complete_marks_job_and_writes_log()
    {
        var job = _system.Jobs.Submit("DONE", "finish", JobKeys.BatchSubsystem);
        _system.Jobs.StartNext(JobKeys.BatchSubsystem);
        _system.Jobs.Complete(job.Key, JobCompletion.Normal, "All done.");

        var completed = _system.Jobs.GetRequired(job.Key);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(JobCompletion.Normal, completed.CompletionCode);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("All done.", completed.CompletionMessage);

        var log = _system.Jobs.GetLog(job.Key);
        Assert.Contains(log, e => e.MessageType == "COMPLETION");
        Assert.Contains(log, e => e.MessageId == "CPC1124");
    }

    [Fact]
    public void Message_wait_and_reply_cycle()
    {
        var job = _system.Jobs.Submit("WAIT", "waiting", JobKeys.BatchSubsystem);
        _system.Jobs.StartNext(JobKeys.BatchSubsystem);

        _system.Jobs.MessageWait(job.Key, "System message?");
        Assert.Equal(JobStatus.MessageWait, _system.Jobs.GetRequired(job.Key).Status);

        _system.Jobs.ReplyToMessage(job.Key);
        Assert.Equal(JobStatus.Active, _system.Jobs.GetRequired(job.Key).Status);
    }

    [Fact]
    public void Subsystem_status_reports_counts()
    {
        _system.Jobs.Submit("JOBA", "a", JobKeys.BatchSubsystem);
        _system.Jobs.StartNext(JobKeys.BatchSubsystem);

        var statuses = _system.Subsystems.StatusAll();
        var batch = statuses.Single(s => s.Name == JobKeys.BatchSubsystem);
        Assert.True(batch.Active);
        Assert.Equal(1, batch.ActiveJobs);
        Assert.True(batch.MaxActiveJobs >= 1);
    }
}