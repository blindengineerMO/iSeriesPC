using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterJobCommands()
    {
        foreach (var name in new[] { "ENDJOB", "HLDJOB", "RLSJOB", "WRKJOB", "WRKACTJOB", "CHGJOB", "RCLACTGRP" }) _catalog.Register(name, ExecuteJobControl);
    }
    private CommandResult ExecuteJobControl(CommandCall call)
    {
        var operation = call.Name.ToUpperInvariant(); var info = new JobInformationStore(_system.Connections);
        if (operation == "RCLACTGRP")
        {
            var groups = _job is null ? null : _system.JobRuntime.Groups(_job.Key);
            if (groups is null) throw new CpfException("IPC0124", "An executing job is required.");
            groups.Reclaim(CommandParser.Unquote(call.GetOption("ACTGRP") ?? "*ELIGIBLE"));
            return CommandResult.Ok("Activation groups reclaimed.");
        }
        if (operation == "WRKACTJOB")
        {
            var subsystem = CommandParser.Unquote(call.GetOption("SBS") ?? "*ALL").ToUpperInvariant();
            return WorkResult("Work with active jobs", call.ToString(), _system.Jobs.List(subsystem: subsystem == "*ALL" ? null : subsystem)
                .Where(j => j.ExecutionState == JobExecutionState.Running).Select(JobWorkRow));
        }
        var key = ResolveJob(CommandParser.Unquote(call.GetOption("JOB") ?? "*"));
        switch (operation)
        {
            case "ENDJOB":
                if (CommandParser.Unquote(call.GetOption("OPTION") ?? "*IMMED").ToUpperInvariant() != "*IMMED")
                    throw new CpfException("IPC0123", "ENDJOB currently supports OPTION(*IMMED), with cooperative interpreter cancellation.");
                _system.Jobs.RequestEnd(key); break;
            case "HLDJOB": _system.Jobs.Hold(key); break;
            case "RLSJOB": _system.Jobs.Release(key); break;
            case "CHGJOB":
                string? Queue(string parameter, string type)
                {
                    if (call.GetOption(parameter) is not { } value) return null;
                    var (library, name) = ResolveWorkObject(CommandParser.Unquote(value).ToUpperInvariant(), type);
                    return library + "/" + name;
                }
                var inquiry=CommandParser.Unquote(call.GetOption("INQMSGRPY")??"*SAME").ToUpperInvariant();
                if(inquiry!="*SAME"&&!Ipc.Services.Messages.InquiryReplyPolicy.Valid(inquiry))throw new CpfException("IPC0003","Invalid INQMSGRPY.");
                if(call.GetOption("OUTQ") is not null || call.GetOption("MSGQ") is not null)info.SetBindings(key, Queue("OUTQ", ObjectType.OutputQueue), Queue("MSGQ", ObjectType.MessageQueue));
                new Ipc.Services.Messages.InquiryReplyPolicy(_system.Connections).Set(key,inquiry);break;
            case "WRKJOB":
                var job = _system.Jobs.GetRequired(key); var bindings = info.Bindings(key);
                return new CommandResult { Message = DescribeJob(job, info.Accounting(key)), Listing =
                    new[] { DescribeJob(job, info.Accounting(key)), $"Output queue: {bindings.OutputQueue}; message queue: {bindings.MessageQueue}", $"Inquiry message reply: {job.InquiryMessageReply}" }
                    .Concat(info.ActivationGroups(key).Select(g => $"Activation group {g.Name}: {g.ActiveCalls} active call(s)"))
                    .Concat(info.Processes(key).Select(p => FormattableString.Invariant($"Process {p.ProcessId}: {p.State}, CPU sample {p.CpuNanoseconds / 1000000000.0:F6}s, peak sampled threads {p.PeakThreads}, exit {p.ExitCode}")))
                    .Concat(info.Threads(key).Select(t => $"Thread {t.ManagedThread} (Linux {t.NativeThread}): {t.State}, CPU {t.CpuNanoseconds / 1000000000.0:F6}s"))
                    .ToArray() };
        }
        return CommandResult.Ok(operation + " accepted for " + key + ".");
    }
    private static IReadOnlyDictionary<string, string> ActivationAttributes(CommandCall call)
    {
        var group = CommandParser.Unquote(call.GetOption("ACTGRP") ?? "*CALLER").ToUpperInvariant();
        if (group is not ("*NEW" or "*CALLER") && !ObjectName.IsValid(group)) throw new CpfException("IPC0124", "Invalid activation group.");
        var attributes = new Dictionary<string, string> { ["ipc.program.activationGroup"] = group };
        if (call.GetOption("ALWUPD") is { } option)
        {
            var allow = CommandParser.Unquote(option).ToUpperInvariant();
            if (allow is not ("*YES" or "*NO")) throw new CpfException("IPC0003", "ALWUPD must be *YES or *NO.");
            attributes["ipc.program.allowUpdate"] = allow;
        }
        if (call.GetOption("ALWLIBUPD") is { } libraryOption)
        {
            var allow = CommandParser.Unquote(libraryOption).ToUpperInvariant();
            if (allow is not ("*YES" or "*NO")) throw new CpfException("IPC0003", "ALWLIBUPD must be *YES or *NO.");
            attributes["ipc.program.allowLibraryUpdate"] = allow;
        }
        return attributes;
    }
    private JobKey ResolveJob(string value)
    {
        if (value is "*" or "*CURRENT") return _job?.Key ?? throw new CpfException("CPF1241", "A current job is required.");
        var parts = value.ToUpperInvariant().Split('/');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            throw new CpfException("IPC0123", "JOB must be * or number/name/user.");
        var key = new JobKey(number, parts[1], parts[2]); _ = _system.Jobs.GetRequired(key); return key;
    }
    private static string DescribeJob(Job job, JobAccounting accounting) => FormattableString.Invariant(
        $"{job.Key}: {job.Type}, {job.Status}/{job.ExecutionState}; CPU {accounting.CpuNanoseconds / 1000000000.0:F6}s + native sample {accounting.NativeCpuNanoseconds / 1000000000.0:F6}s; execution {accounting.ElapsedMilliseconds}ms; managed threads {accounting.ActiveThreads}; processes {accounting.ActiveProcesses}");
}
