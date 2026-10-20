using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

internal sealed class RpgNullAddress
{
    private RpgNullAddress() { }
    internal static RpgNullAddress Value { get; } = new();
}

/// <summary>An opaque procedure address; its bytes are meaningful only in its execution registry.</summary>
public sealed class RpgProcedureAddress
{
    internal RpgProcedureAddress(Guid value) => Value = value;
    internal Guid Value { get; }
    public bool IsNull => Value == Guid.Empty;
    public static RpgProcedureAddress Null { get; } = new(Guid.Empty);
}

public sealed class RpgProcedurePointerRegistry
{
    internal sealed record Target(RpgSubprocedure Procedure, object Owner, Action Validate, Func<IDisposable?> Enter, Func<RpgHost, RpgRuntimeContext> Context);
    private readonly Dictionary<Guid, Target> _targets = new();
    internal RpgProcedureAddress Register(Target target)
    {
        foreach (var pair in _targets)
            if (ReferenceEquals(pair.Value.Owner, target.Owner) && ReferenceEquals(pair.Value.Procedure, target.Procedure)) return new(pair.Key);
        if (_targets.Count >= 4096) throw new RpgRuntimeException("Procedure address storage exceeds 4096 live targets.") { StatusCode = 202 };
        var id = Guid.NewGuid(); _targets.Add(id, target); return new(id);
    }
    internal Target Resolve(RpgProcedureAddress pointer)
    {
        if (pointer.IsNull || !_targets.TryGetValue(pointer.Value, out var target)) throw new RpgRuntimeException("Null, expired or foreign procedure address.") { StatusCode = 202, CallArgumentError = true };
        target.Validate(); return target;
    }
    internal void Release(object owner)
    {
        foreach (var id in _targets.Where(p => ReferenceEquals(p.Value.Owner, owner)).Select(p => p.Key).ToArray()) _targets.Remove(id);
    }
}

public sealed partial class RpgRuntimeContext
{
    public bool HasProcedureAddresses => _exportStorage.HasProcedureAddresses;
    public void ConfigureProcedureActivation(Action validate, Func<IDisposable?> enter)
    { _exportStorage.ValidateProcedureActivation = validate; _exportStorage.EnterProcedureActivation = enter; }
    public void ReleaseProcedureAddresses()
    {
        _exportStorage.ProcedureActivationEnded = true; _exportStorage.Pointers?.Release(_exportStorage); ReleaseDataAddresses();
    }
    internal RpgProcedurePointerRegistry PointerRegistry => _exportStorage.Pointers ??= _host.ProcedurePointers ?? new();
    internal RpgProcedureAddress ProcedureAddress(string symbol)
    {
        var local = _program.FindSubprocedure(symbol);
        var procedure = local?.NativeBody is not null && local.Name == symbol ? local : _program.FindBoundProcedure(symbol);
        RpgServiceProcedure? service = null;
        RpgRuntimeContext owner;
        if (procedure is null && _program.FindBoundService(symbol) is { } import)
        {
            service = _host.ServiceProcedure?.Invoke(import, _host) ?? throw NativeFailure(202, "No service procedure host is available.");
            procedure = service.Procedure; owner = service.ModuleContext();
        }
        else
        {
            if (procedure?.NativeBody?.GlobalProgram is not { } module) throw NativeFailure(202, "Unknown bound procedure address: " + symbol);
            owner = BoundModuleContext(module, _host);
        }
        var storage = owner._exportStorage; var target = procedure!; var serviceTarget = service;
        storage.Pointers ??= PointerRegistry;
        if (!ReferenceEquals(storage.Pointers, PointerRegistry)) throw NativeFailure(202, "The procedure belongs to another execution registry.");
        void Validate()
        {
            if (storage.ProcedureActivationEnded) throw NativeFailure(202, "The procedure activation has been reclaimed.");
            storage.ValidateProcedureActivation?.Invoke(); serviceTarget?.Validate();
        }
        IDisposable? Enter() => storage.EnterProcedureActivation?.Invoke() ?? serviceTarget?.Enter();
        RpgRuntimeContext Context(RpgHost host)
        {
            Validate(); return storage.ProcedureRoot!.BoundModuleContext(target.NativeBody!.GlobalProgram!, host);
        }
        storage.HasProcedureAddresses = true;
        return PointerRegistry.Register(new(target, storage, Validate, Enter, Context));
    }
    internal object EvaluateProcedureAddress(IReadOnlyList<RpgExpr> arguments)
    {
        if (arguments.Count != 1) throw NativeFailure(105, "%PADDR requires one procedure or prototype name.");
        if (arguments[0] is RpgFieldRef name && _program.FindPrototype(name.Name) is { PointerTarget: { } pointer })
        {
            return RpgExpressionParser.Parse(pointer).Eval(this) as RpgProcedureAddress ?? throw NativeFailure(105, "EXTPROC requires a procedure pointer.");
        }
        var symbol = RpgCompiler.ProcedureAddressSymbol(_program, arguments[0]) ?? throw NativeFailure(105, "Invalid procedure address.");
        return ProcedureAddress(symbol);
    }
}
