namespace Ipc.Core.Work;

public enum JobType
{
    System,
    Interactive,
    Communication,
    Batch,
    Autostart,
    Prestart,
    SubsystemMonitor,
}

public enum JobStatus
{
    Submitted,
    JobQueue,
    Held,
    Active,
    MessageWait,
    OutputQueue,
    Completed,
    Ended,
}

public enum JobExecutionState { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

public enum JobCompletion
{
    Normal,
    Warning,
    Abnormal,
}

public readonly record struct JobKey(int Number, string Name, string User)
{
    public override string ToString() => $"{Number}/{Name}/{User}";
}

public sealed class Job
{
    public required JobKey Key { get; init; }

    public JobType Type { get; init; } = JobType.Batch;

    public JobStatus Status { get; set; } = JobStatus.Submitted;
    public JobExecutionState ExecutionState { get; set; } = JobExecutionState.Queued;

    public string? Subsystem { get; set; }

    public string? JobQueue { get; set; }

    public int Priority { get; set; } = 9;

    public string? UserProfile { get; set; }
    public string? AuthSessionId { get; init; }

    public string? CurrentLibrary { get; set; }

    public string? LibraryList { get; set; } = "*LIBL";

    public int Ccsid { get; set; } = 37;

    public string? SubmitterName { get; set; }

    public string? SubmitterUser { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Description { get; set; }

    public string? RoutingData { get; set; }
    public string? JobDescription { get; init; }
    public string? JobClass { get; set; }
    public int RunPriority { get; set; } = 50;
    public int TimeSliceMilliseconds { get; set; } = 2000;
    public string? RoutingProgram { get; set; }
    public string? StartupError { get; set; }

    public JobCompletion? CompletionCode { get; set; }

    public string? CompletionMessage { get; set; }
}

public sealed class JobLogEntry
{
    public required JobKey Job { get; init; }

    public required int Sequence { get; init; }

    public required DateTimeOffset Time { get; init; }

    public string? MessageId { get; set; }

    public int Severity { get; set; }

    public string MessageType { get; set; } = "INFO";

    public string? Text { get; set; }
}

public sealed class SubsystemStatus
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Active { get; set; }

    public int MaxActiveJobs { get; set; } = 1;

    public int ActiveJobs { get; set; }

    public int QueuedJobs { get; set; }
}

public static class JobKeys
{
    public const string InteractiveSubsystem = "QINTER";
    public const string BatchSubsystem = "QBATCH";
    public const string SystemSubsystem = "QSYS";
    public const string CommunicationSubsystem = "QSERVER";
}
