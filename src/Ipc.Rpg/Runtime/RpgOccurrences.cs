using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<string, int> _nativeOccurrences = new(StringComparer.OrdinalIgnoreCase);
    private int NativeOccurrenceOffset(RpgDataStructure ds) => ds.IsMultipleOccurrence ? (NativeOccurrence(ds) - 1) * ds.Length : 0;
    private int NativeOccurrence(RpgDataStructure ds) => _program.GlobalStructures.Contains(ds) ? GlobalContext!.NativeOccurrence(ds) : _nativeOccurrences.GetValueOrDefault(ds.Name, 1);
    private RpgDataStructure OccurrenceStructure(IReadOnlyList<RpgExpr> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is not RpgFieldRef field ||
            _program.FindDs(field.Name) is not { NativeStorage: true, IsMultipleOccurrence: true } ds)
            throw new RpgRuntimeException("%OCCUR requires one named multiple-occurrence native data structure.") { StatusCode = 105 };
        return ds;
    }
    internal void SetOccurrence(IReadOnlyList<RpgExpr> arguments, object? value, RpgExpr? source = null)
    {
        var ds = OccurrenceStructure(arguments);
        if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value) ||
            value is decimal numeric && (decimal.GetBits(numeric)[3] & 0x00FF0000) != 0 ||
            source is not null && HasNonIntegralSource(source))
            throw new RpgRuntimeException("%OCCUR requires a non-float numeric value with zero decimal places.") { StatusCode = 105 };
        var number = RpgValues.ToDecimal(value);
        if (number != decimal.Truncate(number)) throw new RpgRuntimeException("%OCCUR cannot select a fractional occurrence.") { StatusCode = 105 };
        if (number < 1 || number > ds.Occurrences) throw new RpgRuntimeException("Data structure occurrence is out of range.") { StatusCode = 122 };
        if (_program.GlobalStructures.Contains(ds)) GlobalContext!._nativeOccurrences[ds.Name] = (int)number;
        else _nativeOccurrences[ds.Name] = (int)number;
    }
    private bool HasNonIntegralSource(RpgExpr source)
    {
        if (source is RpgUnary unary) return HasNonIntegralSource(unary.Operand);
        if (source is RpgBinary binary) return HasNonIntegralSource(binary.Lhs) || HasNonIntegralSource(binary.Rhs);
        if (source is RpgFieldRef field && _program.FindField(field.Name) is { } declared &&
            (declared.Decimals != 0 || declared.Kind == RpgFieldKind.Float)) return true;
        if (source is RpgBuiltin { Name: "ABS", Arguments.Count: 1 } absolute)
            return HasNonIntegralSource(absolute.Arguments[0]);
        if (source is RpgBuiltin { Name: "DEC", Arguments.Count: 3 } conversion)
            return RpgValues.ToLong(conversion.Arguments[2].Eval(this)) != 0;
        return RpgCompiler.ExactActualType(_program, source) is { } type &&
            (type.Decimals != 0 || type.Kind == RpgFieldKind.Float);
    }
    internal bool TryNativeArgument(RpgExpr expression, out NativeView view)
    {
        if (!TryNativeReference(expression, out view)) return false;
        EnsureNativeParameterAvailable(view);
        // Native parameters pass the entire MO structure, but only the selected occurrence of a subfield.
        if (view.Element is null && view.Layout.IsMultipleOccurrence)
        {
            view = view with { Offset = 0, Width = view.Bytes.Length };
            if (view.Structure.BasedOn is not null) view = BindBasedStorage(view with { Binding = null });
        }
        return true;
    }
}
