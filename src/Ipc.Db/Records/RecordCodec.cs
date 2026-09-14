using System.Globalization;
using System.Text;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Records;

public sealed class RecordCodec
{
    private readonly int _ccsid;

    public RecordCodec(int ccsid = CodePage.DefaultSystem)
    {
        _ccsid = ccsid;
    }

    public byte[] Encode(RecordFormat format, IReadOnlyDictionary<string, object?> values)
    {
        var buffer = new byte[format.RecordLength];
        foreach (var field in format.Fields)
        {
            values.TryGetValue(field.Name, out var value);
            WriteValue(buffer, field, value ?? DefaultValue(field));
        }

        return buffer;
    }

    public object?[] Decode(RecordFormat format, byte[] buffer)
    {
        if (buffer.Length < format.RecordLength)
        {
            throw new ArgumentException("Record buffer is shorter than the record format.");
        }

        var values = new object?[format.Fields.Count];
        for (var index = 0; index < format.Fields.Count; index++)
        {
            values[index] = ReadValue(buffer, format.Fields[index]);
        }

        return values;
    }

    public void WriteValue(byte[] buffer, FieldSpec field, object? value)
    {
        var offset = field.Position - 1;
        var f = value ?? DefaultValue(field);

        switch (field.Type)
        {
            case FieldType.Alpha:
            {
                var bytes = CodePage.ToBytes(field.Ccsid, PadRight(ToString(f), field.Length));
                Buffer.BlockCopy(bytes, 0, buffer, offset, field.Length);
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
                for (var i = field.Length - 1; i >= 0; i--)
                {
                    buffer[offset + i] = (byte)(number & 0xFF);
                    number >>= 8;
                }

                break;
            }

            case FieldType.Float:
            {
                var bits = field.Length == 4
                    ? (long)BitConverter.SingleToInt32Bits((float)ToDouble(f))
                    : BitConverter.DoubleToInt64Bits(ToDouble(f));
                var bytes = ConvertToBytes(bits);
                for (var i = 0; i < field.Length; i++)
                {
                    buffer[offset + i] = bytes[(8 - field.Length) + i];
                }

                break;
            }

            case FieldType.Logic:
                buffer[offset] = ToBool(f) ? (byte)'1' : (byte)'0';
                break;

            case FieldType.Date:
                WriteText(buffer, offset, field.Length, ToDate(f).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                break;

            case FieldType.Time:
                WriteText(buffer, offset, field.Length, ToTime(f).ToString(@"hhmmss", CultureInfo.InvariantCulture));
                break;

            case FieldType.Timestamp:
                WriteText(buffer, offset, field.Length, ToDate(f).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                break;
        }
    }

    public object? ReadValue(byte[] buffer, FieldSpec field)
    {
        var offset = field.Position - 1;

        switch (field.Type)
        {
            case FieldType.Alpha:
                return CodePage.FromBytes(field.Ccsid, buffer, offset, field.Length).TrimEnd(' ');

            case FieldType.Zoned:
                return ReadZoned(buffer, offset, field);

            case FieldType.Packed:
                return ReadPacked(buffer, offset, field);

            case FieldType.Binary:
            {
                long number = 0;
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
                return buffer[offset] == (byte)'1';

            case FieldType.Date:
                return ParseDate(TextAt(buffer, offset, field.Length), "yyyyMMdd");

            case FieldType.Time:
            {
                var text = TextAt(buffer, offset, field.Length);
                return TimeSpan.ParseExact(text, "hhmmss", CultureInfo.InvariantCulture);
            }

            case FieldType.Timestamp:
                return ParseDate(TextAt(buffer, offset, field.Length), "yyyyMMddHHmmss");

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
        var byteCount = (field.Length + 1) / 2;
        var signNibble = value < 0 ? 0xD : 0xF;
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
            digits[i] = (char)('0' + (b & 0x0F));
        }

        var negative = (buffer[offset + field.Length - 1] >> 4) == 0x0D;
        return ParseDecimal(digits, field.Decimals, negative);
    }

    private decimal ReadPacked(byte[] buffer, int offset, FieldSpec field)
    {
        var byteCount = (field.Length + 1) / 2;
        var firstNibble = field.Length % 2 == 0;
        var digits = new List<char>(field.Length);
        for (var i = 0; i < byteCount; i++)
        {
            var b = buffer[offset + i];
            var high = (b >> 4) & 0x0F;
            var low = b & 0x0F;
            if (!(firstNibble && i == 0))
            {
                digits.Add((char)('0' + high));
            }

            var isLast = i == byteCount - 1;
            if (!isLast)
            {
                digits.Add((char)('0' + low));
            }
        }

        var negative = (buffer[offset + byteCount - 1] & 0x0F) == 0x0D;
        return ParseDecimal(digits, field.Decimals, negative);
    }

    private decimal ParseDecimal(IReadOnlyList<char> digits, int decimals, bool negative)
    {
        var whole = new string(digits.ToArray());
        if (whole.Length == 0)
        {
            whole = "0";
        }

        if (!decimal.TryParse(whole, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid numeric digits '{whole}'.");
        }

        if (decimals > 0)
        {
            parsed *= Pow10(decimals);
        }

        return negative ? -parsed : parsed;
    }

    private string DigitsString(decimal value, int decimals, int totalLength)
    {
        var scaled = decimal.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero);
        if (decimals > 0)
        {
            scaled /= Pow10(decimals);
        }

        var integer = decimal.ToInt64(scaled);
        var digits = integer.ToString(CultureInfo.InvariantCulture).PadLeft(totalLength, '0');
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

        var bytes = CodePage.ToBytes(_ccsid, text);
        for (var i = 0; i < bytes.Length; i++)
        {
            buffer[offset + i] = bytes[i];
        }

        for (var i = bytes.Length; i < length; i++)
        {
            buffer[offset + i] = (byte)' ';
        }
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

    private static decimal ToDecimal(object value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double f => System.Convert.ToDecimal(f),
        _ => decimal.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
    };

    private static long ToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        decimal d => decimal.ToInt64(d),
        _ => long.TryParse(ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L,
    };

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        decimal m => (double)m,
        long l => l,
        _ => double.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d,
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        _ => ToString(value) is "1" or "Y" or "T" or "true",
    };

    private static DateTimeOffset ToDate(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d, TimeSpan.Zero),
        _ => DateTimeOffset.TryParse(ToString(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static TimeSpan ToTime(object value) => value switch
    {
        TimeSpan t => t,
        DateTimeOffset d => d.TimeOfDay,
        DateTime d => d.TimeOfDay,
        _ => TimeSpan.TryParseExact(ToString(value), @"hhmmss", CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TimeSpan.Zero,
    };

    private static string PadRight(string text, int length) =>
        text.Length >= length ? text[..length] : text.PadRight(length);

    private static decimal Pow10(int exponent) =>
        new decimal(1, 0, 0, false, (byte)exponent);

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