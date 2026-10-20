using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteCreateRpgModule(CommandCall call)
    {
        var text = CommandParser.Unquote(call.GetOption("MODULE"));
        if (text.Length == 0) return CommandResult.Error("CRTRPGMOD requires MODULE(name).");
        var (library, name) = SplitQualified(text, CommandParser.Unquote(call.GetOption("LIB") ?? "*CURLIB").ToUpperInvariant());
        if (library == "*CURLIB") library = SearchLibraries(library).First();
        var resolver = new Ipc.Session.Sources.ProgramSourceResolver(_system, _job, _cancellationToken);
        try
        {
            var stream = CommandParser.Unquote(call.GetOption("SRCSTMF")); var member = CommandParser.Unquote(call.GetOption("SRCMBR") ?? "*MODULE");
            if (member == "*MODULE") member = name;
            var source = stream.Length > 0 ? resolver.Stream(stream) : resolver.Member(CommandParser.Unquote(call.GetOption("SRCFILE") ?? "QRPGLESRC"), member);
            var module = RpgCompiler.CompileModule(library, name, source, resolver.ResolveRpg, _cancellationToken, DescribeRpgWorkstation, DescribeRpgDisk, DescribeRpgExternal);
            _system.Objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.Module, Attribute = "RPGLE", Owner = _job?.UserProfile ?? "QSECOFR",
                Source = module.Source!.Text, Description = "RPG module source " + source.Identity, ExtendedAttributes = new Dictionary<string, string> {
                    [RpgModuleImage.VersionAttribute] = "1", [CompiledSourceMap.AttributeName] = CompiledSourceMap.Serialize(module.Source),
                    [RpgDiskBindings.AttributeName] = RpgDiskBindings.Serialize(module), [RpgWorkstationBindings.AttributeName] = RpgWorkstationBindings.Serialize(module),
                    [RpgExternalBindings.AttributeName] = RpgExternalBindings.Serialize(module) } });
            return CommandResult.Ok($"Module {library}/{name} created.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or RpgCompileException)
        { return CommandResult.Error("IPC0006: " + error.Message); }
    }
    private ObjectDescriptor LoadRpgModule(string text)
    {
        var (library, name) = SplitQualified(CommandParser.Unquote(text), LibraryUnknown);
        foreach (var candidate in SearchLibraries(library))
        {
            var module = _system.Objects.Get(candidate, name, ObjectType.Module);
            if (module is null) continue;
            _system.ObjectSigning.RequireExecutable(module); return module;
        }
        throw new CpfException("CPF9801", "Module not found: " + text);
    }
    private CommandResult ExecuteCreateBoundRpgProgram(CommandCall call)
    {
        var target = CommandParser.Unquote(call.GetOption("PGM")); var names = call.Split("MODULE");
        if (target.Length == 0 || names.Count is < 1 or > 16) return CommandResult.Error("CRTPGM requires PGM(name) and 1 through 16 explicit MODULE names.");
        var (library, name) = SplitQualified(target, CommandParser.Unquote(call.GetOption("LIB") ?? "*CURLIB").ToUpperInvariant());
        if (library == "*CURLIB") library = SearchLibraries(library).First();
        try
        {
            var images = names.Select(n => RpgModuleImage.Capture(LoadRpgModule(n))).ToArray();
            var modules = images.Select(i => i.Restore(_cancellationToken)).ToArray(); var entryName = CommandParser.Unquote(call.GetOption("ENTMOD") ?? "*FIRST").ToUpperInvariant();
            var entry = entryName == "*FIRST" ? modules.FirstOrDefault(m => !m.NoMain) : null;
            if (entryName != "*FIRST")
            {
                var descriptor = LoadRpgModule(entryName);
                entry = modules.SingleOrDefault(m => m.Library == descriptor.Library && m.Name == descriptor.Name);
            }
            if (entry is null) throw new InvalidDataException("ENTMOD must select a supplied module with a main procedure.");
            var binding = BindRpgModules(modules, entry, call);
            var manifest = new RpgBoundProgram(RpgServiceReference.ImageVersion(entry.ServiceReferences), entry.Library + "/" + entry.Name, binding.Modules.Select(RpgModuleImage.Capture).ToArray()) { Services = entry.ServiceReferences };
            _system.Objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.Program, Attribute = RpgBoundProgram.Attribute,
                Owner = _job?.UserProfile ?? "QSECOFR", Source = manifest.Serialize(), ExtendedAttributes = ActivationAttributes(call), Description = "Statically bound RPG modules" });
            return CommandResult.Ok(binding.Message($"Program {library}/{name} created."));
        }
        catch (Exception error) when (error is InvalidDataException or RpgCompileException)
        { return CommandResult.Error("IPC0006: " + error.Message); }
    }
    private CommandResult ExecuteDisplayRpgModule(CommandCall call)
    {
        if (string.IsNullOrWhiteSpace(call.GetOption("MODULE"))) return CommandResult.Error("DSPMOD requires MODULE(name).");
        try
        {
            var descriptor = LoadRpgModule(CommandParser.Unquote(call.GetOption("MODULE"))); var module = RpgModuleImage.Capture(descriptor).Restore(_cancellationToken);
            return CommandResult.Ok($"Module {CommandParser.Unquote(call.GetOption("MODULE"))}; main: {(module.NoMain ? "no" : "yes")}; exports: " +
                string.Join(", ", module.Subprocedures.Where(p => p.IsExport).Select(p => p.Name)) + "; imports: " + string.Join(", ", RpgCompiler.ModuleImports(module)) + "; directories: " + string.Join(", ", module.BindingDirectories));
        }
        catch (Exception error) when (error is InvalidDataException or RpgCompileException) { return CommandResult.Error("IPC0006: " + error.Message); }
    }
}
