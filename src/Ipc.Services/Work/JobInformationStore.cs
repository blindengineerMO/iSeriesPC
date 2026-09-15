using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record JobQueueBindings(string OutputQueue, string MessageQueue);
public sealed record JobThreadInfo(int ManagedThread, int NativeThread, string State, long CpuNanoseconds, long ElapsedMilliseconds);
public sealed record JobAccounting(long CpuNanoseconds, long ElapsedMilliseconds, int ActiveThreads, long Commands, long NativeCpuNanoseconds = 0, int ActiveProcesses = 0);
public sealed record ActivationGroupInfo(string Name, int ActiveCalls);
public sealed record JobProcessInfo(int ProcessId, string State, long CpuNanoseconds, int PeakThreads, int? ExitCode);

public sealed class JobInformationStore(SqliteConnectionFactory factory)
{
    public IReadOnlyList<JobProcessInfo> Processes(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_id,state,cpu_nanoseconds,peak_threads,exit_code FROM sys_job_processes WHERE job_number=$job ORDER BY started_at";
        command.Parameters.AddWithValue("$job", job.Number); using var reader = command.ExecuteReader(); var rows = new List<JobProcessInfo>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        return rows;
    }
    public IReadOnlyList<ActivationGroupInfo> ActivationGroups(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,active_calls FROM sys_activation_groups WHERE job_number=$job ORDER BY name";
        command.Parameters.AddWithValue("$job", job.Number); using var reader = command.ExecuteReader(); var rows = new List<ActivationGroupInfo>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt32(1)));
        return rows;
    }
    public JobAccounting Accounting(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT cpu_nanoseconds,elapsed_milliseconds,active_threads,commands,(SELECT coalesce(sum(cpu_nanoseconds),0) FROM sys_job_processes WHERE job_number=$job),(SELECT count(*) FROM sys_job_processes WHERE job_number=$job AND state='Running') FROM sys_job_accounting WHERE job_number=$job";
        command.Parameters.AddWithValue("$job", job.Number); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5)) : throw new CpfException("CPF1241", "Job not found.");
    }
    public IReadOnlyList<JobThreadInfo> Threads(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT managed_thread,native_thread,state,cpu_nanoseconds,elapsed_milliseconds FROM sys_job_threads WHERE job_number=$job ORDER BY managed_thread";
        command.Parameters.AddWithValue("$job", job.Number); using var reader = command.ExecuteReader(); var rows = new List<JobThreadInfo>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rows;
    }
    public JobQueueBindings Bindings(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(output_lib||'/'||output_name,output_snapshot),coalesce(message_lib||'/'||message_name,message_snapshot) FROM sys_job_bindings WHERE job_number=$job";
        command.Parameters.AddWithValue("$job", job.Number); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : throw new CpfException("CPF1241", "Job not found.");
    }
    public void SetBindings(JobKey job, string? outputQueue, string? messageQueue)
    {
        var authorization = new ServiceAuthorization(factory); authorization.RequireJob(job);
        var current = Bindings(job);
        var output = QualifiedName.Parse((outputQueue ?? current.OutputQueue).ToUpperInvariant());
        var message = QualifiedName.Parse((messageQueue ?? current.MessageQueue).ToUpperInvariant());
        authorization.RequireObject(output.Library, output.Name.Value, ObjectType.OutputQueue, Authorities.UseBits);
        authorization.RequireObject(message.Library, message.Name.Value, ObjectType.MessageQueue, Authorities.UseBits);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sys_job_bindings SET output_lib=$olib,output_name=$oname,message_lib=$mlib,message_name=$mname
            WHERE job_number=$job AND EXISTS(SELECT 1 FROM sys_jobs j WHERE j.number=$job AND j.execution_state IN ('Queued','Running'))
            """;
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$olib", output.Library);
        command.Parameters.AddWithValue("$oname", output.Name.Value); command.Parameters.AddWithValue("$mlib", message.Library);
        command.Parameters.AddWithValue("$mname", message.Name.Value);
        if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF1241", "Job not found or already ended.");
    }
}
