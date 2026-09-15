using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record BatchExecutionInfo(JobKey Job, JobExecutionState State, string Command, int Attempts, string? HostId,
    string? ClaimToken, string? ClaimedAt, string? FinishedAt);

public sealed class BatchQueue(SqliteConnectionFactory factory, JobService jobs)
{
    private readonly string _hostId = Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");
    public (Job Job, string Command)? ClaimNext() => ClaimCore(null, false);
    internal (Job Job, string Command)? ClaimLegacy(string subsystem) => ClaimCore(subsystem, true);
    private (Job Job, string Command)? ClaimCore(string? subsystemFilter, bool legacy)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        JobKey key;
        string command;
        string subsystem;
        string? program, jobClass, startupError;
        int runPriority, timeSlice;
        using (var connection = factory.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT j.number,j.name,j.user,x.command,
                  CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END,
                  r.program,c.library||'/'||c.name,coalesce(c.run_priority,50),coalesce(c.time_slice_ms,2000),
                  CASE WHEN r.program IS NULL THEN 'No matching routing entry.' WHEN c.name IS NULL THEN 'Routing class is missing.' END
                FROM sys_jobs j
                LEFT JOIN sys_job_execution x ON x.job_number=j.number
                JOIN sys_jobqs q ON j.jobq=q.library||'/'||q.name
                JOIN sys_jobq_entries e ON e.queue_lib=q.library AND e.queue_name=q.name
                JOIN sys_subsystems s ON s.library=e.subsystem_lib AND s.name=e.subsystem_name
                LEFT JOIN sys_routing r ON r.subsystem=CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END
                  AND r.seq=(SELECT rr.seq FROM sys_routing rr
                    WHERE rr.subsystem=CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END
                      AND (rr.compare_mode='*ANY' OR rr.compare_mode='*EQ' AND rr.compare_value=coalesce(j.routing_data,'QCMDB')
                        OR rr.compare_mode='*SECTION' AND substr(coalesce(j.routing_data,'QCMDB'),rr.start_position,length(rr.compare_value))=rr.compare_value)
                    ORDER BY rr.seq LIMIT 1)
                LEFT JOIN sys_classes c ON c.library=coalesce(r.class_lib,'QSYS') AND c.name=coalesce(r.class_name,'QBATCH')
                WHERE j.status='JobQueue' AND j.execution_state='Queued' AND s.status='Active' AND q.held=0
                  AND (($legacy=1 AND x.job_number IS NULL) OR ($legacy=0 AND x.job_number IS NOT NULL AND x.attempts=0))
                  AND ($filter IS NULL OR s.library||'/'||s.name=$filter)
                  AND (SELECT count(*) FROM sys_jobs a
                    WHERE a.subsystem=CASE WHEN s.library='QSYS' THEN s.name ELSE s.library||'/'||s.name END
                      AND (a.status IN ('Active','MessageWait') OR a.status='Held' AND a.started_at IS NOT NULL))<s.max_active
                  AND (SELECT count(*) FROM sys_jobs a WHERE a.jobq=j.jobq
                    AND (a.status IN ('Active','MessageWait') OR a.status='Held' AND a.started_at IS NOT NULL))<e.max_active
                ORDER BY e.sequence,j.priority,j.submitted_at,j.number LIMIT 1
                """;
            select.Parameters.AddWithValue("$legacy", legacy ? 1 : 0);
            select.Parameters.AddWithValue("$filter", subsystemFilter is null ? DBNull.Value :
                Ipc.Core.Objects.QualifiedName.Parse(subsystemFilter.ToUpperInvariant(), "QSYS").ToString());
            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read()) return null;
                key = new JobKey(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
                command = reader.IsDBNull(3) ? "" : reader.GetString(3);
                subsystem = reader.GetString(4); program = reader.IsDBNull(5) ? null : reader.GetString(5);
                jobClass = reader.IsDBNull(6) ? null : reader.GetString(6);
                runPriority = reader.GetInt32(7); timeSlice = reader.GetInt32(8); startupError = reader.IsDBNull(9) ? null : reader.GetString(9);
            }
            using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE sys_jobs SET status='Active',execution_state='Running',subsystem=$subsystem,started_at=$now,
                  routing_program=$program,job_class=$class,run_priority=$priority,time_slice_ms=$slice,startup_error=$error
                WHERE number = $number AND name = $name AND user = $user AND status = 'JobQueue';
                UPDATE sys_job_execution SET attempts=1,host_id=$host,claim_token=$claim,claimed_at=$now WHERE job_number=$number;
                """;
            claim.Parameters.AddWithValue("$host", _hostId);
            claim.Parameters.AddWithValue("$claim", Guid.NewGuid().ToString("N"));
            claim.Parameters.AddWithValue("$number", key.Number);
            claim.Parameters.AddWithValue("$name", key.Name);
            claim.Parameters.AddWithValue("$user", key.User);
            claim.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            claim.Parameters.AddWithValue("$subsystem", subsystem); claim.Parameters.AddWithValue("$program", (object?)program ?? DBNull.Value);
            claim.Parameters.AddWithValue("$class", (object?)jobClass ?? DBNull.Value); claim.Parameters.AddWithValue("$priority", runPriority);
            claim.Parameters.AddWithValue("$slice", timeSlice); claim.Parameters.AddWithValue("$error", (object?)startupError ?? DBNull.Value);
            claim.ExecuteNonQuery();
            JobDataAreaStore.StartRouting(connection, transaction, key.Number);
            transaction.Commit();
        }
        return (jobs.GetRequired(key), command);
    }

    public BatchExecutionInfo? Inspect(JobKey key)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireJob(key);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.execution_state,x.command,x.attempts,x.host_id,x.claim_token,x.claimed_at,x.finished_at FROM sys_jobs j JOIN sys_job_execution x ON x.job_number=j.number WHERE j.number=$number AND j.name=$name AND j.user=$user";
        command.Parameters.AddWithValue("$number", key.Number); command.Parameters.AddWithValue("$name", key.Name); command.Parameters.AddWithValue("$user", key.User);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(key, Enum.Parse<JobExecutionState>(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)) : null;
    }

    public void RecoverInterrupted()
    {
        // Recovery runs under the server's catalog ownership lock before any dispatcher starts.
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        foreach (var job in jobs.List().Where(j => j.Type == JobType.Batch &&
            (j.ExecutionState == JobExecutionState.Running || j.ExecutionState == JobExecutionState.Queued && Inspect(j.Key)?.Attempts > 0)))
            jobs.Complete(job.Key, JobCompletion.Abnormal,
                "Server interrupted this batch job; external effects are uncertain. No automatic retry was performed.", JobExecutionState.Interrupted);
    }
}
