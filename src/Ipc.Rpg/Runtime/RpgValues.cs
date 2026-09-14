using System.Globalization;

namespace Ipc.Rpg.Runtime;

public static class RpgValues
{
    public static readonly DateTimeOffset MaxDate = DateTimeOffset.MaxValue;
    public static readonly DateTimeOffset MinDate = DateTimeOffset.MinValue;

    public static bool IsNumeric(object? value) =>
        value is decimal or double or float or long or int or short or byte or sbyte or uint or ulong;

    public static bool IsFloat(object? value) => value is double or float;

    public static bool IsDateLike(object? value) => value is DateTimeOffset or DateTime or TimeSpan;

    public static decimal ToDecimal(object? value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        ulong ul => ul,
        double f => System.Convert.ToDecimal(f, CultureInfo.InvariantCulture),
        float f => System.Convert.ToDecimal(f, CultureInfo.InvariantCulture),
        DateTimeOffset d => d.Ticks,
        DateTime d => d.Ticks,
        TimeSpan t => t.Ticks,
        bool b => b ? 1m : 0m,
        null => 0m,
        _ => decimal.TryParse(value.ToString(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m,
    };

    public static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        decimal d => decimal.ToInt64(decimal.Round(d, 0, MidpointRounding.AwayFromZero)),
        double f => (long)f,
        float f => (long)f,
        bool b => b ? 1L : 0L,
        null => 0L,
        _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L,
    };

    public static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        bool b => b ? 1d : 0d,
        null => 0d,
        _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d,
    };

    public static bool ToBool(object? value) => value switch
    {
        bool b => b,
        null => false,
        long l => l != 0,
        int i => i != 0,
        decimal d => d != 0m,
        _ => value.ToString() is "1" or "Y" or "T" or "true",
    };

    public static string ToText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "1" : "0",
        DateTimeOffset d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString("G17", CultureInfo.InvariantCulture),
        decimal m => NormalizeDecimal(m),
        _ => value.ToString() ?? string.Empty,
    };

    public static string NormalizeDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToDate(object? value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d),
        TimeSpan t => MinDate.Add(t),
        null => MinDate,
        _ => DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : MinDate,
    };

    public static TimeSpan ToTime(object? value) => value switch
    {
        TimeSpan t => t,
        DateTimeOffset d => d.TimeOfDay,
        DateTime d => d.TimeOfDay,
        null => TimeSpan.Zero,
        _ => TimeSpan.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TimeSpan.Zero,
    };

    public static object Positive(object? value) => IsFloat(value)
        ? ToDouble(value)
        : ToDecimal(value);

    public static object Negate(object? value) => IsFloat(value)
        ? -ToDouble(value)
        : -ToDecimal(value);

    public static object Add(object? left, object? right)
    {
        if (left is string ls)
        {
            return ls + ToText(right);
        }

        if (right is string rs)
        {
            return ToText(left) + rs;
        }

        if (IsFloat(left) || IsFloat(right))
        {
            return ToDouble(left) + ToDouble(right);
        }

        return ToDecimal(left) + ToDecimal(right);
    }

    public static object Subtract(object? left, object? right)
    {
        if (IsDateLike(left) && IsDateLike(right))
        {
            if (left is TimeSpan leftTime && right is TimeSpan rightTime)
            {
                return leftTime.Subtract(rightTime);
            }

            return new TimeSpan(ToDate(left).Ticks - ToDate(right).Ticks);
        }

        if (IsFloat(left) || IsFloat(right))
        {
            return ToDouble(left) - ToDouble(right);
        }

        return ToDecimal(left) - ToDecimal(right);
    }

    public static object Multiply(object? left, object? right)
    {
        if (IsFloat(left) || IsFloat(right))
        {
            return ToDouble(left) * ToDouble(right);
        }

        return ToDecimal(left) * ToDecimal(right);
    }

    public static object Divide(object? left, object? right)
    {
        if (IsFloat(left) || IsFloat(right))
        {
            var divisor = ToDouble(right);
            if (divisor == 0d)
            {
                throw new RpgRuntimeException("Division by zero.");
            }

            return ToDouble(left) / divisor;
        }

        var denominator = ToDecimal(right);
        if (denominator == 0m)
        {
            throw new RpgRuntimeException("Division by zero.");
        }

        return ToDecimal(left) / denominator;
    }

    public static object? Modulus(object? left, object? right)
    {
        if (IsFloat(left) || IsFloat(right))
        {
            return ToDouble(left) % ToDouble(right);
        }

        return ToDecimal(left) % ToDecimal(right);
    }

    public static int Compare(object? left, object? right)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            if (IsFloat(left) || IsFloat(right))
            {
                return ToDouble(left).CompareTo(ToDouble(right));
            }

            return ToDecimal(left).CompareTo(ToDecimal(right));
        }

        if (IsDateLike(left) || IsDateLike(right))
        {
            var l = ToDate(left);
            var r = ToDate(right);
            return l.CompareTo(r);
        }

        if (left is string ls2 && (ls2 == "*LOVAL" || ls2 == "*HIVAL"))
        {
            return ls2 == "*LOVAL" ? -1 : 1;
        }

        if (right is string rs2 && (rs2 == "*LOVAL" || rs2 == "*HIVAL"))
        {
            return rs2 == "*LOVAL" ? 1 : -1;
        }

        if (left is bool lb && right is bool rb)
        {
            return lb.CompareTo(rb);
        }

        return string.Compare(ToText(left), ToText(right), StringComparison.Ordinal);
    }
}