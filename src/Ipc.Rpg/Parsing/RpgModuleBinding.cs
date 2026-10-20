using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    public static RpgProgram CompileModule(string library, string name, SourceDocument source,
        Func<SourceDocument, string, SourceDocument>? resolver = null, CancellationToken cancellationToken = default,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null,
        Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null, Func<RpgExternalRequest, RpgExternalLayout>? externalResolver = null)
    {
        try { return CompileModule(library, name, new RpgSourcePreprocessor(resolver).Expand(source, cancellationToken), cancellationToken, workstationResolver, diskResolver, externalResolver); }
        catch (SourcePreprocessException error) { throw new RpgCompileException(error.Location, error.Message); }
    }
    public static RpgProgram CompileModule(string library, string name, PreprocessedSource source, CancellationToken cancellationToken = default,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null,
        Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null, Func<RpgExternalRequest, RpgExternalLayout>? externalResolver = null)
        => CompileUnit(library, name, source, cancellationToken, workstationResolver, diskResolver, externalResolver: externalResolver, moduleCompilation: true);

    private static void ValidateModuleBoundCalls(RpgProgram program)
    {
        if (!program.IsModule) return;
        foreach (var statement in program.MainStatements.Where(s => s.Opcode == RpgOpcode.CallB))
        {
            var name = (statement.Factor1 ?? statement.Value ?? "").Split('(')[0].Trim().Trim('\'');
            if (program.FindPrototype(name) is not { NativeProcedure: true })
                throw new RpgCompileException(statement.LineNumber, "A module CALLB requires a native procedure prototype.");
        }
    }
    private static RpgCompileException LinkError(RpgProgram module, int line, string message) =>
        module.Source is { } source && line > 0 && line <= source.Locations.Count ? new(source.Locations[line - 1], message) : new(line, message);

    private static IEnumerable<string> ModuleProcedureImports(RpgProgram module) => module.Prototypes.Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.Prototypes))
        .Where(p => p.NativeProcedure && p.PointerTarget is null).Select(p => p.ExternalName ?? p.Name)
        .Concat(module.ProcedureAddresses).Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.ProcedureAddresses))
        .Where(name => !module.Subprocedures.Any(s => s.NativeBody is not null && s.Name == name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<string> ModuleImports(RpgProgram module) => ModuleProcedureImports(module).Concat(module.DataDefinitions.Where(d => d.Import).Select(d => d.Symbol)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static IReadOnlySet<string> UnresolvedModuleImports(IReadOnlyList<RpgProgram> modules, IReadOnlyList<RpgServiceReference> services)
    {
        var exports = ModuleExports(modules); var data = ModuleDataExports(modules);
        return modules.SelectMany(ModuleProcedureImports).Where(name => !exports.ContainsKey(name) && !services.Any(s => s.Exports.Contains(name, StringComparer.Ordinal) && !s.DataExports.Contains(name, StringComparer.Ordinal)))
            .Concat(modules.SelectMany(m => m.DataDefinitions.Where(d => d.Import)).Select(d => d.Symbol).Where(name => !data.ContainsKey(name) && !services.Any(s => s.DataExports.Contains(name, StringComparer.Ordinal)))).ToHashSet(StringComparer.Ordinal);
    }

    public static RpgProgram BindModules(IReadOnlyList<RpgProgram> modules, RpgProgram entry, CancellationToken cancellationToken = default, IReadOnlyList<RpgServiceReference>? services = null)
    {
        if (entry is null) throw new RpgCompileException(0, "A program requires an entry module.");
        BindModuleSet(modules, entry, cancellationToken, services ?? []); return entry;
    }

    public static IReadOnlyList<RpgServiceReference> BindServiceModules(IReadOnlyList<RpgProgram> modules, CancellationToken cancellationToken = default, IReadOnlyList<RpgServiceReference>? services = null)
    {
        var references = services ?? []; BindModuleSet(modules, null, cancellationToken, references);
        return references.Where(s => modules.Any(m => m.BoundServices.Values.Any(i => ReferenceEquals(i.Service, s)) || m.BoundData.Values.Any(i => ReferenceEquals(i.Service?.Service, s)))).ToArray();
    }

    public static IReadOnlyDictionary<string, RpgSubprocedure> ModuleExports(IReadOnlyList<RpgProgram> modules)
    {
        var exports = new Dictionary<string, RpgSubprocedure>(StringComparer.Ordinal);
        foreach (var module in modules)
            foreach (var procedure in module.Subprocedures.Where(p => p.IsExport && p.NativeBody is not null))
                if (!exports.TryAdd(procedure.Name, procedure)) throw new RpgCompileException(0, "Duplicate exported procedure: " + procedure.Name);
        return exports;
    }

    private static void BindModuleSet(IReadOnlyList<RpgProgram> modules, RpgProgram? entry, CancellationToken cancellationToken, IReadOnlyList<RpgServiceReference> services)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (modules.Count is < 1 or > 16 || modules.Any(m => m is null || !m.IsModule || m.GlobalProgram is not null) || modules.Distinct().Count() != modules.Count ||
            modules.Select(m => m.Library + "/" + m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != modules.Count || entry is not null && (!modules.Contains(entry) || entry.NoMain))
            throw new RpgCompileException(0, "Binding requires 1 through 16 distinct modules and an eligible entry module when creating a program.");
        if (modules.Sum(m => (long)System.Text.Encoding.UTF8.GetByteCount(m.Source?.Text ?? "")) > 4194304 ||
            modules.Sum(m => m.DataStructures.Where(d => !d.IsTemplate).Sum(d => (long)d.Length * d.StorageCount)) > 67108864)
            throw new RpgCompileException(0, "Bound module source or global storage exceeds 4/64 MiB.");
        RpgServiceReference.ValidateList(services);
        var exports = ModuleExports(modules);
        var dataExports = ModuleDataExports(modules);
        var links = new Dictionary<RpgProgram, Dictionary<string, RpgSubprocedure>>();
        var dataLinks = new Dictionary<RpgProgram, Dictionary<string, RpgDataBinding>>();
        var dynamicLinks = new Dictionary<RpgProgram, Dictionary<string, RpgServiceImport>>();
        foreach (var module in modules)
        {
            var dataTargets = new Dictionary<string, RpgDataBinding>(StringComparer.OrdinalIgnoreCase); dataLinks.Add(module, dataTargets);
            foreach (var import in module.DataDefinitions.Where(d => d.Import))
            {
                if (dataExports.TryGetValue(import.Symbol, out var target))
                {
                    if (!DataCompatible(module, import, target)) throw LinkError(module, import.Line, "Imported data layout differs from its exporter: " + import.Symbol);
                    dataTargets.Add(import.Name, new(target, null));
                }
                else
                {
                    var service = services.FirstOrDefault(s => s.DataExports.Contains(import.Symbol, StringComparer.Ordinal));
                    if (service is null) throw LinkError(module, import.Line, "Unresolved module data: " + import.Symbol);
                    dataTargets.Add(import.Name, new(null, new(service, service.Exports.ToList().IndexOf(import.Symbol), import.Symbol)));
                }
            }
            var serviceTargets = new Dictionary<string, RpgServiceImport>(StringComparer.Ordinal); dynamicLinks.Add(module, serviceTargets);
            var targets = new Dictionary<string, RpgSubprocedure>(StringComparer.Ordinal); links.Add(module, targets);
            foreach (var symbol in module.ProcedureAddresses.Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.ProcedureAddresses)).Distinct(StringComparer.Ordinal))
            {
                if (module.Subprocedures.Any(p => p.NativeBody is not null && p.Name == symbol)) continue;
                if (exports.TryGetValue(symbol, out var target)) targets[symbol] = target;
                else
                {
                    var provider = services.FirstOrDefault(s => s.Exports.Contains(symbol, StringComparer.Ordinal) && !s.DataExports.Contains(symbol, StringComparer.Ordinal));
                    if (provider is null) throw LinkError(module, 0, "Unresolved procedure address: " + symbol);
                    serviceTargets[symbol] = new(provider, provider.Exports.ToList().IndexOf(symbol), symbol);
                }
            }
            foreach (var prototype in module.Prototypes.Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.Prototypes)).Distinct().Where(p => p.NativeProcedure && p.PointerTarget is null))
            {
                cancellationToken.ThrowIfCancellationRequested(); var name = prototype.ExternalName ?? prototype.Name;
                var procedure = module.Subprocedures.FirstOrDefault(p => p.Name == name && p.NativeBody is not null) ?? exports.GetValueOrDefault(name);
                if (procedure is null)
                {
                    var service = services.FirstOrDefault(s => s.Exports.Contains(name, StringComparer.Ordinal) && !s.DataExports.Contains(name, StringComparer.Ordinal));
                    if (service is null) throw LinkError(module, prototype.DeclarationLine, "Unresolved module procedure: " + name);
                    serviceTargets[name] = new(service, service.Exports.ToList().IndexOf(name), name); continue;
                }
                if (!ProcedureCompatible(prototype, procedure))
                    throw LinkError(module, prototype.DeclarationLine, "Bound procedure PI differs from its prototype: " + name);
                if (!module.Subprocedures.Contains(procedure)) targets[name] = procedure;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var module in modules)
        {
            module.BoundProcedures.Clear(); foreach (var link in links[module]) module.BoundProcedures.Add(link.Key, link.Value);
            module.BoundServices.Clear(); foreach (var link in dynamicLinks[module]) module.BoundServices.Add(link.Key, link.Value);
            module.BoundData.Clear(); foreach (var link in dataLinks[module]) module.BoundData.Add(link.Key, link.Value);
            module.IsBoundProgram = ReferenceEquals(module, entry);
        }
        if (entry is not null)
        {
            entry.BoundModules = modules.ToArray();
            entry.ServiceReferences = services.Where(s => dynamicLinks.Values.Any(links => links.Values.Any(i => ReferenceEquals(i.Service, s))) || dataLinks.Values.Any(links => links.Values.Any(i => ReferenceEquals(i.Service?.Service, s)))).ToArray();
        }
    }
    public static bool ProcedureCompatible(RpgPrototype prototype, RpgSubprocedure procedure)
    {
        if (procedure.NativeBody is not { } body || !body.NativeParameterLists.TryGetValue("/*ENTRY", out var parameters)) return false;
        return SameProcedureType(prototype.NativeReturnField, body.NativeReturnField) && parameters.Count == prototype.NativeParameters.Count &&
            parameters.Select(body.FindField).Zip(prototype.NativeParameters).All(pair => SameProcedureType(pair.First, pair.Second));
    }
    public static void ValidateServiceInterfaces(RpgProgram program, RpgServiceReference reference, RpgServiceDefinition service)
        => ValidateServiceInterfaces(program.BoundModules, reference, service);
    public static void ValidateServiceInterfaces(IReadOnlyList<RpgProgram> modules, RpgServiceReference reference, RpgServiceDefinition service)
    {
        foreach (var module in modules)
            foreach (var definition in module.DataDefinitions.Where(d => d.Import))
                if (module.BoundData.GetValueOrDefault(definition.Name)?.Service is { } import && ReferenceEquals(import.Service, reference) && !DataCompatible(module, definition, service.ResolveData(import)))
                    throw LinkError(module, definition.Line, "Service data layout differs from its import: " + import.Symbol);
        foreach (var module in modules)
            foreach (var prototype in module.Prototypes.Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.Prototypes)).Distinct().Where(p => p.NativeProcedure && p.PointerTarget is null))
                if (module.FindBoundService(prototype.ExternalName ?? prototype.Name) is { } import && import.Service.Library == reference.Library && import.Service.Name == reference.Name &&
                    !ProcedureCompatible(prototype, service.Resolve(import)))
                    throw LinkError(module, prototype.DeclarationLine, "Service procedure PI differs from its prototype: " + import.Symbol);
    }
}
