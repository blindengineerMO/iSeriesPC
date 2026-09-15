using System.Globalization;
using System.Text;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Records;

public sealed partial class RecordCodec
{
    private readonly int _ccsid;

    public RecordCodec(int ccsid = CodePage.DefaultSystem)
    {
        if (!CodePage.IsSupported(ccsid)) throw new ArgumentOutOfRangeException(nameof(ccsid));
        _ccsid = ccsid;
    }

    public byte[] Encode(RecordFormat format, IReadOnlyDictionary<string, object?> values) => EncodeCore(format, values, false, out _);

    public byte[] Encode(RecordFormat format, IReadOnlyDictionary<string, object?> values, out short[] nullIndicators) => EncodeCore(format, values, true, out nullIndicators);

    private byte[] EncodeCore(RecordFormat format, IReadOnlyDictionary<string, object?> values, bool withNulls, out short[] nullIndicators)
    {
        ValidateRecord(format);
        var buffer = new byte[format.RecordLength];
        nullIndicators = new short[format.Fields.Count];
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var field = format.Fields[index];
            if (values.TryGetValue(field.Name, out var value) && value is null)
            {
                if (!field.NullCapable || !withNulls) throw new ArgumentException("Explicit null requires a nullable field and a null-indicator buffer.");
                nullIndicators[index] = -1;
            }
            WriteValue(buffer, field, value ?? DefaultValue(field));
        }

