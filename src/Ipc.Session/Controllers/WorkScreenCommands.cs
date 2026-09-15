using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterWorkScreenCommands()
    {
        foreach (var name in new[] { "WRKOBJ", "WRKLIB", "DSPLIB", "DSPOBJD", "DSPFD", "DSPFFD", "DSPPGM", "WRKSYSVAL", "WRKUSRPRF", "DSPUSRPRF", "WRKSBS", "WRKJOBQ", "WRKOUTQ", "WRKMSGQ", "WRKSBMJOB", "DSPJOBLOG", "SLTCMD", "WRKSYSSTS" })
            _catalog.Register(name, ExecuteWorkScreen);
    }
    private static CommandResult WorkResult(string title, string refresh, IEnumerable<WorkRow> rows)
    {
        var items = rows.Take(4001).ToArray();
        if (items.Length > 4000) throw new CpfException("IPC0133", "Narrow the filter; work lists support at most 4000 rows.");
        return new() { Message = title, Listing = items.Select(r => r.Text).ToArray(), WorkList = new(title, items, refresh) };
    }
    private IReadOnlyList<ObjectDescriptor> FindWorkObjects(string value, string type)
    {
        var parts = value.ToUpperInvariant().Split('/'); if (parts.Length > 2) throw new CpfException("IPC0003", "Invalid object filter.");
        var library = parts.Length == 2 ? parts[0] : "*LIBL"; var name = parts[^1];
        if (type != "*ALL" && !ObjectType.IsKnown(type)) throw new CpfException("IPC0003", "Unknown object type.");
        if (name is not ("*ALL" or "*") && !ObjectName.IsValid(name.EndsWith('*') ? name[..^1] : name)) throw new CpfException("IPC0003", "Use a name, prefix*, or *ALL.");
        var libraries = (library == "*ALL" ? _system.Objects.ListLibraries() : _system.SearchLibraries(_job, library)).ToArray();
        if (libraries.Length > 4000) throw new CpfException("IPC0133", "Narrow the library filter.");
        var rows = new List<ObjectDescriptor>();
        foreach (var lib in libraries)
        {
            try { rows.AddRange(new Ipc.Services.Sqlite.SqliteObjectStore(_system.Connections).FindSummaries(lib, name is "*ALL" or "*" ? null : name, type == "*ALL" ? null : type, 4001 - rows.Count)); }
            catch (CpfException error) when (error.MessageId == "CPF9802" && library == "*ALL") { }
            if (rows.Count > 4000) throw new CpfException("IPC0133", "Narrow the object filter; work lists support at most 4000 rows.");
        }
        return rows.OrderBy(r => r.Library, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.ObjectType, StringComparer.Ordinal).ToArray();
    }
    private static WorkRow ObjectWorkRow(ObjectDescriptor item) => new(item.Key + " " + item.ObjectType,
        $"{item.Library,-10} {item.Name,-10} {item.ObjectType,-10} {item.Owner,-10} {item.Description}", new[] {
            new WorkAction("5", "Display", $"DSPOBJD OBJ({item.Key}) OBJTYPE({item.ObjectType})"),
            item.ObjectType == ObjectType.Library
                ? new WorkAction("4", "Delete library and contents", $"DLTLIB LIB({item.Name})", Confirm: true)
                : new WorkAction("4", "Delete", $"DLTOBJ OBJ({item.Key}) OBJTYPE({item.ObjectType})", Confirm: true),
        });
    private static WorkRow JobWorkRow(Job job)
    {
        var actions = new List<WorkAction> {
            new("5", "Display", $"WRKJOB JOB({job.Key})"), new("2", "Change", $"CHGJOB JOB({job.Key})", Prompt: true),
            new("4", "End", $"ENDJOB JOB({job.Key}) OPTION(*IMMED)", Confirm: true), new("10", "Job log", $"DSPJOBLOG JOB({job.Key})"),
        };
        if (job.Type == JobType.Batch && job.Status == JobStatus.JobQueue) actions.Add(new("3", "Hold", $"HLDJOB JOB({job.Key})"));
        if (job.Type == JobType.Batch && job.Status == JobStatus.Held) actions.Add(new("6", "Release", $"RLSJOB JOB({job.Key})"));
        return new(job.Key.ToString(), $"{job.Key,-32} {job.Type,-13} {job.Status,-12} {job.Subsystem}", actions);
    }
    private static bool MatchesName(string name, string filter)
    {
        if (filter == "*ALL") return true;
        var prefix = filter.EndsWith('*'); var value = prefix ? filter[..^1] : filter;
        if (!ObjectName.IsValid(value)) throw new CpfException("IPC0003", "Use a name, prefix*, or *ALL.");
        return prefix ? name.StartsWith(value, StringComparison.Ordinal) : name == value;
    }
    private CommandResult ExecuteWorkScreen(CommandCall call)
    {
        string Value(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback).ToUpperInvariant();
        var operation = call.Name.ToUpperInvariant(); var refresh = call.ToString();
        if (operation == "SLTCMD")
        {
            var filter = Value("CMD", "*ALL");
            if (filter.Length > 12 || filter.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('*' or '_')))
                throw new CpfException("IPC0003", "Use a command name or a pattern containing *.");
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(filter == "*ALL" ? "*" : filter).Replace("\\*", ".*") + "$";
            return WorkResult("Select a command", refresh, AvailableCommands.Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, pattern))
                .Select(name => new WorkRow(name, name, new[] { new WorkAction("1", "Prompt", name, Prompt: true) })));
        }
        if (operation == "WRKSYSSTS")
        {
            var jobs = _system.Jobs.List(); var subsystems = _system.Subsystems.StatusAll();
            return WorkResult("iSeriesPC runtime status", refresh, new[] {
                new WorkRow("time", "Snapshot UTC: " + DateTimeOffset.UtcNow.ToString("O"), Array.Empty<WorkAction>()),
                new WorkRow("jobs", "Jobs: " + jobs.Count + "   Active: " + jobs.Count(j => j.Status == JobStatus.Active) + "   Held: " + jobs.Count(j => j.Status == JobStatus.Held), new[] { new WorkAction("5", "Display", "WRKACTJOB") }),
                new WorkRow("subsystems", "Active subsystems: " + subsystems.Count(s => s.Active), new[] { new WorkAction("5", "Display", "WRKSBS") }),
            });
        }
        if (operation is "WRKOBJ" or "WRKLIB" or "DSPLIB")
        {
            var libraryView = operation == "WRKLIB"; var displayLibrary = operation == "DSPLIB";
            var filter = libraryView ? "QSYS/" + Value("LIB", "*ALL") : displayLibrary ? Value("LIB", _job?.CurrentLibrary ?? "QGPL") + "/*ALL" : Value("OBJ", "*LIBL/*ALL");
            var type = libraryView ? ObjectType.Library : Value("OBJTYPE", "*ALL");
            return WorkResult(libraryView ? "Work with libraries" : "Work with objects", refresh, FindWorkObjects(filter, type).Select(ObjectWorkRow));
        }
        if (operation is "DSPOBJD" or "DSPFD" or "DSPFFD" or "DSPPGM")
        {
            var type = operation is "DSPFD" or "DSPFFD" ? ObjectType.File : operation == "DSPPGM" ? ObjectType.Program : Value("OBJTYPE");
            var key = Value(operation is "DSPFD" or "DSPFFD" ? "FILE" : operation == "DSPPGM" ? "PGM" : "OBJ");
            var matches = FindWorkObjects(key, type);
            if (matches.Count == 0) throw new CpfException("CPF9801", "Object not found.");
            var lines = new List<string>();
            foreach (var item in matches)
            {
                lines.AddRange(new[] { $"Object: {item.Key} {item.ObjectType}", "Owner: " + item.Owner, "Attribute: " + item.Attribute,
                    "Text: " + item.Description, "Created: " + item.Created.ToString("O"), "Changed: " + item.Changed.ToString("O"), "CCSID: " + item.Ccsid });
                if (operation is "DSPFD" or "DSPFFD")
                {
                    var definition = _files.GetDefinition(item.Library, item.Name);
                    if (definition is not null)
                    {
                        lines.Add("Members: " + string.Join(' ', _files.ListMembers(item.Library, item.Name)));
                        foreach (var format in definition.Formats)
                        {
                            lines.Add("Record format: " + format.Name);
                            if (operation == "DSPFFD") lines.AddRange(format.Fields.Select(f => f.Name + "  " + f.Type + "  length=" + f.Length + " decimals=" + f.Decimals));
                        }
                    }
                }
            }
            return new() { Message = operation + " object details", Listing = lines };
        }
        if (operation == "WRKSYSVAL")
        {
            var filter = Value("SYSVAL", "*ALL");
            return WorkResult("Work with system values", refresh, _system.SystemValues.All.Where(v => MatchesName(v.Name, filter)).Select(v =>
                new WorkRow(v.Name, $"{v.Name,-12} {v.Value}", new[] { new WorkAction("2", "Change", "CHGSYSVAL SYSVAL(" + v.Name + ")", Prompt: true) })));
        }
        if (operation is "WRKUSRPRF" or "DSPUSRPRF")
        {
            if (operation == "DSPUSRPRF")
            {
                var profile = _system.Security.Profiles.Get(Value("USRPRF", _job?.UserProfile ?? "QUSER"));
                return new() { Message = "User profile " + profile.Name, Listing = new[] { "Class: " + profile.UserClass, "Status: " + profile.Status,
                    "Special authorities: " + profile.SpecialAuthorities, "Group: " + profile.GroupProfile, "Initial program: " + profile.InitialProgram,
                    "Initial menu: " + profile.InitialMenu, "Current library: " + profile.InitialCurrentLibrary, "CCSID: " + profile.Ccsid, "Text: " + profile.Description } };
            }
            var filter = Value("USRPRF", "*ALL");
            return WorkResult("Work with user profiles", refresh, _system.Security.Profiles.ListAll().Where(p => MatchesName(p.Name, filter))
                .Select(p => new WorkRow(p.Name, $"{p.Name,-10} {p.UserClass,-24} {p.Status,-16} {p.Description}", new[] { new WorkAction("5", "Display", "DSPUSRPRF USRPRF(" + p.Name + ")"), new WorkAction("2", "Change", "CHGUSRPRF USRPRF(" + p.Name + ")", Prompt: true), new WorkAction("4", "Delete", "DLTUSRPRF USRPRF(" + p.Name + ")", Confirm: true) })));
        }
        if (operation == "DSPJOBLOG")
        {
            var job = ResolveJob(Value("JOB", "*"));
            var events = _system.Jobs.GetLog(job, 4000).Select(e => (e.Time, Text: $"{e.Sequence} {e.Time:O} {e.MessageType} {e.MessageId}: {e.Text}"));
            var messages = new Ipc.Services.Messages.MessageQueueStore(_system.Connections).ListJobMessages(job, limit: 4000)
                .Select(m => (Time: m.Sent, Text: $"Message {m.Key:X8} {m.Sent:O} {m.Kind} {m.MessageId}: {DisplayEncodedText(m.Data)}"));
            return new() { Message = "Job log " + job, Listing = events.Concat(messages).OrderBy(e => e.Time).Take(4000).Select(e => e.Text).ToArray() };
        }
        if (operation == "WRKSBMJOB")
        {
            var user = Value("USER", _job?.UserProfile ?? "*ALL");
            return WorkResult("Work with submitted jobs", refresh, _system.Jobs.List().Where(j => j.Type == JobType.Batch && (user == "*ALL" || j.Key.User == user)).Select(JobWorkRow));
        }
        if (operation == "WRKSBS")
            return WorkResult("Work with subsystems", refresh, _system.Subsystems.StatusAll().Select(s => new WorkRow(s.Name,
                $"{s.Name,-21} {(s.Active ? "Active" : "Inactive"),-10} Jobs {s.ActiveJobs}/{s.MaxActiveJobs}", new[] {
                    new WorkAction("5", "Display", "WRKACTJOB SBS(" + s.Name + ")"),
                    new WorkAction(s.Active ? "4" : "1", s.Active ? "End" : "Start", (s.Active ? "ENDSBS SBS(" : "STRSBS SBSD(") + s.Name + ")", Confirm: s.Active) })));
        if (operation is "WRKJOBQ" or "WRKOUTQ" or "WRKMSGQ")
        {
            var type = operation == "WRKJOBQ" ? ObjectType.JobQueue : operation == "WRKOUTQ" ? ObjectType.OutputQueue : ObjectType.MessageQueue;
            var parameter = operation == "WRKJOBQ" ? "JOBQ" : operation == "WRKOUTQ" ? "OUTQ" : "MSGQ";
            var filter = Value(parameter, "*ALL/*ALL");
            return WorkResult("Work with " + parameter + " definitions", refresh, FindWorkObjects(filter, type).Select(item =>
            {
                var row = ObjectWorkRow(item);
                if (operation == "WRKMSGQ") return row with { Actions = row.Actions.Concat(new[] { new WorkAction("8", "Messages", "DSPMSG MSGQ(" + item.Key + ")") }).ToArray() };
                if (operation != "WRKJOBQ") return row;
                return row with { Actions = row.Actions.Concat(new[] { new WorkAction("3", "Hold", "HLDJOBQ JOBQ(" + item.Key + ")"), new WorkAction("6", "Release", "RLSJOBQ JOBQ(" + item.Key + ")") }).ToArray() };
            }));
        }
        throw new CpfException("IPC0001", "Work screen command is unavailable.");
    }
    private static string DisplayEncodedText(ProgramBuffer data)
    {
        try { return data.ToText(); }
        catch (CpfException) { return "CCSID " + data.Ccsid + " bytes " + Convert.ToHexString(data.ToArray()); }
    }
}
