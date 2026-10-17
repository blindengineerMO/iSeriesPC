using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record NativeJobRecord(Job Job, byte[] Identifier, long CpuNanoseconds)
{
    public IReadOnlyList<string>? SystemLibraries { get; init; }
    public IReadOnlySet<string>? ExistingLibraries { get; init; }
}

/// <summary>Read snapshots for native job APIs. Identifiers are shared across connections within one host epoch.</summary>
public sealed class NativeJobStore(SqliteConnectionFactory factory)
{
    internal void BeginHostEpoch()
    {
        if (OperationIdentity.Current is not null) throw new CpfException("CPF9802", "Only the exclusive host owner can reset native job identifiers.");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var reset = connection.CreateCommand(); reset.Transaction = transaction;
        reset.CommandText = "UPDATE sys_job_api_epoch SET secret=randomblob(32); UPDATE sys_job_api_ids SET identifier=randomblob(16)";
        reset.ExecuteNonQuery(); transaction.Commit();
    }

    public NativeJobRecord Get(JobKey? key = null, byte[]? identifier = null, bool includeLibraries = false)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.None);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var query = Query(connection, transaction);
        if (identifier is not null) { query.CommandText += " WHERE i.identifier=$identifier"; query.Parameters.AddWithValue("$identifier", identifier); }
        else
        {
            if (key is null) throw new CpfException("CPF3C58", "A qualified job name is required.");
            query.CommandText += " WHERE j.number=$number AND j.name=$name AND j.user=$user";
            query.Parameters.AddWithValue("$number", key.Value.Number); query.Parameters.AddWithValue("$name", key.Value.Name); query.Parameters.AddWithValue("$user", key.Value.User);
        }
        NativeJobRecord result;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) throw new CpfException(identifier is null ? "CPF3C53" : "CPF3C52", "Requested job was not found.");
            result = Read(reader);
        }
        if (!Visible(result.Job)) throw new CpfException(identifier is null ? "CPF3C55" : "CPF3C52", "Job has ended and has no pending output.");
        if (includeLibraries)
        {
            try { new ServiceAuthorization(factory).RequireJob(result.Job.Key); }
            catch (CpfException failure) when (failure.MessageId == "CPF9802") { throw new CpfException("CPF3C57", "Not authorized to retrieve the job library list."); }
            if (Status(result.Job) != "*ACTIVE") throw new CpfException("CPF3C54", "Job library list is not available for an inactive job.");
            query.CommandText = "SELECT value FROM sys_sysvals WHERE name='QSYSLIBL'";
            var system = (query.ExecuteScalar() as string ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            query.CommandText = "SELECT name FROM sys_objects o WHERE lib='QSYS' AND type='*LIB' AND NOT EXISTS(SELECT 1 FROM sys_job_temp_libraries t WHERE t.library=o.name)";
            var libraries = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = query.ExecuteReader()) while (reader.Read()) libraries.Add(reader.GetString(0));
            query.CommandText = "SELECT 1 FROM sys_job_temp_libraries WHERE job_number=$owner";
            query.Parameters.AddWithValue("$owner", result.Job.Key.Number); if (query.ExecuteScalar() is not null) libraries.Add("QTEMP");
            result = result with { SystemLibraries = system, ExistingLibraries = libraries };
        }
        return result;
    }

    public IEnumerable<NativeJobRecord> List(string namePattern, string userPattern, int? number, string status, string type, int after,
        CancellationToken cancellationToken = default)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.None);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var query = Query(connection, transaction);
        query.CommandText += """
             WHERE (j.execution_state IN ('Queued','Running') OR j.status='OutputQueue')
               AND j.number>$after AND j.name LIKE $name ESCAPE '\' AND j.user LIKE $user ESCAPE '\'
               AND ($number IS NULL OR j.number=$number)
             ORDER BY j.number
            """;
        query.Parameters.AddWithValue("$name", Pattern(namePattern)); query.Parameters.AddWithValue("$user", Pattern(userPattern));
        query.Parameters.AddWithValue("$after", after); query.Parameters.AddWithValue("$number", (object?)number ?? DBNull.Value);
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint(); var row = Read(reader);
            if ((status == "*ALL" || Status(row.Job) == status) && (type == "*" || Type(row.Job.Type).Type == type)) yield return row;
        }
    }
    internal byte[] CursorSecret()
    {
        using var connection = factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT secret FROM sys_job_api_epoch WHERE singleton=1";
        return query.ExecuteScalar() as byte[] ?? throw new CpfException("IPC0136", "Missing native job epoch.");
    }
    public static bool Visible(Job job) => job.ExecutionState is JobExecutionState.Queued or JobExecutionState.Running || job.Status == JobStatus.OutputQueue;
    public static string Status(Job job) => job.Status == JobStatus.OutputQueue ? "*OUTQ" : job.ExecutionState == JobExecutionState.Queued ? "*JOBQ" : "*ACTIVE";
    public static (string Type, string Subtype) Type(JobType type) => type switch {
        JobType.Interactive => ("I", ""), JobType.Batch => ("B", ""), JobType.System => ("S", ""),
        JobType.Communication => ("B", "D"), JobType.Prestart => ("B", "J"), JobType.Autostart => ("A", ""),
        JobType.SubsystemMonitor => ("M", ""), _ => throw new CpfException("IPC0136", "Invalid stored job type.") };
    private static string Pattern(string value) => value == "*ALL" ? "%" : value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace('*', '%');

    private static SqliteCommand Query(SqliteConnection connection, SqliteTransaction transaction)
    {
        var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT j.number,j.name,j.user,j.type,j.status,j.execution_state,j.user_profile,j.current_lib,j.libl,j.ccsid,
                   j.subsystem,j.jobq,j.priority,j.run_priority,j.time_slice_ms,j.job_description,j.routing_data,j.submitted_at,j.started_at,j.completed_at,
                   i.identifier,coalesce(a.cpu_nanoseconds,0)+(SELECT coalesce(sum(p.cpu_nanoseconds),0) FROM sys_job_processes p WHERE p.job_number=j.number)
            FROM sys_jobs j JOIN sys_job_api_ids i ON i.job_number=j.number
            LEFT JOIN sys_job_accounting a ON a.job_number=j.number
            """;
        return query;
    }
    private static NativeJobRecord Read(SqliteDataReader r)
    {
        string? Text(int index) => r.IsDBNull(index) ? null : r.GetString(index);
        DateTimeOffset? Time(int index) => Text(index) is { Length: > 0 } text ? DateTimeOffset.Parse(text, global::System.Globalization.CultureInfo.InvariantCulture) : null;
        var job = new Job { Key = new(r.GetInt32(0), r.GetString(1), r.GetString(2)), Type = Enum.Parse<JobType>(r.GetString(3)),
            Status = Enum.Parse<JobStatus>(r.GetString(4)), ExecutionState = Enum.Parse<JobExecutionState>(r.GetString(5)), UserProfile = Text(6),
            CurrentLibrary = Text(7), LibraryList = Text(8), Ccsid = r.GetInt32(9), Subsystem = Text(10), JobQueue = Text(11), Priority = r.GetInt32(12),
            RunPriority = r.GetInt32(13), TimeSliceMilliseconds = r.GetInt32(14), JobDescription = Text(15), RoutingData = Text(16),
            SubmittedAt = Time(17), StartedAt = Time(18), CompletedAt = Time(19) };
        return new(job, (byte[])r[20], r.GetInt64(21));
    }
}
