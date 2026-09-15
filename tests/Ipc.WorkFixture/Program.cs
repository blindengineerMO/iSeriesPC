using System.Text.Json;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.WorkFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args[0]; var root = args[1]; var count = int.Parse(args[2]);
        using var factory = new SqliteConnectionFactory(root);
        var jobs = new JobService(factory); var queue = new BatchQueue(factory, jobs);
        File.WriteAllText(Path.Combine(root, $"ready-{Environment.ProcessId}"), "ready");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(Path.Combine(root, "start"))) await Task.Delay(10, stop.Token);
        if (mode is "lock-hold" or "lock-wait")
        {
            var job = jobs.CreateInteractive("QUSER"); var locks = new JobLockStore(factory);
            File.WriteAllText(Path.Combine(root, $"lock-job-{Environment.ProcessId}"), job.Key.Number.ToString());
            using (locks.Acquire(job.Key, new("QGPL", "DATA", "*FILE"), JobLockMode.Exclusive, TimeSpan.FromSeconds(15), stop.Token))
            {
                File.WriteAllText(Path.Combine(root, $"lock-acquired-{Environment.ProcessId}"), "acquired");
                if (mode == "lock-hold") while (!File.Exists(Path.Combine(root, "release-lock"))) await Task.Delay(10, stop.Token);
            }
            jobs.Complete(job.Key); return 0;
        }
        var numbers = new List<int>();
        for (var index = 0; index < count; index++)
        {
            if (mode == "allocate") numbers.Add(jobs.Submit("WORK" + index, "", "QUSRSYS/QBATCH", profile: "QUSER", command: "DSPJOB").Key.Number);
            else if (queue.ClaimNext() is { } claim)
            {
                numbers.Add(claim.Job.Key.Number);
                if (mode == "effect-crash")
                {
                    File.AppendAllText(Path.Combine(root, "effect"), claim.Job.Key.Number + "\n");
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
            }
            else break;
        }
        Console.Write(JsonSerializer.Serialize(numbers));
        return 0;
    }
}
