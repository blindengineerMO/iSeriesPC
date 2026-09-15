using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteCreateLogicalFile(CommandCall call)
    {
        var file = RequiredFile(call);
        var (library, name) = ResolveFileReference(file, creating: true);
        var options = new Dictionary<string, string>(call.Keywords, StringComparer.OrdinalIgnoreCase);
        if (!options.ContainsKey("SRCMBR") || options["SRCMBR"] == "*FILE") options["SRCMBR"] = name;
        var source = LoadSourceMember(new CommandCall { Name = call.Name, Keywords = options }) ?? throw new CpfException("CPF2817", "CRTLF requires DDS source.");
        try
        {
            var compiler = new LogicalDdsCompiler(target => {
                var (sourceLibrary, sourceFile) = ResolveFileReference(target);
                return (new QualifiedName(sourceLibrary, sourceFile), _files.GetDefinition(sourceLibrary, sourceFile) ?? throw new CpfException("CPF9801", "PFILE not found."));
            });
            var definition = compiler.Compile(name, source, CommandParser.Unquote(call.GetOption("SRCFILE") ?? "*SOURCE") + "(" + options["SRCMBR"] + ")");
            var maximum = CommandParser.Unquote(call.GetOption("MAXMBRS") ?? "1").ToUpperInvariant();
            if (maximum == "*NOMAX") maximum = "256";
            if (!int.TryParse(maximum, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maximumMembers) || maximumMembers is < 1 or > 256)
                throw new CpfException("IPC0138", "MAXMBRS requires 1–256 or *NOMAX (bounded to 256).");
            _files.CreateLogicalFile(library, name, definition, source, LogicalPhysicalMembers(call, definition.Logical!),
                CommandParser.Unquote(call.GetOption("MBR") ?? "*FILE"), maximumMembers);
            return CommandResult.Ok($"Logical file {library}/{name} created.");
        }
        catch (DdsCompileException error) { return CommandResult.Error(error.Message); }
    }

    private static IReadOnlyList<string>? LogicalPhysicalMembers(CommandCall call, LogicalFileDefinition logical)
    {
        var raw = call.GetOption("DTAMBRS")?.Trim() ?? "*ALL";
        if (raw.Equals("*ALL", StringComparison.OrdinalIgnoreCase)) return null;
        IReadOnlyList<string> Parts(string value)
        {
            var parts = CommandParser.Tokenize(value);
            return parts.Count == 1 && parts[0].StartsWith('(') && parts[0].EndsWith(')')
                ? CommandParser.Tokenize(parts[0][1..^1]) : parts;
        }
        var binding = Parts(raw);
        if (binding.Count is < 1 or > 2 || binding[0].Contains('(')) throw new CpfException("IPC0138", "DTAMBRS requires one physical file and member list for this simple LF.");
        var file = CommandParser.Unquote(binding[0]).ToUpperInvariant().Split('/');
        var library = file.Length == 1 || file[0] == "*CURRENT" ? logical.SourceLibrary : file[0];
        if (file.Length is < 1 or > 2 || library != logical.SourceLibrary || file[^1] != logical.SourceFile)
            throw new CpfException("IPC0138", "DTAMBRS file must match the compiled PFILE identity.");
        var members = Parts(binding.Count == 1 ? "*NONE" : binding[1]).Select(m => CommandParser.Unquote(m).ToUpperInvariant()).ToArray();
        if (members.Length == 1 && members[0] == "*NONE") return Array.Empty<string>();
        if (members.Length is < 1 or > 32 || members.Any(m => !ObjectName.IsValid(m)) || members.Distinct().Count() != members.Length)
            throw new CpfException("IPC0138", "DTAMBRS requires 1–32 distinct member names or *NONE.");
        return members;
    }
}
