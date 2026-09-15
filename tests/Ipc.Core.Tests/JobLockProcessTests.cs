using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class JobLockProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-locks-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly List<Process> _children = new();
    private readonly JobLockResource _resource = new("QGPL", "DATA", ObjectType.File);
    public JobLockProcessTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start();
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DATA"), ObjectType = ObjectType.File });
        File.WriteAllText(Path.Combine(_directory, "start"), "start");
    }
    [Fact]
    public async Task Independent_process_waiter_observes_holder_and_acquires_after_release()
    {
        var holder = Start("lock-hold"); await Marker(holder, "lock-acquired");
        var waiter = Start("lock-wait"); await Marker(waiter, "lock-job");
        var locks = new JobLockStore(_system.Connections);
        await JobLockTests.WaitFor(() => locks.Inspect(_resource).Any(x => x.Waiting));
        Assert.False(File.Exists(Path.Combine(_directory, $"lock-acquired-{waiter.Id}")));
        File.WriteAllText(Path.Combine(_directory, "release-lock"), "release");
        await Exit(holder); await Exit(waiter); Assert.Empty(locks.Inspect(_resource));
        Assert.True(File.Exists(Path.Combine(_directory, $"lock-acquired-{waiter.Id}")));
    }
    [Fact]
    public async Task Killed_process_keeps_allocations_until_job_recovery_then_a_new_process_can_acquire()
    {
        var holder = Start("lock-hold"); await Marker(holder, "lock-acquired");
        holder.Kill(true); await holder.WaitForExitAsync();
        var locks = new JobLockStore(_system.Connections); Assert.NotEmpty(locks.Inspect(_resource));
        locks.RecoverEndedJobs(); Assert.NotEmpty(locks.Inspect(_resource)); // A process death alone is not evidence that its job ended.
        _system.Jobs.RecoverInterruptedInteractiveJobs(); locks.RecoverEndedJobs(); Assert.Empty(locks.Inspect(_resource));
        var waiter = Start("lock-wait"); await Exit(waiter);
        Assert.True(File.Exists(Path.Combine(_directory, $"lock-acquired-{waiter.Id}")));
        Assert.Empty(locks.Inspect(_resource));
    }
    private Process Start(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(WorkFixture.Program).Assembly.Location, mode, _directory, "1" }) start.ArgumentList.Add(arg);
        var child = Process.Start(start)!; _children.Add(child); return child;
    }
    private async Task Marker(Process child, string marker)
    {
        await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, $"{marker}-{child.Id}")) || child.HasExited);
        if (child.HasExited) Assert.Fail(await child.StandardError.ReadToEndAsync());
    }
    private static async Task Exit(Process child)
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
        _system.Dispose(); Directory.Delete(_directory, true);
    }
}
