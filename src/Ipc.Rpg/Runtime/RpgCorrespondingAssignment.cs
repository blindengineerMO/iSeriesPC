using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal void AssignCorresponding(RpgExpr target, RpgExpr source, bool halfAdjust)
    {
        // Capture operand subscripts once, in the same source-before-target order as EVAL.
        if (!TryNativeReference(source, out var from) || from.Element is not null || !TryNativeReference(target, out var to) || to.Element is not null || from.IsArray && !to.IsArray)
            throw NativeFailure(105, "EVAL-CORR requires compatible data structure variables.");
        AssignCorresponding(to, from, halfAdjust);
    }
    private static bool SameCorrespondingLayout(RpgDataStructure target, RpgDataStructure source)
    {
        if (!ReferenceEquals(target.TypeOrigin ?? target.LikeType, source.TypeOrigin ?? source.LikeType) || target.Length != source.Length ||
            target.RecordOrigin?.NullMap != source.RecordOrigin?.NullMap || target.Elements.Count != source.Elements.Count) return false;
        return target.Elements.Zip(source.Elements).All(pair =>
        {
            var (left, right) = pair;
            return string.Equals(left.LocalName ?? left.Name, right.LocalName ?? right.Name, StringComparison.OrdinalIgnoreCase) &&
                left.Anonymous == right.Anonymous && left.Kind == right.Kind && left.Length == right.Length && left.Decimals == right.Decimals &&
                left.Offset == right.Offset && left.StorageLength == right.StorageLength && left.Stride == right.Stride && left.Dimension == right.Dimension &&
                left.IsArray == right.IsArray && left.NullCapable == right.NullCapable && left.OpaqueBytes == right.OpaqueBytes &&
                (left.NestedStructure is null ? right.NestedStructure is null : right.NestedStructure is not null && SameCorrespondingLayout(left.NestedStructure, right.NestedStructure));
        });
    }
    private void AssignCorresponding(NativeView target, NativeView source, bool halfAdjust)
    {
        EnsureArgumentStorage(source); EnsureArgumentStorage(target); EnsureNativeWritable(target);
        var count = target.IsArray ? source.IsArray ? Math.Min(target.Count, source.Count) : target.Count : 1;
        // Related layouts use EVAL's complete image move, including padding and overlays.
        if (SameCorrespondingLayout(target.Layout, source.Layout))
        {
            var flags = new List<(NativeView Target, bool Value)>();
            for (var index = 0; index < count; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                var from = source.IsArray ? NativeIndex(source, index + 1) : source;
                var to = target.IsArray ? NativeIndex(target, index + 1) : target;
                for (var item = 0; item < NullLeafCount(to); item++)
                {
                    _host.CancellationToken.ThrowIfCancellationRequested();
                    var leaf = NullLeafAt(to, item);
                    if (leaf.Element?.NullCapable != true) continue;
                    PrepareNullIndicatorWrite(leaf); var input = NullLeafAt(from, item);
                    flags.Add((leaf, input.Element!.NullCapable && ReadNullIndicator(input)));
                }
            }
            var image = ReadNative(source); WriteNative(target, image);
            foreach (var flag in flags) WriteNullIndicator(flag.Target, flag.Value);
            return;
        }
        // Field order is significant when destination overlays change later source fields.
        foreach (var (input, output) in RpgCompiler.CorrespondingFields(source.Layout, target.Layout))
            for (var index = 0; index < count; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                var from = NativeMember(source.IsArray ? NativeIndex(source, index + 1) : source, input);
                var to = NativeMember(target.IsArray ? NativeIndex(target, index + 1) : target, output);
                if (input.NestedStructure is not null) { AssignCorresponding(to, from, halfAdjust); continue; }
                var assigned = to.IsArray ? Math.Min(to.Count, from.Count) : 1;
                var flags = new bool[output.NullCapable ? assigned : 0];
                for (var item = 0; item < flags.Length; item++)
                {
                    var leaf = to.IsArray ? NativeIndex(to, item + 1) : to; PrepareNullIndicatorWrite(leaf);
                    flags[item] = input.NullCapable && ReadNullIndicator(from.IsArray ? NativeIndex(from, item + 1) : from);
                }
                var value = ReadNative(from.IsArray && from.Count > assigned ? SliceArray(from, 1, assigned) : from);
                object? ConvertValue(object? item)
                {
                    if (halfAdjust && output.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer)
                    {
                        try { return decimal.Round(System.Convert.ToDecimal(item, System.Globalization.CultureInfo.InvariantCulture), output.Kind == RpgFieldKind.Integer ? 0 : output.Decimals, MidpointRounding.AwayFromZero); }
                        catch (OverflowException error) { throw NativeFailure(103, error.Message); }
                    }
                    return item;
                }
                WriteNative(to, value is object?[] array ? array.Select(ConvertValue).ToArray() : ConvertValue(value));
                for (var item = 0; item < flags.Length; item++) WriteNullIndicator(to.IsArray ? NativeIndex(to, item + 1) : to, flags[item]);
            }
    }
}
