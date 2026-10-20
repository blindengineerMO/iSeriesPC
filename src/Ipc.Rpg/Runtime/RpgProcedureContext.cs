using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal RpgRuntimeContext? GlobalContext { get; }
    internal Func<string, IReadOnlyList<RpgExpr>, object?>? NativeFunction { get; set; }
    internal bool HasProcedureReturn { get; set; }
    internal object? ProcedureReturn { get; set; }
    private Dictionary<RpgDataStructure, (byte[] Bytes, object Owner)> _procedureStaticStorage = new();
    private long _procedureStaticBytes;
    internal RpgRuntimeContext ModuleContext => GlobalContext?.ModuleContext ?? this;
    private bool IsGlobalName(string name)
    {
        if (GlobalContext is null) return false;
        if (_program.FindField(name) is { } field) return _program.GlobalFields.Contains(field);
        if (_program.FindDs(name) is { } ds) return _program.GlobalStructures.Contains(ds);
        return _program.FindDsElement(name) is { } element && _program.GlobalStructures.Any(d => d.Elements.Contains(element));
    }
    private bool IsGlobalExpression(RpgExpr expression)
    {
        return expression switch
        {
            RpgFieldRef field => IsGlobalName(field.Name), RpgDynamicIndexRef field => IsGlobalName(field.Name), RpgIndexedRef field => IsGlobalName(field.Name),
            RpgMemberRef member => IsGlobalExpression(member.Target), RpgElementRef element => IsGlobalExpression(element.Target),
            RpgBuiltin { Name: "SUBARR", Arguments.Count: > 0 } slice => IsGlobalExpression(slice.Arguments[0]), _ => false
        };
    }
    internal object? CallNativeFunction(string name, IReadOnlyList<RpgExpr> arguments) => NativeFunction?.Invoke(name, arguments)
        ?? throw new RpgRuntimeException("A procedure call in an expression requires a bound return value.") { StatusCode = 211 };
}
