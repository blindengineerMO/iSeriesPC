using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal void RequireExactType(RpgField formal, RpgExpr expression, NativeView? view)
    {
        var actual = RpgCompiler.ExactActualType(_program, expression);
        if (actual is null && view is not null)
            actual = view.Element is { } element ? new RpgField { Name = element.Name, Kind = element.Kind, Length = element.Length,
                LikeType = element.LikeType, DeclaredLike = element.DeclaredLike, Decimals = element.Decimals, IsArray = view.IsArray, Dimension = view.Count, ArrayDescending = element.ArrayDescending, OpaqueBytes = element.OpaqueBytes }
                : new RpgField { Name = view.Structure.Name, Kind = RpgFieldKind.Character, Length = view.Layout.Length, IsDataStructure = true,
                    ParameterStructure = view.Layout, IsArray = view.IsArray, Dimension = view.Count, OpaqueBytes = true };
        if (actual is null && formal.DeclaredLike) throw NativeFailure(105, "*EXACT LIKE parameters require related declared data or return metadata.");
        if (actual is not null && RpgCompiler.ExactTypeError(actual, formal) is { } error) throw NativeFailure(105, error);
    }

    internal void RequireExactValue(RpgField formal, object? value)
    {
        if (value is object?[] array)
        {
            if (!formal.IsArray || array.Length != formal.Dimension || formal.ArrayDescending is not null)
                throw NativeFailure(105, "*EXACT array expressions must match dimension and declared order.");
            foreach (var item in array) { _host.CancellationToken.ThrowIfCancellationRequested(); RequireExactScalar(formal, item); }
        }
        else { if (formal.IsArray) throw NativeFailure(105, "*EXACT requires an array actual."); RequireExactScalar(formal, value); }
    }
    private void RequireExactScalar(RpgField formal, object? value)
    {
        if (formal.IsDataStructure) throw NativeFailure(105, "*EXACT structure expressions require related LIKEDS return metadata.");
        if (formal.Kind == RpgFieldKind.Character)
        {
            var bytes = CharacterArgumentBytes(value);
            var count = bytes.Length;
            if (formal.TrimParameter) { var first = 0; var end = count; while (first < end && bytes[first] == NativeBlank) first++; while (end > first && bytes[end - 1] == NativeBlank) end--; count = end - first; }
            if (count > formal.Length) throw NativeFailure(105, "*EXACT forbids truncating character arguments.");
            return;
        }
        if (formal.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Decimal)
        {
            if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value)) throw NativeFailure(105, "*EXACT requires non-float numeric data.");
            var number = RpgValues.ToDecimal(value); var scale = (decimal.GetBits(number)[3] >> 16) & 255;
            var coefficient = decimal.Abs(number); var integers = 0; while (coefficient >= 1) { integers++; coefficient = decimal.Truncate(coefficient / 10); }
            if (scale > formal.Decimals || integers > formal.Length - formal.Decimals) throw NativeFailure(105, "*EXACT forbids losing numeric positions.");
            return;
        }
        if (formal.Kind == RpgFieldKind.Float && !RpgValues.IsFloat(value) || formal.Kind == RpgFieldKind.Indicator && value is not bool)
            throw NativeFailure(105, "*EXACT argument types differ.");
        if (formal.Kind == RpgFieldKind.Float && formal.Length < (value is float ? 4 : 8) ||
            formal.Kind is RpgFieldKind.Date or RpgFieldKind.Timestamp && value is not (DateTime or DateTimeOffset) ||
            formal.Kind == RpgFieldKind.Time && value is not TimeSpan) throw NativeFailure(105, "*EXACT argument types or lengths differ.");
    }

    private byte[] CharacterArgumentBytes(object? value)
    {
        if (value is ProgramBuffer buffer)
        {
            if (buffer.Ccsid != NativeCcsid) throw NativeFailure(452, "Character argument CCSIDs differ.");
            return buffer.ToArray();
        }
        if (value is byte[] hex) return hex.ToArray();
        if (value is not string text) throw NativeFailure(105, "Character parameter options require character data.");
        try { return NativeEncoding.GetBytes(text); }
        catch (System.Text.EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
    }

    internal ProgramArgument CharacterOptionTemporary(RpgField formal, object? value)
    {
        object Transform(object? item)
        {
            var bytes = CharacterArgumentBytes(item); var first = 0; var end = bytes.Length;
            if (formal.TrimParameter) { while (first < end && bytes[first] == NativeBlank) first++; while (end > first && bytes[end - 1] == NativeBlank) end--; }
            if (formal.ExactParameter && end - first > formal.Length) throw NativeFailure(105, "*EXACT forbids truncating the transformed character value.");
            var image = Enumerable.Repeat(NativeBlank, formal.Length).ToArray(); var count = Math.Min(end - first, image.Length);
            bytes.AsSpan(formal.RightAdjustParameter ? end - count : first, count).CopyTo(image.AsSpan(formal.RightAdjustParameter ? image.Length - count : 0));
            return new ProgramBuffer(image, NativeCcsid);
        }
        if (formal.IsArray)
        {
            if (value is not object?[] values) throw NativeFailure(105, "Character array parameters require array actuals.");
            if (!formal.VariableSizeParameter && values.Length < formal.Dimension) throw NativeFailure(105, "Character array actual is too short.");
            var converted = new object?[formal.Dimension];
            for (var index = 0; index < converted.Length; index++) { _host.CancellationToken.ThrowIfCancellationRequested(); converted[index] = Transform(index < values.Length ? values[index] : ""); }
            value = converted;
        }
        else value = Transform(value);
        return ConstTemporary(formal, value, readOnly: !formal.ValueParameter);
    }

    internal object? ParameterOptionValue(NativeView view) => VariableTransport(view) ? ActualVariableValues(view) : ReadNative(view);
}
