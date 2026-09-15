using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;

namespace Ipc.Session;

/// <summary>Created once by as400server, using the same facade as terminal and API sessions.</summary>
internal sealed class BatchDispatcher(IpcSystem system)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var queue = new BatchQueue(system.Connections, system.Jobs);
        var workers = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var failed in workers.Where(w => w.IsFaulted)) await failed;
                workers.RemoveAll(w => w.IsCompletedSuccessfully);
                if (workers.Count < 64 && queue.ClaimNext() is { } next)
                {
                    workers.Add(Task.Run(() => Execute(next.Job, next.Command, cancellationToken), CancellationToken.None));
                    continue;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(workers); }
    }

    private void Execute(Job job, string command, CancellationToken cancellationToken)
    {
        using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
        using var execution = new ExecutionSession(system, job, cancellationToken);
        try
        {
            if (job.StartupError is { } startupError)
            {
                execution.End(JobCompletion.Abnormal, startupError);
                return;
            }
            if (job.JobClass is { } jobClass)
            {
                var key = Ipc.Core.Objects.QualifiedName.Parse(jobClass);
                new Ipc.Services.Security.ServiceAuthorization(system.Connections).RequireObject(key.Library, key.Name.Value,
                    Ipc.Core.Objects.ObjectType.Class, Ipc.Core.Objects.Authorities.UseBits);
            }
            system.Jobs.WriteLog(job, "INFO", "IPC0120", 0, $"Routing {job.RoutingProgram}; class {job.JobClass}; run priority {job.RunPriority}; time slice {job.TimeSliceMilliseconds}ms.");
            var routedCommand = job.RoutingProgram == "QSYS/QCMD" ? command :
                $"CALL PGM({job.RoutingProgram}) PARM('{command.Replace("'", "''")}')";
            var result = execution.Execute(routedCommand);
            system.Jobs.WriteLog(job, result.IsError ? "ERROR" : "INFO", null, result.IsError ? 40 : 0, result.Message);
            execution.End(result.IsError ? JobCompletion.Abnormal : JobCompletion.Normal,
                result.IsError ? result.Message ?? "Batch command failed." : "Batch command completed.");
        }
        catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
        { execution.End(JobCompletion.Abnormal, "Batch job cancelled by server shutdown; no automatic retry.", JobExecutionState.Cancelled); }
        catch (Exception ex)
        { execution.End(JobCompletion.Abnormal, $"Batch execution failed ({ex.GetType().Name})."); }
    }
}
