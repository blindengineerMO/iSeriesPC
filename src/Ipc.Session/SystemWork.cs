using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Session;

/// <summary>Entry point for host adapters and scheduled system work using the shared job lifecycle.</summary>
public static class SystemWork
{
    public static async Task<Job> RunAsync(IpcSystem system, string name, string command, CancellationToken cancellationToken = default)
    {
        var job = system.Jobs.CreateSystem(name);
        using var execution = new ExecutionSession(system, job, cancellationToken);
        try
        {
            var result = await Task.Run(() => execution.Execute(command), CancellationToken.None);
            using var identity = OperationIdentity.Enter(job.UserProfile!, job.Key);
            system.Jobs.WriteLog(job, result.IsError ? "ERROR" : "INFO", null, result.IsError ? 40 : 0, result.Message);
            execution.End(result.IsError ? JobCompletion.Abnormal : JobCompletion.Normal, result.Message ?? "System work completed.");
        }
        catch (OperationCanceledException)
        { execution.End(JobCompletion.Abnormal, "System work cancelled.", JobExecutionState.Cancelled); throw; }
        catch
        { execution.End(JobCompletion.Abnormal, "System work failed."); throw; }
        return system.Jobs.GetRequired(job.Key);
    }
}
