using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private ObjectDescriptor LoadRpgUpdateTarget(string text, bool service)
    {
        var (library, name) = SplitQualified(text, LibraryUnknown); var type = service ? ObjectType.ServiceProgram : ObjectType.Program;
        foreach (var candidate in SearchLibraries(library))
            if (_system.Objects.Get(candidate, name, type) is { } descriptor)
            {
                _system.ObjectSigning.RequireExecutable(descriptor);
                new RpgObjectUpdateStore(_system.Connections, _system.ObjectSigning).Authorize(descriptor); return descriptor;
            }
        throw new CpfException("CPF9801", "Update target not found: " + text);
    }
    private IReadOnlyList<RpgModuleImage> ReplaceRpgModules(IReadOnlyList<RpgModuleImage> original, CommandCall call)
    {
        var names = call.Split("MODULE");
        if (names.Count is < 1 or > 16) throw new InvalidDataException("MODULE requires *NONE or 1 through 16 module specifications.");
        var policy = CommandParser.Unquote(call.GetOption("RPLLIB") ?? "*ONLY").ToUpperInvariant();
        if (policy is not ("*ONLY" or "*FIRST" or "*MODULE") && !ObjectName.IsValid(policy)) throw new InvalidDataException("RPLLIB must be *ONLY, *FIRST, *MODULE or a library name.");
        if (names.Count == 1 && CommandParser.Unquote(names[0]).Equals("*NONE", StringComparison.OrdinalIgnoreCase)) return original;
        var result = original.ToArray(); var replaced = new HashSet<int>();
        foreach (var specification in names)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (library, pattern) = SplitQualified(CommandParser.Unquote(specification), LibraryUnknown);
            var generic = pattern == "*ALL" || pattern.EndsWith('*');
            var stem = pattern.EndsWith('*') ? pattern[..^1] : pattern;
            if (pattern != "*ALL" && !ObjectName.IsValid(stem)) throw new InvalidDataException("Invalid replacement module specification.");
            var candidates = generic ? original.Select(m => m.Name).Distinct(StringComparer.Ordinal).Where(n => pattern == "*ALL" || NamePattern.Matches(n, pattern)).Select(n => library + "/" + n).ToArray() : [specification];
            var matched = false;
            foreach (var candidate in candidates)
            {
                ObjectDescriptor replacement;
                try { replacement = LoadRpgModule(candidate); }
                catch (CpfException error) when (generic && error.MessageId is "CPF9801" or "CPF9802") { continue; }
                var indices = original.Select((image, index) => (image, index)).Where(p => p.image.Name == replacement.Name &&
                    (policy == "*MODULE" ? p.image.Library == replacement.Library : ObjectName.IsValid(policy) ? p.image.Library == policy : true)).Select(p => p.index).ToArray();
                if (indices.Length == 0 || policy == "*ONLY" && indices.Length != 1) throw new InvalidDataException("Replacement must select one existing module under RPLLIB: " + replacement.Name);
                var index = indices[0]; if (!replaced.Add(index)) throw new InvalidDataException("A bound module cannot be replaced twice in one update.");
                var image = RpgModuleImage.Capture(replacement); _ = image.Restore(_cancellationToken);
                result[index] = image with { Library = original[index].Library, Name = original[index].Name }; matched = true;
            }
            if (!matched) throw new InvalidDataException("No bound replacement module matched: " + specification);
        }
        return result;
    }
    private static SourceDocument SavedExportSource(ObjectDescriptor descriptor, IReadOnlyList<RpgExportLevel> levels)
        => new(descriptor.Library + "/" + descriptor.Name + "(*CURRENT)", string.Join('\n', levels.Select(level =>
            "STRPGMEXP PGMLVL(" + level.Level + ") SIGNATURE(X'" + level.Signature + "')\n" +
            string.Join('\n', level.Exports.Select(symbol => "EXPORT SYMBOL('" + symbol + "')")) + "\nENDPGMEXP")));
    private IReadOnlyList<RpgServiceReference> RelocateRpgServices(ObjectDescriptor target, IReadOnlyList<RpgServiceReference> references, CommandCall call)
    {
        var policy = CommandParser.Unquote(call.GetOption("SRVPGMLIB") ?? "*SAME").ToUpperInvariant();
        if (policy is not ("*SAME" or "*LIBL") && !ObjectName.IsValid(policy)) throw new InvalidDataException("SRVPGMLIB requires *SAME, *LIBL or a library name.");
        if (policy != "*SAME" && target.ExtendedAttributes?.GetValueOrDefault("ipc.program.allowLibraryUpdate") != "*YES")
            throw new CpfException("CPF5CA7", "SRVPGMLIB must be *SAME when ALWLIBUPD is *NO.");
        return references.Select(reference =>
        {
            var preferred = policy == "*SAME" ? reference.LookupLibrary ?? reference.Library : policy;
            ObjectDescriptor provider; var found = true;
            try { provider = LoadRpgService(preferred + "/" + reference.Name); }
            catch (CpfException error) when (error.MessageId == "CPF9801" && preferred != reference.Library)
            { found = false; provider = LoadRpgService(reference.Library + "/" + reference.Name); }
            return reference with { Library = provider.Library, LookupLibrary = found ? preferred == "*LIBL" ? "*LIBL" : null : reference.LookupLibrary };
        }).ToArray();
    }
    private SourceDocument UpdateBinderSource(CommandCall call, string name)
    {
        var resolver = new Ipc.Session.Sources.ProgramSourceResolver(_system, _job, _cancellationToken);
        var stream = CommandParser.Unquote(call.GetOption("SRCSTMF")); var member = CommandParser.Unquote(call.GetOption("SRCMBR") ?? "*SRVPGM");
        if (member.Equals("*SRVPGM", StringComparison.OrdinalIgnoreCase)) member = name;
        if (stream.Length > 0 && call.GetOption("SRCFILE", "SRCMBR") is not null) throw new InvalidDataException("Choose stream or member binder source.");
        return stream.Length > 0 ? resolver.Stream(stream) : resolver.Member(CommandParser.Unquote(call.GetOption("SRCFILE") ?? "QSRVSRC"), member);
    }
    private CommandResult ExecuteUpdateRpgObject(CommandCall call)
    {
        var service = call.Name.Equals("UPDSRVPGM", StringComparison.OrdinalIgnoreCase);
        var target = CommandParser.Unquote(call.GetOption(service ? "SRVPGM" : "PGM"));
        if (target.Length == 0 || call.GetOption("MODULE") is null) return CommandResult.Error(call.Name + " requires its target and MODULE(names | *NONE).");
        try
        {
            var descriptor = LoadRpgUpdateTarget(target, service); string source; RpgBindingSet binding;
            var activationGroup = RpgObjectUpdateStore.UpdatedActivationGroup(descriptor, CommandParser.Unquote(call.GetOption("ACTGRP") ?? "*SAME"));
            if (service)
            {
                if (descriptor.Attribute != RpgServiceImage.Attribute || descriptor.Source is null) throw new InvalidDataException("A source-bound RPG service is required.");
                var original = RpgServiceImage.Deserialize(descriptor.Source); _ = original.Restore(_cancellationToken);
                var references = RelocateRpgServices(descriptor, original.Services, call);
                var modules = ReplaceRpgModules(original.Modules, call).Select(m => m.Restore(_cancellationToken)).ToArray(); binding = BindRpgServiceModules(modules, call, references);
                var mode = CommandParser.Unquote(call.GetOption("EXPORT") ?? "*CURRENT").ToUpperInvariant();
                if (mode is not ("*CURRENT" or "*ALL" or "*SRCFILE")) throw new InvalidDataException("EXPORT must be *CURRENT, *ALL or *SRCFILE.");
                if (mode != "*SRCFILE" && call.GetOption("SRCFILE", "SRCMBR", "SRCSTMF") is not null) throw new InvalidDataException("Binder source options require EXPORT(*SRCFILE).");
                SourceDocument? binder = mode == "*CURRENT" ? original.BinderSource ?? SavedExportSource(descriptor, original.Levels) : mode == "*SRCFILE" ? UpdateBinderSource(call, descriptor.Name) : null;
                var exports = RpgCompiler.ModuleExports(binding.Modules); var data = RpgCompiler.ModuleDataExports(binding.Modules);
                var levels = mode == "*CURRENT" ? original.Levels : binder is null ? [RpgCompiler.GeneratedExports(exports.Keys.Concat(data.Keys).ToArray())] : RpgCompiler.ParseBinderSource(binder, _cancellationToken);
                if (levels[0].Exports.Any(symbol => !exports.ContainsKey(symbol) && !data.ContainsKey(symbol))) throw new InvalidDataException("Updated service no longer provides a required export.");
                var dataExports = levels[0].Exports.Where(data.ContainsKey).ToArray();
                if (mode == "*CURRENT" && !original.DataExports.SequenceEqual(dataExports, StringComparer.Ordinal)) throw new InvalidDataException("EXPORT(*CURRENT) cannot change a data/procedure export kind.");
                var dependencies = binding.Services.Select(s => s.Reference).ToArray();
                var image = new RpgServiceImage(dataExports.Length > 0 ? 5 : RpgServiceReference.ImageVersion(dependencies), binding.Modules.Select(RpgModuleImage.Capture).ToArray(), levels, binder) { Services = dependencies, DataExports = dataExports };
                source = image.Serialize(); _ = image.Restore(_cancellationToken);
            }
            else
            {
                if (descriptor.Source is null) throw new InvalidDataException("Saved RPG source is required.");
                RpgBoundProgram original;
                if (descriptor.Attribute == RpgBoundProgram.Attribute) original = RpgBoundProgram.Deserialize(descriptor.Source);
                else if (descriptor.Attribute == RpgAttribute)
                {
                    var attrs = new Dictionary<string, string>(descriptor.ExtendedAttributes ?? new Dictionary<string, string>()) { [RpgModuleImage.VersionAttribute] = "1" };
                    var module = RpgModuleImage.Capture(new ObjectDescriptor { Key = descriptor.Key, ObjectType = ObjectType.Module, Attribute = "RPGLE", Source = descriptor.Source, ExtendedAttributes = attrs });
                    original = new(1, module.Library + "/" + module.Name, [module]);
                }
                else throw new InvalidDataException("A source-bound RPG program is required.");
                _ = original.Restore(_cancellationToken);
                var references = RelocateRpgServices(descriptor, original.Services, call);
                var modules = ReplaceRpgModules(original.Modules, call).Select(m => m.Restore(_cancellationToken)).ToArray();
                var entry = modules.Single(m => m.Library + "/" + m.Name == original.Entry); binding = BindRpgModules(modules, entry, call, references);
                var image = new RpgBoundProgram(RpgServiceReference.ImageVersion(entry.ServiceReferences), original.Entry, binding.Modules.Select(RpgModuleImage.Capture).ToArray()) { Services = entry.ServiceReferences };
                source = image.Serialize(); _ = image.Restore(_cancellationToken);
            }
            new RpgObjectUpdateStore(_system.Connections, _system.ObjectSigning).Publish(descriptor, source, service ? RpgServiceImage.Attribute : RpgBoundProgram.Attribute, _cancellationToken, activationGroup);
            return CommandResult.Ok(binding.Message($"{call.Name.ToUpperInvariant()} completed for {descriptor.Library}/{descriptor.Name}."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or RpgCompileException)
        { return CommandResult.Error("IPC0006: " + error.Message); }
    }
}
