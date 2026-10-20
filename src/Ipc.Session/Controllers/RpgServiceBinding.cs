using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;
using System.Runtime.CompilerServices;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private ObjectDescriptor LoadRpgService(string text)
    {
        var (library, name) = SplitQualified(CommandParser.Unquote(text), LibraryUnknown);
        foreach (var candidate in SearchLibraries(library))
        {
            var descriptor = _system.Objects.Get(candidate, name, ObjectType.ServiceProgram);
            if (descriptor is null) continue;
            _system.ObjectSigning.RequireExecutable(descriptor);
            if (descriptor.Attribute != RpgServiceImage.Attribute || descriptor.Source is null) throw new InvalidDataException("Invalid source-bound RPG service program.");
            var group = descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.program.activationGroup") ?? "*CALLER";
            if (group != "*CALLER" && (!ObjectName.IsValid(group) || group != group.ToUpperInvariant())) throw new InvalidDataException("Invalid service activation group.");
            return descriptor;
        }
        throw new CpfException("CPF9801", "Service program not found: " + text);
    }
    private IReadOnlyList<(RpgServiceReference Reference, RpgServiceDefinition Definition)> ServiceDefinitions(CommandCall call, IReadOnlyList<RpgServiceReference>? existing)
    {
        var names = call.Split("BNDSRVPGM"); if (names.Count == 1 && CommandParser.Unquote(names[0]).Equals("*NONE", StringComparison.OrdinalIgnoreCase)) return [];
        if (names.Count == 2 && CommandParser.Unquote(names[1]).StartsWith('*') && !names[0].StartsWith('(')) names = [call.GetOption("BNDSRVPGM")!];
        if (names.Count > 16) throw new InvalidDataException("BNDSRVPGM accepts at most 16 explicit service names.");
        var result = names.Select(n =>
        {
            var terms = BindingEntryTerms(n);
            if (terms.Count is < 1 or > 2) throw new InvalidDataException("BNDSRVPGM entries use (library/service activation).");
            var update = call.Name.Equals("UPDPGM", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("UPDSRVPGM", StringComparison.OrdinalIgnoreCase);
            var mode = terms.Count == 2 ? terms[1] : update ? "*SAME" : "*IMMED";
            if (mode is not ("*IMMED" or "*DEFER") && !(update && mode == "*SAME")) throw new InvalidDataException("Invalid BNDSRVPGM activation mode.");
            var descriptor = LoadRpgService(terms[0]); var definition = RpgServiceImage.Deserialize(descriptor.Source!).Restore(_cancellationToken);
            if (mode == "*SAME") mode = existing?.FirstOrDefault(r => r.Library == descriptor.Library && r.Name == descriptor.Name)?.Activation ?? "*IMMED";
            return (Reference: definition.Reference(descriptor.Library, descriptor.Name) with { Activation = mode }, Definition: definition);
        }).ToArray();
        RpgServiceReference.ValidateList(result.Select(s => s.Reference).ToArray()); return result;
    }
    private RpgBindingSet BindRpgModules(IReadOnlyList<RpgProgram> modules, RpgProgram entry, CommandCall call, IReadOnlyList<RpgServiceReference>? existing = null)
    {
        var set = DiscoverRpgBindings(modules, call, existing);
        RpgCompiler.BindModules(set.Modules, entry, _cancellationToken, set.Services.Select(s => s.Reference).ToArray());
        foreach (var service in set.Services.Where(s => entry.ServiceReferences.Contains(s.Reference)))
            RpgCompiler.ValidateServiceInterfaces(entry, service.Reference, service.Definition);
        return set;
    }
    private RpgBindingSet BindRpgServiceModules(IReadOnlyList<RpgProgram> modules, CommandCall call, IReadOnlyList<RpgServiceReference>? existing = null)
    {
        var set = DiscoverRpgBindings(modules, call, existing);
        var used = RpgCompiler.BindServiceModules(set.Modules, _cancellationToken, set.Services.Select(s => s.Reference).ToArray());
        foreach (var service in set.Services.Where(s => used.Contains(s.Reference)))
            RpgCompiler.ValidateServiceInterfaces(set.Modules, service.Reference, service.Definition);
        return set with { Services = set.Services.Where(s => used.Contains(s.Reference)).ToArray() };
    }
    private int _rpgServiceDepth;
    private readonly Dictionary<string, RpgServiceState> _ephemeralServices = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<RpgServiceReference, RpgServiceLookup> RpgServiceLookups = new();
    private static readonly ConditionalWeakTable<object, RpgProcedurePointerRegistry> RpgPointerRegistries = new();
    private RpgProcedurePointerRegistry _ephemeralProcedurePointers = new();
    private static readonly ConditionalWeakTable<object, RpgDataPointerRegistry> RpgDataPointerRegistries = new();
    private RpgDataPointerRegistry _ephemeralDataPointers = new();
    private RpgDataPointerRegistry RpgDataRegistry() => _job is not null && _system.JobRuntime.Groups(_job.Key) is { } groups
        ? RpgDataPointerRegistries.GetValue(groups, _ => new()) : _ephemeralDataPointers;
    private RpgProcedurePointerRegistry RpgPointerRegistry() => _job is not null && _system.JobRuntime.Groups(_job.Key) is { } groups
        ? RpgPointerRegistries.GetValue(groups, _ => new()) : _ephemeralProcedurePointers;
    private void ConfigureRpgProcedureActivation(RpgRuntimeContext context, ObjectDescriptor descriptor)
    {
        var enterOwner = _job is null ? null : _system.JobRuntime.Groups(_job.Key)?.CaptureProcedureScope();
        void Validate()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var live = _system.Objects.GetRequired(descriptor.Library, descriptor.Name, descriptor.ObjectType);
            _system.ObjectSigning.RequireExecutable(live); _system.ObjectSigning.RequireExecutable(descriptor);
        }
        IDisposable? Enter()
        {
            var activation = enterOwner?.Invoke();
            try { return new RpgServiceScope(activation, _job is null ? null : _system.JobRuntime.Environment(_job.Key)?.EnterCall(descriptor.Name, false)); }
            catch { activation?.Dispose(); throw; }
        }
        context.ConfigureProcedureActivation(Validate, Enter);
    }
    private sealed record RpgServiceLookup(string Key);
    private sealed record RpgServiceBinding(WeakReference<RpgServiceState> State);
    private sealed record RpgServiceState(ObjectDescriptor Descriptor, RpgServiceRuntime Runtime) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }

    private RpgServiceState ActivateRpgService(RpgServiceReference reference, RpgHost host)
    {
        var groups = _job is null ? null : _system.JobRuntime.Groups(_job.Key);
        // A *LIBL connection belongs to this caller image; another caller can resolve a different library.
        var key = "SRVBIND:" + (reference.LookupLibrary is null ? reference.Library + "/" + reference.Name :
            "*LIBL:" + RpgServiceLookups.GetValue(reference, _ => new(Guid.NewGuid().ToString("N"))).Key);
        RpgServiceState Create()
        {
            var live = LoadRpgService((reference.LookupLibrary ?? reference.Library) + "/" + reference.Name);
            RpgServiceState Capture()
            {
                var runtime = new RpgServiceRuntime(RpgServiceImage.Deserialize(live.Source!).Restore(_cancellationToken));
                var enterOwner = groups?.CaptureProcedureScope();
                void Validate()
                {
                    _ = LoadRpgService(live.Library + "/" + live.Name); _system.ObjectSigning.RequireExecutable(live); runtime.ValidateHost(host);
                }
                IDisposable? Enter()
                {
                    var activation = enterOwner?.Invoke();
                    try { return new RpgServiceScope(activation, _job is null ? null : _system.JobRuntime.Environment(_job.Key)?.EnterCall(live.Name, false)); }
                    catch { activation?.Dispose(); throw; }
                }
                runtime.ConfigureProcedureActivation(Validate, Enter); return new(live, runtime);
            }
            var ownerKey = "SRVSTATE:" + live.Library + "/" + live.Name;
            if (groups is null)
            {
                if (!_ephemeralServices.TryGetValue(ownerKey, out var captured)) _ephemeralServices.Add(ownerKey, captured = Capture());
                return captured;
            }
            using var activation = groups.Enter(live);
            return groups.Resource(ownerKey, Capture);
        }
        RpgServiceState binding;
        if (groups is null)
        {
            if (!_ephemeralServices.TryGetValue(key, out binding!)) _ephemeralServices.Add(key, binding = Create());
        }
        else
        {
            var cached = groups.Resource(key, () => new RpgServiceBinding(new(Create())));
            if (!cached.State.TryGetTarget(out binding!) || binding.Runtime.IsDisposed)
            {
                groups.RemoveResource(key);
                cached = groups.Resource(key, () => new RpgServiceBinding(new(Create())));
                if (!cached.State.TryGetTarget(out binding!)) throw new InvalidOperationException("Service activation has no owning group.");
            }
        }
        // Recheck the actual connected object, even if the library list has since changed.
        _ = LoadRpgService(binding.Descriptor.Library + "/" + binding.Descriptor.Name);
        _system.ObjectSigning.RequireExecutable(binding.Descriptor);
        binding.Runtime.ValidateHost(host);
        // A retained activation accepts only signatures and export positions it actually contains.
        var level = binding.Runtime.Definition.Levels.FirstOrDefault(l => l.Signature == reference.Signature);
        if (level is null || !level.Exports.SequenceEqual(reference.Exports, StringComparer.Ordinal) || !level.Exports.Where(binding.Runtime.Definition.Data.ContainsKey).SequenceEqual(reference.DataExports, StringComparer.Ordinal))
            throw new InvalidDataException("Service-program signature or export position mismatch: " + reference.Library + "/" + reference.Name);
        using (groups?.Enter(binding.Descriptor)) binding.Runtime.InitializeExportedData(host);
        return binding;
    }
    private void PreflightRpgServices(RpgProgram program, RpgHost host)
        => PreflightRpgServices(program, null, host);
    private static bool NeedsServiceActivation(IReadOnlyList<RpgProgram> modules, RpgServiceReference reference)
        => reference.Activation == "*IMMED" || RpgCompiler.ServiceDataImports(modules, reference).Count > 0 ||
            modules.Any(m => RpgCompiler.ProcedureAddressSymbols(m).Any(s => reference.Exports.Contains(s, StringComparer.Ordinal)));
    private void PreflightRpgServices(RpgProgram? program, RpgServiceState? root, RpgHost host)
    {
        var visited = new HashSet<RpgServiceState>();
        var groups = _job is null ? null : _system.JobRuntime.Groups(_job.Key);
        void Visit(IReadOnlyList<RpgProgram>? callerModules, RpgServiceReference reference, int depth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var binding = ActivateRpgService(reference, host);
            if (callerModules is null) RpgCompiler.ValidateServiceInterfaces(program!, reference, binding.Runtime.Definition);
            else RpgCompiler.ValidateServiceInterfaces(callerModules, reference, binding.Runtime.Definition);
            using var activation = groups?.Enter(binding.Descriptor);
            foreach (var data in RpgCompiler.ServiceDataImports(callerModules ?? RpgCompiler.ProgramModules(program!), reference))
                _ = binding.Runtime.DataStorage(binding.Runtime.Definition.ResolveData(data), host);
            if (!visited.Add(binding)) return;
            if (depth > 64 || visited.Count > 256) throw new InvalidDataException("Service activation graph exceeds 64 levels or 256 activations.");
            foreach (var dependency in binding.Runtime.Definition.ServiceReferences.Where(r => NeedsServiceActivation(binding.Runtime.Definition.Modules, r)))
                Visit(binding.Runtime.Definition.Modules, dependency, depth + 1);
        }
        if (root is null)
        {
            foreach (var reference in program!.ServiceReferences.Where(r => NeedsServiceActivation(RpgCompiler.ProgramModules(program), r))) Visit(null, reference, 1);
        }
        else
        {
            visited.Add(root);
            using var activation = groups?.Enter(root.Descriptor);
            foreach (var reference in root.Runtime.Definition.ServiceReferences.Where(r => NeedsServiceActivation(root.Runtime.Definition.Modules, r)))
                Visit(root.Runtime.Definition.Modules, reference, 2);
        }
    }
    private RpgServiceProcedure ResolveRpgService(RpgServiceImport import, RpgHost host)
    {
        var binding = ActivateRpgService(import.Service, host); var procedure = binding.Runtime.Definition.Resolve(import);
        // Deferred roots still activate their immediate dependency closure before any procedure effects.
        PreflightRpgServices(null, binding, host);
        IDisposable? Enter()
        {
            binding.Runtime.ValidateHost(host);
            var groups = _job is null ? null : _system.JobRuntime.Groups(_job.Key);
            var activation = groups?.Enter(binding.Descriptor);
            try
            {
                var environment = _job is null ? null : _system.JobRuntime.Environment(_job.Key)?.EnterCall(binding.Descriptor.Name, false);
                return new RpgServiceScope(activation, environment);
            }
            catch { activation?.Dispose(); throw; }
        }
        void Validate()
        {
            _ = LoadRpgService(binding.Descriptor.Library + "/" + binding.Descriptor.Name);
            _system.ObjectSigning.RequireExecutable(binding.Descriptor); binding.Runtime.ValidateHost(host);
        }
        return new(procedure, Enter, () => binding.Runtime.ModuleContext(procedure, host), Validate);
    }
    private RpgServiceData ResolveRpgServiceData(RpgServiceImport import, RpgHost host)
    {
        var binding = ActivateRpgService(import.Service, host); var data = binding.Runtime.Definition.ResolveData(import);
        PreflightRpgServices(null, binding, host);
        using var activation = _job is null ? null : _system.JobRuntime.Groups(_job.Key)?.Enter(binding.Descriptor);
        return binding.Runtime.DataStorage(data, host);
    }
    private sealed class RpgServiceScope(IDisposable? activation, IDisposable? environment) : IDisposable
    {
        public void Dispose() { try { environment?.Dispose(); } finally { activation?.Dispose(); } }
    }
    private void EndRpgServiceCall()
    {
        if (--_rpgServiceDepth != 0) return;
        try { foreach (var binding in _ephemeralServices.Values.Distinct()) binding.Runtime.Dispose(); }
        finally { _ephemeralServices.Clear(); _ephemeralProcedurePointers = new(); _ephemeralDataPointers = new(); }
    }
}
