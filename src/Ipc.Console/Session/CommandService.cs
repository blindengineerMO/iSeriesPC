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

public sealed class CommandService
{
    private const string ProgramType = "*PGM";
    private const string RpgAttribute = "RPG";
    private const string LibraryUnknown = "*LIBL";
    private const string DefaultLibrary = "QGPL";
    private const string FirstMember = "*FIRST";

    private readonly IpcSystem _system;
    private readonly CommandCatalog _catalog = new();
    private readonly ClCompiler _compiler = new();
    private readonly Dictionary<string, ClProgram> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _messages = new();
    private readonly ClInterpreter _interpreter;
    private readonly SqliteFileStore _files;

    public CommandService(IpcSystem system)
    {
        _system = system;
        _files = new SqliteFileStore(system.Connections, system.Objects);
        _interpreter = new ClInterpreter(LoadProgram, RunCommand, message => _messages.AppendLine(message));
        _catalog.Register("GO", ExecuteGo);
        _catalog.Register("DSPMENU", ExecuteGo);
        _catalog.Register("SIGNOFF", ExecuteSignOff);
        _catalog.Register("MSG", ExecuteNoMessages);
        _catalog.Register("DSPMSG", ExecuteNoMessages);
        _catalog.Register("DSPJOB", ExecuteDisplayJob);
        _catalog.Register("CRTCLPGM", ExecuteCreateClProgram);
        _catalog.Register("CRTBNDRPG", ExecuteCreateRpgProgram);
        _catalog.Register("CALL", ExecuteCall);
        _catalog.Register("CRTSRCPF", ExecuteCreateSourceFile);
        _catalog.Register("CRTPF", ExecuteCreatePhysicalFile);
        _catalog.Register("ADDPFM", ExecuteAddMember);
        _catalog.Register("ADDSRCPFM", ExecuteAddSourceRecord);
        _catalog.Register("RMVFM", ExecuteRemoveMember);
        _catalog.Register("CLRPFM", ExecuteClearMember);
        _catalog.Register("DSPPFM", ExecuteDisplayMember);
        _catalog.Register("CPYF", ExecuteCopyFile);
        _catalog.Register("DLTF", ExecuteDeleteFile);
    }

    public CommandResult Execute(string line)
    {
        _messages.Clear();
        return _catalog.Execute(line);
    }

    public CommandResult RunCommand(CommandCall call) => _catalog.Execute(call);

    private CommandResult ExecuteGo(CommandCall call)
    {
        var target = call.Positional.FirstOrDefault() ?? call.GetOption("MENU") ?? call.GetOption("PGM");
        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Error("A menu must be specified with GO.");
        }

