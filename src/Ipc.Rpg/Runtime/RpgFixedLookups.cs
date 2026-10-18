using System.Numerics;
using System.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private static BigInteger LookupCoefficient(object? value, int? declaredScale = null)
    {
        var bits = decimal.GetBits(RpgValues.ToDecimal(value)); var scale = (bits[3] >> 16) & 255;
        var coefficient = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        if (declaredScale is { } target)
            coefficient = target >= scale ? coefficient * BigInteger.Pow(10, target - scale) : coefficient / BigInteger.Pow(10, scale - target);
        return bits[3] < 0 ? -coefficient : coefficient;
    }
    private object? FixedLookupKey(NativeView array, RpgExpr search, string text)
    {
        var element = array.Element!; var figurative = text.ToUpperInvariant();
        var nonFloat = element.Kind is RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned;
        if (figurative is "*HIVAL" or "*LOVAL")
        {
            var high = figurative == "*HIVAL";
            if (element.Kind == RpgFieldKind.Character) return Enumerable.Repeat(high ? (byte)255 : (byte)0, array.UnitWidth).ToArray();
            if (nonFloat) return element.Kind == RpgFieldKind.Integer
                ? high ? (BigInteger.One << (array.UnitWidth * 8 - 1)) - 1 : -(BigInteger.One << (array.UnitWidth * 8 - 1))
                : (BigInteger.Pow(10, element.Length) - 1) * (high ? 1 : -1);
            return element.Kind switch
            {
                RpgFieldKind.Float => (element.Length == 4 ? float.MaxValue : double.MaxValue) * (high ? 1d : -1d),
                RpgFieldKind.Indicator => high,
                RpgFieldKind.Date => high ? new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero) : DateTimeOffset.MinValue,
                RpgFieldKind.Timestamp => high ? DateTimeOffset.MaxValue.AddTicks(-9) : DateTimeOffset.MinValue,
                RpgFieldKind.Time => high ? new TimeSpan(23, 59, 59) : TimeSpan.Zero,
                _ => throw NativeFailure(105, "Unsupported LOOKUP figurative type.")
            };
        }
        if (element.Kind == RpgFieldKind.Character && figurative is "*ZERO" or "*ZEROS")
            return Enumerable.Repeat(NativeZero, array.UnitWidth).ToArray();
        var native = TryNativeReference(search, out var source);
        if (native && source.IsArray) throw NativeFailure(105, "LOOKUP search argument requires a scalar.");
        var value = native ? ArrayKey(source) : search.Eval(this);
        if (element.Kind == RpgFieldKind.Character)
        {
            byte[] bytes;
            try { bytes = value switch { byte[] raw => raw, ProgramBuffer buffer => buffer.ToArray(), string literal => NativeEncoding.GetBytes(literal),
                _ => throw NativeFailure(105, "Character LOOKUP requires character data.") }; }
            catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
            if (bytes.Length > array.UnitWidth) throw NativeFailure(105, "LOOKUP search argument exceeds the array element length.");
            return bytes.Concat(Enumerable.Repeat(NativeBlank, array.UnitWidth - bytes.Length)).ToArray();
        }
        if (nonFloat)
        {
            if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value)) throw NativeFailure(105, "Non-float LOOKUP requires non-float numeric data.");
            var scale = RpgCompiler.FixedLookupLayout(Program, search)?.Decimals;
            var result = LookupCoefficient(value, scale);
            if (BigInteger.Abs(result) >= BigInteger.Pow(10, element.Length)) throw NativeFailure(105, "LOOKUP argument exceeds the array digit length.");
            return result;
        }
        return SearchKey(array, value);
    }
    internal void FixedLookup(RpgStatement statement)
    {
        var (operand, index) = RpgCompiler.FixedLookupTarget(RpgExpressionParser.Parse(statement.Factor2!));
        if (operand is RpgFieldRef table && Program.FindField(table.Name) is { IsTable: true })
        {
            if (index is not null) throw NativeFailure(105, "A table cannot have an index.");
            FixedTableLookup(statement, table.Name); return;
        }
        if (statement.Result is not null) throw NativeFailure(105, "An array LOOKUP cannot have a result operand.");
        var array = ArrayView(operand);
        var key = FixedLookupKey(array, RpgExpressionParser.Parse(statement.Factor1!), statement.Factor1!);
        var capturedIndex = index is not null && TryNativeReference(index, out var receiver) ? receiver : null;
        var start = index is null ? 1 : NativeSubscript(capturedIndex is null ? index : new RpgLiteral(ReadNative(capturedIndex)));
        CheckArrayRange(array.Count, start, array.Count - start + 1);
        var (found, comparison) = SearchFixedLookup(statement, array, key, (int)start);
        _host.CancellationToken.ThrowIfCancellationRequested();
        if (index is not null && RpgCompiler.FixedLookupLayout(Program, index) is { Constant: false })
        {
            if (capturedIndex is not null) WriteCapturedNative(capturedIndex, found == 0 ? 1 : found);
            else if (index is RpgFieldRef field) WriteValue(field.Name, found == 0 ? 1 : found);
            else throw NativeFailure(105, "LOOKUP index is not writable.");
        }
        PublishFixedLookup(statement, found, comparison);
    }
    private (int Found, int Comparison) SearchFixedLookup(RpgStatement statement, NativeView array, object? key, int start)
    {
        int Compare(int position)
        {
            _host.CancellationToken.ThrowIfCancellationRequested();
            var cell = NativeIndex(array, position + 1); var value = ArrayKey(cell);
            return key is BigInteger coefficient ? LookupCoefficient(value, cell.Element!.Decimals).CompareTo(coefficient) : CompareArrayValues(value, key);
        }
        var operation = statement.Indicator1 != 0 ? statement.Indicator3 != 0 ? "LOOKUPGE" : "LOOKUPGT" :
            statement.Indicator2 != 0 ? statement.Indicator3 != 0 ? "LOOKUPLE" : "LOOKUPLT" : "LOOKUP";
        var found = FindArrayPosition(operation, start, array.Count - start + 1,
            operation == "LOOKUP" ? null : array.Element!.ArrayDescending, Compare);
        var comparison = found == 0 ? 0 : Compare(found - 1);
        return (found, comparison);
    }
    private void PublishFixedLookup(RpgStatement statement, int found, int comparison)
    {
        foreach (var indicator in new[] { statement.Indicator1, statement.Indicator2, statement.Indicator3 }.Where(i => i != 0).Distinct())
            Indicators[indicator] = found != 0 && (indicator == statement.Indicator1 && comparison > 0 ||
                indicator == statement.Indicator2 && comparison < 0 || indicator == statement.Indicator3 && comparison == 0);
        _lastFileStatus = _lastFileStatus with { Found = found != 0, Equal = statement.Indicator3 != 0 && found != 0 && comparison == 0 };
    }
}
