using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobLifecycleTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public JobLifecycleTests()
    {
        _system.Start();
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "SPIN"), ObjectType = ObjectType.Program, Attribute = "CLP",
            Source = "PGM\nAGAIN:\nGOTO AGAIN\nENDPGM" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_end_and_subsystem_end_cancel_live_execution_with_cpu_and_thread_accounting(bool subsystem)
    {
        var job = _system.Jobs.Submit("SPINNER", "", "QBATCH", profile: "QUSER", command: "CALL PGM(QGPL/SPIN)");
        job = new BatchQueue(_system.Connections, _system.Jobs).ClaimNext()!.Value.Job;
        using var execution = new ExecutionSession(_system, job, CancellationToken.None);
        var running = Task.Run(() => execution.Execute("CALL PGM(QGPL/SPIN)"));
        var info = new JobInformationStore(_system.Connections);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (info.Accounting(job.Key).CpuNanoseconds == 0 || !info.ActivationGroups(job.Key).Any(g => g.ActiveCalls > 0)) await Task.Delay(20, timeout.Token);
            Assert.Equal(1, info.Accounting(job.Key).ActiveThreads);
            Assert.Contains(info.Threads(job.Key), t => t.State == "Executing" && t.NativeThread > 0 && t.CpuNanoseconds > 0);
            var commands = new CommandService(_system);
            Assert.Contains(commands.Execute("WRKACTJOB SBS(QBATCH)").Listing!, row => row.Contains("SPINNER"));
            var result = commands.Execute(subsystem ? "ENDSBS SBS(QSYS/QBATCH)" : $"ENDJOB JOB({job.Key})");
            Assert.False(result.IsError, result.Message);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(timeout.Token));
            execution.End(JobCompletion.Abnormal, "Cancelled.");
            Assert.Equal(JobExecutionState.Cancelled, _system.Jobs.GetRequired(job.Key).ExecutionState);
            Assert.Equal(0, info.Accounting(job.Key).ActiveThreads);
            Assert.True(info.Accounting(job.Key).ElapsedMilliseconds > 0);
            Assert.Equal(1, info.Accounting(job.Key).Commands);
        }
        finally
        {
            _system.Jobs.RequestEnd(job.Key);
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Terminal_disconnect_cancels_a_running_interactive_command_before_job_cleanup()
    {
        using var menu = new MenuController(_system, _system.Security.Profiles.Get("QUSER"));
        foreach (var c in "CALL PGM(QGPL/SPIN)") menu.Handle(new(Ipc.Terminal.AidKey.None, Character: c));
        var running = Task.Run(() => menu.Handle(new(Ipc.Terminal.AidKey.Enter)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var info = new JobInformationStore(_system.Connections);
        try
        {
            while (!info.ActivationGroups(menu.JobKey).Any(g => g.ActiveCalls > 0)) await Task.Delay(10, timeout.Token);
            menu.RequestDisconnect();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(timeout.Token));
            menu.Dispose();
            var job = _system.Jobs.GetRequired(menu.JobKey);
            Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
            Assert.Equal(JobExecutionState.Cancelled, job.ExecutionState);
            Assert.Empty(info.ActivationGroups(menu.JobKey));
        }
        finally
        {
            menu.RequestDisconnect();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void Cancelling_queued_work_prevents_claiming_and_cross_job_controls_require_authority()
    {
        var job = _system.Jobs.Submit("QUEUED", "", "QBATCH", profile: "QUSER", command: "DSPJOB");
        _system.Security.Profiles.Create(new UserProfile { Name = "OTHER" });
        using (OperationIdentity.Enter("OTHER")) Assert.Throws<CpfException>(() => _system.Jobs.RequestEnd(job.Key));
        using (OperationIdentity.Enter("QUSER")) _system.Jobs.RequestEnd(job.Key);
        Assert.Equal(JobExecutionState.Cancelled, _system.Jobs.GetRequired(job.Key).ExecutionState);
        Assert.Null(new BatchQueue(_system.Connections, _system.Jobs).ClaimNext());
        Assert.Single(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
    }

    [Fact]
    public void Queue_bindings_are_typed_authorized_and_follow_object_rename()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var info = new JobInformationStore(_system.Connections);
        Assert.Equal(new JobQueueBindings("QUSRSYS/QPRINT", "QSYS/QSYSOPR"), info.Bindings(job.Key));
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "OUTPUT"), ObjectType = ObjectType.OutputQueue });
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "MESSAGES"), ObjectType = ObjectType.MessageQueue });
        var command = new CommandService(_system, job);
        var result = command.Execute("CHGJOB OUTQ(QGPL/OUTPUT) MSGQ(QGPL/MESSAGES)"); Assert.False(result.IsError, result.Message);
        _system.ObjectOperations.Relocate(new("QGPL", "OUTPUT"), ObjectType.OutputQueue, new("QGPL", "RENAMED"));
        Assert.Equal("QGPL/RENAMED", info.Bindings(job.Key).OutputQueue);
        Assert.ThrowsAny<Exception>(() => _system.ObjectOperations.Delete(new("QGPL", "RENAMED"), ObjectType.OutputQueue));
        Assert.Equal("QGPL/MESSAGES", info.Bindings(job.Key).MessageQueue);
        _system.Jobs.Complete(job.Key);
        _system.ObjectOperations.Delete(new("QGPL", "RENAMED"), ObjectType.OutputQueue);
        Assert.Equal("QGPL/RENAMED", info.Bindings(job.Key).OutputQueue);
    }

    [Fact]
    public async Task System_work_uses_its_own_job_and_records_success_failure_and_cleanup()
    {
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QSYSOPR", Authorities.ChangeBits);
        var job = await SystemWork.RunAsync(_system, "SYSTEMTEST", "CRTSRCPF FILE(QGPL/SYSEFFECT)");
        Assert.Equal(JobType.System, job.Type); Assert.Equal("QSYS", job.Subsystem); Assert.Equal(JobExecutionState.Succeeded, job.ExecutionState);
        Assert.True(_system.Objects.Exists("QGPL", "SYSEFFECT", ObjectType.File));
        Assert.Equal(0, new JobInformationStore(_system.Connections).Accounting(job.Key).ActiveThreads);
        job = await SystemWork.RunAsync(_system, "SYSFAIL", "CALL PGM(QGPL/MISSING)");
        Assert.Equal(JobExecutionState.Failed, job.ExecutionState);
        Assert.Empty(new JobInformationStore(_system.Connections).ActivationGroups(job.Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancellation_and_completion_respect_the_order_of_durable_requests(bool cancelFirst)
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        if (cancelFirst) _system.Jobs.RequestEnd(job.Key);
        _system.Jobs.Complete(job.Key);
        if (!cancelFirst) _system.Jobs.RequestEnd(job.Key);
        Assert.Equal(cancelFirst ? JobExecutionState.Cancelled : JobExecutionState.Succeeded, _system.Jobs.GetRequired(job.Key).ExecutionState);
        Assert.Single(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
    }

    public void Dispose() => _system.Dispose();
}
