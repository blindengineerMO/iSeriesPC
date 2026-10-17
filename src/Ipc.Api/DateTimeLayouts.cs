using System.Buffers.Binary;
using System.Globalization;
using Ipc.Core.Messages;

namespace Ipc.Api;

public sealed partial class DateTimeApi
{
    private static int Digits(string value, string error)
    {
        if (value.Length == 0 || value.Any(c => !char.IsAsciiDigit(c))) throw new CpfException(error, "Date/time field requires decimal digits.");
        return int.Parse(value, CultureInfo.InvariantCulture);
    }
    private static DateTime Parse(string format, string text)
    {
        var yearDigits = Long(format) ? 4 : 2; var start = Long(format) ? 0 : 1;
        var century = Long(format) ? 0 : Digits(text[..1], "CPF1848") + 19;
        var julian = format is "*JUL" or "*LONGJUL";
        var yearAt = format is "*MDY" or "*DMY" or "*MDYY" or "*DMYY" ? start + 4 : start;
        var year = century * 100 + Digits(text.Substring(yearAt, yearDigits), "CPF1060");
        try
        {
            if (julian)
            {
                if (text[^1] != ' ') throw BadDate();
                var day = Digits(text.Substring(start + yearDigits, 3), "CPF1060");
                if (day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) throw BadDate();
                return new DateTime(year, 1, 1).AddDays(day - 1);
            }
            var monthAt = format is "*MDY" or "*MDYY" ? start : format is "*DMY" or "*DMYY" ? start + 2 : start + yearDigits;
            var dayAt = format is "*MDY" or "*MDYY" ? start + 2 : format is "*DMY" or "*DMYY" ? start : start + yearDigits + 2;
            return new DateTime(year, Digits(text.Substring(monthAt, 2), "CPF1060"), Digits(text.Substring(dayAt, 2), "CPF1060"));
        }
        catch (ArgumentOutOfRangeException) { throw BadDate(); }
    }
    private static DateTime WithTime(DateTime date, string text, string precision)
    {
        var hour = Digits(text[..2], "CPF1061"); var minute = Digits(text.Substring(2, 2), "CPF1061"); var second = Digits(text.Substring(4, 2), "CPF1061");
        if (hour > 23 || minute > 59 || second > 59) throw new CpfException("CPF1061", "Time is not valid.");
        var fraction = Digits(text[6..], "CPF1849");
        return date.AddHours(hour).AddMinutes(minute).AddSeconds(second).AddTicks(fraction * (precision == "1" ? 10L : 10000L));
    }
    private static string Date(string format, DateTime value)
    {
        if (!Long(format) && value.Year is < 1900 or > 2899) throw BadDate();
        var pattern = format switch { "*YMD" => "yyMMdd", "*MDY" => "MMddyy", "*DMY" => "ddMMyy",
            "*YYMD" => "yyyyMMdd", "*MDYY" => "MMddyyyy", "*DMYY" => "ddMMyyyy", "*JUL" => "yy", "*LONGJUL" => "yyyy", _ => throw BadDate() };
        var date = value.ToString(pattern, CultureInfo.InvariantCulture);
        if (format is "*JUL" or "*LONGJUL") date += value.DayOfYear.ToString("D3", CultureInfo.InvariantCulture) + " ";
        return Long(format) ? date : (value.Year / 100 - 19).ToString(CultureInfo.InvariantCulture) + date;
    }
    private static string Fraction(DateTime value, string precision) => (value.Ticks % TimeSpan.TicksPerSecond / (precision == "1" ? 10 : 10000)).ToString(precision == "1" ? "D6" : "D3", CultureInfo.InvariantCulture);
    private static DateTime Round(DateTime date, string precision)
    {
        var unit = precision == "1" ? 10 : 10000;
        var ticks = date.Ticks - date.Ticks % unit + (date.Ticks % unit >= unit / 2 ? unit : 0);
        if (ticks > DateTime.MaxValue.Ticks) throw BadDate();
        return new DateTime(ticks);
    }
    private static DateTime QuantizeTimestamp(DateTime date) => new(date.Ticks - date.Ticks % 80);
    private static DateTimeOffset ChangeZone(DateTime date, TimeZoneInfo input, TimeZoneInfo output, string segment, string precision)
    {
        if (input.IsInvalidTime(date)) throw BadDate();
        var instant = new DateTimeOffset(date, Offset(date, input, segment));
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(Round(instant.UtcDateTime, precision), TimeSpan.Zero), output);
    }
    private static TimeSpan Offset(DateTime date, TimeZoneInfo zone, string segment) => zone.IsAmbiguousTime(date)
        ? segment == "1" ? zone.GetAmbiguousTimeOffsets(date).Max() : zone.GetAmbiguousTimeOffsets(date).Min() : zone.GetUtcOffset(date);
    private static byte[] Dos(DateTime date, TimeSpan offset)
    {
        var bytes = new byte[11]; bytes[0] = (byte)date.Hour; bytes[1] = (byte)date.Minute; bytes[2] = (byte)date.Second;
        bytes[3] = (byte)(date.Millisecond / 10); bytes[4] = (byte)date.Day; bytes[5] = (byte)date.Month;
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6), (short)date.Year);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(8), (short)-offset.TotalMinutes); bytes[10] = (byte)date.DayOfWeek; return bytes;
    }
    private byte[] ZoneInformation(string name, DateTime date)
    {
        if (name != "*UTC" && date >= ZoneMinimum && date < ZoneMaximum)
            throw Invalid("Native time-zone description metadata is not yet backed; use zero information length.");
        // IBM specifies zero binary fields and blank character fields for UTC/out-of-range identity conversions.
        var bytes = new byte[116]; var blank = _arguments.Encode(" ")[0];
        bytes.AsSpan(8, 12).Fill(blank); bytes.AsSpan(24, 88).Fill(blank); return bytes;
    }
}
