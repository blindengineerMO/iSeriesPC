using System.Text;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private sealed record RpgBindingSet(IReadOnlyList<RpgProgram> Modules, IReadOnlyList<(RpgServiceReference Reference, RpgServiceDefinition Definition)> Services, IReadOnlyList<string> Diagnostics)
    {
        public string Message(string text) => Diagnostics.Count == 0 ? text : text + "\n" + string.Join('\n', Diagnostics);
    }
    private RpgBindingSet DiscoverRpgBindings(IReadOnlyList<RpgProgram> initial, CommandCall call, IReadOnlyList<RpgServiceReference>? existing = null)
    {
        var additional = ServiceDefinitions(call, existing);
        var services = (existing ?? []).Select(reference =>
        {
            var descriptor = LoadRpgService(reference.Library + "/" + reference.Name);
            var definition = RpgServiceImage.Deserialize(descriptor.Source!).Restore(_cancellationToken);
            var mode = additional.FirstOrDefault(s => s.Reference.Library == reference.Library && s.Reference.Name == reference.Name).Reference?.Activation ?? reference.Activation;
            return (Reference: definition.Reference(descriptor.Library, descriptor.Name) with { Activation = mode, LookupLibrary = reference.LookupLibrary }, Definition: definition);
        }).Concat(additional).DistinctBy(s => (s.Reference.Library, s.Reference.Name)).ToList();
        var modules = initial.ToList();
        var directoryNames = call.Split("BNDDIR");
        if (directoryNames.Count == 1 && CommandParser.Unquote(directoryNames[0]).Equals("*NONE", StringComparison.OrdinalIgnoreCase)) directoryNames = [];
        if (directoryNames.Count > 16) throw new InvalidDataException("BNDDIR accepts at most 16 explicit directories.");
        var store = new BindingDirectoryStore(_system.Connections); var candidates = new List<BindingDirectoryEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal); var entryKeys = new HashSet<(string, string, string)>();
        void AppendDirectory(string name)
        {
            var key = ResolveBindingDirectory(name, Authorities.UseBits);
            if (!directories.Add(key.Library + "/" + key.Name)) return;
            if (directories.Count > 16) throw new InvalidDataException("Binding accepts at most 16 distinct directories.");
            foreach (var entry in store.Read(key.Library, key.Name)) if (entryKeys.Add((entry.Library, entry.Name, entry.Type))) candidates.Add(entry);
        }
        foreach (var name in directoryNames) AppendDirectory(name);
        var sourceDirectories = new Queue<string>(modules.SelectMany(m => m.BindingDirectories));
        var examined = new Dictionary<BindingDirectoryEntry, (RpgProgram? Module, RpgServiceReference? Reference, RpgServiceDefinition? Definition)>();
        var selected = new HashSet<BindingDirectoryEntry>(); var missing = new HashSet<BindingDirectoryEntry>(); var diagnostics = new List<string>(); long examinedBytes = 0;
        IReadOnlySet<string> Unresolved() => RpgCompiler.UnresolvedModuleImports(modules, services.Select(s => s.Reference).ToArray());
        var unresolved = Unresolved(); bool progress;
        do
        {
            progress = false;
            foreach (var entry in candidates)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (unresolved.Count == 0) break;
                if (selected.Contains(entry) || missing.Contains(entry)) continue;
                if (!examined.TryGetValue(entry, out var candidate))
                {
                    try
                    {
                        var descriptor = entry.Type == ObjectType.Module ? LoadRpgModule(entry.Library + "/" + entry.Name) : LoadRpgService(entry.Library + "/" + entry.Name);
                        examinedBytes += Encoding.UTF8.GetByteCount(descriptor.Source ?? "") + (descriptor.ExtendedAttributes?.Sum(p => (long)Encoding.UTF8.GetByteCount(p.Value)) ?? 0);
                        if (examinedBytes > 67108864) throw new InvalidDataException("Examined binding-directory artifacts exceed 64 MiB.");
                        if (entry.Type == ObjectType.Module) candidate = (RpgModuleImage.Capture(descriptor).Restore(_cancellationToken), null, null);
                        else
                        {
                            var definition = RpgServiceImage.Deserialize(descriptor.Source!).Restore(_cancellationToken);
                            candidate = (null, definition.Reference(descriptor.Library, descriptor.Name) with { Activation = entry.Activation }, definition);
                        }
                        examined.Add(entry, candidate);
                    }
                    catch (CpfException error) when (error.MessageId == "CPF9801")
                    {
                        missing.Add(entry); diagnostics.Add($"CPD5D1D: {entry.Type} {entry.Library}/{entry.Name} not found for binding."); continue;
                    }
                }
                if (candidate.Module is { } module)
                {
                    if (!RpgCompiler.ModuleExports([module]).Keys.Concat(RpgCompiler.ModuleDataExports([module]).Keys).Any(unresolved.Contains)) continue;
                    selected.Add(entry);
                    if (modules.Any(m => m.Library == module.Library && m.Name == module.Name)) continue;
                    if (modules.Count >= 16) throw new InvalidDataException("Binding accepts at most 16 selected modules.");
                    modules.Add(module); foreach (var name in module.BindingDirectories) sourceDirectories.Enqueue(name);
                }
                else
                {
                    var reference = candidate.Reference!;
                    if (!reference.Exports.Any(unresolved.Contains)) continue;
                    selected.Add(entry);
                    if (services.Any(s => s.Reference.Library == reference.Library && s.Reference.Name == reference.Name)) continue;
                    if (services.Count >= 16) throw new InvalidDataException("Binding accepts at most 16 selected services.");
                    services.Add((reference, candidate.Definition!));
                }
                progress = true; unresolved = Unresolved();
            }
            if (!progress && unresolved.Count != 0 && sourceDirectories.TryDequeue(out var sourceDirectory))
            { AppendDirectory(sourceDirectory); progress = true; }
        } while (progress && unresolved.Count != 0);
        return new(modules, services, diagnostics);
    }
}
