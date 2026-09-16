using Ipc.Services.Events;
using System.Globalization;
using System.Text;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private const string ProgramType = "*PGM";
    private const string RpgAttribute = "RPG";
    private const string LibraryUnknown = "*LIBL";
    private const string FirstMember = "*FIRST";

    private readonly IpcSystem _system;
    private readonly CommandCatalog _catalog = new(builtinContracts: true);
    private readonly ClCompiler _compiler;
    private readonly StringBuilder _messages = new();
    private readonly ClInterpreter _interpreter;
    private readonly SqliteFileStore _files;
    private readonly Job? _job;
    private readonly CancellationToken _cancellationToken;

    public CommandService(IpcSystem system, Job? job = null, CancellationToken cancellationToken = default)
    {
        _system = system;
        _job = job;
        _cancellationToken = cancellationToken;
        _files = new SqliteFileStore(system.Connections, system.Objects, job?.UserProfile ?? "QSECOFR");
        _compiler = new ClCompiler(DescribeClDatabaseFile);
        _interpreter = new ClInterpreter(LoadProgram, RunCommand, message => _messages.AppendLine(message), cancellationToken,
            program => EnterProgram(program), () => _job?.Ccsid ?? _system.Config.Ccsid, OpenClDatabaseFile, CallFromCl, RunClContextCommand, SendClProgramMessage, ReportClFailure,
            error => { if (error.ExceptionReference is { } reference) new Ipc.Services.Messages.MessageQueueStore(_system.Connections).MarkExceptionHandled(reference); }, ClLocalDataArea);
        RegisterCommandObjectCommands();
        _catalog.Register("GO", ExecuteGo);
        _catalog.Register("SIGNOFF", ExecuteSignOff);
        _catalog.Register("DSPJOB", ExecuteDisplayJob);
        _catalog.Register("CRTCLPGM", ExecuteCreateClProgram);
        _catalog.Register("CRTBNDRPG", ExecuteCreateRpgProgram);
        _catalog.Register("CALL", ExecuteCall);
        _catalog.Register("CRTSRCPF", ExecuteCreateSourceFile);
        _catalog.Register("CRTPF", ExecuteCreatePhysicalFile);
        _catalog.Register("CHGPF", ExecuteChangePhysicalFile);
        _catalog.Register("CRTLF", ExecuteCreateLogicalFile);
        _catalog.Register("ADDPFM", ExecuteAddMember);
        _catalog.Register("ADDLFM", ExecuteAddMember);
        _catalog.Register("ADDSRCPFM", ExecuteAddSourceRecord);
        _catalog.Register("RMVM", ExecuteRemoveMember);
        _catalog.Register("CLRPFM", ExecuteClearMember);
        _catalog.Register("DSPPFM", ExecuteDisplayMember);
        _catalog.Register("CPYF", ExecuteCopyFile);
        _catalog.Register("DLTF", ExecuteDeleteFile);
        _catalog.Register("SBMJOB", ExecuteSubmitJob);
        _catalog.Register("CRTLIB", ExecuteObjectManagement);
        _catalog.Register("DLTLIB", ExecuteObjectManagement);
        _catalog.Register("CRTDTADCT", ExecuteObjectManagement);
        _catalog.Register("RNMOBJ", ExecuteObjectManagement);
        _catalog.Register("MOVOBJ", ExecuteObjectManagement);
        _catalog.Register("CRTDUPOBJ", ExecuteObjectManagement);
        _catalog.Register("DLTOBJ", ExecuteObjectManagement);
        _catalog.Register("ADDLIBLE", ExecuteLibraryList);
        _catalog.Register("RMVLIBLE", ExecuteLibraryList);
        _catalog.Register("CHGLIBL", ExecuteLibraryList);
        _catalog.Register("CHGCURLIB", ExecuteLibraryList);
        _catalog.Register("CHGSYSVAL", ExecuteSystemValues);
        _catalog.Register("WRKSYSVAL", ExecuteSystemValues);
        _catalog.Register("ADDEIMMAP", ExecuteEnterpriseIdentity);
        _catalog.Register("RMVEIMMAP", ExecuteEnterpriseIdentity);
        _catalog.Register("DSPEIMMAP", ExecuteEnterpriseIdentity);
        _catalog.Register("RFREIMMAP", ExecuteEnterpriseIdentity);
        RegisterCertificateCommands();
        RegisterWorkCommands();
        RegisterJobCommands();
        RegisterLockCommands();
        RegisterEnvironmentCommands();
        RegisterDisplayCommands();
        RegisterGroupCommands();
        RegisterWorkScreenCommands();
        RegisterProfileCommands();
        _catalog.Register("CRTEXTPGM", ExecuteCreateExternalProgram);
    }

    public IReadOnlyList<string> DescribeCommand(string name)
    {
        var key = ResolveCommandObject(name);
        return new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value).Describe(key.ToString());
    }
    public IReadOnlyList<string> AvailableCommands => SearchLibraries("*LIBL").SelectMany(library =>
        new Ipc.Services.Sqlite.SqliteObjectStore(_system.Connections).FindSummaries(library, null, ObjectType.Command).Select(o => o.Name))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public CommandMetadata CommandMetadata(string name)
    {
        var key = ResolveCommandObject(name);
        var definition = new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
        return definition.PromptMetadata(name.Contains('/') ? key.ToString() : key.Name.Value);
    }
    public CommandResult Execute(string line)
    {
        using var identity = EnterJobIdentity(); _messages.Clear();
        try { return DispatchCommand(CommandParser.Parse(line)); }
        catch (ClParseException error) { return CommandResult.Error("IPC0005: " + error.Message); }
    }
    public CommandResult RunCommand(CommandCall call)
    {
        using var identity = EnterJobIdentity(); if (_commandDepth == 0) _messages.Clear(); return DispatchCommand(call);
    }

    private IDisposable? EnterJobIdentity() => OperationIdentity.Current is null && _job is not null
        ? OperationIdentity.Enter(_job.UserProfile ?? _job.Key.User, _job.Key, _job.AuthSessionId) : null;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ObjectDescriptor> _loadedDescriptors = new();

    private IDisposable EnterProgram(object program)
    {
        if (!_loadedDescriptors.TryGetValue(program, out var descriptor)) throw new CpfException("IPC0110", "Program has no verified source snapshot.");
        _ = _system.Objects.GetRequired(descriptor.Library, descriptor.Name, descriptor.ObjectType);
        _system.ObjectSigning.RequireExecutable(descriptor);
        var identity = OperationIdentity.EnterProgram(descriptor);
        IDisposable? activation = null;
        try
        {
            activation = _job is null ? null : _system.JobRuntime.Groups(_job.Key)?.Enter(descriptor);
            var environment = _job is null ? null : _system.JobRuntime.Environment(_job.Key)?.EnterCall(descriptor.Name);
            return new ProgramCallScope(identity, activation, environment);
        }
        catch { activation?.Dispose(); identity.Dispose(); throw; }
    }

    private sealed class ProgramCallScope(IDisposable identity, IDisposable? activation, IDisposable? environment) : IDisposable
    {
        public void Dispose() { try { environment?.Dispose(); } finally { try { activation?.Dispose(); } finally { identity.Dispose(); } } }
    }

    private void RunRpg(RpgProgram program, IReadOnlyList<object?> parameters)
    {
        var host = BuildRpgHost();
        var groups = _job is null ? null : _system.JobRuntime.Groups(_job.Key);
        var interpreter = new RpgInterpreter(host);
        if (groups is null) { interpreter.Run(program, parameters); return; }
        var descriptor = _loadedDescriptors.GetValue(program, _ => throw new CpfException("IPC0110", "Missing RPG source snapshot."));
        var key = program.Library + "/" + program.Name;
        var fingerprint = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.Source!)));
        var state = groups.Resource(key, () => new RpgActivationState(fingerprint, new RpgRuntimeContext(program, host)));
        if (state.Fingerprint != fingerprint)
        {
            groups.RemoveResource(key);
            state = groups.Resource(key, () => new RpgActivationState(fingerprint, new RpgRuntimeContext(program, host)));
        }
        if (state.Active) throw new CpfException("IPC0124", "Recursive RPG calls require a new activation group.");
        state.Active = true;
        try { interpreter.Run(program, parameters, state.Context); }
        finally { state.Active = false; if (state.Context.Indicators[0]) groups.RemoveResource(key); }
        // LR ends this program's retained storage.
    }
    private sealed record RpgActivationState(string Fingerprint, RpgRuntimeContext Context) { public bool Active { get; set; } }

    private CommandResult ExecuteSystemValues(CommandCall call)
    {
        var name = CommandParser.Unquote(call.GetOption("SYSVAL") ?? "*ALL").ToUpperInvariant();
        if (call.Name.Equals("CHGSYSVAL", StringComparison.OrdinalIgnoreCase))
        {
            new Ipc.Services.Security.ServiceAuthorization(_system.Connections).RequireSpecial(Ipc.Core.Security.SpecialAuthority.SecurityAdministrator);
            _system.SetSystemValue(name, CommandParser.Unquote(call.GetOption("VALUE")));
            return CommandResult.Ok($"System value {name} changed.");
        }
        return new CommandResult
        {
            Outcome = CommandOutcome.Continue,
            Listing = _system.SystemValues.All.Where(v => name == "*ALL" || NamePattern.Matches(v.Name, name))
                .Select(v => $"{v.Name}  {v.Value}").ToArray(),
        };
    }

    private CommandResult ExecuteEnterpriseIdentity(CommandCall call)
    {
        var identities = _system.EnterpriseIdentities;
        var profile = CommandParser.Unquote(call.GetOption("PROFILE")).ToUpperInvariant();
        switch (call.Name.ToUpperInvariant())
        {
            case "ADDEIMMAP":
                if (_system.Config.Authentication.PamAccounts.Keys.Any(p => p.Equals(profile, StringComparison.OrdinalIgnoreCase)))
                    return CommandResult.Error("IPC0003: Remove the profile's direct PAM mapping before adding an enterprise mapping.");
                identities.Mappings.Add(new(profile, CommandParser.Unquote(call.GetOption("DIRECTORY")),
                    CommandParser.Unquote(call.GetOption("DN")), CommandParser.Unquote(call.GetOption("ENTRYID")),
                    CommandParser.Unquote(call.GetOption("PRINCIPAL")), CommandParser.Unquote(call.GetOption("ACCOUNT"))));
                return CommandResult.Ok($"EIM mapping stored; directory state: {identities.Refresh(profile)}.");
            case "RMVEIMMAP":
                identities.Mappings.Remove(profile);
                return CommandResult.Ok("EIM mapping removed; the profile is disabled pending explicit recovery.");
            case "RFREIMMAP":
                return CommandResult.Ok($"Directory state: {identities.Refresh(profile)}.");
            default:
                return new CommandResult { Listing = identities.Mappings.List()
                    .Where(mapping => profile is "" or "*ALL" || mapping.Profile == profile)
                    .Select(mapping => $"{mapping.Profile}  {mapping.Directory}  {mapping.Principal}  {mapping.LinuxAccount}  {mapping.DirectoryState}").ToArray() };
        }
    }

    private CommandResult ExecuteObjectManagement(CommandCall call)
    {
        string Value(string name, string fallback = "") => CommandParser.Unquote(call.GetOption(name) ?? fallback).ToUpperInvariant();
        try
        {
            if (call.Name.Equals("CRTLIB", StringComparison.OrdinalIgnoreCase))
            {
                _system.Libraries.CreateLibrary(Value("LIB"), description: CommandParser.Unquote(call.GetOption("TEXT")), owner: _job?.UserProfile ?? "QSECOFR");
                return CommandResult.Ok("Library created.");
            }
            if (call.Name.Equals("DLTLIB", StringComparison.OrdinalIgnoreCase))
            {
                _system.ObjectOperations.DeleteLibrary(Value("LIB")); return CommandResult.Ok("Library deleted.");
            }
            if (call.Name.Equals("CRTDTADCT", StringComparison.OrdinalIgnoreCase))
            {
                new Ipc.Services.Sqlite.DataDictionaryStore(_system.Connections).Create(Value("DTADCT"), CommandParser.Unquote(call.GetOption("TEXT")), Value("AUT", "*LIBCRTAUT"));
                return CommandResult.Ok("Data dictionary created.");
            }
            var name = call.Name.ToUpperInvariant();
            var type = Value("OBJTYPE");
            if (!ObjectType.IsKnown(type)) return CommandResult.Error("IPC0003: A known OBJTYPE is required.");
            var (lib, obj) = SplitQualified(Value("OBJ"), type == ObjectType.Library ? "QSYS" : name == "CRTDUPOBJ" ? Value("FROMLIB", "QGPL") : "*LIBL");
            if (lib is "*LIBL" or "*CURLIB")
                lib = SearchLibraries(lib).FirstOrDefault(l => _system.Objects.Exists(l, obj, type))
                    ?? throw new CpfException("CPF9801", "Object not found in the job library list.");
            var source = new QualifiedName(lib, obj);
            if (name == "DLTOBJ") _system.ObjectOperations.Delete(source, type);
            else
            {
                var target = name switch
                {
                    "RNMOBJ" => new QualifiedName(lib, Value("NEWOBJ")),
                    "MOVOBJ" => new QualifiedName(Value("TOLIB"), obj),
                    _ => new QualifiedName(Value("TOLIB"), Value("NEWOBJ", obj)),
                };
                _system.ObjectOperations.Relocate(source, type, target, copy: name == "CRTDUPOBJ",
                    newOwner: name == "CRTDUPOBJ" ? _job?.UserProfile ?? "QSECOFR" : null);
            }
            return CommandResult.Ok($"{name} completed for {source} {type}.");
        }
        catch (CpfException ex) { return CommandResult.Error(ex.Message); }
        catch (ArgumentException ex) { return CommandResult.Error($"IPC0003: {ex.Message}"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return CommandResult.Error("IPC0201: Catalog operation failed; changes were rolled back."); }
    }

    private CommandResult ExecuteSubmitJob(CommandCall call)
    {
        if (_job is null) return CommandResult.Error("IPC0101: An authenticated submitting job is required.");
        var command = call.GetOption("CMD");
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768)
            return CommandResult.Error("IPC0003: SBMJOB requires CMD(command), at most 32768 characters.");
        var jobdName = CommandParser.Unquote(call.GetOption("JOBD") ?? "QGPL/QBATCH").ToUpperInvariant();
        var (jobdLibrary, jobdObject) = ResolveWorkObject(jobdName, ObjectType.JobDescription);
        var jobd = _system.WorkDefinitions.JobDescription(jobdLibrary, jobdObject)
            ?? throw new CpfException("CPF9801", "Job description not found.");
        var (queueLibrary, queueName) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("JOBQ") ?? jobd.JobQueue).ToUpperInvariant(), ObjectType.JobQueue);
        var queue = queueLibrary + "/" + queueName;
        var name = CommandParser.Unquote(call.GetOption("JOB") ?? "QBATCH").ToUpperInvariant();
        if (!Ipc.Core.Objects.ObjectName.IsValid(name))
            return CommandResult.Error("IPC0003: Invalid batch job name.");
        var priority = ParseWorkInteger(call, "JOBPTY", jobd.Priority);
        var profile = CommandParser.Unquote(call.GetOption("USER") ?? jobd.RunAs ?? "*CURRENT").ToUpperInvariant();
        if (profile == "*CURRENT") profile = _job.UserProfile!;
        var current = jobd.CurrentLibrary ?? _job.CurrentLibrary ?? "QGPL";
        var libraries = jobd.LibraryMode switch {
            "SYSVAL" => _system.SystemValues.Get(Ipc.Core.System.SystemValueNames.UserLibraryList).Value.Replace(',', ' '),
            "EXPLICIT" => string.Join(' ', jobd.Libraries ?? Array.Empty<string>()),
            _ => _job.LibraryList ?? "QGPL QUSRSYS",
        };
        if (call.GetOption("INLLIBL") is { } overrideLibraries)
        {
            libraries = overrideLibraries.ToUpperInvariant() switch {
                "*CURRENT" => _job.LibraryList ?? "QGPL QUSRSYS", "*JOBD" => libraries,
                "*SYSVAL" => _system.SystemValues.Get(Ipc.Core.System.SystemValueNames.UserLibraryList).Value.Replace(',', ' '),
                "*NONE" => "", _ => string.Join(' ', call.Split("INLLIBL").Select(v => CommandParser.Unquote(v).ToUpperInvariant())),
            };
        }
        var job = _system.Jobs.Submit(name, "Submitted command", queue, priority,
            profile: profile, routingData: CommandParser.Unquote(call.GetOption("RTGDTA") ?? jobd.RoutingData),
            submitterName: _job.Key.Name, submitterUser: _job.Key.User, command: command,
            currentLibrary: current, libraryList: libraries, ccsid: _job.Ccsid, jobDescription: jobdLibrary + "/" + jobdObject);
        return CommandResult.Ok($"Job {job.Key} submitted to {queue}.");
    }

    private CommandResult ExecuteGo(CommandCall call)
    {
        var target = call.Positional.FirstOrDefault() ?? call.GetOption("MENU") ?? call.GetOption("PGM");
        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Error("A menu must be specified with GO.");
        }

        target = CommandParser.Unquote(target);
        var menu = _system.FindMenu(target, _job);
        return menu is null ? CommandResult.Error("CPF9824: Menu not found in the job library list.")
            : CommandResult.Go(menu.Name, menu.Library);
    }

    private static CommandResult ExecuteSignOff(CommandCall call) =>
        CommandResult.SignOff("Sign off.");

    private CommandResult ExecuteDisplayJob(CommandCall call)
    {
        var job = _job is null ? null : _system.Jobs.Get(_job.Key);
        return job is null
            ? CommandResult.Error("No active job for this session.")
            : CommandResult.Ok($"Job {job.Key} is active in subsystem {job.Subsystem}. Profile: {job.UserProfile}.");
    }

    private CommandResult ExecuteCreateClProgram(CommandCall call)
    {
        var library = CommandParser.Unquote(call.GetOption("LIB") ?? "QSYS");
        var name = CommandParser.Unquote(call.GetOption("PGM"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("CRTCLPGM requires PGM(name).");
        }

        if (library == LibraryUnknown)
        {
            library = "QSYS";
        }

        if (name.Contains('/')) (library, name) = SplitQualified(name, library);
        var sourceFile = CommandParser.Unquote(call.GetOption("SRCSTMF"));
        var resolver = new Ipc.Session.Sources.ProgramSourceResolver(_system, _job, _cancellationToken);
        Ipc.Core.Compilation.SourceDocument source;
        try
        {
            var member = CommandParser.Unquote(call.GetOption("SRCMBR") ?? "*PGM");
            if (member == "*PGM") member = name;
            source = sourceFile.Length > 0 ? resolver.Stream(sourceFile) : resolver.Member(CommandParser.Unquote(call.GetOption("SRCFILE") ?? "QCLSRC"), member);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return CommandResult.Error("IPC0006: Cannot load CL source: " + error.Message); }

        if (_system.Objects.Exists(library, name, ProgramType))
        {
            return CommandResult.Error($"Program {library}/{name} already exists.");
        }

        ClProgram program;
        try
        {
            program = _compiler.Compile(name, library, source, resolver.Resolve, _cancellationToken);
        }
        catch (ClCompileException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        var attributes = new Dictionary<string, string>(ActivationAttributes(call));
        attributes[Ipc.Core.Compilation.CompiledSourceMap.AttributeName] = Ipc.Core.Compilation.CompiledSourceMap.Serialize(program.Source!);
        attributes[ClFileBindings.AttributeName] = ClFileBindings.Serialize(program);
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(library, name),
            ObjectType = ProgramType,
            Owner = _job?.UserProfile ?? "QSECOFR",
            Attribute = "CLP",
            Source = program.Source!.Text,
            Description = $"CL source {source.Identity}",
            ExtendedAttributes = attributes,
        });

        return CommandResult.Ok($"Program {library}/{name} created.");
    }

    private CommandResult ExecuteCreateRpgProgram(CommandCall call)
    {
        var library = CommandParser.Unquote(call.GetOption("LIB") ?? "QSYS");
        var name = CommandParser.Unquote(call.GetOption("PGM"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("CRTBNDRPG requires PGM(name).");
        }

        if (library == LibraryUnknown)
        {
            library = "QSYS";
        }

        var sourceFile = CommandParser.Unquote(call.GetOption("SRCSTMF"));
        string source;
        if (sourceFile.Length > 0)
        {
            try
            {
                new Ipc.Services.Security.ServiceAuthorization(_system.Connections).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Service, allowAdopted: false);
                source = File.ReadAllText(sourceFile);
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Cannot open source file {sourceFile}: {ex.Message}");
            }
        }
        else
        {
            var sourceRead = LoadSourceMember(call);
            if (sourceRead is null)
            {
                return CommandResult.Error("CRTBNDRPG requires SRCSTMF(path) or SRCFILE(file/member).");
            }

            source = sourceRead;
        }

        if (_system.Objects.Exists(library, name, ProgramType))
        {
            return CommandResult.Error($"Program {library}/{name} already exists.");
        }

        RpgProgram program;
        try
        {
            program = RpgCompiler.Compile(library: library, name: name, source: source);
        }
        catch (RpgCompileException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        var sourceDescription = sourceFile.Length > 0 ? sourceFile : "from source member";
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(library, name),
            ObjectType = ProgramType,
            Owner = _job?.UserProfile ?? "QSECOFR",
            Attribute = RpgAttribute,
            ExtendedAttributes = ActivationAttributes(call),
            Source = source,
            Description = $"RPG source {sourceDescription}",
        });

        return CommandResult.Ok($"Program {library}/{name} created.");
    }

    private string? LoadSourceMember(CommandCall call)
    {
        var sourceFile = CommandParser.Unquote(call.GetOption("SRCFILE"));
        var sourceMember = CommandParser.Unquote(call.GetOption("SRCMBR"));
        if (sourceFile.Length == 0)
        {
            return null;
        }

        var (library, name) = ResolveFileReference(sourceFile);
        var member = ResolveMember(sourceMember, name);
        if (!_files.FileExists(library, name) || !_files.MemberExists(library, name, member))
        {
            return null;
        }

        var records = _files.ReadAll(library, name, member);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var record in records)
        {
            if (record.TryGetValue("SRCDTA", out var data) && data is string text)
            {
                builder.AppendLine(text.TrimEnd());
            }
        }

        return builder.ToString();
    }

    private CommandResult ExecuteCreateSourceFile(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("CRTSRCPF requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file, creating: true);
        var text = CommandParser.Unquote(call.GetOption("TEXT"));
        try
        {
            var width = call.GetOption("SRCDTALEN") is { } sourceWidth && int.TryParse(sourceWidth, out var parsedWidth) ? parsedWidth : call.GetOption("SRCDTALEN") is null ? 100 : throw new CpfException("IPC0003", "SRCDTALEN requires an integer.");
            _files.CreateSourceFile(library, name, text.Length > 0 ? text : $"Source physical file {name}.", width);
        }
        catch (CpfException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        return CommandResult.Ok($"Source physical file {library}/{name} created.");
    }

    private CommandResult ExecuteCreatePhysicalFile(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("CRTPF requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file, creating: true);
        var sourceFile = CommandParser.Unquote(call.GetOption("SRCFILE"));
        var sourceMember = CommandParser.Unquote(call.GetOption("SRCMBR"));
        var text = CommandParser.Unquote(call.GetOption("TEXT"));
        var member = CommandParser.Unquote(call.GetOption("MBR") ?? "*FILE");
        var maximumText = CommandParser.Unquote(call.GetOption("MAXMBRS") ?? "1").ToUpperInvariant();
        var maximum = maximumText == "*NOMAX" ? 32767 : int.TryParse(maximumText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
        if (maximum is < 1 or > 32767) return CommandResult.Error("IPC0003: MAXMBRS requires 1–32767 or *NOMAX.");
        if (sourceMember.Length == 0 || sourceMember == "*FILE") sourceMember = name;

        if (sourceFile.Length == 0)
        {
            return CommandResult.Error("CRTPF requires SRCFILE(library/file) with the DDS source.");
        }

        var sourceText = LoadSourceMember(new CommandCall
        {
            Name = "CRTPF",
            Keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SRCFILE"] = sourceFile,
                ["SRCMBR"] = sourceMember,
            },
        });

        if (sourceText is null)
        {
            var (sourceLibrary, sourceName) = ResolveFileReference(sourceFile);
            var missingMember = ResolveMember(sourceMember, sourceName);
            return CommandResult.Error($"Source member {sourceName}: {missingMember} not found.");
        }

        FileDefinition definition;
        try
        {
            definition = new DdsCompiler().CompilePhysical(name, sourceText, _job?.Ccsid ?? _system.Config.Ccsid, sourceFile + "(" + sourceMember + ")");
        }
        catch (DdsCompileException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        try
        {
            _files.CreatePhysicalFile(library, name, definition, sourceText,
                text.Length > 0 ? text : $"Physical file {name}.", member, maximum);
        }
        catch (CpfException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        return CommandResult.Ok($"Physical file {library}/{name} created.");
    }

    private CommandResult ExecuteAddMember(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("ADDPFM requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        var member = CommandParser.Unquote(call.GetOption("MBR"));
        if (member.Length == 0 || member == FirstMember)
        {
            return CommandResult.Error("ADDPFM requires MBR(member).");
        }

        if (!_files.FileExists(library, name))
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        if ((_files.GetDefinition(library, name)?.Logical is not null) != (call.Name == "ADDLFM")) return CommandResult.Error("IPC0003: Use ADDLFM for a logical file and ADDPFM for a physical file.");
        if (call.Name == "ADDLFM") _files.AddLogicalMember(library, name, member, LogicalPhysicalMembers(call, _files.GetDefinition(library, name)!.Logical!));
        else _files.AddMember(library, name, member);
        return CommandResult.Ok($"Member {member} added to file {name}.");
    }

    private CommandResult ExecuteAddSourceRecord(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("ADDSRCPFM requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        var member = RequiredMember(call, library, name);
        var data = CommandParser.Unquote(call.GetOption("DATA"));
        if (data.Length == 0)
        {
            return CommandResult.Error("ADDSRCPFM requires DATA(source line).");
        }

        var definition = _files.GetDefinition(library, name);
        if (definition is null)
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        if (!_files.MemberExists(library, name, member))
        {
            return CommandResult.Error($"Member {member} not found in file {name}.");
        }

        var sequence = NextSourceSequence(library, name, member, CommandParser.Unquote(call.GetOption("SEQ")));
        var line = data.Length > 100 ? data[..100] : data;
        _files.Insert(library, name, member, definition.PrimaryFormat.Name, new Dictionary<string, object?>
        {
            ["SRCSEQ"] = sequence,
            ["SRCDAT"] = DateTimeOffset.UtcNow,
            ["SRCDTA"] = line,
        });

        return CommandResult.Ok($"Source record {sequence} added to member {member}.");
    }

    private long NextSourceSequence(string library, string name, string member, string explicitSequence)
    {
        if (long.TryParse(explicitSequence, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        var records = _files.ReadAll(library, name, member);
        return records.Count == 0
            ? 10L
            : records.Max(r => r.TryGetValue("SRCSEQ", out var value) && value is long seq ? seq : 0L) + 10L;
    }

    private CommandResult ExecuteRemoveMember(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("RMVFM requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        var member = RequiredMember(call, library, name);
        if (!_files.MemberExists(library, name, member))
        {
            return CommandResult.Error($"Member {member} not found in file {name}.");
        }

        _files.RemoveMember(library, name, member);
        return CommandResult.Ok($"Member {member} removed from file {name}.");
    }

    private CommandResult ExecuteClearMember(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("CLRPFM requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        var member = RequiredMember(call, library, name);
        var definition = _files.GetDefinition(library, name);
        if (definition is null)
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        _files.ClearMember(library, name, member);
        return CommandResult.Ok($"Member {member} in file {name} cleared.");
    }

    private CommandResult ExecuteDisplayMember(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("DSPPFM requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        var member = RequiredMember(call, library, name);
        var definition = _files.GetDefinition(library, name);
        if (definition is null)
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        if (!_files.MemberExists(library, name, member))
        {
            return CommandResult.Error($"Member {member} not found in file {name}.");
        }

        var header = DbFormatting.ColumnHeader(definition.PrimaryFormat).Split('\n');
        var lines = new List<string> { header[0], header[1] };
        foreach (var record in _files.ReadKeyed(library, name, member))
        {
            lines.Add(string.Join(" | ", DbFormatting.FormatColumn(definition.PrimaryFormat, record)));
        }

        var count = _files.RowCount(library, name, member);
        return new CommandResult
        {
            Outcome = CommandOutcome.Continue,
            Message = $"Display of file {library}/{name} member {member}: {count} record(s).",
            Listing = lines,
        };
    }

    private CommandResult ExecuteCopyFile(CommandCall call)
    {
        var (fromLibrary, fromName) = ResolveFileReference(CommandParser.Unquote(call.GetOption("FROMFILE")));
        var (toLibrary, toName) = ResolveFileReference(CommandParser.Unquote(call.GetOption("TOFILE")));
        string CopyMember(string? option, string library, string file, string? from = null)
        {
            var member = CommandParser.Unquote(option ?? "*FIRST").ToUpperInvariant();
            if (member == "*FIRST") return _files.FirstMember(library, file);
            if (member == "*FROMMBR" && from is not null) return from;
            if (!ObjectName.IsValid(member)) throw new CpfException("IPC0139", "CPYF requires an explicit member, *FIRST, or TOMBR(*FROMMBR).");
            return member;
        }
        var fromMember = CopyMember(call.GetOption("FROMMBR"), fromLibrary, fromName);
        var toMember = CopyMember(call.GetOption("TOMBR"), toLibrary, toName, fromMember);

        if (fromLibrary.Length == 0 || toLibrary.Length == 0)
        {
            return CommandResult.Error("CPYF requires FROMFILE and TOFILE.");
        }

        var sourceDefinition = _files.GetDefinition(fromLibrary, fromName);
        var targetDefinition = _files.GetDefinition(toLibrary, toName);
        if (sourceDefinition is null)
        {
            return CommandResult.Error($"File {fromLibrary}/{fromName} not found.");
        }

        if (targetDefinition is null)
        {
            return CommandResult.Error($"File {toLibrary}/{toName} not found.");
        }

        if (!_files.MemberExists(fromLibrary, fromName, fromMember))
        {
            return CommandResult.Error($"Member {fromMember} not found in file {fromName}.");
        }

        if (!_files.MemberExists(toLibrary, toName, toMember))
        {
            return CommandResult.Error($"Member {toMember} not found in file {toName}.");
        }

        var option = CommandParser.Unquote(call.GetOption("MBROPT") ?? "*NONE").ToUpperInvariant();
        if (option is not ("*ADD" or "*REPLACE")) return CommandResult.Error("IPC0139: CPYF requires MBROPT(*ADD) or MBROPT(*REPLACE) for an existing physical target.");
        var formats = CommandParser.Tokenize(call.GetOption("FMTOPT") ?? "*NONE").Select(v => v.ToUpperInvariant()).ToArray();
        if (formats.Length == 0 || formats.Length > 2 || formats.Distinct().Count() != formats.Length ||
            formats.Any(v => v is not ("*NONE" or "*MAP" or "*DROP")) || formats.Contains("*NONE") && formats.Length != 1)
            return CommandResult.Error("IPC0139: Supported FMTOPT values are *NONE or *MAP/*DROP.");
        var count = _files.CopyRecords(new(fromLibrary, fromName), fromMember, new(toLibrary, toName), toMember,
            replace: option == "*REPLACE", map: formats.Contains("*MAP"), drop: formats.Contains("*DROP"), cancellationToken: _cancellationToken);
        return CommandResult.Ok($"{count} records copied from {fromName} to {toName}.");
    }

    private CommandResult ExecuteDeleteFile(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("DLTF requires FILE(library/file).");
        }

        var (library, name) = ResolveFileReference(file);
        if (!_files.FileExists(library, name))
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        _files.DeleteFile(library, name);
        return CommandResult.Ok($"File {library}/{name} deleted.");
    }

    private CommandResult ExecuteCall(CommandCall call)
    {
        var target = CommandParser.Unquote(call.GetOption("PGM") ?? call.Positional.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Error("CALL requires PGM(program).");
        }

        var supplied = call.Split("PARM");
        if (supplied.Count > 255) return CommandResult.Error("CALL supports at most 255 parameters.");
        var parameters = supplied.Select(value => (object?)ClCallArgument.Compile(value).Evaluate(
            _ => throw new ClRuntimeException("Interactive CALL cannot reference CL variables."), _job?.Ccsid ?? _system.Config.Ccsid)).ToArray();
        if (parameters.Cast<ProgramConstant>().Sum(value => (long)value.Buffer.Length) > 1048576) return CommandResult.Error("CALL arguments exceed 1 MiB.");
        return ExecuteProgram(target, parameters);
    }
    private CommandResult ExecuteProgram(string target, IReadOnlyList<object?> parameters)
    {
        var program = LoadProgramObject(LibraryUnknown, target);
        if (program.Api is not null) return ExecuteBuiltinProgram(program.Api, parameters);
        if (program.External is not null)
        {
            using var scope = EnterProgram(program.External);
            var result = _system.ExternalPrograms.Execute(program.External, parameters, _cancellationToken);
            if (result.Message.Length > 0) _messages.AppendLine(result.Message);
            return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Error(result.Message);
        }
        if (program.Rpg is not null)
        {
            using var scope = EnterProgram(program.Rpg);
            return ExecuteRpg(program.Rpg, BindRpgConstants(program.Rpg, parameters));
        }

        if (program.Cl is not null)
        {
            var result = _interpreter.RunWithArguments(program.Cl, parameters).Result;
            if (result.Outcome == CommandOutcome.Error)
            {
                return result;
            }

            var text = _messages.ToString().Trim();
            return CommandResult.Ok(text.Length > 0 ? text : $"Program {target} ended normally.");
        }

        return CommandResult.Error($"Program {target} not found.");
    }

    private CommandResult ExecuteRpg(RpgProgram program, IReadOnlyList<object?> parameters)
    {
        try
        {
            RunRpg(program, parameters);
            var text = _messages.ToString().Trim();
            return CommandResult.Ok(text.Length > 0 ? text : $"Program {program.Name} ended normally.");
        }
        catch (RpgException ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    private RpgHost BuildRpgHost() => new()
    {
        CancellationToken = _cancellationToken,
        Files = new RpgSqliteFileAccess(_files),
        OpenFile = _job is null ? null : OpenRpgFile,
        LibraryResolver = (_, fileName) => FindFileLibrary(fileName),
        ProgramCaller = CallExternalProgram,
        SendMessage = message => _messages.AppendLine(message),
        Display = text => _messages.AppendLine(text),
        ReceiveMessage = () => null,
        Now = DateTimeOffset.Now,
    };

    private string? FindFileLibrary(string fileName)
    {
        foreach (var library in SearchLibraries(LibraryUnknown))
        {
            if (_files.FileExists(library, fileName.Trim().ToUpperInvariant()))
            {
                return library;
            }
        }

        return null;
    }

    private RpgExternalCallResult? CallExternalProgram(string library, string name, IReadOnlyList<object?> parameters)
    {
        try
        {
            var loaded = LoadProgramObject(library, name);
            if (loaded.Api is not null)
            {
                var result = ExecuteBuiltinProgram(loaded.Api, parameters);
                return new RpgExternalCallResult { Success = !result.IsError, Message = result.Message, UpdatedParameters = parameters };
            }
            if (loaded.External is not null)
            {
                using var scope = EnterProgram(loaded.External);
                var result = _system.ExternalPrograms.Execute(loaded.External, parameters, _cancellationToken);
                if (result.Message.Length > 0) _messages.AppendLine(result.Message);
                return new RpgExternalCallResult { Success = result.Success, Message = result.Message, UpdatedParameters = result.Parameters };
            }
            if (loaded.Rpg is not null)
            {
                using var scope = EnterProgram(loaded.Rpg);
                RunRpg(loaded.Rpg, parameters);
                return new RpgExternalCallResult
                {
                    Success = true,
                    Message = _messages.ToString().Trim(),
                };
            }

            if (loaded.Cl is not null)
            {
                var invocation = _interpreter.RunWithArguments(loaded.Cl, parameters);
                var result = invocation.Result;
                return new RpgExternalCallResult
                {
                    Success = result.Outcome != CommandOutcome.Error,
                    Message = result.IsError ? result.Message : _messages.ToString().Trim(),
                    UpdatedParameters = invocation.Parameters,
                };
            }

            return new RpgExternalCallResult { Success = false, Message = $"Program {name} not found." };
        }
        catch (RpgException ex)
        {
            return new RpgExternalCallResult { Success = false, Message = ex.Message };
        }
    }

    private ClProgram? LoadProgram(string library, string name) => LoadProgramObject(library, name).Cl;

    private (ClProgram? Cl, RpgProgram? Rpg, ObjectDescriptor? External, ObjectDescriptor? Api) LoadProgramObject(string library, string name)
    {
        var slash = name.IndexOf('/');
        if (slash >= 0)
        {
            library = name[..slash];
            name = name[(slash + 1)..];
        }

        foreach (var lib in SearchLibraries(library.ToUpperInvariant()))
        {
            var descriptor = _system.Objects.Get(lib, name, ProgramType);
            if (descriptor?.Source is { Length: > 0 } source)
            {
                _system.ObjectSigning.RequireExecutable(descriptor);
                if (descriptor.Attribute == Ipc.Services.Work.BuiltinProgramService.Attribute)
                {
                    Ipc.Services.Work.BuiltinProgramService.Validate(descriptor);
                    _loadedDescriptors.Add(descriptor, descriptor);
                    return (null, null, null, descriptor);
                }
                if (descriptor.Attribute == Ipc.Services.Work.ExternalProgramService.Attribute)
                {
                    _loadedDescriptors.Add(descriptor, descriptor);
                    return (null, null, descriptor, null);
                }
                if (string.Equals(descriptor.Attribute, RpgAttribute, StringComparison.OrdinalIgnoreCase))
                {
                    var rpg = RpgCompiler.Compile(library: lib, name: name, source: source);
                    _loadedDescriptors.Add(rpg, descriptor);
                    return (null, rpg, null, null);
                }

                var fileBindings = descriptor.ExtendedAttributes?.TryGetValue(ClFileBindings.AttributeName, out var serializedFiles) == true ? ClFileBindings.Restore(source, serializedFiles) : null;
                var compiler = fileBindings is null ? _compiler : new ClCompiler(fileBindings.Resolve);
                var cl = descriptor.ExtendedAttributes?.TryGetValue(Ipc.Core.Compilation.CompiledSourceMap.AttributeName, out var map) == true
                    ? compiler.Compile(name, lib, Ipc.Core.Compilation.CompiledSourceMap.Restore(source, map), _cancellationToken)
                    : compiler.Compile(name, lib, source, _cancellationToken);
                if (fileBindings is not null && fileBindings.Files.Count != cl.Files.Count) throw new InvalidDataException("Unused compiled CL file bindings.");
                _loadedDescriptors.Add(cl, descriptor);
                return (cl, null, null, null);
            }
        }

        return (null, null, null, null);
    }

    private IEnumerable<string> SearchLibraries(string library) => _system.SearchLibraries(_job, library);

    private (string Library, string Name) ResolveFileReference(string file, bool creating = false)
    {
        var (library, name) = SplitQualified(file, creating ? "*CURLIB" : "*LIBL");
        if (library == "*CURLIB") return (SearchLibraries(library).First(), name);
        if (library == "*LIBL")
            return (SearchLibraries(library).FirstOrDefault(l => _files.FileExists(l, name))
                ?? SearchLibraries("*CURLIB").First(), name);
        return (library, name);
    }

    private static (string Library, string Name) SplitQualified(string file, string defaultLibrary)
    {
        var slash = file.IndexOf('/');
        return slash >= 0
            ? (file[..slash].ToUpperInvariant(), file[(slash + 1)..].ToUpperInvariant())
            : (defaultLibrary, file.ToUpperInvariant());
    }

    private static string RequiredFile(CommandCall call)
    {
        var file = CommandParser.Unquote(call.GetOption("FILE"));
        return file.Length == 0 ? string.Empty : file;
    }

    private string RequiredMember(CommandCall call, string library, string name)
    {
        var member = CommandParser.Unquote(call.GetOption("MBR"));
        if (member.Equals(FirstMember, StringComparison.OrdinalIgnoreCase) || member.Length == 0 && call.Name == "DSPPFM") return _files.FirstMember(library, name);
        return ResolveMember(member, name);
    }

    private static string ResolveMember(string member, string defaultMember) =>
        member.Length == 0 || member == FirstMember ? defaultMember.ToUpperInvariant() : member.ToUpperInvariant();
}
