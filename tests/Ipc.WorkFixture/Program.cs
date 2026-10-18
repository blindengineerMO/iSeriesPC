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
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "record-publish" ? 60 : 30));
        while (!File.Exists(Path.Combine(root, "start"))) await Task.Delay(10, stop.Token);
        if (mode is "record-stage" or "record-publish")
        {
            var job = new Ipc.Core.Work.JobKey(int.Parse(args[3]), args[4], args[5]);
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.User, job);
            factory.RecordTransactions.Begin(job); factory.RecordTransactions.Enlist(job, new("QGPL", "FIRST"));
            using (var statement = factory.RecordTransactions.Open(job))
            { statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='interrupted pending'"); statement.Complete(); }
            File.WriteAllText(Path.Combine(root, "staged-" + Environment.ProcessId), "staged");
            if (mode == "record-publish")
            {
                while (!File.Exists(Path.Combine(root, "publish"))) await Task.Delay(10, stop.Token);
                factory.RecordTransactions.Commit(job, stop.Token); Console.Write("committed"); return 0;
            }
            await Task.Delay(Timeout.Infinite, stop.Token); return 0;
        }
        if (mode == "journal-recover")
        {
            var result = new JournalStore(factory).Recover(new("QGPL", "RECOVERY"), [new(new("QGPL", "RECORDS"), "*ALL")], count, 1, remove: true, cancellationToken: stop.Token);
            Console.Write(result.Changes); return 0;
        }
        if (mode == "journal-append")
        {
            var journals = new JournalStore(factory);
            var sequences = new List<long>();
            for (var index = 0; index < count; index++)
                sequences.Add(journals.Send(new("QGPL", "TESTJRN"), "AA", BitConverter.GetBytes(Environment.ProcessId), stop.Token));
            Console.Write(JsonSerializer.Serialize(sequences)); return 0;
        }
        if (mode == "system-value-pairs")
        {
            for (var index = 0; index < count; index++)
            {
                using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE sys_sysvals SET value=CASE name WHEN 'QCCSID' THEN $ccsid ELSE $level END WHERE name IN ('QCCSID','QPWDLVL')";
                update.Parameters.AddWithValue("$ccsid", index % 2 == 0 ? "1208" : "37");
                update.Parameters.AddWithValue("$level", index % 2 == 0 ? "4" : "3");
                update.ExecuteNonQuery(); transaction.Commit(); await Task.Delay(1, stop.Token);
            }
            Console.Write(count); return 0;
        }
        if (mode is "temp-message-send" or "temp-message-consume" or "temp-message-reply")
        {
            var job = new Ipc.Core.Work.JobKey(int.Parse(args[3]), args[4], args[5]); var tag = int.Parse(args[6]);
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.User, job);
            var messages = new Ipc.Services.Messages.MessageQueueStore(factory); var values = new List<long>();
            for (var index = 0; index < count; index++)
            {
                if (mode == "temp-message-send")
                {
                    var value = ((long)tag << 32) | (uint)index;
                    messages.Send([new("QTEMP", "PROCESS")], new(BitConverter.GetBytes(value), 37), cancellationToken: stop.Token); values.Add(value);
                }
                else if (mode == "temp-message-consume")
                    values.Add(BitConverter.ToInt64(messages.Receive(new("QTEMP", "PROCESS"), wait: TimeSpan.FromSeconds(15), cancellationToken: stop.Token)!.Data.ToArray()));
                else
                {
                    var inbox = new Ipc.Core.Objects.QualifiedName("QGPL", "OPERATOR"); var inquiry = messages.List(inbox).First();
                    messages.Reply(inbox, inquiry.Key, inquiry.Data, cancellationToken: stop.Token); values.Add(BitConverter.ToInt64(inquiry.Data.ToArray()));
                }
            }
            Console.Write(JsonSerializer.Serialize(values)); return 0;
        }
        if (mode == "temp-file-append")
        {
            var job = new Ipc.Core.Work.JobKey(int.Parse(args[3]), args[4], args[5]); var offset = int.Parse(args[6]);
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.User, job);
            var files = new Ipc.Db.Store.SqliteFileStore(factory, new SqliteObjectStore(factory), job.User);
            for (var index = 0; index < count; index++) files.Insert("QTEMP", "ROWS", "ROWS", "ROW",
                new Dictionary<string, object?> { ["ID"] = offset + index, ["AMOUNT"] = 0m, ["TEXT"] = "process" });
            Console.Write(JsonSerializer.Serialize(files.RowCount("QTEMP", "ROWS", "ROWS"))); return 0;
        }
        if (mode == "temp-space-write")
        {
            var job = new Ipc.Core.Work.JobKey(int.Parse(args[3]), args[4], args[5]);
            var offset = int.Parse(args[6]); var spaces = new UserSpaceStore(factory);
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.User, job);
            for (var index = 1; index <= count; index++)
                spaces.Write(new("QTEMP", "PROCESS"), offset + 1, Enumerable.Repeat((byte)index, 16).ToArray(), stop.Token);
            Console.Write(JsonSerializer.Serialize(spaces.Read(new("QTEMP", "PROCESS"), offset + 1, 16))); return 0;
        }
        if (mode == "job-area-write")
        {
            var job = new Ipc.Core.Work.JobKey(int.Parse(args[3]), args[4], args[5]);
            var offset = int.Parse(args[6]); var areas = new JobDataAreaStore(factory);
            using var identity = Ipc.Services.Events.OperationIdentity.Enter(job.User, job);
            for (var index = 1; index <= count; index++)
                areas.Write(job, JobDataArea.Group, offset, Enumerable.Repeat((byte)index, 16).ToArray());
            Console.Write(JsonSerializer.Serialize(areas.Read(job, JobDataArea.Group).Skip(offset).Take(16).ToArray())); return 0;
        }
        if (mode is "data-produce" or "data-consume")
        {
            var dataQueues = new Ipc.Services.Messages.DataQueueStore(factory);
            var received = new List<long>();
            for (var index = 0; index < count; index++)
            {
                if (mode == "data-produce")
                {
                    var value = ((long)Environment.ProcessId << 32) | (uint)index;
                    dataQueues.Send(new("QGPL", "DATA"), new(BitConverter.GetBytes(value), 37), cancellationToken: stop.Token);
                    received.Add(value);
                }
                else received.Add(BitConverter.ToInt64(dataQueues.Receive(new("QGPL", "DATA"), wait: TimeSpan.FromSeconds(15), cancellationToken: stop.Token)!.Data.ToArray()));
            }
            Console.Write(JsonSerializer.Serialize(received)); return 0;
        }
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
