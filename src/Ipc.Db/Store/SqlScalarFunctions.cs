using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;

namespace Ipc.Db.Sql;

internal static partial class SqlExpressionRuntime
{
    internal sealed partial class SqlBinder
    {
        private SqlBound Function(SqlFunction call, int depth)
        {
            if (call.Distinct) throw Invalid("DISTINCT is valid only for aggregate functions.");
            var args = call.Arguments.Select(value => Bind(value, depth + 1)).ToArray();
            void Arity(int minimum, int maximum) { if (args.Length < minimum || args.Length > maximum) throw Invalid("Invalid argument count for " + call.Name + "."); }
            SqlBound Unary(SqlBound type, Func<object, object> operation) => type with { Reads = args.Any(a => a.Reads), Nullable = args.Any(a => a.Nullable), Literal = false,
                Evaluate = row => args[0].Evaluate(row) is { } raw ? operation(raw) : null };
            switch (call.Name)
            {
                case "NULLIF":
                    Arity(2, 2); Comparable(args[0], args[1]);
                    return args[0] with { Reads = args.Any(a => a.Reads), Nullable = true, Literal = false, Evaluate = row => {
                        var first = args[0].Evaluate(row); var second = args[1].Evaluate(row);
                        return first is not null && second is not null && Compare(args[0], first, args[1], second, conversion) == 0 ? null : first;
                    }};
                case "ABS":
                    Arity(1, 1); Numeric(args[0]);
                    return Unary(args[0], raw => raw is double number ? Finite(Math.Abs(number)) : args[0].Kind == SqlKind.Integer
                        ? new SqlExactNumber(BigInteger.Abs(((SqlExactNumber)raw).Units), ((SqlExactNumber)raw).Scale).CheckedInteger(Math.Max(32, args[0].Bits))
                        : new SqlExactNumber(BigInteger.Abs(((SqlExactNumber)raw).Units), ((SqlExactNumber)raw).Scale));
                case "ROUND":
                    Arity(1, 2); Numeric(args[0]); if (args.Length == 2) Integral(args[1]);
                    var rounded = args[0].Kind == SqlKind.Decimal ? args[0] with { Precision = Math.Min(args[0].Precision > 31 ? 63 : 31, args[0].Precision + 1) } : args[0];
                    return rounded with { Reads = args.Any(a => a.Reads), Nullable = args.Any(a => a.Nullable), Literal = false, Evaluate = row => {
                        var raw = args[0].Evaluate(row); var position = args.Length == 2 ? args[1].Evaluate(row) : new SqlExactNumber(0, 0);
                        if (raw is null || position is null) return null;
                        var places = Int(position); var value = raw is double number ? SqlExactNumber.Parse(number.ToString("R", CultureInfo.InvariantCulture)) : (SqlExactNumber)raw;
                        var delta = (long)value.Scale - places;
                        if (delta > 0)
                        {
                            if (delta > 2048) value = new(0, value.Scale);
                            else { var factor = BigInteger.Pow(10, (int)delta); var units = value.Units / factor;
                                if (BigInteger.Abs(value.Units % factor) * 2 >= factor) units += value.Units.Sign;
                                value = new(units * factor, value.Scale); }
                        }
                        return rounded.Kind switch { SqlKind.Float => Finite(value.Double()), SqlKind.Integer => value.CheckedInteger(Math.Max(32, rounded.Bits)), _ => value.Checked(rounded.Precision, rounded.Scale) };
                    }};
                case "UPPER": case "UCASE": case "LOWER": case "LCASE":
                    Arity(1, 1); Text(args[0]);
                    return Unary(args[0], raw => call.Name is "UPPER" or "UCASE" ? ((string)raw).ToUpperInvariant() : ((string)raw).ToLowerInvariant());
                case "LENGTH": case "OCTET_LENGTH": case "CHARACTER_LENGTH": case "CHAR_LENGTH":
                    Arity(1, 1);
                    var integer = new SqlBound(SqlKind.Integer, _ => null, Precision: 11, Bits: 32, Storage: FieldType.Binary);
                    return Unary(integer, raw => new SqlExactNumber(call.Name is "CHAR_LENGTH" or "CHARACTER_LENGTH" ? TextValue(raw).EnumerateRunes().Count() : StorageLength(args[0], raw), 0));
                case "TRIM": case "TRIM_BOTH": case "TRIM_LEADING": case "TRIM_TRAILING": case "LTRIM": case "RTRIM":
                    Arity(1, 2); Text(args[0]); if (args.Length == 2) Text(args[1]);
                    return args[0] with { Varying = true, Reads = args.Any(a => a.Reads), Nullable = args.Any(a => a.Nullable), Literal = false, Evaluate = row => {
                        var value = args[0].Evaluate(row) as string; var strip = args.Length == 1 ? " " : args[1].Evaluate(row) as string;
                        if (value is null || strip is null) return null;
                        var leading = call.Name is not ("TRIM_TRAILING" or "RTRIM"); var trailing = call.Name is not ("TRIM_LEADING" or "LTRIM");
                        if (call.Name is "LTRIM" or "RTRIM") return leading ? value.TrimStart(strip.ToCharArray()) : value.TrimEnd(strip.ToCharArray());
                        if (strip.EnumerateRunes().Count() != 1) throw new CpfException("SQL0171", "TRIM requires a single strip character.");
                        while (leading && value.StartsWith(strip, StringComparison.Ordinal)) value = value[strip.Length..];
                        while (trailing && value.EndsWith(strip, StringComparison.Ordinal)) value = value[..^strip.Length];
                        return value;
                    }};
                case "SUBSTR": case "SUBSTRING":
                    Arity(2, 3); Text(args[0]); Integral(args[1]); if (args.Length == 3) Integral(args[2]);
                    var width = args[0].Width; var varying = true;
                    if (call.Name == "SUBSTR" && args.Length == 3 && args[2].Literal && args[2].Evaluate([]) is { } literalLength)
                    { width = Int(literalLength); varying = width == 0; if (width < 0 || width > 32740) throw new CpfException("SQL0138", "Invalid substring length."); }
                    else if (call.Name == "SUBSTR" && args.Length == 2 && !args[0].Varying && args[1].Literal && args[1].Evaluate([]) is { } literalStart)
                    { width = args[0].Width - Int(literalStart) + 1; varying = width == 0; if (width < 0) throw new CpfException("SQL0138", "Substring exceeds fixed source bounds."); }
                    return args[0] with { Width = width, Varying = varying, Reads = args.Any(a => a.Reads), Nullable = args.Any(a => a.Nullable), Literal = false, Evaluate = row => {
                        var value = args[0].Evaluate(row) as string; var first = args[1].Evaluate(row); var count = args.Length == 3 ? args[2].Evaluate(row) : null;
                        if (value is null || first is null || args.Length == 3 && count is null) return null;
                        var start = Int(first); var length = count is null ? (int?)null : Int(count);
                        if (start < 1 || length < 0 || length > 32740) throw new CpfException("SQL0138", "Invalid substring start/length.");
                        if (call.Name == "SUBSTRING")
                        {
                            var chars = value.EnumerateRunes().ToArray(); var take = length ?? Math.Max(0, chars.Length - start + 1);
                            return string.Concat(chars.Skip(start - 1).Take(take).Select(r => r.ToString()));
                        }
                        var encoding = StrictEncoding(args[0].Ccsid); var bytes = encoding.GetBytes(value); var takeBytes = length ?? Math.Max(0, bytes.Length - start + 1);
                        if (takeBytes > 32740 || takeBytes > width || length is null && start > Math.Max(args[0].Width, bytes.Length) && !args[0].Varying) throw new CpfException("SQL0138", "Substring exceeds the source bounds.");
                        var result = Enumerable.Repeat(encoding.GetBytes(" ")[0], takeBytes).ToArray();
                        if (start <= bytes.Length) bytes.AsSpan(start - 1, Math.Min(takeBytes, bytes.Length - start + 1)).CopyTo(result);
                        try { return encoding.GetString(result); } catch (DecoderFallbackException) { throw new CpfException("SQL0332", "Substring is not representable as complete characters in its CCSID."); }
                    }};
                case "DATE": case "TIME": case "TIMESTAMP":
                    Arity(1, 1); return Cast(args[0], new(call.Name switch { "DATE" => FieldType.Date, "TIME" => FieldType.Time, _ => FieldType.Timestamp }, call.Name switch { "DATE" => 10, "TIME" => 8, _ => 26 }));
                case "SECOND":
                    Arity(1, 2); var secondsPrecision = 0;
                    if (args.Length == 2)
                    {
                        if (call.Arguments[1] is not SqlConstant || args[1].Kind != SqlKind.Integer || args[1].Evaluate([]) is not { } precisionValue || (secondsPrecision = Int(precisionValue)) is < 0 or > 6)
                            throw Invalid("SECOND precision requires an integer constant from 0 through 6.");
                    }
                    return Extract(args[0], "SECOND", secondsPrecision, args.Length == 2);
                case "YEAR": case "MONTH": case "DAY": case "HOUR": case "MINUTE": case "QUARTER":
                    Arity(1, 1); return Extract(args[0], call.Name);
                default: throw new CpfException("SQL0440", "SQL function " + call.Name + " is not implemented in this context.");
            }
        }
        private SqlBound Cast(SqlBound value, SqlDataType type)
        {
            var field = new FieldSpec { Name = "CAST", Type = type.Type, Length = type.Length, Decimals = type.Scale, VariableLength = type.Varying, Ccsid = type.Ccsid == 0 ? value.Ccsid : type.Ccsid, NullCapable = true };
            var result = Column(field, 0);
            return result with { Reads = value.Reads, Nullable = value.Nullable, Evaluate = row => {
                var raw = value.Evaluate(row); if (raw is null) return null;
                object? converted = raw;
                try
                {
                    if (type.Type is FieldType.Binary or FieldType.Packed or FieldType.Zoned)
                    {
                        var number = raw is SqlExactNumber exact ? exact : SqlExactNumber.Parse(Convert.ToString(SqlInputConversion.Normalize(type.Type, TextValue(raw), conversion), CultureInfo.InvariantCulture)!);
                        number = number.Rescale(type.Type == FieldType.Binary ? 0 : type.Scale);
                        if (type.Type == FieldType.Binary) return number.CheckedInteger(type.Length * 8);
                        return number.Checked(type.Length, type.Scale);
                    }
                    if (type.Type == FieldType.Float)
                    { var number = Finite(raw is SqlExactNumber exact ? exact.Double() : raw is string floatingText ? double.Parse((string)SqlInputConversion.Normalize(FieldType.Float, floatingText, conversion)!, NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(raw, CultureInfo.InvariantCulture)); return Finite(type.Length == 4 ? (double)(float)number : number); }
                    var text = TextValue(raw);
                    if (type.Type == FieldType.Alpha && value.Kind == SqlKind.Date && conversion is not null) text = conversion.FormatDate(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (type.Type == FieldType.Alpha && value.Kind == SqlKind.Time && conversion is not null) text = conversion.FormatTime(TimeOnly.ParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture));
                    if (type.Type == FieldType.Alpha && value.Numeric && conversion?.DecimalComma == true) text = text.Replace('.', ',');
                    if (type.Type == FieldType.Alpha)
                    {
                        var encoding = StrictEncoding(field.Ccsid);
                        if (encoding.GetByteCount(text) > field.Length) warning?.Invoke("01004", "SQL CAST truncated a character value.");
                        var runes = text.EnumerateRunes(); var output = new StringBuilder(); var bytes = 0;
                        foreach (var rune in runes) { var next = encoding.GetByteCount(rune.ToString()); if (bytes + next > field.Length) break; output.Append(rune); bytes += next; }
                        return output.ToString() + (type.Varying ? "" : new string(' ', field.Length - bytes));
                    }
                    if (type.Type == FieldType.Date && value.Kind == SqlKind.Timestamp) converted = text[..10];
                    else if (type.Type == FieldType.Time && value.Kind == SqlKind.Timestamp) converted = text.Substring(11, 8);
                    else if (type.Type == FieldType.Timestamp && value.Kind == SqlKind.Date) converted = text + " 00:00:00.000000";
                    return SqlFieldValue(field, converted, conversion);
                }
                catch (CpfException) { throw; }
                catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
                { throw new CpfException("SQL0420", "Invalid value for SQL CAST."); }
            }};
        }
        private static SqlBound Extract(SqlBound value, string unit, int? secondPrecision = null, bool decimalSeconds = false)
        {
            unit = unit.TrimEnd('S');
            if (value.Kind is not (SqlKind.Date or SqlKind.Time or SqlKind.Timestamp or SqlKind.Null)) throw Invalid("EXTRACT requires a datetime value.");
            if (unit is not ("YEAR" or "MONTH" or "DAY" or "HOUR" or "MINUTE" or "SECOND" or "QUARTER")) throw Invalid("Unsupported datetime extraction unit.");
            if (value.Kind == SqlKind.Time && unit is "YEAR" or "MONTH" or "DAY" or "QUARTER") throw Invalid("Datetime type does not contain the requested field.");
            var scale = unit == "SECOND" ? secondPrecision ?? (value.Kind == SqlKind.Timestamp ? value.Scale : value.Kind == SqlKind.Time ? 6 : 0) : 0;
            var decimalResult = unit == "SECOND" && (decimalSeconds || secondPrecision is null && value.Kind is SqlKind.Time or SqlKind.Timestamp);
            return new(decimalResult ? SqlKind.Decimal : SqlKind.Integer, row => {
                var raw = value.Evaluate(row); if (raw is null) return null;
                var text = (string)raw; var date = value.Kind == SqlKind.Time ? DateTime.ParseExact("2000-01-01 " + text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) :
                    DateTime.ParseExact(text, value.Kind == SqlKind.Date ? ["yyyy-MM-dd"] : ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.ffffff"], CultureInfo.InvariantCulture, DateTimeStyles.None);
                var whole = unit switch { "YEAR" => date.Year, "MONTH" => date.Month, "DAY" => date.Day, "HOUR" => date.Hour, "MINUTE" => date.Minute, "QUARTER" => (date.Month - 1) / 3 + 1, _ => date.Second };
                return new SqlExactNumber(whole * 1000000L + date.Ticks % TimeSpan.TicksPerSecond / 10, 6).Rescale(scale);
            }, value.Reads, Precision: decimalResult ? 2 + scale : 11, Scale: scale, Bits: 32, Nullable: value.Nullable);
        }
        private static SqlBound DatetimeArithmetic(string operation, SqlBound left, SqlBound right)
        {
            if (left.Kind == SqlKind.Duration && operation == "+") (left, right) = (right, left);
            if (left.Kind is not (SqlKind.Date or SqlKind.Time or SqlKind.Timestamp) || right.Kind != SqlKind.Duration || operation is not ("+" or "-"))
                throw Invalid("Datetime arithmetic requires a datetime plus/minus a labeled duration.");
            if (left.Kind == SqlKind.Date && right.Unit is not ("YEAR" or "MONTH" or "DAY") || left.Kind == SqlKind.Time && right.Unit is not ("HOUR" or "MINUTE" or "SECOND"))
                throw Invalid("Duration unit is incompatible with the datetime type.");
            return left with { Reads = left.Reads || right.Reads, Nullable = left.Nullable || right.Nullable, Literal = false, Evaluate = row => {
                var raw = left.Evaluate(row); var amount = right.Evaluate(row); if (raw is null || amount is null) return null;
                var number = (SqlExactNumber)amount; if (operation == "-") number = number with { Units = -number.Units };
                try
                {
                    var text = (string)raw;
                    var date = left.Kind == SqlKind.Time ? DateTime.ParseExact("2000-01-01 " + text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) :
                        DateTime.ParseExact(text, left.Kind == SqlKind.Date ? ["yyyy-MM-dd"] : ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.ffffff"], CultureInfo.InvariantCulture, DateTimeStyles.None);
                    if (right.Unit != "SECOND" && number.CompareTo(number.Rescale(0)) != 0) throw Invalid("This duration unit requires an integer value.");
                    date = right.Unit switch {
                        "YEAR" => date.AddYears(Int(number)), "MONTH" => date.AddMonths(Int(number)), "DAY" => date.AddDays(Int(number)),
                        "HOUR" => date.AddHours(Int(number)), "MINUTE" => date.AddMinutes(Int(number)),
                        _ => date.AddTicks(checked((long)number.Rescale(6).Units * 10)) };
                    return date.ToString(left.Kind switch { SqlKind.Date => "yyyy-MM-dd", SqlKind.Time => "HH:mm:ss", _ => left.Scale == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff" }, CultureInfo.InvariantCulture);
                }
                catch (Exception error) when (error is ArgumentException or OverflowException or FormatException) { throw new CpfException("SQL0183", "Datetime arithmetic is out of range."); }
            }};
        }
        private static SqlBound Like(SqlBound value, SqlBound pattern, SqlBound? escape, bool negated)
        {
            Text(value); Text(pattern); if (escape is not null) Text(escape);
            var code = TextCcsid(value, pattern);
            return new(SqlKind.Boolean, row => {
                var text = value.Evaluate(row) as string; var mask = pattern.Evaluate(row) as string; var esc = escape?.Evaluate(row) as string;
                if (text is null || mask is null || escape is not null && esc is null) return null;
                if (esc is not null && esc.EnumerateRunes().Count() != 1) throw new CpfException("SQL0130", "LIKE ESCAPE requires one character.");
                _ = EncodedLength(text, code); _ = EncodedLength(mask, code);
                var output = new StringBuilder("\\A"); var runes = mask.EnumerateRunes().ToArray();
                for (var i = 0; i < runes.Length; i++)
                {
                    var item = runes[i].ToString();
                    if (esc is not null && item == esc)
                    {
                        if (++i == runes.Length || runes[i].ToString() is not ("%" or "_") && runes[i].ToString() != esc) throw new CpfException("SQL0130", "Invalid LIKE escape sequence.");
                        output.Append(Regex.Escape(runes[i].ToString()));
                    }
                    else output.Append(item switch { "%" => ".*", "_" => "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])", _ => Regex.Escape(item) });
                }
                output.Append("\\z");
                try { return Regex.IsMatch(text, output.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)) != negated; }
                catch (Exception error) when (error is RegexMatchTimeoutException or NotSupportedException) { throw new CpfException("SQL0904", "LIKE pattern exceeds the supported complexity budget."); }
            }, value.Reads || pattern.Reads || escape?.Reads == true, Nullable: value.Nullable || pattern.Nullable || escape?.Nullable == true);
        }
        private static string TextValue(object value) => value switch { SqlExactNumber exact => exact.ToString(), double number => number.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
        private static int StorageLength(SqlBound type, object value) => type.Kind switch {
            SqlKind.Text => EncodedLength((string)value, type.Ccsid), SqlKind.Integer => Math.Max(2, type.Bits / 8),
            SqlKind.Decimal => type.Storage == FieldType.Zoned ? type.Precision : type.Precision / 2 + 1,
            SqlKind.Float => type.Bits == 32 ? 4 : 8, SqlKind.Date => 4, SqlKind.Time => 3, SqlKind.Timestamp => 7 + (type.Scale + 1) / 2, _ => 1 };
        private static Encoding StrictEncoding(int code) { var encoding = (Encoding)CodePage.FromCcsid(code).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback; return encoding; }
        private static int Int(object value)
        { var exact = (SqlExactNumber)value; if (exact.CompareTo(exact.Rescale(0)) != 0 || exact.Units / BigInteger.Pow(10, exact.Scale) < int.MinValue || exact.Units / BigInteger.Pow(10, exact.Scale) > int.MaxValue) throw new CpfException("SQL0802", "Integer argument is out of range."); return (int)exact.Rescale(0).Units; }
        private static void Numeric(SqlBound type) { if (!type.Numeric && type.Kind != SqlKind.Null) throw Invalid("Numeric argument required."); }
        private static void Integral(SqlBound type) { if (type.Kind is not (SqlKind.Integer or SqlKind.Null)) throw Invalid("Integer argument required."); }
        private static void Text(SqlBound type) { if (type.Kind is not (SqlKind.Text or SqlKind.Null)) throw Invalid("Character argument required."); }
    }
}
