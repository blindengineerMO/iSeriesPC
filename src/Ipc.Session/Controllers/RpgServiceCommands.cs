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
    private CommandResult ExecuteCreateRpgService(CommandCall call)
    {
        var target = CommandParser.Unquote(call.GetOption("SRVPGM")); var names = call.Split("MODULE");
        if (target.Length == 0 || names.Count is < 1 or > 16) return CommandResult.Error("CRTSRVPGM requires SRVPGM(name) and 1 through 16 explicit MODULE names.");
        var (library, name) = SplitQualified(target, CommandParser.Unquote(call.GetOption("LIB") ?? "*CURLIB").ToUpperInvariant());
        if (library == "*CURLIB") library = SearchLibraries(library).First();
        try
        {
            var activation = ActivationAttributes(call);
            if (activation["ipc.program.activationGroup"] == "*NEW") throw new InvalidDataException("Service programs require ACTGRP(*CALLER) or a named activation group.");
            var export = CommandParser.Unquote(call.GetOption("EXPORT") ?? "*SRCFILE").ToUpperInvariant();
            if (export is not ("*ALL" or "*SRCFILE")) throw new InvalidDataException("EXPORT must be *ALL or *SRCFILE.");
            SourceDocument? binder = null;
            if (export == "*SRCFILE")
            {
                var resolver = new Ipc.Session.Sources.ProgramSourceResolver(_system, _job, _cancellationToken);
                var stream = CommandParser.Unquote(call.GetOption("SRCSTMF")); var member = CommandParser.Unquote(call.GetOption("SRCMBR") ?? "*SRVPGM");
                if (member.Equals("*SRVPGM", StringComparison.OrdinalIgnoreCase)) member = name;
                if (stream.Length > 0 && (call.GetOption("SRCFILE") is not null || call.GetOption("SRCMBR") is not null)) throw new InvalidDataException("Choose SRCSTMF or SRCFILE/SRCMBR for binder source.");
                binder = stream.Length > 0 ? resolver.Stream(stream) : resolver.Member(CommandParser.Unquote(call.GetOption("SRCFILE") ?? "QSRVSRC"), member);
            }
            else if (call.GetOption("SRCFILE", "SRCMBR", "SRCSTMF") is not null) throw new InvalidDataException("Binder source options require EXPORT(*SRCFILE).");
            var images = names.Select(n => RpgModuleImage.Capture(LoadRpgModule(n))).ToArray();
            var modules = images.Select(i => i.Restore(_cancellationToken)).ToArray(); var binding = BindRpgServiceModules(modules, call);
            var dependencies = binding.Services.Select(s => s.Reference).ToArray();
            var exports = RpgCompiler.ModuleExports(binding.Modules); var data = RpgCompiler.ModuleDataExports(binding.Modules);
            var levels = binder is null ? new[] { RpgCompiler.GeneratedExports(exports.Keys.Concat(data.Keys).ToArray()) } : RpgCompiler.ParseBinderSource(binder, _cancellationToken);
            if (levels[0].Exports.Any(symbol => !exports.ContainsKey(symbol) && !data.ContainsKey(symbol))) throw new InvalidDataException("Binder export is not an exported native module symbol.");
            var dataExports = levels[0].Exports.Where(data.ContainsKey).ToArray();
            var image = new RpgServiceImage(dataExports.Length > 0 ? 5 : RpgServiceReference.ImageVersion(dependencies), binding.Modules.Select(RpgModuleImage.Capture).ToArray(), levels, binder) { Services = dependencies, DataExports = dataExports }.Serialize();
            _cancellationToken.ThrowIfCancellationRequested();
            _system.Objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.ServiceProgram, Attribute = RpgServiceImage.Attribute,
                Owner = _job?.UserProfile ?? "QSECOFR", Source = image, ExtendedAttributes = activation, Description = "Captured RPG service modules and export definitions" });
            return CommandResult.Ok(binding.Message($"Service program {library}/{name} created."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or RpgCompileException)
        { return CommandResult.Error("IPC0006: " + error.Message); }
    }
    private CommandResult ExecuteDisplayRpgService(CommandCall call)
    {
        var text = CommandParser.Unquote(call.GetOption("SRVPGM"));
        if (text.Length == 0) return CommandResult.Error("DSPSRVPGM requires SRVPGM(name).");
        var (library, name) = SplitQualified(text, LibraryUnknown);
        try
        {
            ObjectDescriptor? descriptor = null;
            foreach (var candidate in SearchLibraries(library))
                if ((descriptor = _system.Objects.Get(candidate, name, ObjectType.ServiceProgram)) is not null) break;
            if (descriptor is null) throw new CpfException("CPF9801", "Service program not found: " + text);
            _system.ObjectSigning.RequireExecutable(descriptor);
            if (descriptor.Attribute != RpgServiceImage.Attribute || descriptor.Source is null) throw new InvalidDataException("Invalid source-bound RPG service program.");
            var definition = RpgServiceImage.Deserialize(descriptor.Source).Restore(_cancellationToken);
            return CommandResult.Ok($"Service {descriptor.Library}/{descriptor.Name}; exports: " + string.Join(", ", definition.Levels[0].Exports) +
                "; levels: " + string.Join("; ", definition.Levels.Select(l => l.Level + "=" + l.Signature + " [" + string.Join(", ", l.Exports) + "]")) +
                "; data: " + string.Join(", ", definition.Data.Keys) + "; modules: " + string.Join(", ", definition.Modules.Select(m => m.Library + "/" + m.Name)) +
                "; ACTGRP: " + descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.program.activationGroup") +
                "; services: " + string.Join(", ", definition.ServiceReferences.Select(r => (r.LookupLibrary ?? r.Library) + "/" + r.Name)));
        }
        catch (Exception error) when (error is InvalidDataException or RpgCompileException) { return CommandResult.Error("IPC0006: " + error.Message); }
    }
}
