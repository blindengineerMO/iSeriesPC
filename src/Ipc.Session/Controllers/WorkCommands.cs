using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterWorkCommands()
    {
        _catalog.Register("CRTJOBQ", ExecuteWork);
        _catalog.Register("HLDJOBQ", ExecuteWork);
        _catalog.Register("RLSJOBQ", ExecuteWork);
        _catalog.Register("ADDJOBQE", ExecuteWork);
        _catalog.Register("CHGJOBQE", ExecuteWork);
        _catalog.Register("RMVJOBQE", ExecuteWork);
        _catalog.Register("CRTSBSD", ExecuteWork);
        _catalog.Register("STRSBS", ExecuteWork);
        _catalog.Register("ENDSBS", ExecuteWork);
        _catalog.Register("ADDRTGE", ExecuteWork);
        _catalog.Register("CHGRTGE", ExecuteWork);
        _catalog.Register("RMVRTGE", ExecuteWork);
        _catalog.Register("CRTCLS", ExecuteWork);
        _catalog.Register("CHGCLS", ExecuteWork);
        _catalog.Register("WRKCLS", ExecuteWork);
        _catalog.Register("CRTJOBD", ExecuteWork);
        _catalog.Register("CHGJOBD", ExecuteWork);
        _catalog.Register("WRKJOBD", ExecuteWork);
    }

    private CommandResult ExecuteWork(CommandCall call)
    {
        string Value(string name, string fallback = "") => CommandParser.Unquote(call.GetOption(name) ?? fallback).ToUpperInvariant();
        var queues = new JobQueueStore(_system.Connections);
        var operation = call.Name.ToUpperInvariant();
        try
        {
            if (operation is "CRTCLS" or "CHGCLS")
            {
                var (library, name) = ResolveWorkObject(Value("CLS"), ObjectType.Class, operation == "CRTCLS");
                var old = operation == "CHGCLS" ? _system.WorkDefinitions.Class(library, name) ?? throw new CpfException("CPF9801", "Class not found.") : null;
                _system.WorkDefinitions.PutClass(new(library, name, ParseWorkInteger(call, "RUNPTY", old?.RunPriority ?? 50),
                    ParseWorkInteger(call, "TIMESLICE", old?.TimeSliceMilliseconds ?? 2000)), replace: old is not null);
            }
            else if (operation is "CRTJOBD" or "CHGJOBD")
            {
                var (library, name) = ResolveWorkObject(Value("JOBD"), ObjectType.JobDescription, operation == "CRTJOBD");
                var old = operation == "CHGJOBD" ? _system.WorkDefinitions.JobDescription(library, name) ?? throw new CpfException("CPF9801", "Job description not found.") : null;
                var (qlib, qname) = ResolveWorkObject(Value("JOBQ", old?.JobQueue ?? "QUSRSYS/QBATCH"), ObjectType.JobQueue);
                var user = Value("USER", old?.RunAs ?? "*CURRENT");
                var current = Value("CURLIB", old?.CurrentLibrary ?? "*CURRENT");
                var list = Value("INLLIBL", old is null ? "*SYSVAL" : "*SAME");
                var mode = list switch { "*CURRENT" => "CURRENT", "*SYSVAL" => "SYSVAL", "*SAME" => old?.LibraryMode ?? "CURRENT", _ => "EXPLICIT" };
                var libraries = list == "*SAME" ? old?.Libraries : list is "*NONE" or "*CURRENT" or "*SYSVAL" ? Array.Empty<string>() :
                    call.Split("INLLIBL").Select(v => CommandParser.Unquote(v).ToUpperInvariant()).ToArray();
                _system.WorkDefinitions.PutJobDescription(new(library, name, qlib + "/" + qname,
                    ParseWorkInteger(call, "JOBPTY", old?.Priority ?? 9), user == "*CURRENT" ? null : user,
                    CommandParser.Unquote(call.GetOption("RTGDTA") ?? old?.RoutingData ?? "QCMDB"),
                    current == "*CURRENT" ? null : current, mode, libraries), replace: old is not null);
            }
            else if (operation is "WRKCLS" or "WRKJOBD")
            {
                var type = operation == "WRKCLS" ? ObjectType.Class : ObjectType.JobDescription;
                var filter = Value(operation == "WRKCLS" ? "CLS" : "JOBD", "*ALL");
                var rows = _system.Objects.ListLibraries().SelectMany(library => _system.Objects.Find(library, null, type, null))
                    .Where(d => filter == "*ALL" || d.Name == filter || d.Key.ToString() == filter).Select(d =>
                    {
                        if (type == ObjectType.Class)
                        {
                            var c = _system.WorkDefinitions.Class(d.Library, d.Name);
                            return c is null ? $"{d.Key}: definition missing" : $"{d.Key}: RUNPTY({c.RunPriority}) TIMESLICE({c.TimeSliceMilliseconds})";
                        }
                        var j = _system.WorkDefinitions.JobDescription(d.Library, d.Name);
                        return j is null ? $"{d.Key}: definition missing" : $"{d.Key}: JOBQ({j.JobQueue}) JOBPTY({j.Priority}) USER({j.RunAs ?? "*CURRENT"}) RTGDTA({j.RoutingData})";
                    }).ToArray();
                return new CommandResult { Listing = rows, Message = $"{rows.Length} work definition(s)." };
            }
            else if (operation is "CRTSBSD" or "STRSBS" or "ENDSBS")
            {
                var subsystem = Value(operation == "ENDSBS" ? "SBS" : "SBSD");
                if (operation == "CRTSBSD")
                {
                    var (library, name) = ResolveWorkObject(subsystem, ObjectType.SubsystemDescription, creating: true);
                    if (_system.Subsystems.Exists(library + "/" + name)) throw new CpfException("IPC0120", "Subsystem already exists.");
                    _system.Subsystems.Ensure(library + "/" + name, CommandParser.Unquote(call.GetOption("TEXT")), ParseWorkInteger(call, "MAXJOBS", 1));
                }
                else
                {
                    var (library, name) = ResolveWorkObject(subsystem, ObjectType.SubsystemDescription);
                    if (operation == "STRSBS") _system.Subsystems.Start(library + "/" + name);
                    else _system.Subsystems.End(library + "/" + name);
                }
            }
            else if (operation is "ADDRTGE" or "CHGRTGE" or "RMVRTGE")
            {
                var (library, name) = ResolveWorkObject(Value("SBSD"), ObjectType.SubsystemDescription);
                var routing = new RoutingTable(_system.Connections);
                var sequence = ParseWorkInteger(call, "SEQNBR", 9999);
                if (operation == "RMVRTGE") routing.RemoveEntry(library + "/" + name, sequence);
                else
                {
                    var old = operation == "CHGRTGE" ? routing.Entries(library + "/" + name).SingleOrDefault(e => e.Sequence == sequence)
                        ?? throw new CpfException("CPF9801", "Routing entry not found.") : null;
                    var cmp = call.GetOption("CMPVAL") is null && old is not null ? new[] { old.CompareValue, old.StartPosition.ToString() }
                        : call.Split("CMPVAL").Select(CommandParser.Unquote).ToArray();
                    if (cmp.Length is < 1 or > 2) throw new CpfException("IPC0120", "CMPVAL requires a value and optional starting position.");
                    var position = cmp.Length == 2 && int.TryParse(cmp[1], out var offset) ? offset : cmp.Length == 1 ? 1 : throw new CpfException("IPC0120", "Invalid comparison position.");
                    var (classLib, className) = ResolveWorkObject(Value("CLS", old?.JobClass ?? "QSYS/QBATCH"), ObjectType.Class);
                    var program = Value("PGM", old?.Program ?? "QSYS/QCMD");
                    if (program != "QSYS/QCMD") { var p = ResolveWorkObject(program, ObjectType.Program); program = p.Library + "/" + p.Name; }
                    routing.EnsureEntry(library + "/" + name, sequence, cmp[0], program,
                        compareMode: Value("CMPMODE", call.GetOption("CMPVAL") is null && old is not null ? old.CompareMode : cmp.Length == 2 ? "*SECTION" : "*EQ"), startPosition: position, jobClass: classLib + "/" + className, replace: old is not null);
                }
            }
            else
            {
                var (library, name) = ResolveWorkObject(Value("JOBQ"), ObjectType.JobQueue, operation == "CRTJOBQ");
                var queue = library + "/" + name;
                if (operation == "CRTJOBQ")
                {
                    if (queues.Exists(queue)) throw new CpfException("IPC0120", "Job queue already exists.");
                    queues.Ensure(queue, description: CommandParser.Unquote(call.GetOption("TEXT")));
                }
                else if (operation is "HLDJOBQ" or "RLSJOBQ") queues.SetHeld(queue, operation == "HLDJOBQ");
                else
                {
                    var (slib, sname) = ResolveWorkObject(Value("SBSD"), ObjectType.SubsystemDescription);
                    if (operation == "RMVJOBQE") queues.Detach(queue, slib + "/" + sname);
                    else
                    {
                        var old = operation == "CHGJOBQE" ? queues.Entries(slib + "/" + sname).SingleOrDefault(e => e.Queue == queue)
                            ?? throw new CpfException("CPF9801", "Queue entry not found.") : null;
                        queues.Attach(queue, slib + "/" + sname, ParseWorkInteger(call, "SEQNBR", old?.Sequence ?? 9999),
                            Value("MAXACT") == "*NOMAX" ? 32000 : ParseWorkInteger(call, "MAXACT", old?.MaximumActive ?? 32000), replace: old is not null);
                    }
                }
            }
            return CommandResult.Ok(operation + " completed.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { return CommandResult.Error("IPC0120: Work definition has a missing dependency, duplicate identity or invalid attribute."); }
    }

    private CommandResult ExecuteCreateExternalProgram(CommandCall call)
    {
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("PGM")).ToUpperInvariant(), ObjectType.Program, creating: true);
        _system.ExternalPrograms.Register(library, name, CommandParser.Unquote(call.GetOption("EXEC")),
            call.Split("ARGS").Select(CommandParser.Unquote), call.Split("DEPENDS").Select(CommandParser.Unquote), ParseWorkInteger(call, "TIMEOUT", 60), ParseWorkInteger(call, "PROTOCOL", 1));
        return CommandResult.Ok($"External program {library}/{name} registered.");
    }

    private (string Library, string Name) ResolveWorkObject(string value, string type, bool creating = false)
    {
        var (library, name) = SplitQualified(value, creating ? "*CURLIB" : "*LIBL");
        if (library == "*CURLIB" && creating) library = SearchLibraries("*CURLIB").First();
        else if (library is "*LIBL" or "*CURLIB") library = SearchLibraries(library).FirstOrDefault(l => _system.Objects.Exists(l, name, type))
            ?? throw new CpfException("CPF9801", "Work object not found in the job library list.");
        return (library, name);
    }
    private static int ParseWorkInteger(CommandCall call, string parameter, int fallback) =>
        call.GetOption(parameter) is not { } value ? fallback : int.TryParse(CommandParser.Unquote(value), out var number)
            ? number : throw new CpfException("IPC0003", parameter + " must be an integer.");
}
