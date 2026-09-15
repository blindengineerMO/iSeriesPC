using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class SchedulerDefinitionTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly JobQueueStore _queues;
    private readonly BatchQueue _batch;
    public SchedulerDefinitionTests()
    {
        _system.Start(); _queues = new(_system.Connections); _batch = new(_system.Connections, _system.Jobs);
        _system.Subsystems.Ensure("MULTI", "two queues", 2);
        _queues.Ensure("QGPL/FIRST"); _queues.Ensure("QGPL/SECOND");
        _queues.Attach("QGPL/FIRST", "MULTI", 10, 1); _queues.Attach("QGPL/SECOND", "MULTI", 20, 1);
        _system.Subsystems.Start("MULTI");
    }

    private Job Submit(string name, string queue = "FIRST", int priority = 9, string? routing = null) =>
        _system.Jobs.Submit(name, "", "QGPL/" + queue, priority, profile: "QUSER", routingData: routing, command: "DSPJOB");

    [Fact]
    public void Queue_sequence_priority_fifo_and_both_capacity_limits_control_admission()
    {
        var later = Submit("LATER", "SECOND", 0); var low = Submit("LOW"); var high = Submit("HIGH", priority: 1);
        var fifo = Submit("FIFO", priority: 1);
        Assert.Equal(high.Key, _batch.ClaimNext()!.Value.Job.Key);
        Assert.Equal(later.Key, _batch.ClaimNext()!.Value.Job.Key);
        Assert.Null(_batch.ClaimNext());
        _system.Jobs.Complete(high.Key);
        Assert.Equal(fifo.Key, _batch.ClaimNext()!.Value.Job.Key);
        _system.Jobs.Complete(fifo.Key);
        Assert.Equal(low.Key, _batch.ClaimNext()!.Value.Job.Key);
        Assert.Equal(2, _system.Subsystems.StatusAll().Single(s => s.Name == "MULTI").ActiveJobs);
    }

    [Fact]
    public void Held_queues_zero_capacity_and_single_owner_are_enforced()
    {
        var first = Submit("FIRST"); var second = Submit("SECOND", "SECOND");
        _queues.SetHeld("QGPL/FIRST", true); _queues.Attach("QGPL/SECOND", "MULTI", 20, 0, replace: true);
        Assert.Null(_batch.ClaimNext());
        Assert.Throws<CpfException>(() => _queues.Attach("QGPL/FIRST", "QBATCH", 1));
        _queues.SetHeld("QGPL/FIRST", false);
        Assert.Equal(first.Key, _batch.ClaimNext()!.Value.Job.Key);
        Assert.Throws<CpfException>(() => _queues.Detach("QGPL/FIRST", "MULTI"));
        _system.Jobs.Complete(first.Key); _queues.Detach("QGPL/SECOND", "MULTI");
        _queues.Attach("QGPL/SECOND", "QBATCH", 1);
        Assert.Equal(second.Key, _batch.ClaimNext()!.Value.Job.Key);
        Assert.Equal("QBATCH", _system.Jobs.GetRequired(second.Key).Subsystem);
    }

    [Fact]
    public void Routing_selects_first_matching_section_and_snapshots_class_attributes()
    {
        _system.WorkDefinitions.PutClass(new("QGPL", "QUICK", 15, 25));
        var routing = new RoutingTable(_system.Connections);
        routing.EnsureEntry("MULTI", 10, "PAY", "QSYS/QCMD", compareMode: "*SECTION", startPosition: 3, jobClass: "QGPL/QUICK");
        routing.EnsureEntry("MULTI", 20, "XXPAYDATA", "QSYS/QCMD");
        Submit("MATCH", routing: "XXPAYDATA");
        var job = _batch.ClaimNext()!.Value.Job;
        Assert.Equal("QGPL/QUICK", job.JobClass); Assert.Equal(15, job.RunPriority); Assert.Equal(25, job.TimeSliceMilliseconds);
        _system.WorkDefinitions.PutClass(new("QGPL", "QUICK", 90, 9000), replace: true);
        Assert.Equal(15, _system.Jobs.GetRequired(job.Key).RunPriority);
        Assert.Equal("QSYS/QCMD", routing.Route("MULTI", "other"));
        routing.RemoveEntry("MULTI", 9999);
        Assert.Null(routing.Route("MULTI", "other"));
        _system.Jobs.Complete(job.Key); Submit("UNMATCHED", routing: "other");
        Assert.Contains("No matching", _batch.ClaimNext()!.Value.Job.StartupError);
    }

    [Fact]
    public void SBMJOB_uses_JOBD_and_explicit_overrides_without_changing_the_submitter()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "SUBMITTER", SpecialAuthorities = SpecialAuthority.AllObject });
        _system.WorkDefinitions.PutJobDescription(new("QGPL", "PAYROLL", "QGPL/FIRST", 2, "QUSER", "PAY",
            "QUSRSYS", "EXPLICIT", new[] { "QUSRSYS", "QGPL" }));
        var submitting = _system.Jobs.CreateInteractive("SUBMITTER", libraryList: "QGPL");
        var commands = new CommandService(_system, submitting);
        var result = commands.Execute("SBMJOB CMD(DSPJOB) JOB(FROMJOBD) JOBD(QGPL/PAYROLL)"); Assert.False(result.IsError, result.Message);
        var job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "FROMJOBD");
        Assert.Equal("QGPL/PAYROLL", job.JobDescription); Assert.Equal("QGPL/FIRST", job.JobQueue);
        Assert.Equal("QUSER", job.UserProfile); Assert.Equal(2, job.Priority); Assert.Equal("PAY", job.RoutingData);
        Assert.Equal("QUSRSYS", job.CurrentLibrary); Assert.Equal("QUSRSYS QGPL", job.LibraryList);
        result = commands.Execute("SBMJOB CMD(DSPJOB) JOB(OVERRIDE) JOBD(QGPL/PAYROLL) JOBQ(QGPL/SECOND) JOBPTY(7) USER(*CURRENT) RTGDTA(OTHER) INLLIBL(*CURRENT)");
        Assert.False(result.IsError, result.Message); job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "OVERRIDE");
        Assert.Equal("QGPL/SECOND", job.JobQueue); Assert.Equal(7, job.Priority); Assert.Equal("SUBMITTER", job.UserProfile);
        Assert.Equal("OTHER", job.RoutingData); Assert.Equal("QGPL", job.LibraryList);
        Assert.Equal("QGPL", _system.Jobs.GetRequired(submitting.Key).CurrentLibrary);
    }

    [Fact]
    public void Work_commands_preserve_unspecified_attributes_and_reject_missing_dependencies()
    {
        var commands = new CommandService(_system);
        Assert.False(commands.Execute("CHGJOBQE SBSD(QSYS/MULTI) JOBQ(QGPL/FIRST) MAXACT(2)").IsError);
        var entry = Assert.Single(_queues.Entries("MULTI"), e => e.Queue == "QGPL/FIRST");
        Assert.Equal(10, entry.Sequence); Assert.Equal(2, entry.MaximumActive);
        Assert.True(commands.Execute("CHGJOBQE SBSD(QSYS/QBATCH) JOBQ(QGPL/FIRST) MAXACT(1)").IsError);
        Assert.False(commands.Execute("CRTCLS CLS(QGPL/TESTCLS) RUNPTY(30) TIMESLICE(500)").IsError);
        Assert.False(commands.Execute("CHGCLS CLS(QGPL/TESTCLS) RUNPTY(40)").IsError);
        Assert.Equal(500, _system.WorkDefinitions.Class("QGPL", "TESTCLS")!.TimeSliceMilliseconds);
        Assert.True(commands.Execute("CRTJOBD JOBD(QGPL/BAD) JOBQ(QGPL/MISSING)").IsError);
        Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.JobDescription));
    }

    [Fact]
    public void Job_description_and_class_payloads_copy_rename_and_protect_dependencies()
    {
        _system.Libraries.CreateLibrary("JOBLIB");
        _system.WorkDefinitions.PutJobDescription(new("QGPL", "CUSTOM", "QGPL/FIRST", CurrentLibrary: "JOBLIB",
            LibraryMode: "EXPLICIT", Libraries: new[] { "JOBLIB", "QGPL" }));
        _system.ObjectOperations.Relocate(new("QGPL", "CUSTOM"), ObjectType.JobDescription, new("QGPL", "COPY"), copy: true);
        Assert.Equal(new[] { "JOBLIB", "QGPL" }, _system.WorkDefinitions.JobDescription("QGPL", "COPY")!.Libraries);
        _system.ObjectOperations.Relocate(new("QSYS", "JOBLIB"), ObjectType.Library, new("QSYS", "RENAMED"));
        Assert.Equal("RENAMED", _system.WorkDefinitions.JobDescription("QGPL", "COPY")!.CurrentLibrary);
        Assert.Equal(new[] { "RENAMED", "QGPL" }, _system.WorkDefinitions.JobDescription("QGPL", "CUSTOM")!.Libraries);
        Assert.ThrowsAny<Exception>(() => _system.ObjectOperations.Delete(new("QSYS", "RENAMED"), ObjectType.Library));
        _system.WorkDefinitions.PutClass(new("QGPL", "CLASS", 30, 300));
        _system.ObjectOperations.Relocate(new("QGPL", "CLASS"), ObjectType.Class, new("QGPL", "CLASSCOPY"), copy: true);
        Assert.Equal(300, _system.WorkDefinitions.Class("QGPL", "CLASSCOPY")!.TimeSliceMilliseconds);
        new RoutingTable(_system.Connections).EnsureEntry("MULTI", 1, "TEST", "QSYS/QCMD", jobClass: "QGPL/CLASS");
        _system.ObjectOperations.Relocate(new("QGPL", "CLASS"), ObjectType.Class, new("QGPL", "NEWCLASS"));
        Submit("RENAMEDCLS", routing: "TEST"); Assert.Equal("QGPL/NEWCLASS", _batch.ClaimNext()!.Value.Job.JobClass);
        Assert.Throws<CpfException>(() => _system.ObjectOperations.Delete(new("QGPL", "NEWCLASS"), ObjectType.Class));
    }

    public void Dispose() => _system.Dispose();
}
