using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private bool TryKeyedArray(RpgExpr expression, out NativeView records, out NativeView key)
    {
        records = key = null!;
        var indexed = expression as RpgElementRef;
        if ((indexed?.Target ?? expression) is not RpgMemberRef { Target: RpgAllElementsRef all } member) return false;
        records = ArrayView(all.Target);
        var image = records with { Bytes = NativeBuffer(records).ToArray(), Offset = 0, Binding = null, ArrayStride = records.UnitWidth };
        if (records.Element is not null || !records.Structure.Qualified)
            throw NativeFailure(105, "A (*) key requires an array data structure.");
        var element = records.Structure.Find(member.Name) ?? throw NativeFailure(105, "Unknown keyed array subfield: " + member.Name + ".");
        if (element.IsArray != (indexed is not null)) throw NativeFailure(105, "A keyed array requires a scalar or indexed scalar subfield.");
        key = NativeMember(NativeIndex(image, 1), member.Name);
        if (indexed is not null) key = NativeIndex(key, NativeSubscript(indexed.Index));
        key = key with { IsArray = true, Count = records.Count, Width = key.UnitWidth * records.Count, ArrayStride = records.UnitWidth, Binding = null };
        return true;
    }
    private bool TryKeyedSort(RpgExpr expression, out NativeView records, out NativeView key)
    {
        if (expression is not RpgBuiltin { Name: "SUBARR", Arguments.Count: > 0 } slice) return TryKeyedArray(expression, out records, out key);
        if (!TryKeyedArray(slice.Arguments[0], out records, out key)) return false;
        ArrayArguments(slice.Arguments, 2, 3);
        var start = NativeSubscript(slice.Arguments[1]);
        var count = slice.Arguments.Count == 3 ? NativeSubscript(slice.Arguments[2]) : records.Count - start + 1;
        records = SliceArray(records, start, count); key = SliceArray(key, start, count); return true;
    }
    private NativeView SliceArray(NativeView view, long start, long count)
    {
        CheckArrayRange(view.Count, start, count);
        var slice = view with { Offset = view.Offset + (int)(start - 1) * view.Stride, Width = (int)count * view.UnitWidth, Count = (int)count, Binding = null };
        return BindNativeSubview(view, slice, (int)(start - 1) * view.Stride, checked((int)count * view.Stride), view.Binding?.Memory?.Shape);
    }
    private void SortKeyedRecords(NativeView records, NativeView key, bool descending)
    {
        var values = new object?[records.Count];
        for (var index = 0; index < values.Length; index++)
        {
            _host.CancellationToken.ThrowIfCancellationRequested(); values[index] = ArrayKey(NativeIndex(key, index + 1));
        }
        SortRecords(records, (left, right) => CompareArrayValues(values[left], values[right]) * (descending ? -1 : 1));
    }
    private void SortStructureFields(NativeView records, IReadOnlyList<string> fields, bool descending)
    {
        var image = records with { Bytes = NativeBuffer(records).ToArray(), Offset = 0, Binding = null, ArrayStride = records.UnitWidth };
        if (records.Element is not null || fields.Count > 1024) throw NativeFailure(105, "%FIELDS requires an array data structure and at most 1024 keys.");
        var keys = new NativeView[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            if (records.Structure.Find(fields[index]) is not { IsArray: false }) throw NativeFailure(105, "%FIELDS requires scalar subfields of its array data structure.");
            var key = NativeMember(NativeIndex(image, 1), fields[index]);
            keys[index] = key with { IsArray = true, Count = records.Count, Width = key.UnitWidth * records.Count, ArrayStride = records.UnitWidth, Binding = null };
        }
        // Read secondary keys only for ties. Avoid an element-count * key-count object matrix,
        // which can greatly exceed native storage limits when many keys overlay the same bytes.
        SortRecords(records, (left, right) =>
        {
            foreach (var key in keys)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                var compared = CompareArrayValues(ArrayKey(NativeIndex(key, left + 1)), ArrayKey(NativeIndex(key, right + 1)));
                if (compared != 0) return compared * (descending ? -1 : 1);
            }
            return 0;
        });
    }
    private void SortRecords(NativeView records, Func<int, int, int> compare)
    {
        var order = Enumerable.Range(0, records.Count).ToArray(); var scratch = new int[records.Count];
        // Merge sorting keeps cancellation and native conversion exceptions intact.
        for (var width = 1; width < records.Count; width *= 2)
        {
            for (var first = 0; first < records.Count; first += width * 2)
            {
                var middle = Math.Min(first + width, records.Count); var last = Math.Min(first + width * 2, records.Count);
                var left = first; var right = middle;
                for (var output = first; output < last; output++)
                {
                    _host.CancellationToken.ThrowIfCancellationRequested();
                    var takeLeft = right == last || left < middle && compare(order[left], order[right]) <= 0;
                    scratch[output] = takeLeft ? order[left++] : order[right++];
                }
            }
            (order, scratch) = (scratch, order);
        }
        var bytes = new byte[records.Count * records.UnitWidth];
        for (var index = 0; index < records.Count; index++)
        {
            _host.CancellationToken.ThrowIfCancellationRequested();
            records.Bytes.AsSpan(records.Offset + order[index] * records.Stride, records.UnitWidth).CopyTo(bytes.AsSpan(index * records.UnitWidth));
        }
        _host.CancellationToken.ThrowIfCancellationRequested();
        WriteNativeBytes(records, bytes);
    }
}
