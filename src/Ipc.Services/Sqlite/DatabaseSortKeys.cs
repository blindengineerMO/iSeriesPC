using System.Globalization;
using System.Text;
using Ipc.Core.Text;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Deterministic byte keys shared by queries and maintained SQLite expression indexes.</summary>
public static class DatabaseSortKeys
{
    public static string KeyColumns(string expression, bool descending, bool includeNullDuplicates = false, bool nullable = true)
    {
        var direction = descending ? " DESC" : "";
        if (!nullable) return expression + direction;
        // A separate null flag prevents the replacement value from colliding with
        // real zero/empty values, and sorts null above every non-null key.
        return "(" + expression + " IS NULL)" + direction + "," +
            (includeNullDuplicates ? "COALESCE(" + expression + ",0)" : expression) + direction;
    }

    internal static void Register(SqliteConnection connection)
    {
        connection.CreateFunction<string?, byte[]?>("ipc_decimal_key_v1", Decimal, isDeterministic: true);
        connection.CreateFunction<string?, int, int, byte[]?>("ipc_text_key_v1", Text, isDeterministic: true);
    }
    public static byte[]? Decimal(string? text)
    {
        if (text is null) return null;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException("Invalid stored decimal value.");
        // TryParse may silently round excess fractional digits. Keys must never
        // merge distinct stored values because of that conversion.
        var unsigned = text.TrimStart('+', '-').Split('.');
        var integral = unsigned[0].TrimStart('0');
        var fraction = unsigned.Length == 2 ? unsigned[1].TrimEnd('0') : "";
        var canonical = (number < 0 ? "-" : "") + (integral.Length == 0 ? "0" : integral) + (fraction.Length == 0 ? "" : "." + fraction);
        if (canonical != number.ToString("0.############################", CultureInfo.InvariantCulture))
            throw new ArgumentException("Stored decimal value exceeds supported precision.");
        var parts = Math.Abs(number).ToString("F28", CultureInfo.InvariantCulture).Split('.');
        var digits = parts[0].PadLeft(29, '0') + parts[1];
        if (number < 0) digits = new string(digits.Select(c => (char)('9' - (c - '0'))).ToArray());
        return Encoding.ASCII.GetBytes((number < 0 ? "0" : "1") + digits);
    }
    public static byte[]? Text(string? text, int ccsid, int width)
    {
        if (text is null) return null;
        if (width is < 1 or > 32768 || !CodePage.IsSupported(ccsid)) throw new ArgumentException("Invalid character key width or CCSID.");
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        byte[] encoded;
        try { encoded = encoding.GetBytes(text); }
        catch (EncoderFallbackException) { throw new ArgumentException("Character key is not representable in its CCSID."); }
        if (encoded.Length > width) throw new ArgumentException("Character key exceeds its byte width.");
        var bytes = Enumerable.Repeat(encoding.GetBytes(" ")[0], width).ToArray(); encoded.CopyTo(bytes, 0); return bytes;
    }
}
