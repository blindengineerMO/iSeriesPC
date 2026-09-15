using Ipc.Services.Events;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;

namespace Ipc.Session;

/// <summary>A job-owned execution context. The server owns its lifetime and services.</summary>
public sealed class ExecutionSession : IDisposable
{
    private readonly IpcSystem _system;
    private readonly CommandService _commands;
    private readonly Ipc.Services.Work.JobRuntimeLease _runtime;
    public CancellationToken CancellationToken => _runtime.CancellationToken;
    private bool _ended;
    public Job Job { get; }

    internal ExecutionSession(IpcSystem system, UserProfile profile, CancellationToken cancellationToken, string? authSessionId = null, bool communication = false)
        : this(system, CreateInteractive(system, profile, authSessionId, communication), cancellationToken) { }

    private static Job CreateInteractive(IpcSystem system, UserProfile profile, string? authSessionId, bool communication)
    {
        using var identity = OperationIdentity.Enter(profile.Name, authSessionId: authSessionId);
        var currentLibrary = profile.InitialCurrentLibrary?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(currentLibrary)) currentLibrary = "QGPL";
        if (currentLibrary != "*CRTDFT" && !Ipc.Core.Objects.ObjectName.IsValid(currentLibrary)) throw new Ipc.Core.Messages.CpfException("CPF1164", "Invalid initial current library.");
        var libraries = system.SystemValues.Get(Ipc.Core.System.SystemValueNames.UserLibraryList).Value.Replace(',', ' ').Trim().ToUpperInvariant();
        if (libraries.Length == 0) libraries = "QGPL QUSRSYS";
        else if (libraries == "*NONE") libraries = "";
        return communication ? system.Jobs.CreateCommunication(profile.Name, currentLibrary, libraries, profile.Ccsid)
            : system.Jobs.CreateInteractive(profile.Name, currentLibrary: currentLibrary, libraryList: libraries, ccsid: profile.Ccsid);
    }

    internal ExecutionSession(IpcSystem system, Job job, CancellationToken cancellationToken)
    {
        _system = system;
        Job = job;
        _runtime = system.JobRuntime.Attach(job, cancellationToken);
        _commands = new CommandService(system, Job, _runtime.CancellationToken);
    }

    public IReadOnlyList<string> AvailableCommands => _commands.AvailableCommands;
    public CommandMetadata CommandMetadata(string name) => _commands.CommandMetadata(name);

    public IReadOnlyList<string> DescribeCommand(string name) => _commands.DescribeCommand(name);

    public CommandResult Execute(string command)
    {
        ObjectDisposedException.ThrowIf(_ended, this);
        using var identity = OperationIdentity.Enter(Job.UserProfile ?? Job.Key.User, Job.Key, Job.AuthSessionId);
        if (Job.Type == JobType.Interactive && _system.Jobs.GetRequired(Job.Key).Status == JobStatus.Held)
            return CommandResult.Error("CPF1312: This group job is suspended.");
        using var accounting = _runtime.EnterCommand();
        using var budget = JobExecutionBudget.Enter(Job);
        var request = Guid.NewGuid().ToString("N");
        _system.DurableEvents.Append("command.started", new { request });
        var outcome = "Interrupted";
        try
        {
            if (Job.AuthSessionId is { } session && !_system.Security.IsSessionActive(session, Job.UserProfile!))
            {
                outcome = "Denied";
                return CommandResult.Error("CPF9802: Authentication session is expired or revoked.");
            }
            var profile = _system.Security.Profiles.TryGet(Job.UserProfile!);
            if (profile is null || profile.Status != ProfileStatus.Enabled ||
                profile.PasswordExpires is { } expires && expires <= DateTimeOffset.UtcNow)
            {
                outcome = "Denied";
                return CommandResult.Error("IPC0101: Session profile is unavailable or requires a password change.");
            }
            using var locks = new Ipc.Services.Work.JobLockStore(_system.Connections).EnterCommand(Job.Key, CancellationToken);
            var result = _commands.Execute(command);
            outcome = result.Outcome.ToString();
            if (result.IsError && result.Message?.Contains("CPF9802", StringComparison.Ordinal) == true)
                _system.DurableEvents.Append("security.authority.denied", new { request });
            return result;
        }
        finally { _system.DurableEvents.Append("command.finished", new { request, outcome }); }
    }

    public void End(JobCompletion completion, string message, JobExecutionState? outcome = null)
    {
        if (_ended) return;
        using var identity = OperationIdentity.Enter(Job.UserProfile ?? Job.Key.User, Job.Key);
        if (completion == JobCompletion.Abnormal && CancellationToken.IsCancellationRequested)
        { outcome ??= JobExecutionState.Cancelled; message = _runtime.CancellationReason ?? message; }
        _system.Jobs.CompleteHostedSession(Job.Key, completion, message, outcome);
        _runtime.Dispose();
        _ended = true;
    }

    public void Dispose() => End(JobCompletion.Abnormal, "Execution connection ended without signoff.");
}
