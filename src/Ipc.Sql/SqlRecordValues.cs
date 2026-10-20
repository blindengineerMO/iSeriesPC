using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Services.Sqlite;

namespace Ipc.Db.Sql;

internal static partial class SqlExpressionRuntime
{
    private static CpfException LogicalError(string text) => new("IPC0138", text);
    private static string ToString(object value) => ValueText(value);
    internal static object? ToSqlValue(FieldSpec field, object? value)
    {
        var normalized = value ?? DefaultFor(field);
        if (normalized is null) return null;

        return field.Type switch
        {
            FieldType.Alpha => normalized as string ?? ToString(normalized),
            FieldType.Zoned or FieldType.Packed => field.Decimals == 0
                ? ToDecimal(normalized)
                : ToDecimal(normalized).ToString("F" + field.Decimals, CultureInfo.InvariantCulture),
            FieldType.Binary => ToLong(normalized),
            FieldType.Float => field.Length == 4 ? (double)(float)ToDouble(normalized) : ToDouble(normalized),
            FieldType.Logic => ToBool(normalized) ? 1L : 0L,
            FieldType.Date => ToDate(normalized).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Time => ToTime(normalized).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            FieldType.Timestamp => ToDate(normalized).ToString(field.Length == 26 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => normalized as string ?? ToString(normalized),
        };
    }

    internal static object? DefaultFor(FieldSpec field)
    {
        if (field.DefaultValue is { } explicitDefault) return explicitDefault.Value;
        if (field.CurrentDatetimeDefault)
        {
            var now = DateTimeOffset.UtcNow;
            return field.Type switch {
                FieldType.Date => new DateTimeOffset(now.Date, TimeSpan.Zero),
                FieldType.Time => TimeSpan.FromTicks(now.TimeOfDay.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
                FieldType.Timestamp => now.AddTicks(-(now.Ticks % 10)),
                _ => throw LogicalError("Invalid current-datetime default."),
            };
        }
        if (field.NullCapable) return null;
        return field.Type switch {
        FieldType.Alpha => string.Empty,
        FieldType.Logic => false,
        FieldType.Binary => 0L,
        FieldType.Float => 0d,
        FieldType.Date => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        FieldType.Time => TimeSpan.Zero,
        FieldType.Timestamp => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        _ => 0m,
        };
    }

    internal static string ValueText(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    internal static decimal ToDecimal(object value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double f => System.Convert.ToDecimal(f),
        _ => decimal.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
    };

    internal static long ToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        decimal d => decimal.ToInt64(d),
        _ => long.TryParse(ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L,
    };

    internal static double ToDouble(object value) => value switch
    {
        double d => d,
        decimal m => (double)m,
        long l => l,
        _ => double.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d,
    };

    internal static bool ToBool(object value) => value switch
    {
        bool b => b,
        _ => ToString(value) is "1" or "Y" or "T" or "true",
    };

    internal static DateTimeOffset ToDate(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero),
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => DateTimeOffset.TryParse(ToString(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue,
    };

    internal static TimeSpan ToTime(object value) => value switch
    {
        TimeSpan t => t,
        TimeOnly t => t.ToTimeSpan(),
        DateTime d => d.TimeOfDay,
        DateTimeOffset d => d.TimeOfDay,
        _ => TimeSpan.TryParse(ToString(value), CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero,
    };

    internal static object? RecordSqlValue(FieldSpec field, object? value)
    {
        if (value is null) return field.NullCapable ? null : ToSqlValue(field, DefaultFor(field));
        if (field.Type == FieldType.Alpha)
        {
            try { _ = DatabaseSortKeys.Text(Convert.ToString(value, CultureInfo.InvariantCulture), field.Ccsid, field.Length); }
            catch (ArgumentException) { throw LogicalError("Invalid character value for field " + field.Name + "."); }
        }
        if (field.Type == FieldType.Time && value is TimeSpan or TimeOnly or DateTimeOffset or DateTime)
        {
            var time = ToTime(value);
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) || time.Ticks % TimeSpan.TicksPerSecond != 0) throw LogicalError("Time value exceeds field precision.");
            return ToSqlValue(field, value);
        }
        if (field.Type is FieldType.Date or FieldType.Timestamp && value is DateTimeOffset or DateTime or DateOnly)
        {
            if (field.Type == FieldType.Timestamp && ToDate(value).Ticks % (field.Length == 26 ? 10 : TimeSpan.TicksPerSecond) != 0) throw LogicalError("Timestamp exceeds field precision.");
            return ToSqlValue(field, value);
        }
        if (field.Type == FieldType.Binary && value is decimal integral && decimal.Truncate(integral) == integral && integral is >= long.MinValue and <= long.MaxValue) value = decimal.ToInt64(integral);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (field.Type is FieldType.Packed or FieldType.Zoned)
        {
            try { _ = DatabaseSortKeys.Decimal(text); }
            catch (ArgumentException) { throw LogicalError("Invalid decimal precision for field " + field.Name + "."); }
            var numeric = decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (field.Decimals is < 0 or > 28 || field.Length < field.Decimals || field.Length > 29 || decimal.Round(numeric, field.Decimals) != numeric ||
                Math.Abs(decimal.Truncate(numeric)).ToString("0", CultureInfo.InvariantCulture).TrimStart('0').Length > field.Length - field.Decimals)
                throw LogicalError("Decimal value exceeds field precision or scale: " + field.Name + ".");
        }
        var valid = field.Type switch
        {
            FieldType.Alpha => text.Length <= field.Length,
            FieldType.Packed or FieldType.Zoned => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            FieldType.Binary => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var binary) && field.Length switch { 2 => binary is >= short.MinValue and <= short.MaxValue, 4 => binary is >= int.MinValue and <= int.MaxValue, 8 => true, _ => false },
            FieldType.Float => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating) && (field.Length == 8 || field.Length == 4 && float.IsFinite((float)floating)),
            FieldType.Logic => text is "0" or "1" or "True" or "False" or "true" or "false",
            FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.Time => TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.Timestamp => DateTime.TryParseExact(text, field.Length == 26 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            _ => false,
        };
        if (!valid) throw LogicalError("Invalid value for field " + field.Name + ".");
        return ToSqlValue(field, field.Type == FieldType.Logic ? text is "1" or "True" or "true" : value);
    }

    internal static object? SqlFieldValue(FieldSpec field, object? value, SqlConversionOptions? conversion = null)
    {
        if (value is null)
        {
            if (!field.NullCapable) throw new CpfException("SQL0407", "Null is not allowed for column " + field.Name + ".");
            return null;
        }
        value = SqlInputConversion.Normalize(field.Type, value, conversion);
        try { return RecordSqlValue(field, value); }
        catch (Exception error) when (error is CpfException or ArgumentException or OverflowException or FormatException)
        { throw new CpfException(field.Type == FieldType.Alpha ? "SQL0404" : "SQL0406", "Invalid or out-of-range value for column " + field.Name + ": " + error.Message); }
    }

    internal static string SqlNow(FieldType type, DateTimeOffset now) => now.ToString(type switch {
        FieldType.Date => "yyyy-MM-dd", FieldType.Time => "HH:mm:ss", _ => "yyyy-MM-dd HH:mm:ss.ffffff" }, CultureInfo.InvariantCulture);
}
