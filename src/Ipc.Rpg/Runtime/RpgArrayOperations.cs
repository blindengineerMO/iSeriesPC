using System.Numerics;
using System.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal static bool ContainsSubarray(RpgExpr expression) => expression switch
    {
        RpgBuiltin builtin => builtin.Name == "SUBARR" || builtin.Arguments.Any(ContainsSubarray),
        RpgMemberRef member => ContainsSubarray(member.Target),
        RpgElementRef element => ContainsSubarray(element.Target),
        RpgBinary binary => ContainsSubarray(binary.Lhs) || ContainsSubarray(binary.Rhs),
        RpgUnary unary => ContainsSubarray(unary.Operand),
        _ => false
    };
    private NativeView ArrayView(RpgExpr expression)
    {
        if (!TryNativeReference(expression, out var view) || !view.IsArray)
            throw NativeFailure(105, "This operation requires a native array.");
        return view;
    }
    private static void ArrayArguments(IReadOnlyList<RpgExpr> arguments, int minimum, int maximum)
    {
        if (arguments.Count < minimum || arguments.Count > maximum) throw NativeFailure(105, "Invalid array built-in argument count.");
    }
    private NativeView NativeSlice(IReadOnlyList<RpgExpr> arguments)
    {
        ArrayArguments(arguments, 2, 3);
        if (ContainsSubarray(arguments[0])) throw NativeFailure(105, "%SUBARR requires a declared array operand.");
        var view = ArrayView(arguments[0]); var start = NativeSubscript(arguments[1]);
        var count = arguments.Count == 3 ? NativeSubscript(arguments[2]) : view.Count - start + 1;
        return SliceArray(view, start, count);
    }
    private static void CheckArrayRange(int available, long start, long count)
    {
        if (start < 1 || start > available || count < 1 || count > available - start + 1)
            throw NativeFailure(121, "Array range is outside its declared dimension.");
    }
    private object SumArray(IReadOnlyList<RpgExpr> arguments)
    {
        ArrayArguments(arguments, 1, 1);
        if (arguments[0].Eval(this) is not object?[] values || values.Any(v => !RpgValues.IsNumeric(v)))
            throw NativeFailure(105, "%XFOOT requires a numeric array expression.");
        if (values.Any(RpgValues.IsFloat))
        {
            double total = 0;
            foreach (var value in values)
            {
                _host.CancellationToken.ThrowIfCancellationRequested(); total += RpgValues.ToDouble(value);
                if (!double.IsFinite(total)) throw NativeFailure(103, "Float array total exceeds its supported range.");
            }
            return total;
        }
        // Sum scaled coefficients exactly; decimal addition can silently round a small addend.
        BigInteger coefficient = 0; var scale = 0;
        foreach (var value in values)
        {
            _host.CancellationToken.ThrowIfCancellationRequested(); var bits = decimal.GetBits(RpgValues.ToDecimal(value));
            var itemScale = (bits[3] >> 16) & 255;
            var item = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
            if (bits[3] < 0) item = -item;
            if (itemScale > scale) { coefficient *= BigInteger.Pow(10, itemScale - scale); scale = itemScale; }
            coefficient += item * BigInteger.Pow(10, scale - itemScale);
        }
        var magnitude = BigInteger.Abs(coefficient);
        if (magnitude > (BigInteger.One << 96) - 1) throw NativeFailure(103, "Array total exceeds supported decimal precision.");
        return new decimal(unchecked((int)(uint)(magnitude & uint.MaxValue)), unchecked((int)(uint)((magnitude >> 32) & uint.MaxValue)),
            unchecked((int)(uint)((magnitude >> 64) & uint.MaxValue)), coefficient.Sign < 0, (byte)scale);
    }

    private int CompareArrayValues(object? left, object? right)
    {
        if (left is byte[] a && right is byte[] b)
        {
            var blank = NativeBlank;
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var comparison = (i < a.Length ? a[i] : blank).CompareTo(i < b.Length ? b[i] : blank);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
        return RpgValues.Compare(left, right);
    }
    private object? ArrayKey(NativeView cell)
    {
        if (cell.Element?.Kind != RpgFieldKind.Character) return ReadNative(cell);
        return NativeBuffer(cell).ToArray();
    }
    private object? SearchArrayArgument(NativeView array, RpgExpr expression) => array.Element?.Kind == RpgFieldKind.Character &&
        TryNativeReference(expression, out var scalar) && !scalar.IsArray && scalar.Element?.Kind == RpgFieldKind.Character
        ? ArrayKey(scalar) : SearchKey(array, expression.Eval(this));
    private object? SearchKey(NativeView array, object? value)
    {
        var kind = array.Element?.Kind;
        if (kind == RpgFieldKind.Character)
        {
            if (value is ProgramBuffer raw)
            {
                if (raw.Ccsid != NativeCcsid) throw NativeFailure(452, "Array search CCSIDs do not match.");
                return raw.ToArray();
            }
            if (value is not string text) throw NativeFailure(105, "A character array requires a character search argument.");
            try { return NativeEncoding.GetBytes(text); }
            catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
        }
        var valid = kind switch {
            RpgFieldKind.Indicator => value is bool,
            RpgFieldKind.Date or RpgFieldKind.Timestamp => value is DateTimeOffset or DateTime,
            RpgFieldKind.Time => value is TimeSpan,
            RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Float => RpgValues.IsNumeric(value),
            _ => false
        };
        if (!valid) throw NativeFailure(105, "Array and search argument types are incompatible.");
        return value;
    }
    private int LookupArray(string operation, IReadOnlyList<RpgExpr> arguments)
    {
        ArrayArguments(arguments, 2, 4);
        if (ContainsSubarray(arguments[1])) throw NativeFailure(105, "%LOOKUP does not accept %SUBARR; use start/count arguments.");
        var keyed = TryKeyedArray(arguments[1], out _, out var view);
        if (!keyed) view = ArrayView(arguments[1]);
        else if (operation is not ("LOOKUP" or "LOOKUPNE")) throw NativeFailure(105, "Only %LOOKUP and %LOOKUPNE accept an array data structure key.");
        var key = SearchArrayArgument(view, arguments[0]);
        var start = arguments.Count >= 3 ? NativeSubscript(arguments[2]) : 1;
        var count = arguments.Count == 4 ? NativeSubscript(arguments[3]) : view.Count - start + 1;
        CheckArrayRange(view.Count, start, count);
        int Compare(int index)
        {
            _host.CancellationToken.ThrowIfCancellationRequested(); return CompareArrayValues(ArrayKey(NativeIndex(view, index + 1)), key);
        }
        return FindArrayPosition(operation, (int)start, (int)count, keyed ? null : view.Element?.ArrayDescending, Compare);
    }
    private static int FindArrayPosition(string operation, int start, int count, bool? sequence, Func<int, int> compare)
    {
        if (operation == "LOOKUPNE" || sequence is null)
        {
            if (operation is not ("LOOKUP" or "LOOKUPNE")) throw NativeFailure(105, "Relative lookup requires ASCEND or DESCEND on the array.");
            for (var i = start - 1; i < start - 1 + count; i++)
                if (operation == "LOOKUPNE" ? compare(i) != 0 : compare(i) == 0) return i + 1;
            return 0;
        }
        var descending = sequence.Value;
        var begin = start - 1; var end = begin + count;
        int Bound(bool upper)
        {
            var low = begin; var high = end;
            while (low < high)
            {
                var middle = low + (high - low) / 2; var comparison = compare(middle) * (descending ? -1 : 1);
                if (comparison < 0 || upper && comparison == 0) low = middle + 1; else high = middle;
            }
            return low;
        }
        var lower = Bound(false); var equal = lower < end && compare(lower) == 0;
        if (equal && operation is "LOOKUP" or "LOOKUPLE" or "LOOKUPGE") return lower + 1;
        if (operation == "LOOKUP") return 0;
        var candidate = operation is "LOOKUPLT" or "LOOKUPLE" ? descending ? Bound(true) : lower - 1 : descending ? lower - 1 : Bound(true);
        return candidate >= begin && candidate < end ? candidate + 1 : 0;
    }

    internal void SortArray(RpgExpr expression, bool? direction, IReadOnlyList<string> fields)
    {
        if (fields.Count > 0) { SortStructureFields(ArrayView(expression), fields, direction ?? false); return; }
        if (TryKeyedSort(expression, out var records, out var key)) { SortKeyedRecords(records, key, direction ?? false); return; }
        var view = ArrayView(expression);
        if (view.Element is not { } element) throw NativeFailure(105, "SORTA of data structure arrays requires an explicit supported key.");
        if (direction is { } requested && element.ArrayDescending is { } declared && requested != declared)
            throw NativeFailure(105, "SORTA direction conflicts with its declared array sequence.");
        // A keyed OVERLAY sorts complete base elements, including data outside the key bytes.
        var basis = element;
        while (basis.OverlayTarget is { } target && view.Structure.Find(target) is { IsArray: true } parent) basis = parent;
        records = view with { Offset = view.Offset - element.Offset + basis.Offset, Element = basis, Width = view.Count * basis.StorageLength, ArrayStride = basis.Stride };
        SortKeyedRecords(records, view, direction ?? element.ArrayDescending ?? false);
    }
}
