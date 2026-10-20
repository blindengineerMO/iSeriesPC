using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal sealed class ExportStorage
    {
        internal RpgRuntimeContext? ProcedureRoot;
        internal RpgProcedurePointerRegistry? Pointers;
        internal RpgDataPointerRegistry? DataPointers;
        internal bool HasDataAddresses;
        internal Dictionary<RpgDataStructure, (byte[] Bytes, object Owner)> CycleValues { get; } = new();
        internal bool ProcedureActivationEnded, HasProcedureAddresses;
        internal Action? ValidateProcedureActivation;
        internal Func<IDisposable?>? EnterProcedureActivation;
        internal Dictionary<RpgDataStructure, (byte[] Bytes, object Owner)> Values { get; } = new();
        internal HashSet<(RpgDataStructure, RpgDsElement, int)> Nulls { get; } = new();
        internal Dictionary<(object, RpgDataStructure, RpgDsElement), object> NullOwners { get; } = new();
    }
    private readonly ExportStorage _exportStorage;
    private readonly Dictionary<RpgDataStructure, (NativeView Root, ProgramArgumentNullMap Map)> _importedNullMaps = new();
    private bool ExportedStructure(RpgDataStructure structure) => _program.DataDefinitions.Any(d => !d.Import &&
        (d.Name.Equals(structure.Name, StringComparison.OrdinalIgnoreCase) || structure.Anonymous && structure.Elements.Any(e => e.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))));
    internal void InitializeExportedData()
    {
        foreach (var module in RpgCompiler.ProgramModules(_program).Where(m => m.DataDefinitions.Any(d => !d.Import)))
            _ = BoundModuleContext(module, _host);
    }
    internal (ProgramArgumentMemory Memory, ProgramArgumentNullMap Nulls) DataStorage(string name)
    {
        if (!TryNativeReference(new RpgFieldRef(name), out var view)) throw NativeFailure(105, "Exported data has no native storage: " + name);
        if (view.Structure.IsMultipleOccurrence && view.Element is null)
            view = view with { Offset = 0, Width = view.Bytes.Length, IsArray = true, Count = view.Structure.Occurrences };
        var formal = new RpgField { Name = name, Length = view.Width, IsDataStructure = true };
        return (NativeMemory(view), CaptureNullArgument(view, formal));
    }
    private NativeView BindImportedData(string name, NativeView view)
    {
        if (!_program.BoundData.TryGetValue(name, out var binding))
        {
            if (_program.DataDefinitions.Any(d => d.Import && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw NativeFailure(202, "Unbound imported data: " + name);
            return view;
        }
        (ProgramArgumentMemory Memory, ProgramArgumentNullMap Nulls) data;
        if (binding.Static is { } target) data = BoundModuleContext(target.Module, _host).DataStorage(target.Definition.Name);
        else if (binding.Service is { } service && _host.ServiceData is { } resolve)
        {
            var resolved = resolve(service, _host); data = (resolved.Memory, resolved.Nulls);
        }
        else throw NativeFailure(202, "No service data host is available.");
        if (data.Memory.Ccsid != NativeCcsid) throw NativeFailure(452, "Imported data requires the exporter's original CCSID.");
        var whole = view.Structure.IsMultipleOccurrence && view.Element is null ? view with { Offset = 0, Width = view.Bytes.Length, IsArray = true, Count = view.Structure.Occurrences } : view;
        _importedNullMaps[view.Structure] = (whole, data.Nulls);
        var memory = view.Structure.IsMultipleOccurrence && view.Element is null ? data.Memory.Slice(view.Offset, view.Width) : data.Memory;
        var argument = new ProgramArgument(() => memory.Read(0, memory.Capacity), _ => { }, value => value, "*RAW", memory.Capacity, 0)
            { Memory = memory, NullMap = data.Nulls, Shape = memory.Shape };
        return AttachNativeBinding(view, MemoryBinding(argument, memory, view));
    }
}
