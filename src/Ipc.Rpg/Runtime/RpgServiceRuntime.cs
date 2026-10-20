using Ipc.Rpg.Model;
using Ipc.Core.Work;

namespace Ipc.Rpg.Runtime;

public sealed record RpgServiceData(ProgramArgumentMemory Memory, ProgramArgumentNullMap Nulls);

/// <summary>A procedure target whose activation is entered after caller arguments have been captured.</summary>
public sealed class RpgServiceProcedure(RpgSubprocedure procedure, Func<IDisposable?> enter, Func<RpgRuntimeContext> context, Action? validate = null)
{
    public RpgSubprocedure Procedure { get; } = procedure;
    internal IDisposable? Enter() => enter();
    internal RpgRuntimeContext ModuleContext() => context();
    internal void Validate() => validate?.Invoke();
}

/// <summary>One service's module storage, owned by its activation group rather than any caller program.</summary>
public sealed class RpgServiceRuntime(RpgServiceDefinition definition) : IDisposable
{
    private RpgRuntimeContext? _root;
    private Action? _validateProcedureActivation;
    private Func<IDisposable?>? _enterProcedureActivation;
    public void ConfigureProcedureActivation(Action validate, Func<IDisposable?> enter)
    { _validateProcedureActivation = validate; _enterProcedureActivation = enter; }
    public RpgServiceDefinition Definition { get; } = definition;
    public bool IsDisposed { get; private set; }
    public void ValidateHost(RpgHost host)
    {
        if (IsDisposed) throw new RpgRuntimeException("The service activation has been reclaimed.") { StatusCode = 202 };
        host.CancellationToken.ThrowIfCancellationRequested();
        if (_root is not null && _root.NativeCcsid != host.Ccsid())
            throw new RpgRuntimeException("Service globals require their original CCSID.") { StatusCode = 452, CallArgumentError = true };
    }
    public RpgRuntimeContext ModuleContext(RpgSubprocedure procedure, RpgHost host)
    {
        if (procedure.NativeBody?.GlobalProgram is not { } owner) throw new InvalidDataException("Procedure does not belong to this service activation.");
        return ModuleContext(owner, host);
    }
    private RpgRuntimeContext ModuleContext(RpgProgram owner, RpgHost host)
    {
        ValidateHost(host);
        if (!Definition.Modules.Contains(owner)) throw new InvalidDataException("Module does not belong to this service activation.");
        _root ??= new(owner, host);
        if (_validateProcedureActivation is not null && _enterProcedureActivation is not null) _root.ConfigureProcedureActivation(_validateProcedureActivation, _enterProcedureActivation);
        _root.RebindHost(host); return _root.BoundModuleContext(owner, host);
    }
    public RpgServiceData DataStorage(RpgDataExport export, RpgHost host)
    {
        var data = ModuleContext(export.Module, host).DataStorage(export.Definition.Name);
        return new(data.Memory.Scoped(() => ValidateHost(host)), data.Nulls.Scoped(() => ValidateHost(host)));
    }
    public void InitializeExportedData(RpgHost host)
    {
        foreach (var module in Definition.Modules.Where(m => m.DataDefinitions.Any(d => !d.Import)))
            _ = ModuleContext(module, host);
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        var root = _root; _root = null;
        root?.ReleaseProcedureAddresses();
        try { root?.CloseWorkstations(); } finally { root?.CloseDiskFiles(); }
    }
}