        return buffer;
    }

    public object?[] Decode(RecordFormat format, byte[] buffer) => DecodeCore(format, buffer, null);

    public object?[] Decode(RecordFormat format, byte[] buffer, IReadOnlyList<short> nullIndicators) => DecodeCore(format, buffer, nullIndicators);

    private object?[] DecodeCore(RecordFormat format, byte[] buffer, IReadOnlyList<short>? nullIndicators)
    {
        ValidateRecord(format);
        if (nullIndicators is not null && nullIndicators.Count != format.Fields.Count) throw new ArgumentException("Null-indicator count differs from the record format.");
        if (buffer.Length < format.RecordLength)
        {
            throw new ArgumentException("Record buffer is shorter than the record format.");
        }

        var values = new object?[format.Fields.Count];
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var field = format.Fields[index]; ValidateField(buffer, field);
            var indicator = nullIndicators?[index] ?? 0;
            if (indicator is not (0 or -1) || indicator == -1 && !field.NullCapable) throw new ArgumentException("Invalid record null indicator.");
            values[index] = indicator == -1 ? null : ReadValue(buffer, field);
        }

        return values;
    }

    public void WriteValue(byte[] buffer, FieldSpec field, object? value)
    {
        ValidateField(buffer, field);
        var offset = field.Position - 1;
        var f = value ?? DefaultValue(field);

        switch (field.Type)
        {
            case FieldType.Alpha:
            {
                var bytes = EncodedText(field.Ccsid, Convert.ToString(f, CultureInfo.InvariantCulture) ?? "", field.Length);
                if (field.VariableLength)
                {
                    var count = CodePage.ToBytes(field.Ccsid, Convert.ToString(f, CultureInfo.InvariantCulture) ?? "").Length;
                    buffer[offset++] = (byte)(count >> 8); buffer[offset++] = (byte)count;
                }
                Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
                break;
            }

            case FieldType.Zoned:
                WriteZoned(buffer, offset, field, ToDecimal(f));
                break;

            case FieldType.Packed:
                WritePacked(buffer, offset, field, ToDecimal(f));
                break;

            case FieldType.Binary:
            {
                var number = ToLong(f);
                if (field.Length == 2 && number is < short.MinValue or > short.MaxValue || field.Length == 4 && number is < int.MinValue or > int.MaxValue)
                    throw new OverflowException("Binary value exceeds field width.");
                for (var i = field.Length - 1; i >= 0; i--)
                {
                    buffer[offset + i] = (byte)(number & 0xFF);
                    number >>= 8;
                }

                break;
            }

            case FieldType.Float:
            {
                var number = ToDouble(f);
                if (field.Length == 4 && !float.IsFinite((float)number)) throw new OverflowException("Floating-point value exceeds field width.");
                var bits = field.Length == 4
                    ? (long)BitConverter.SingleToInt32Bits((float)number)
                    : BitConverter.DoubleToInt64Bits(number);
                var bytes = ConvertToBytes(bits);
                for (var i = 0; i < field.Length; i++)
                {
                    buffer[offset + i] = bytes[(8 - field.Length) + i];
                }

                break;
            }

            case FieldType.Logic:
                buffer[offset] = CodePage.ToBytes(_ccsid, ToBool(f) ? "1" : "0")[0];
                break;

            case FieldType.Date:
                WriteText(buffer, offset, field.Length, ToDate(f).ToString(field.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;

            case FieldType.Time:
                var time = ToTime(f);
                if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) || time.Ticks % TimeSpan.TicksPerSecond != 0) throw new OverflowException("Time value exceeds the basic time field precision.");
                WriteText(buffer, offset, field.Length, time.ToString(field.Length == 6 ? @"hhmmss" : @"hh\.mm\.ss", CultureInfo.InvariantCulture));
                break;

            case FieldType.Timestamp:
                var timestamp = ToDate(f);
                if (timestamp.Ticks % (field.Length == 14 ? TimeSpan.TicksPerSecond : 10) != 0) throw new OverflowException("Timestamp exceeds its field precision.");
                WriteText(buffer, offset, field.Length, timestamp.ToString(field.Length == 14 ? "yyyyMMddHHmmss" : "yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture));
                break;
        }
    }

    public object? ReadValue(byte[] buffer, FieldSpec field)
    {
        ValidateField(buffer, field);
        var offset = field.Position - 1;

        switch (field.Type)
        {
            case FieldType.Alpha:
                var encoding = (Encoding)CodePage.FromCcsid(field.Ccsid).Clone(); encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
                if (field.VariableLength)
                {
                    var count = (buffer[offset] << 8) | buffer[offset + 1];
                    if (count > field.Length) throw new FormatException("Variable field length exceeds its declared maximum.");
                    return encoding.GetString(buffer, offset + 2, count);
                }
                return encoding.GetString(buffer, offset, field.Length).TrimEnd(' ');

            case FieldType.Zoned:
                return ReadZoned(buffer, offset, field);

            case FieldType.Packed:
                return ReadPacked(buffer, offset, field);

            case FieldType.Binary:
            {
                long number = (buffer[offset] & 0x80) == 0 ? 0 : -1;
                for (var i = 0; i < field.Length; i++)
                {
                    number = (number << 8) | buffer[offset + i];
                }

                return number;
            }

            case FieldType.Float:
            {
                var number = 0L;
                for (var i = 0; i < field.Length; i++)
                {
                    number = (number << 8) | buffer[offset + i];
                }

                if (field.Length == 4)
                {
                    return BitConverter.Int32BitsToSingle(unchecked((int)number));
                }

                return BitConverter.Int64BitsToDouble(number);
            }

            case FieldType.Logic:
                var logical = CodePage.FromBytes(_ccsid, buffer, offset, 1);
                return logical switch { "0" => false, "1" => true, _ => throw new FormatException("Invalid logical record byte.") };

            case FieldType.Date:
                return ParseDate(TextAt(buffer, offset, field.Length), field.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd");

            case FieldType.Time:
            {
                var text = TextAt(buffer, offset, field.Length);
                return TimeSpan.ParseExact(text, field.Length == 6 ? "hhmmss" : @"hh\.mm\.ss", CultureInfo.InvariantCulture);
            }

            case FieldType.Timestamp:
                return ParseDate(TextAt(buffer, offset, field.Length), field.Length == 14 ? "yyyyMMddHHmmss" : "yyyy-MM-dd-HH.mm.ss.ffffff");

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void WriteZoned(byte[] buffer, int offset, FieldSpec field, decimal value)
    {
        var digits = DigitsString(value, field.Decimals, field.Length);
        var sign = value < 0 ? (byte)0xD0 : (byte)0xF0;
        for (var i = 0; i < field.Length - 1; i++)
        {
            buffer[offset + i] = (byte)(0xF0 | (digits[i] - '0'));
        }

        buffer[offset + field.Length - 1] = (byte)((digits[^1] - '0') | sign);
    }

    private void WritePacked(byte[] buffer, int offset, FieldSpec field, decimal value)
    {
        var digits = DigitsString(value, field.Decimals, field.Length);
        var byteCount = (field.Length + 2) / 2;
        var signNibble = value < 0 ? 0xD : 0xC;
        var firstNibble = field.Length % 2 == 0;

        for (var i = 0; i < byteCount; i++)
        {
            var high = firstNibble && i == 0 ? 0 : digits[(i * 2) - (firstNibble ? 1 : 0)] - '0';
            var isLast = i == byteCount - 1;
            var low = isLast ? signNibble : digits[(i * 2) - (firstNibble ? 1 : 0) + 1] - '0';
            buffer[offset + i] = (byte)((high << 4) | low);
        }
    }

    private decimal ReadZoned(byte[] buffer, int offset, FieldSpec field)
    {
        var digits = new char[field.Length];
        for (var i = 0; i < field.Length; i++)
        {
            var b = buffer[offset + i];
            if ((b & 0x0F) > 9 || i < field.Length - 1 && (b >> 4) != 0x0F) throw new FormatException("Invalid zoned decimal digit or zone.");
            digits[i] = (char)('0' + (b & 0x0F));
        }

        var negative = NegativeSign(buffer[offset + field.Length - 1] >> 4);
        return ParseDecimal(digits, field.Decimals, negative);
    }

    private decimal ReadPacked(byte[] buffer, int offset, FieldSpec field)
    {
        var byteCount = (field.Length + 2) / 2;
        var firstNibble = field.Length % 2 == 0;
        var digits = new List<char>(field.Length);
        for (var i = 0; i < byteCount; i++)
        {
            var b = buffer[offset + i];
            var high = (b >> 4) & 0x0F;
            var low = b & 0x0F;
            if (firstNibble && i == 0 ? high != 0 : high > 9) throw new FormatException("Invalid packed decimal digit or padding.");
            if (!(firstNibble && i == 0))
            {
                digits.Add((char)('0' + high));
            }

            var isLast = i == byteCount - 1;
            if (!isLast)
            {
                if (low > 9) throw new FormatException("Invalid packed decimal digit.");
                digits.Add((char)('0' + low));
            }
        }

        var negative = NegativeSign(buffer[offset + byteCount - 1] & 0x0F);
        return ParseDecimal(digits, field.Decimals, negative);
    }

    private decimal ParseDecimal(IReadOnlyList<char> digits, int decimals, bool negative)
    {
        var whole = new string(digits.ToArray());
        if (whole.Length == 0)
        {
            whole = "0";
        }

        if (decimals > 0)
        {
            whole = whole.PadLeft(decimals + 1, '0');
            whole = whole.Insert(whole.Length - decimals, ".");
        }
        return ToDecimal((negative ? "-" : "") + whole);
    }

    private string DigitsString(decimal value, int decimals, int totalLength)
    {
        if (decimal.Round(value, decimals) != value) throw new OverflowException("Decimal value exceeds field scale.");
        var digits = Math.Abs(value).ToString("F" + decimals, CultureInfo.InvariantCulture).Replace(".", "", StringComparison.Ordinal).TrimStart('0').PadLeft(totalLength, '0');
        if (digits.Length > totalLength)
        {
            throw new OverflowException($"Value {value} exceeds the field capacity of {totalLength} digits.");
        }

        return digits;
    }

    private void WriteText(byte[] buffer, int offset, int length, string text)
    {
        if (text.Length > length)
        {
            throw new OverflowException($"Value '{text}' exceeds the field length of {length}.");
        }

        var bytes = EncodedText(_ccsid, text, length);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private string TextAt(byte[] buffer, int offset, int length) =>
        CodePage.FromBytes(_ccsid, buffer, offset, length).Trim();

    private static DateTimeOffset ParseDate(string text, string pattern)
    {
        var date = DateTime.ParseExact(text, pattern, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(date, TimeSpan.Zero);
    }

    private static object DefaultValue(FieldSpec field) => field.Type switch
    {
        FieldType.Alpha => string.Empty,
        FieldType.Logic => false,
        FieldType.Binary => 0L,
        FieldType.Float => 0d,
        FieldType.Date => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        FieldType.Time => TimeSpan.Zero,
        FieldType.Timestamp => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        _ => 0m,
    };

    private static string ToString(object value) => value.ToString() ?? string.Empty;

    private static decimal ToDecimal(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        try { _ = Ipc.Services.Sqlite.DatabaseSortKeys.Decimal(text); }
        catch (ArgumentException) { throw new FormatException("Invalid decimal value or unsupported precision."); }
        return decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private static long ToLong(object value)
    {
        if (value is decimal number && decimal.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue) return decimal.ToInt64(number);
        return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new FormatException("Invalid integral value.");
    }

    private static double ToDouble(object value) => double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
        ? number : throw new FormatException("Invalid finite floating-point value.");

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        _ => ToString(value).ToUpperInvariant() switch { "1" or "Y" or "T" or "TRUE" => true, "0" or "N" or "F" or "FALSE" => false, _ => throw new FormatException("Invalid logical value.") },
    };

    private static DateTimeOffset ToDate(object value) => value switch
    {
        DateTimeOffset d => d,
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero),
        _ => DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new FormatException("Invalid date value."),
    };

    private static TimeSpan ToTime(object value) => value switch
    {
        TimeSpan t => t,
        TimeOnly t => t.ToTimeSpan(),
        DateTimeOffset d => d.TimeOfDay,
        DateTime d => d.TimeOfDay,
        _ => TimeSpan.TryParseExact(Convert.ToString(value, CultureInfo.InvariantCulture), new[] { "hhmmss", @"hh\:mm\:ss", @"hh\.mm\.ss" }, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException("Invalid time value."),
    };

    private static byte[] ConvertToBytes(long value)
    {
        var bytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }
}

public static class RecordFormatting
{
    public static string ToText(FieldSpec field, object? value)
    {
        switch (field.Type)
        {
            case FieldType.Alpha:
                return ToString(value).PadRight(field.Length);

            case FieldType.Zoned:
            case FieldType.Packed:
                return ToDecimal(value).ToString("F" + field.Decimals, CultureInfo.InvariantCulture);

            case FieldType.Binary:
                return ToLong(value).ToString(CultureInfo.InvariantCulture);

            case FieldType.Float:
                return ToDouble(value).ToString("G17", CultureInfo.InvariantCulture);

            case FieldType.Logic:
                return ToBool(value) ? "1" : "0";

            case FieldType.Date:
                return ToDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            case FieldType.Time:
                return ToTime(value).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

            case FieldType.Timestamp:
                return ToDate(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            default:
                return string.Empty;
        }
    }

    private static string ToString(object? value) => value?.ToString() ?? string.Empty;

    private static decimal ToDecimal(object? value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        _ => decimal.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
    };

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        decimal d => decimal.ToInt64(d),
        _ => 0L,
    };

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        decimal m => (double)m,
        long l => l,
        _ => 0d,
    };

    private static bool ToBool(object? value) => value switch
    {
        bool b => b,
        null => false,
        _ => value.ToString() is "1" or "Y" or "T" or "true",
    };

    private static DateTimeOffset ToDate(object? value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d, TimeSpan.Zero),
        _ => DateTimeOffset.MinValue,
    };

    private static TimeSpan ToTime(object? value) => value switch
    {
        TimeSpan t => t,
        _ => TimeSpan.Zero,
    };
}
