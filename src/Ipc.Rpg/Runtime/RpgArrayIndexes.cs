using System.Globalization;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal object? ReadDynamicIndex(RpgDynamicIndexRef expression) => TryNativeReference(expression, out var view)
        ? ReadNative(view) : ReadArrayValue(expression.Name, RpgValues.ToLong(expression.Index.Eval(this)));

    private long NativeSubscript(RpgExpr expression)
    {
        var value = expression.Eval(this);
        if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value) || HasNonIntegralSource(expression))
            throw NativeFailure(105, "A native array index requires a non-float numeric value with zero decimal positions.");
        var number = RpgValues.ToDecimal(value);
        if ((decimal.GetBits(number)[3] & 0x00FF0000) != 0 || number != decimal.Truncate(number))
            throw NativeFailure(105, "A native array index cannot have decimal positions.");
        if (number < long.MinValue || number > long.MaxValue) throw NativeFailure(121, "Native array index is out of range.");
        return (long)number;
    }
    private static long ConvertInteger(object? value)
    {
        try
        {
            if (RpgValues.IsFloat(value))
                return checked((long)Math.Truncate(RpgValues.ToDouble(value)));
            decimal number;
            if (value is string text)
            {
                text = text.Replace(" ", "", StringComparison.Ordinal).Replace(',', '.');
                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out number)) throw NativeFailure(105, "%INT contains invalid character numeric data.");
            }
            else if (RpgValues.IsNumeric(value)) number = RpgValues.ToDecimal(value);
            else throw NativeFailure(105, "%INT requires numeric or character data.");
            return checked((long)decimal.Truncate(number));
        }
        catch (OverflowException error) { throw NativeFailure(103, error.Message); }
    }
}
