using System.Globalization;

namespace Ipc.Core.Text;

public enum QDateFormat
{
    YmD = 1,
    DMY,
    MDY,
    Julian,
    Iso,
    Usa,
    Eur,
    Jis,
}

public enum QDateSeparator
{
    Slash,
    Dash,
    Period,
    Space,
    Comma,
    NoneApplied,
}

public static class QDate
{
    public static QDateFormat ParseFormat(string value) => value.ToUpperInvariant() switch
    {
        "*YMD" => QDateFormat.YmD,
        "*DMY" => QDateFormat.DMY,
        "*MDY" => QDateFormat.MDY,
        "*JUL" => QDateFormat.Julian,
        "*ISO" => QDateFormat.Iso,
        "*USA" => QDateFormat.Usa,
        "*EUR" => QDateFormat.Eur,
        "*JIS" => QDateFormat.Jis,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown date format '{value}'."),
    };

    public static QDateSeparator ParseSeparator(string value) => value switch
    {
        "/" => QDateSeparator.Slash,
        "-" => QDateSeparator.Dash,
        "." => QDateSeparator.Period,
        " " => QDateSeparator.Space,
        "," => QDateSeparator.Comma,
        "*NONE" => QDateSeparator.NoneApplied,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown date separator '{value}'."),
    };

    public static string Format(DateTimeOffset value, QDateFormat format, QDateSeparator sep)
    {
        var y = value.Year;
        var m = value.Month;
        var d = value.Day;
        var (yy, yyyy) = FullYear(y);

        return format switch
        {
            QDateFormat.YmD => F(yy, m, d, sep),
            QDateFormat.DMY => F(d, m, yy, sep),
            QDateFormat.MDY => F(m, d, yy, sep),
            QDateFormat.Julian => $"{yyyy}{value.DayOfYear:000}",
            QDateFormat.Iso => $"{yyyy}-{m:00}-{d:00}",
            QDateFormat.Usa => $"{m:00}/{d:00}/{yyyy}",
            QDateFormat.Eur => $"{d:00}.{m:00}.{yyyy}",
            QDateFormat.Jis => $"{yyyy}-{m:00}-{d:00}",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static DateTimeOffset? TryParse(string text, QDateFormat format, QDateSeparator sep)
    {
        try
        {
            return Parse(text, format, sep);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static DateTimeOffset Parse(string text, QDateFormat format, QDateSeparator sep)
    {
        switch (format)
        {
            case QDateFormat.YmD:
            case QDateFormat.DMY:
            case QDateFormat.MDY:
                return ParseShort(text, format, sep);
            case QDateFormat.Julian:
                if (text.Length != 7 || !int.TryParse(text, out var jul))
                {
                    throw new FormatException($"Invalid Julian date '{text}'.");
                }

                var year = jul / 1000;
                int dayOfYear = jul % 1000;
                var date = new DateTime(year, 1, 1).AddDays(dayOfYear - 1);
                return new DateTimeOffset(date, TimeSpan.Zero);
            case QDateFormat.Iso:
            case QDateFormat.Jis:
                return ParseIso(text);
            case QDateFormat.Usa:
                return ParseSlash(text, "M/d/yyyy");
            case QDateFormat.Eur:
                return ParseDot(text, "d.M.yyyy");
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static DateTimeOffset ParseShort(string text, QDateFormat format, QDateSeparator sep)
    {
        var seps = new[] { SepChar(sep), '/', '-', '.', ' ', ',' };
        var parts = text.Split(seps, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 && text.Length is 6 or 8)
        {
            var shortYear = text.Length == 6;
            parts = format switch
            {
                QDateFormat.YmD => new[]
                {
                    Sub(text, 0, shortYear ? 2 : 4),
                    Sub(text, shortYear ? 2 : 4, 2),
                    Sub(text, shortYear ? 4 : 6, 2),
                },
                QDateFormat.DMY => new[]
                {
                    Sub(text, 0, 2),
                    Sub(text, 2, 2),
                    Sub(text, shortYear ? 4 : 6, shortYear ? 2 : 4),
                },
                _ => new[]
                {
                    Sub(text, 0, 2),
                    Sub(text, 2, 2),
                    Sub(text, shortYear ? 4 : 6, shortYear ? 2 : 4),
                },
            };
        }

        if (parts.Length != 3)
        {
            throw new FormatException($"Invalid date '{text}'.");
        }

        int year;
        int month;
        int day;

        switch (format)
        {
            case QDateFormat.YmD:
                year = TwoDigitToYear(int.Parse(parts[0], CultureInfo.InvariantCulture));
                month = int.Parse(parts[1], CultureInfo.InvariantCulture);
                day = int.Parse(parts[2], CultureInfo.InvariantCulture);
                break;
            case QDateFormat.DMY:
                day = int.Parse(parts[0], CultureInfo.InvariantCulture);
                month = int.Parse(parts[1], CultureInfo.InvariantCulture);
                year = ToYear(parts[2], text.Length == 8 && parts[2].Length == 4);
                break;
            default:
                month = int.Parse(parts[0], CultureInfo.InvariantCulture);
                day = int.Parse(parts[1], CultureInfo.InvariantCulture);
                year = ToYear(parts[2], text.Length == 8 && parts[2].Length == 4);
                break;
        }

        return new DateTimeOffset(new DateTime(year, month, day), TimeSpan.Zero);
    }

    private static string Sub(string s, int start, int len) => s.Substring(start, len);

    private static int ToYear(string part, bool fullYear) =>
        fullYear
            ? int.Parse(part, CultureInfo.InvariantCulture)
            : TwoDigitToYear(int.Parse(part, CultureInfo.InvariantCulture));

    private static DateTimeOffset ParseIso(string text) =>
        new(ParseDate(text, "yyyy-MM-dd"), TimeSpan.Zero);

    private static DateTimeOffset ParseSlash(string text, string pattern) =>
        new(ParseDate(text, pattern), TimeSpan.Zero);

    private static DateTimeOffset ParseDot(string text, string pattern) =>
        new(ParseDate(text, pattern), TimeSpan.Zero);

    private static DateTime ParseDate(string text, string pattern) =>
        DateTime.ParseExact(text, pattern, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static int TwoDigitToYear(int yy)
    {
        if (yy < 40)
        {
            return 2000 + yy;
        }

        return 1900 + yy;
    }

    private static (int yy, int yyyy) FullYear(int y)
    {
        var yy = y % 100;
        return (yy, y);
    }

    private static string F(int p1, int p2, int p3, QDateSeparator sep)
    {
        var s = Sep(sep);
        var a = p1.ToString("00", CultureInfo.InvariantCulture);
        var b = p2.ToString("00", CultureInfo.InvariantCulture);
        var c = p3.ToString("00", CultureInfo.InvariantCulture);
        return s.Length == 0
            ? a + b + c
            : string.Concat(a, s, b, s, c);
    }

    private static string Sep(QDateSeparator sep) => sep switch
    {
        QDateSeparator.Slash => "/",
        QDateSeparator.Dash => "-",
        QDateSeparator.Period => ".",
        QDateSeparator.Space => " ",
        QDateSeparator.Comma => ",",
        _ => "",
    };

    private static char SepChar(QDateSeparator sep) => Sep(sep) switch
    {
        "" => '\0',
        var s => s[0],
    };
}