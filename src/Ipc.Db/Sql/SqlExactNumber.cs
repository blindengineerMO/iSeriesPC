using System.Globalization;
using System.Numerics;
using Ipc.Core.Messages;

namespace Ipc.Db.Sql;

// Decimal intermediates do not pass through SQLite REAL or System.Decimal.
internal readonly record struct SqlExactNumber(BigInteger Units, int Scale)
{
    internal static SqlExactNumber Parse(string text)
    {
        text = text.Trim();
        var exponent = text.IndexOfAny(['e', 'E']); var power = 0;
        if (exponent >= 0)
        {
            if (!int.TryParse(text[(exponent + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out power) || Math.Abs((long)power) > 1024)
                throw Error("Numeric exponent is out of range.");
            text = text[..exponent];
        }
        var unsigned = text.TrimStart('+', '-');
        if (text.Length == 0 || unsigned.Length == 0 || (text[0] is '+' or '-' && text.Length > 1 && text[1] is '+' or '-') || unsigned.Count(c => c == '.') > 1 || !unsigned.Any(char.IsAsciiDigit) || unsigned.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new FormatException("Invalid exact numeric representation.");
        var point = text.IndexOf('.'); var scale = point < 0 ? 0 : text.Length - point - 1;
        var units = BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture);
        scale -= power;
        return scale < 0 ? new(units * Pow(-scale), 0) : new(units, scale);
    }
    internal SqlExactNumber Rescale(int scale) => scale >= Scale ? new(Units * Pow(scale - Scale), scale) : new(Units / Pow(Scale - scale), scale);
    internal SqlExactNumber Checked(int precision, int scale)
    {
        var result = Rescale(scale);
        if (BigInteger.Abs(result.Units) >= Pow(precision)) throw Error("Decimal arithmetic overflow.");
        return result;
    }
    internal SqlExactNumber CheckedInteger(int bits)
    {
        var result = Rescale(0); var limit = BigInteger.One << (bits - 1);
        if (result.Units < -limit || result.Units >= limit) throw Error("Integer arithmetic overflow.");
        return result;
    }
    internal static SqlExactNumber Calculate(string operation, SqlExactNumber left, SqlExactNumber right, int scale)
    {
        if (operation is "+" or "-")
        {
            var common = Math.Max(left.Scale, right.Scale);
            return new SqlExactNumber(operation == "+" ? left.Rescale(common).Units + right.Rescale(common).Units : left.Rescale(common).Units - right.Rescale(common).Units, common).Rescale(scale);
        }
        if (operation == "*") return new SqlExactNumber(left.Units * right.Units, left.Scale + right.Scale).Rescale(scale);
        if (right.Units.IsZero) throw Error("Division by zero.");
        var power = scale + right.Scale - left.Scale;
        return new(power >= 0 ? left.Units * Pow(power) / right.Units : left.Units / (right.Units * Pow(-power)), scale);
    }
    internal int CompareTo(SqlExactNumber other)
    {
        var scale = Math.Max(Scale, other.Scale); return Rescale(scale).Units.CompareTo(other.Rescale(scale).Units);
    }
    internal double Double() => double.Parse(ToString(), CultureInfo.InvariantCulture);
    public override string ToString()
    {
        var digits = BigInteger.Abs(Units).ToString(CultureInfo.InvariantCulture).PadLeft(Scale + 1, '0');
        return (Units.Sign < 0 ? "-" : "") + (Scale == 0 ? digits : digits.Insert(digits.Length - Scale, "."));
    }
    private static BigInteger Pow(int power) => BigInteger.Pow(10, power);
    internal static CpfException Error(string message) => new("SQL0802", message);
}
