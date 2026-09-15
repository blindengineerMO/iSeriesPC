using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class JobEnvironmentPersistenceTests
{
    [Fact]
    public void Queued_environment_survives_restart_and_running_environment_is_removed_by_recovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-environment-" + Guid.NewGuid().ToString("N"));
        JobKey parentKey, childKey;
        try
        {
            using (var first = IpcSystem.Create(directory))
            {
                first.Start(); var parent = first.Jobs.CreateInteractive("QUSER"); parentKey = parent.Key;
                new JobDataAreaStore(first.Connections).Write(parent.Key, JobDataArea.Local, 1023, new byte[] { 19 });
                childKey = first.Jobs.Submit("RESTART", "", "QBATCH", profile: "QUSER", command: "DSPJOB", submittingJob: parent.Key, initializationParameters: new byte[] { 7 }).Key;
            }
            using var second = IpcSystem.Create(directory); second.Start(); second.Jobs.RecoverInterruptedInteractiveJobs();
            var areas = new JobDataAreaStore(second.Connections);
            Assert.Throws<CpfException>(() => areas.Read(parentKey, JobDataArea.Local));
            var child = new BatchQueue(second.Connections, second.Jobs).ClaimNext()!.Value.Job; Assert.Equal(childKey, child.Key);
            Assert.Equal(19, areas.Read(child.Key, JobDataArea.Local)[1023]); Assert.Equal(7, areas.Read(child.Key, JobDataArea.Initialization)[0]);
            second.Jobs.Complete(child.Key);
        }
        finally { Directory.Delete(directory, true); }
    }
}
