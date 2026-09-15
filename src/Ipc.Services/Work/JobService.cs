using Ipc.Services.Security;
using Ipc.Services.Events;
using Ipc.Core.Security;
using Ipc.Core.Objects;
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

    internal void SeedQueues()
    {
        var objects = new SqliteObjectStore(_factory);
        foreach (var (library, name, type) in new[] { ("QUSRSYS", "QPRINT", ObjectType.OutputQueue), ("QSYS", "QSYSOPR", ObjectType.MessageQueue) })
            if (!objects.Exists(library, name, type)) objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = type,
                Owner = "QSYS", PublicAuthority = Authorities.UseBits | AuthorityBit.Add });
    }

    public Job CreateInteractive(string user, string? profile = null, string currentLibrary = "QGPL",
        string libraryList = "QGPL QUSRSYS", int ccsid = 37) => CreateAttached(JobType.Interactive, "QDFTJOB", user, profile, currentLibrary, libraryList, ccsid);

    public Job CreateCommunication(string user, string currentLibrary = "QGPL", string libraryList = "QGPL QUSRSYS", int ccsid = 37) =>
        CreateAttached(JobType.Communication, "QIPCCOMM", user, user, currentLibrary, libraryList, ccsid);

    public Job CreateSystem(string name, string? profile = null)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl, allowAdopted: false);
        profile ??= OperationIdentity.Current?.Principal ?? "QSYSOPR";
        return CreateAttached(JobType.System, name, profile, profile, "QGPL", "QGPL QUSRSYS", 37);
    }

    public Job CreateGroupJob(JobKey parent, string name)
    {
        new JobDataAreaStore(_factory).RequireOwner(parent);
        var source = GetRequired(parent);
        if (source.Type != JobType.Interactive) throw new CpfException("IPC0126", "Only interactive jobs can create group jobs.");
        return CreateAttached(JobType.Interactive, name, parent.User, source.UserProfile,
            source.CurrentLibrary ?? "QGPL", source.LibraryList ?? "QGPL QUSRSYS", source.Ccsid, parent);
    }

    private Job CreateAttached(JobType type, string name, string user, string? profile, string currentLibrary, string libraryList, int ccsid, JobKey? groupParent = null)
    {
        if (!ObjectName.IsValid(name)) throw new CpfException("IPC0123", "Invalid job name.");
        ValidateLibraries(currentLibrary, libraryList, ccsid);
        new ServiceAuthorization(_factory).RequireRunAs(profile ?? user);
        if (OperationIdentity.Current is { } identity && user != identity.Principal)
            new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl);
        var jobClass = new WorkDefinitionStore(_factory).Class("QSYS", type == JobType.System ? "QBATCH" : "QINTER")
            ?? throw new CpfException("CPF9801", "Job class is missing.");
        var number = NextNumber();
        var job = new Job
        {
            Key = new JobKey(number, name, profile ?? user),
            Type = type,
            JobClass = jobClass.Library + "/" + jobClass.Name,
            RunPriority = jobClass.RunPriority,
            TimeSliceMilliseconds = jobClass.TimeSliceMilliseconds,
            AuthSessionId = OperationIdentity.Current?.AuthSessionId,
            Status = JobStatus.Active,
            ExecutionState = JobExecutionState.Running,
            Subsystem = type switch { JobType.Communication => JobKeys.CommunicationSubsystem, JobType.System => JobKeys.SystemSubsystem, _ => JobKeys.InteractiveSubsystem },
            JobQueue = null,
            UserProfile = profile ?? user,
            CurrentLibrary = currentLibrary,
            LibraryList = libraryList,
            Ccsid = ccsid,
            SubmittedAt = SystemClock(),
            StartedAt = SystemClock(),
        };
        using (var connection = _factory.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using var admission = connection.CreateCommand(); admission.Transaction = transaction;
            admission.CommandText = "SELECT count(*) FROM sys_subsystems s WHERE s.library='QSYS' AND s.name=$subsystem AND s.status='Active' AND (SELECT count(*) FROM sys_jobs j WHERE j.subsystem=$subsystem AND j.execution_state='Running')<s.max_active";
            admission.Parameters.AddWithValue("$subsystem", job.Subsystem);
            if (Convert.ToInt64(admission.ExecuteScalar()) != 1) throw new CpfException("IPC0123", "Subsystem is stopped or has no job capacity.");
            Insert(job, connection, transaction);
            JobDataAreaStore.Initialize(connection, transaction, job.Key.Number, groupParent, null, groupChild: groupParent is not null);
            WriteLogCore(job.Key, "INFO", "IPCS0001", 0, type + " job started.", connection, transaction);
            transaction.Commit();
        }
        return job;
    }

    public void SetLibraries(Job job, string currentLibrary, string libraryList)
    {
        new JobDataAreaStore(_factory).RequireOwner(job.Key);
        ValidateLibraries(currentLibrary, libraryList, job.Ccsid);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sys_jobs SET current_lib = $library, libl = $list WHERE number = $number AND name = $name AND user = $user AND execution_state='Running' AND cancel_requested=0";
        cmd.Parameters.AddWithValue("$library", currentLibrary);
        cmd.Parameters.AddWithValue("$list", libraryList);
        cmd.Parameters.AddWithValue("$number", job.Key.Number);
        cmd.Parameters.AddWithValue("$name", job.Key.Name);
        cmd.Parameters.AddWithValue("$user", job.Key.User);
        if (cmd.ExecuteNonQuery() != 1) throw new CpfException("CPF1241", "Job not found.");
        job.CurrentLibrary = currentLibrary;
        job.LibraryList = libraryList;
    }

    public Job Submit(
        string name,
        string description,
        string jobq,
        int priority = 9,
        string? profile = null,
        string? routingData = null,
        string? submitterName = null,
        string? submitterUser = null,
        string? command = null,
        string currentLibrary = "QGPL", string libraryList = "QGPL QUSRSYS", int ccsid = 37, string? jobDescription = null, JobKey? submittingJob = null, byte[]? initializationParameters = null)
    {
        if (!ObjectName.IsValid(name) || priority is < 0 or > 9 || routingData?.Length > 80)
            throw new CpfException("IPC0120", "Invalid job name, priority or routing data.");
        ValidateLibraries(currentLibrary, libraryList, ccsid);
        submittingJob ??= OperationIdentity.Current?.Job;
        if (submittingJob is { } parent) new JobDataAreaStore(_factory).RequireOwner(parent);
        if (initializationParameters?.Length > 2000) throw new CpfException("IPC0126", "Initialization data exceeds 2000 bytes.");
        initializationParameters = initializationParameters?.ToArray();
        var actor = OperationIdentity.Current?.Principal;
        var submitter = actor ?? submitterUser ?? submitterName ?? Environment.UserName;
        profile ??= submitter;
        var authorization = new ServiceAuthorization(_factory);
        authorization.RequireRunAs(profile);
        var queue = QualifiedName.Parse(jobq.ToUpperInvariant(), "QUSRSYS");
        authorization.RequireObject(queue.Library, queue.Name.Value, ObjectType.JobQueue, Authorities.UseBits);
        if (!new JobQueueStore(_factory).Exists(queue.ToString())) throw new CpfException("CPF9801", "Job queue not found.");
        if (jobDescription is not null)
        {
            var jobd = QualifiedName.Parse(jobDescription.ToUpperInvariant(), "QGPL");
            authorization.RequireObject(jobd.Library, jobd.Name.Value, ObjectType.JobDescription, Authorities.UseBits);
        }
        var number = NextNumber();
        var job = new Job
        {
            Key = new JobKey(number, name, profile),
            Type = JobType.Batch,
            Status = JobStatus.JobQueue,
            JobQueue = queue.ToString(),
            JobDescription = jobDescription,
            Priority = priority,
            UserProfile = profile ?? submitter,
            CurrentLibrary = currentLibrary,
            LibraryList = libraryList,
            Ccsid = ccsid,
            SubmitterName = submitterName ?? name,
            SubmitterUser = actor ?? submitterUser ?? submitter,
            SubmittedAt = SystemClock(),
            Description = description,
            RoutingData = routingData ?? "QCMDB",
        };
        if (command is not null && (string.IsNullOrWhiteSpace(command) || command.Length > 32768))
            throw new ArgumentException("Batch command length must be 1 through 32768 characters.", nameof(command));
        using (var connection = _factory.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            Insert(job, connection, transaction);
            JobDataAreaStore.Initialize(connection, transaction, job.Key.Number, submittingJob, initializationParameters, groupChild: false);
            if (command is not null)
            {
                using var request = connection.CreateCommand(); request.Transaction = transaction;
                request.CommandText = "INSERT INTO sys_job_execution(job_number, command) VALUES ($number, $command)";
                request.Parameters.AddWithValue("$number", number); request.Parameters.AddWithValue("$command", command);
                request.ExecuteNonQuery();
            }
            WriteLogCore(job.Key, "INFO", "CPC1228", 0, $"Job {name} ({number}) submitted to job queue {jobq}.", connection, transaction);
            transaction.Commit();
        }
        return job;
    }

    private void ValidateLibraries(string currentLibrary, string libraryList, int ccsid)
    {
        var libraries = libraryList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (libraries.Length > 250 || libraries.Distinct(StringComparer.Ordinal).Count() != libraries.Length ||
            ccsid is not (37 or 500 or 1047 or 850 or 819 or 1208 or 367))
            throw new CpfException("IPC0126", "Invalid job library list or unsupported job CCSID.");
        if (libraries.Any(l => !ObjectName.IsValid(l)) || currentLibrary != "*CRTDFT" && !ObjectName.IsValid(currentLibrary))
            throw new CpfException("IPC0126", "Invalid library name.");
        foreach (var library in libraries.Concat(currentLibrary == "*CRTDFT" ? Array.Empty<string>() : new[] { currentLibrary }).Distinct(StringComparer.Ordinal))
        {
            _ = new SqliteObjectStore(_factory).GetRequired("QSYS", library, ObjectType.Library);
            new ServiceAuthorization(_factory).RequireObject("QSYS", library, ObjectType.Library, Authorities.UseBits);
        }
    }

    public Job? Get(JobKey key)
    {
        new ServiceAuthorization(_factory).RequireJob(key);
        return GetCore(key);
    }

    private Job? GetCore(JobKey key)
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
            if (job.Type == JobType.Interactive) throw new CpfException("CPF1312", "Resume interactive group jobs with TFRGRPJOB.");
            SetStatus(job, job.StartedAt is null ? JobStatus.JobQueue : JobStatus.Active);
            WriteLog(job, "INFO", "CPC1204", 0, $"Job {job.Key.Name} ({job.Key.Number}) released.");
        }
    }

    public int StartNext(string subsystem)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl);
        var started = 0;
        var queue = new BatchQueue(_factory, this);
        while (queue.ClaimLegacy(subsystem) is { } claim)
        {
            WriteLog(claim.Job, "INFO", "CPC1222", 0, $"Job {claim.Job.Key} started in subsystem {claim.Job.Subsystem}.");
            started++;
        }

        return started;
    }

    public void Complete(JobKey key, JobCompletion completion = JobCompletion.Normal, string? message = null, JobExecutionState? outcome = null)
    {
        new ServiceAuthorization(_factory).RequireJob(key);
        CompleteCore(key, completion, message, outcome);
    }

    public void RequestEnd(JobKey key, string reason = "Job ended by request.")
    {
        new ServiceAuthorization(_factory).RequireJob(key);
        if (reason.Length > 1024) throw new CpfException("IPC0123", "Job-end reason exceeds 1024 characters.");
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT execution_state FROM sys_jobs WHERE number=$number AND name=$name AND user=$user";
        command.Parameters.AddWithValue("$number", key.Number); command.Parameters.AddWithValue("$name", key.Name); command.Parameters.AddWithValue("$user", key.User);
        var state = command.ExecuteScalar() as string ?? throw new CpfException("CPF1241", "Job not found.");
        if (state is not ("Queued" or "Running")) return;
        command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$at", SystemClock().ToString("O"));
        command.CommandText = "UPDATE sys_jobs SET cancel_requested=1,cancel_reason=$reason WHERE number=$number";
        command.ExecuteNonQuery();
        if (state == "Queued")
        {
            command.CommandText = """
                UPDATE sys_jobs SET status='Completed',execution_state='Cancelled',completion='Abnormal',completion_msg=$reason,completed_at=$at WHERE number=$number;
                UPDATE sys_job_execution SET finished_at=$at WHERE job_number=$number;
                UPDATE sys_job_bindings SET output_snapshot=coalesce(output_lib||'/'||output_name,output_snapshot),
                  message_snapshot=coalesce(message_lib||'/'||message_name,message_snapshot),output_lib=NULL,output_name=NULL,message_lib=NULL,message_name=NULL WHERE job_number=$number;
                """;
            command.ExecuteNonQuery(); WriteLogCore(key, "COMPLETION", "CPF1126", 40, reason, connection, transaction);
        }
        else WriteLogCore(key, "INFO", "IPC0123", 0, reason, connection, transaction);
        transaction.Commit();
    }

    internal void CompleteHostedSession(JobKey key, JobCompletion completion, string message, JobExecutionState? outcome = null)
    {
        if (OperationIdentity.Current?.Job != key) throw new InvalidOperationException("Hosted cleanup must identify its owned job.");
        new ServiceAuthorization(_factory).RequireJob(key, allowInactiveProfile: true);
        CompleteCore(key, completion, message, outcome);
    }

    private void CompleteCore(JobKey key, JobCompletion completion, string? message, JobExecutionState? outcome)
    {
        var state = outcome ?? (completion == JobCompletion.Abnormal ? JobExecutionState.Failed : JobExecutionState.Succeeded);
        if (state is JobExecutionState.Queued or JobExecutionState.Running || !Enum.IsDefined(state) ||
            (state == JobExecutionState.Succeeded) != (completion != JobCompletion.Abnormal))
            throw new CpfException("IPC0122", "Invalid job completion outcome.");
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT execution_state,cancel_requested,cancel_reason FROM sys_jobs WHERE number=$number AND name=$name AND user=$user";
        command.Parameters.AddWithValue("$number", key.Number); command.Parameters.AddWithValue("$name", key.Name); command.Parameters.AddWithValue("$user", key.User);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new CpfException("CPF1241", "Job not found.");
            if (reader.GetString(0) is not ("Queued" or "Running")) return; // First terminal outcome wins.
            if (reader.GetInt32(1) != 0 && state != JobExecutionState.Interrupted)
            {
                state = JobExecutionState.Cancelled; completion = JobCompletion.Abnormal;
                message = reader.IsDBNull(2) ? "Job cancelled." : reader.GetString(2);
            }
        }
        var text = message ?? (state == JobExecutionState.Succeeded ? "Completed normally." : $"Job {state.ToString().ToLowerInvariant()}.");
        command.CommandText = """
            UPDATE sys_jobs SET status='Completed',completed_at=$at,completion=$completion,completion_msg=$text,execution_state=$state
              WHERE number=$number AND name=$name AND user=$user;
            UPDATE sys_job_execution SET finished_at=$at WHERE job_number=$number;
            UPDATE sys_job_bindings SET output_snapshot=coalesce(output_lib||'/'||output_name,output_snapshot),
              message_snapshot=coalesce(message_lib||'/'||message_name,message_snapshot),output_lib=NULL,output_name=NULL,message_lib=NULL,message_name=NULL WHERE job_number=$number;
            UPDATE sys_job_accounting SET active_threads=0 WHERE job_number=$number;
            UPDATE sys_job_threads SET state='Ended' WHERE job_number=$number;
            DELETE FROM sys_activation_groups WHERE job_number=$number;
            UPDATE sys_job_processes SET state='Interrupted',ended_at=$at WHERE job_number=$number AND state='Running';
            """;
        command.Parameters.AddWithValue("$at", SystemClock().ToString("O")); command.Parameters.AddWithValue("$completion", completion.ToString());
        command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$state", state.ToString()); command.ExecuteNonQuery();
        var messageId = completion switch { JobCompletion.Normal => "CPC1124", JobCompletion.Warning => "CPC1125", _ => "CPF1126" };
        WriteLogCore(key, "COMPLETION", messageId, completion == JobCompletion.Normal ? 0 : 40, text, connection, transaction);
        transaction.Commit();
        new JobLockStore(_factory).ReleaseJob(key);
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
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        var where = "WHERE subsystem = $subsystem";
        if (status is not null)
        {
            where += " AND status = $status";
        }
        else where += " AND (status IN ('Active','MessageWait') OR status='Held' AND started_at IS NOT NULL)";

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
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM sys_jobs j JOIN sys_jobq_entries e ON j.jobq=e.queue_lib||'/'||e.queue_name
            WHERE e.subsystem_lib||'/'||e.subsystem_name=$subsystem AND j.status IN ('JobQueue','Held') AND j.started_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$subsystem", QualifiedName.Parse(subsystemAmount.ToUpperInvariant(), "QSYS").ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountActive()
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.JobControl);
        return ActiveJobs();
    }

    public void RecoverInterruptedInteractiveJobs()
    {
        foreach (var job in List().Where(j => (j.Type is JobType.Interactive or JobType.Communication or JobType.System) &&
            j.ExecutionState == JobExecutionState.Running))
            Complete(job.Key, JobCompletion.Abnormal, "Session server stopped before this interactive job completed.", JobExecutionState.Interrupted);
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
            where.Add("(subsystem = $subsystem OR (started_at IS NULL AND jobq IN (SELECT queue_lib||'/'||queue_name FROM sys_jobq_entries WHERE subsystem_lib||'/'||subsystem_name=$qualified)))");
            var key = QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
            cmd.Parameters.AddWithValue("$subsystem", key.Library == "QSYS" ? key.Name.Value : key.ToString());
            cmd.Parameters.AddWithValue("$qualified", key.ToString());
        }

        cmd.CommandText = "SELECT * FROM sys_jobs" +
                          (where.Count > 0 ? $" WHERE {string.Join(" AND ", where)}" : string.Empty) +
                          " ORDER BY submitted_at DESC, number DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<Job>();
        while (reader.Read())
        {
            var job = FromReader(reader);
            try { new ServiceAuthorization(_factory).RequireJob(job.Key); list.Add(job); }
            catch (CpfException ex) when (ex.MessageId == "CPF9802") { }
        }

        return list;
    }

    public IReadOnlyList<JobLogEntry> GetLog(JobKey key, int limit = 100)
    {
        new ServiceAuthorization(_factory).RequireJob(key);
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
        new ServiceAuthorization(_factory).RequireJob(key);
        WriteLogCore(key, messageType, messageId, severity, text);
    }

    private void WriteLogCore(JobKey key, string messageType, string? messageId, int severity, string? text)
    {
        using var connection = _factory.Open();
        WriteLogCore(key, messageType, messageId, severity, text, connection, null);
    }

    private static void WriteLogCore(JobKey key, string messageType, string? messageId, int severity, string? text,
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_joblog (job_number, job_name, job_user, seq, time, message_id, severity, message_type, text, principal)
            SELECT $number, $name, $user, COALESCE(MAX(seq), 0) + 1, $time, $message_id, $severity, $type, $text, $principal
            FROM sys_joblog WHERE job_number = $number AND job_name = $name AND job_user = $user;
            """;
        cmd.Parameters.AddWithValue("$number", key.Number);
        cmd.Parameters.AddWithValue("$name", key.Name);
        cmd.Parameters.AddWithValue("$user", key.User);
        cmd.Parameters.AddWithValue("$time", SystemClock().ToString("o",
            System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$message_id", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$severity", severity);
        cmd.Parameters.AddWithValue("$type", messageType);
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$principal", Ipc.Services.Events.OperationIdentity.Current?.Principal ?? "*SYSTEM");
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
        cmd.CommandText = """
            INSERT INTO sys_meta(key, value)
            SELECT 'last_job_number', CAST(COALESCE(MAX(number), 0) + 1 AS TEXT) FROM sys_jobs
            HAVING COALESCE(MAX(number), 0)<2147483647
            ON CONFLICT(key) DO UPDATE SET value = CAST(max(CAST(sys_meta.value AS INTEGER),
                (SELECT COALESCE(MAX(number),0) FROM sys_jobs)) + 1 AS TEXT)
            WHERE sys_meta.value NOT GLOB '*[^0-9]*' AND length(sys_meta.value)>0 AND CAST(sys_meta.value AS INTEGER)<2147483647
            RETURNING CAST(value AS INTEGER)
            """;
        return cmd.ExecuteScalar() is { } value ? Convert.ToInt32(value) : throw new CpfException("IPC0122", "Job-number sequence is invalid or exhausted.");
    }

    private void Insert(Job job)
    {
        using var connection = _factory.Open();
        Insert(job, connection, null);
    }

    private static void Insert(Job job, Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_jobs (
                number, name, user, type, status, subsystem, jobq, priority, user_profile,
                current_lib, libl, ccsid, submitter_name, submitter_user, submitted_at,
                started_at, completed_at, description, routing_data, completion, completion_msg, auth_session,
                job_description,job_class,run_priority,time_slice_ms,routing_program,startup_error,execution_state)
            VALUES (
                $number, $name, $user, $type, $status, $subsystem, $jobq, $priority, $profile,
                $curlib, $libl, $ccsid, $subname, $subuser, $submitted,
                $started, $completed, $description, $routing, $completion, $completion_msg, $auth_session,
                $jobd,$class,$runpriority,$slice,$routeprogram,$startuperror,$executionstate);
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
            WHERE number = $number AND name = $name AND user = $user AND execution_state IN ('Queued','Running');
            """;
        AddParameters(cmd, job);
        if (cmd.ExecuteNonQuery() != 1) throw new CpfException("CPF1241", "Job has already ended.");
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
        cmd.Parameters.AddWithValue("$auth_session", (object?)job.AuthSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$jobd", (object?)job.JobDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$class", (object?)job.JobClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runpriority", job.RunPriority);
        cmd.Parameters.AddWithValue("$slice", job.TimeSliceMilliseconds);
        cmd.Parameters.AddWithValue("$routeprogram", (object?)job.RoutingProgram ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startuperror", (object?)job.StartupError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$executionstate", job.ExecutionState.ToString());
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
            AuthSessionId = N(reader.GetOrdinal("auth_session")),
            ExecutionState = Enum.Parse<JobExecutionState>(reader.GetString(reader.GetOrdinal("execution_state"))),
            JobDescription = N(reader.GetOrdinal("job_description")),
            JobClass = N(reader.GetOrdinal("job_class")),
            RunPriority = reader.GetInt32(reader.GetOrdinal("run_priority")),
            TimeSliceMilliseconds = reader.GetInt32(reader.GetOrdinal("time_slice_ms")),
            RoutingProgram = N(reader.GetOrdinal("routing_program")),
            StartupError = N(reader.GetOrdinal("startup_error")),
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