        target = CommandParser.Unquote(target);
        var slash = target.IndexOf('/');
        return slash >= 0
            ? CommandResult.Go(target[(slash + 1)..].ToUpperInvariant(), target[..slash].ToUpperInvariant())
            : CommandResult.Go(target.ToUpperInvariant());
    }

    private static CommandResult ExecuteSignOff(CommandCall call) =>
        CommandResult.SignOff("Sign off.");

    private CommandResult ExecuteNoMessages(CommandCall call) =>
        CommandResult.Ok("Message queue QMSG is empty.");

    private CommandResult ExecuteDisplayJob(CommandCall call)
    {
        var job = _system.Jobs.List(JobStatus.Active, JobKeys.InteractiveSubsystem)
            .OrderByDescending(j => j.StartedAt)
            .FirstOrDefault() ?? _system.Jobs.List(JobStatus.Active).LastOrDefault();
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

        var sourceFile = CommandParser.Unquote(call.GetOption("SRCSTMF"));
        string source;
        if (sourceFile.Length > 0)
        {
            try
            {
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
                return CommandResult.Error("CRTCLPGM requires SRCSTMF(path) or SRCFILE(file/member).");
            }

            source = sourceRead;
        }

        if (_system.Objects.Exists(library, name, ProgramType))
        {
            return CommandResult.Error($"Program {library}/{name} already exists.");
        }

        ClProgram program;
        try
        {
            program = _compiler.Compile(name, library, source);
        }
        catch (ClCompileException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        var sourceDescription = sourceFile.Length > 0 ? sourceFile : "from source member";
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(library, name),
            ObjectType = ProgramType,
            Attribute = "CLP",
            Source = source,
            Description = $"CL source {sourceDescription}",
        });

        _cache[$"{library}/{name}"] = program;
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
            program = RpgCompiler.Compile(name, library, source);
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
            Attribute = RpgAttribute,
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

        var (library, name) = SplitQualified(sourceFile, DefaultLibrary);
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var text = CommandParser.Unquote(call.GetOption("TEXT"));
        try
        {
            _files.CreateSourceFile(library, name, text.Length > 0 ? text : $"Source physical file {name}.");
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var sourceFile = CommandParser.Unquote(call.GetOption("SRCFILE"));
        var sourceMember = CommandParser.Unquote(call.GetOption("SRCMBR"));
        var text = CommandParser.Unquote(call.GetOption("TEXT"));

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
            var (sourceLibrary, sourceName) = SplitQualified(sourceFile, DefaultLibrary);
            var member = ResolveMember(sourceMember, sourceName);
            return CommandResult.Error($"Source member {sourceName}: {member} not found.");
        }

        FileDefinition definition;
        try
        {
            definition = new DdsCompiler().CompilePhysical(name, sourceText, _system.Config.Ccsid);
        }
        catch (DdsCompileException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        try
        {
            _files.CreatePhysicalFile(library, name, definition, sourceText,
                text.Length > 0 ? text : $"Physical file {name}.");
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var member = CommandParser.Unquote(call.GetOption("MBR"));
        if (member.Length == 0 || member == FirstMember)
        {
            return CommandResult.Error("ADDPFM requires MBR(member).");
        }

        if (!_files.FileExists(library, name))
        {
            return CommandResult.Error($"File {library}/{name} not found.");
        }

        _files.AddMember(library, name, member);
        return CommandResult.Ok($"Member {member} added to file {name}.");
    }

    private CommandResult ExecuteAddSourceRecord(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("ADDSRCPFM requires FILE(library/file).");
        }

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var member = RequiredMember(call, name);
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var member = RequiredMember(call, name);
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var member = RequiredMember(call, name);
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

        var (library, name) = SplitQualified(file, DefaultLibrary);
        var member = RequiredMember(call, name);
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
        var (fromLibrary, fromName) = SplitQualified(CommandParser.Unquote(call.GetOption("FROMFILE")), DefaultLibrary);
        var (toLibrary, toName) = SplitQualified(CommandParser.Unquote(call.GetOption("TOFILE")), DefaultLibrary);
        var fromMember = ResolveMember(CommandParser.Unquote(call.GetOption("FROMMBR")), fromName);
        var toMember = ResolveMember(CommandParser.Unquote(call.GetOption("TOMBR")), toName);

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

        var targetFields = targetDefinition.PrimaryFormat.Fields.Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = _files.ReadAll(fromLibrary, fromName, fromMember);
        foreach (var record in records)
        {
            var target = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in record)
            {
                if (targetFields.Contains(pair.Key))
                {
                    target[pair.Key] = pair.Value;
                }
            }

            _files.Insert(toLibrary, toName, toMember, targetDefinition.PrimaryFormat.Name, target);
        }

        return CommandResult.Ok($"{records.Count} records copied from {fromName} to {toName}.");
    }

    private CommandResult ExecuteDeleteFile(CommandCall call)
    {
        var file = RequiredFile(call);
        if (file.Length == 0)
        {
            return CommandResult.Error("DLTF requires FILE(library/file).");
        }

        var (library, name) = SplitQualified(file, DefaultLibrary);
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

        var parameters = call.Split("PARM").Select(CommandParser.Unquote).ToList();
        var program = LoadProgramObject(LibraryUnknown, target);
        if (program.Rpg is not null)
        {
            return ExecuteRpg(program.Rpg, parameters);
        }

        if (program.Cl is not null)
        {
            var result = _interpreter.Run(program.Cl, parameters);
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
            var host = BuildRpgHost();
            var interpreter = new RpgInterpreter(host);
            interpreter.Run(program, parameters);
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
        Files = new RpgSqliteFileAccess(_files),
        LibraryResolver = (_, fileName) => FindFileLibrary(fileName),
        ProgramCaller = CallExternalProgram,
        SendMessage = message => _messages.AppendLine(message),
        Display = text => _messages.AppendLine(text),
        ReceiveMessage = () => null,
        Now = DateTimeOffset.Now,
    };

    private string? FindFileLibrary(string fileName)
    {
        var candidates = new List<string> { "QSYS" };
        candidates.AddRange(_system.Objects.ListLibraries());
        foreach (var library in candidates.Distinct())
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
            if (loaded.Rpg is not null)
            {
                var host = BuildRpgHost();
                new RpgInterpreter(host).Run(loaded.Rpg, parameters);
                return new RpgExternalCallResult
                {
                    Success = true,
                    Message = _messages.ToString().Trim(),
                };
            }

            if (loaded.Cl is not null)
            {
                var arguments = parameters.Select(RpgValues.ToText).ToList();
                var result = _interpreter.Run(loaded.Cl, arguments);
                return new RpgExternalCallResult
                {
                    Success = result.Outcome != CommandOutcome.Error,
                    Message = _messages.ToString().Trim(),
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

    private (ClProgram? Cl, RpgProgram? Rpg) LoadProgramObject(string library, string name)
    {
        var slash = name.IndexOf('/');
        if (slash >= 0)
        {
            library = name[..slash];
            name = name[(slash + 1)..];
        }

        var candidates = new List<string>();
        if (library == LibraryUnknown)
        {
            candidates.Add("QSYS");
            candidates.AddRange(_system.Objects.ListLibraries());
        }
        else
        {
            candidates.Add(library);
        }

        foreach (var lib in candidates.Distinct())
        {
            if (_cache.TryGetValue($"{lib}/{name}", out var cached))
            {
                return (cached, null);
            }

            var descriptor = _system.Objects.Get(lib, name, ProgramType);
            if (descriptor?.Source is { Length: > 0 } source)
            {
                if (string.Equals(descriptor.Attribute, RpgAttribute, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, RpgCompiler.Compile(name, lib, source));
                }

                var cl = _compiler.Compile(name, lib, source);
                _cache[$"{lib}/{name}"] = cl;
                return (cl, null);
            }
        }

        return (null, null);
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

    private static string RequiredMember(CommandCall call, string name)
    {
        var member = CommandParser.Unquote(call.GetOption("MBR"));
        return ResolveMember(member, name);
    }

    private static string ResolveMember(string member, string defaultMember) =>
        member.Length == 0 || member == FirstMember ? defaultMember.ToUpperInvariant() : member.ToUpperInvariant();
}