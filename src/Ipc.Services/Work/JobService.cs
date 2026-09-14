using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed class JobService
{
    private readonly SqliteConnectionFactory _factory;

    public JobService(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Job CreateInteractive(string user, string? profile = null)
    {
        var number = NextNumber();
        var job = new Job
        {
            Key = new JobKey(number, "QDFTJOB", user),
            Type = JobType.Interactive,
            Status = JobStatus.Active,
            Subsystem = JobKeys.InteractiveSubsystem,
            JobQueue = JobKeys.InteractiveSubsystem,
            UserProfile = profile ?? user,
            CurrentLibrary = "*CURLIB",
            SubmittedAt = SystemClock(),
            StartedAt = SystemClock(),
        };
        Insert(job);
        WriteLog(job, "INFO", "IPCS0001", 0, "Interactive job started.");
        return job;
    }

    public Job Submit(
        string name,
        string description,
        string jobq,
        int priority = 9,
        string? profile = null,
        string? routingData = null,
        string? submitterName = null,
        string? submitterUser = null)
    {
        var number = NextNumber();
        var submitter = submitterUser ?? submitterName ?? Environment.UserName;
        var job = new Job
        {
            Key = new JobKey(number, name, submitter),
            Type = JobType.Batch,
            Status = JobStatus.JobQueue,
            JobQueue = jobq,
            Priority = priority,
            UserProfile = profile ?? submitter,
            CurrentLibrary = "*CURLIB",
            SubmitterName = submitterName ?? name,
            SubmitterUser = submitterUser ?? submitter,
            SubmittedAt = SystemClock(),
            Description = description,
            RoutingData = routingData,
        };
        Insert(job);
        WriteLog(job, "INFO", "CPC1228", 0, $"Job {name} ({number}) submitted to job queue {jobq}.");
        return job;
    }

    public Job? Get(JobKey key)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM sys_jobs WHERE number = $number AND name = $name AND user = $user";
        cmd.Parameters.AddWithValue("$number", key.Number);
        cmd.Parameters.AddWithValue("$name", key.Name);
        cmd.Parameters.AddWithValue("$user", key.User);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? FromReader(reader) : null;
    }

    public Job GetRequired(JobKey key) =>
        Get(key) ?? throw new CpfException("CPF1241", $"Job {key} not found.");

    public void Hold(JobKey key)
    {
        var job = GetRequired(key);
        if (job.Status == JobStatus.JobQueue)
        {
            SetStatus(job, JobStatus.Held);
            WriteLog(job, "INFO", "CPC1203", 0, $"Job {job.Key.Name} ({job.Key.Number}) held.");
        }
    }

    public void Release(JobKey key)
    {
        var job = GetRequired(key);
        if (job.Status == JobStatus.Held)
        {
            SetStatus(job, JobStatus.JobQueue);
            WriteLog(job, "INFO", "CPC1204", 0, $"Job {job.Key.Name} ({job.Key.Number}) released.");
        }
    }

    public int StartNext(string subsystem)
    {
        var started = 0;
        var candidates = new List<Job>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM sys_jobs
                WHERE status = 'JobQueue' AND jobq = $subsystem
                ORDER BY priority ASC, submitted_at ASC, number ASC
                """;
            cmd.Parameters.AddWithValue("$subsystem", subsystem);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(FromReader(reader));
            }
        }

        foreach (var job in candidates)
        {
            if (!HasRoomForActive(subsystem))
            {
                break;
            }

            job.Status = JobStatus.Active;
            job.Subsystem = subsystem;
            job.StartedAt = SystemClock();
            Persist(job);
            WriteLog(job, "INFO", "CPC1222", 0, $"Job {job.Key.Name} ({job.Key.Number}) started in subsystem {subsystem}.");
            started++;
        }

        return started;
    }

    public void Complete(JobKey key, JobCompletion completion = JobCompletion.Normal, string? message = null)
    {
        var job = GetRequired(key);
        job.Status = JobStatus.Completed;
        job.CompletedAt = SystemClock();
        job.CompletionCode = completion;
        job.CompletionMessage = message ?? (completion == JobCompletion.Normal ? "Completed normally." : "Completed abnormally.");
        Persist(job);
        var messageId = completion switch
        {
            JobCompletion.Normal => "CPC1124",
            JobCompletion.Warning => "CPC1125",
            _ => "CPF1126",
        };
        WriteLog(job, "COMPLETION", messageId, completion == JobCompletion.Normal ? 0 : 40,
            job.CompletionMessage);
    }

    public void MessageWait(JobKey key, string? message)
    {
        var job = GetRequired(key);
        job.Status = JobStatus.MessageWait;
        Persist(job);
        WriteLog(job, "INQUIRY", "CPC0000", 0, message ?? "Waiting on system message.");
    }

    public void ReplyToMessage(JobKey key)
    {
        var job = GetRequired(key);
        if (job.Status == JobStatus.MessageWait)
        {
            job.Status = JobStatus.Active;
            Persist(job);
        }
    }

    public int CountWithStatus(string subsystem, JobStatus? status)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        var where = "WHERE subsystem = $subsystem";
        if (status is not null)
        {
            where += " AND status = $status";
        }

        cmd.CommandText = $"SELECT count(*) FROM sys_jobs {where}";
        cmd.Parameters.AddWithValue("$subsystem", subsystem);
        if (status is not null)
        {
            cmd.Parameters.AddWithValue("$status", status.Value.ToString());
        }

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountInQueueForSubsystem(string subsystemAmount)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sys_jobs WHERE jobq = $jobq AND status IN ('JobQueue', 'Held')";
        cmd.Parameters.AddWithValue("$jobq", subsystemAmount);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountActive() => ActiveJobs();

    private bool HasRoomForActive(string subsystem)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT max_active FROM sys_subsystems WHERE name = $subsystem)
                 - (SELECT count(*) FROM sys_jobs WHERE subsystem = $subsystem AND status = 'Active')
            """;
        cmd.Parameters.AddWithValue("$subsystem", subsystem);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public IReadOnlyList<Job> List(JobStatus? status = null, string? subsystem = null)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        var where = new List<string>();
        if (status is not null)
        {
            where.Add("status = $status");
            cmd.Parameters.AddWithValue("$status", status.Value.ToString());
        }

        if (subsystem is not null)
        {
            where.Add("(subsystem = $subsystem OR jobq = $subsystem)");
            cmd.Parameters.AddWithValue("$subsystem", subsystem);
        }

        cmd.CommandText = "SELECT * FROM sys_jobs" +
                          (where.Count > 0 ? $" WHERE {string.Join(" AND ", where)}" : string.Empty) +
                          " ORDER BY submitted_at DESC, number DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<Job>();
        while (reader.Read())
        {
            list.Add(FromReader(reader));
        }

        return list;
    }

    public IReadOnlyList<JobLogEntry> GetLog(JobKey key, int limit = 100)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT seq, time, message_id, severity, message_type, text FROM sys_joblog
            WHERE job_number = $number AND job_name = $name AND job_user = $user
            ORDER BY seq ASC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$number", key.Number);
        cmd.Parameters.AddWithValue("$name", key.Name);
        cmd.Parameters.AddWithValue("$user", key.User);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var entries = new List<JobLogEntry>();
        while (reader.Read())
        {
            entries.Add(new JobLogEntry
            {
                Job = key,
                Sequence = reader.GetInt32(0),
                Time = DateTimeOffset.Parse(reader.GetString(1),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal),
                MessageId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Severity = reader.GetInt32(3),
                MessageType = reader.GetString(4),
                Text = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return entries;
    }

    public void WriteLog(Job job, string messageType, string? messageId, int severity, string? text)
    {
        WriteLog(job.Key, messageType, messageId, severity, text);
    }

    public void WriteLog(JobKey key, string messageType, string? messageId, int severity, string? text)
    {
        var sequence = NextLogSequence(key);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_joblog (job_number, job_name, job_user, seq, time, message_id, severity, message_type, text)
            VALUES ($number, $name, $user, $seq, $time, $message_id, $severity, $type, $text);
            """;
        cmd.Parameters.AddWithValue("$number", key.Number);
        cmd.Parameters.AddWithValue("$name", key.Name);
        cmd.Parameters.AddWithValue("$user", key.User);
        cmd.Parameters.AddWithValue("$seq", sequence);
        cmd.Parameters.AddWithValue("$time", SystemClock().ToString("o",
            System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$message_id", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$severity", severity);
        cmd.Parameters.AddWithValue("$type", messageType);
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private int ActiveJobs()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sys_jobs WHERE status = 'Active'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int NextNumber()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM sys_jobs";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int NextLogSequence(JobKey key)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM sys_joblog " +
                          "WHERE job_number = $number AND job_name = $name AND job_user = $user";
        cmd.Parameters.AddWithValue("$number", key.Number);
        cmd.Parameters.AddWithValue("$name", key.Name);
        cmd.Parameters.AddWithValue("$user", key.User);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Insert(Job job)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_jobs (
                number, name, user, type, status, subsystem, jobq, priority, user_profile,
                current_lib, libl, ccsid, submitter_name, submitter_user, submitted_at,
                started_at, completed_at, description, routing_data, completion, completion_msg)
            VALUES (
                $number, $name, $user, $type, $status, $subsystem, $jobq, $priority, $profile,
                $curlib, $libl, $ccsid, $subname, $subuser, $submitted,
                $started, $completed, $description, $routing, $completion, $completion_msg);
            """;
        AddParameters(cmd, job);
        cmd.ExecuteNonQuery();
    }

    private void Persist(Job job)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sys_jobs SET
                status = $status, subsystem = $subsystem, jobq = $jobq, priority = $priority,
                user_profile = $profile, current_lib = $curlib, libl = $libl, ccsid = $ccsid,
                submitter_name = $subname, submitter_user = $subuser, submitted_at = $submitted,
                started_at = $started, completed_at = $completed, description = $description,
                routing_data = $routing, completion = $completion, completion_msg = $completion_msg
            WHERE number = $number AND name = $name AND user = $user;
            """;
        AddParameters(cmd, job);
        cmd.ExecuteNonQuery();
    }

    private static void AddParameters(Microsoft.Data.Sqlite.SqliteCommand cmd, Job job)
    {
        cmd.Parameters.AddWithValue("$number", job.Key.Number);
        cmd.Parameters.AddWithValue("$name", job.Key.Name);
        cmd.Parameters.AddWithValue("$user", job.Key.User);
        cmd.Parameters.AddWithValue("$type", job.Type.ToString());
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$subsystem", (object?)job.Subsystem ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$jobq", (object?)job.JobQueue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$priority", job.Priority);
        cmd.Parameters.AddWithValue("$profile", (object?)job.UserProfile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$curlib", (object?)job.CurrentLibrary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$libl", (object?)job.LibraryList ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccsid", job.Ccsid);
        cmd.Parameters.AddWithValue("$subname", (object?)job.SubmitterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subuser", (object?)job.SubmitterUser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$submitted", job.SubmittedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$started", job.StartedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", job.CompletedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$description", (object?)job.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$routing", (object?)job.RoutingData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completion", job.CompletionCode?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$completion_msg", (object?)job.CompletionMessage ?? DBNull.Value);
    }

    private void SetStatus(Job job, JobStatus status)
    {
        job.Status = status;
        Persist(job);
    }

    private static Job FromReader(System.Data.Common.DbDataReader reader)
    {
        string? N(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        string? T(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        DateTimeOffset? D(int index) =>
            T(index) is { } s &&
            DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        return new Job
        {
            Key = new JobKey(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)),
            Type = Enum.TryParse<JobType>(reader.GetString(3), out var type) ? type : JobType.Batch,
            Status = Enum.TryParse<JobStatus>(reader.GetString(4), out var status) ? status : JobStatus.Submitted,
            Subsystem = N(5),
            JobQueue = N(6),
            Priority = reader.GetInt32(7),
            UserProfile = N(8),
            CurrentLibrary = N(9),
            LibraryList = N(10),
            Ccsid = reader.GetInt32(11),
            SubmitterName = N(12),
            SubmitterUser = N(13),
            SubmittedAt = D(14),
            StartedAt = D(15),
            CompletedAt = D(16),
            Description = N(17),
            RoutingData = N(18),
            CompletionCode = Enum.TryParse<JobCompletion>(T(19) ?? string.Empty, out var completion)
                ? completion
                : null,
            CompletionMessage = N(20),
        };
    }

    private static DateTimeOffset SystemClock() => DateTimeOffset.UtcNow;
}