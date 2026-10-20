using System.Globalization;
using System.Numerics;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed partial class SqlQueryCompiler
    {
        private static SqlBound AggregateType(string name, SqlBound? argument)
        {
            if (name is "COUNT" or "COUNT_BIG") return new(SqlKind.Integer, _ => null, Precision: name == "COUNT" ? 11 : 19, Bits: name == "COUNT" ? 32 : 64, Nullable: false, Storage: FieldType.Binary);
            if (argument is null) throw Invalid("Missing aggregate argument.");
            if (name is "MIN" or "MAX") return argument with { Nullable = true, Literal = false };
            if (!argument.Numeric && argument.Kind != SqlKind.Null) throw new CpfException("SQL0402", "SUM/AVG requires a numeric argument.");
            if (argument.Kind == SqlKind.Float) return argument with { Bits = 64, Nullable = true, Literal = false };
            if (argument.Kind == SqlKind.Integer) return argument with { Bits = Math.Max(32, argument.Bits), Precision = argument.Bits <= 32 ? 11 : 19, Nullable = true, Literal = false };
            var maximum = argument.Precision > 31 ? 63 : 31;
            var scale = name == "AVG" ? Math.Min(31, maximum - argument.Precision + argument.Scale) : argument.Scale;
            var precision = name == "AVG" ? argument.Precision - argument.Scale + scale : maximum;
            return argument with { Kind = SqlKind.Decimal, Precision = precision, Scale = scale, Nullable = true, Literal = false, Storage = FieldType.Packed };
        }
        private sealed class QueryAggregateState(string name, bool distinct, SqlBound? input, SqlBound result, QueryBudget budget)
        {
            private long _count;
            private object? _value;
            private readonly HashSet<string>? _seen = distinct ? new(StringComparer.Ordinal) : null;
            private bool _allocated;
            private long _retainedBytes;
            internal void Add(object? value)
            {
                if (!_allocated) { budget.Memory(1, 128); _allocated = true; }
                if (value is null) return;
                if (_seen is not null)
                {
                    var key = TypedKey(input!, value); var text = key is byte[] bytes ? Convert.ToBase64String(bytes) : Convert.ToString(key, CultureInfo.InvariantCulture)!;
                    if (!_seen.Add(text)) return;
                    budget.Memory(1, text.Length * 2L + 32);
                }
                _count++;
                if (name is "COUNT" or "COUNT_BIG") { _ = new SqlExactNumber(_count, 0).CheckedInteger(result.Bits); return; }
                if (name is "MIN" or "MAX")
                {
                    if (_value is null || (SqlBinder.Compare(input!, value, input!, _value) is var comparison && (name == "MIN" ? comparison < 0 : comparison > 0)))
                    {
                        var bytes = value is string text ? text.Length * 2L : 128L;
                        budget.Memory(0, bytes - _retainedBytes); _retainedBytes = bytes; _value = value;
                    }
                    return;
                }
                if (result.Kind == SqlKind.Float)
                {
                    var sum = (_value is null ? 0d : (double)_value) + (value is SqlExactNumber exact ? exact.Double() : (double)value);
                    if (!double.IsFinite(sum)) throw SqlExactNumber.Error("Floating-point aggregate overflow."); _value = sum; return;
                }
                var previous = _value is null ? new SqlExactNumber(0, result.Scale) : (SqlExactNumber)_value;
                var total = SqlExactNumber.Calculate("+", previous, (SqlExactNumber)value, result.Scale);
                _value = result.Kind == SqlKind.Integer ? total.CheckedInteger(result.Bits) : total.Checked(result.Precision, result.Scale);
            }
            internal object? Finish()
            {
                if (name is "COUNT" or "COUNT_BIG") return new SqlExactNumber(_count, 0);
                if (_count == 0) return null;
                if (name != "AVG") return _value;
                if (result.Kind == SqlKind.Float) return (double)_value! / _count;
                var average = SqlExactNumber.Calculate("/", (SqlExactNumber)_value!, new(_count, 0), result.Scale);
                return result.Kind == SqlKind.Integer ? average.CheckedInteger(result.Bits) : average.Checked(result.Precision, result.Scale);
            }
        }
        private SqlBound Parameter(SqlQueryParameter binding)
        {
            var raw = binding.Value is DBNull ? null : binding.Value; var type = binding.Type;
            if (raw is BigInteger big && big.GetByteCount() > 27) throw new CpfException("SQL0406", "Exact parameter exceeds 63 digits.");
            if ((raw is SqlDecimalValue exactText && exactText.Text.Length > 1024) || (raw is string numericText && type?.Kind is SqlQueryKind.Decimal or SqlQueryKind.Numeric or SqlQueryKind.Integer or SqlQueryKind.SmallInt or SqlQueryKind.BigInt && numericText.Length > 1024))
                throw new CpfException("SQL0301", "Numeric parameter text exceeds the supported representation length.");
            if (type is null)
            {
                type = raw switch {
                    null => new(SqlQueryKind.Null), short or sbyte or byte => new(SqlQueryKind.SmallInt), int or ushort => new(SqlQueryKind.Integer), long or uint => new(SqlQueryKind.BigInt),
                    float => new(SqlQueryKind.Real), double => new(SqlQueryKind.Double), bool => new(SqlQueryKind.Boolean),
                    decimal number => DecimalParameter(number.ToString(CultureInfo.InvariantCulture)),
                    SqlDecimalValue exact => new(SqlQueryKind.Decimal, exact.Precision, exact.Scale),
                    BigInteger number => DecimalParameter(number.ToString(CultureInfo.InvariantCulture)),
                    DateOnly => new(SqlQueryKind.Date), TimeOnly or TimeSpan => new(SqlQueryKind.Time), DateTime or DateTimeOffset => new(SqlQueryKind.Timestamp, Scale: 6),
                    string text => new(SqlQueryKind.VarChar, Length: StrictTextLength(text, options.Ccsid), Ccsid: options.Ccsid),
                    _ => throw new CpfException("SQL0301", "Unsupported query parameter type.") };
            }
            if (raw is null && !type.Nullable) throw new CpfException("SQL0407", "Null query parameter requires a nullable type.");
            if (type.Precision is < 0 or > 63 || type.Precision == 0 && type.Kind is SqlQueryKind.Decimal or SqlQueryKind.Numeric || type.Kind == SqlQueryKind.Timestamp && type.Scale is not (0 or 6) || type.Scale is < 0 or > 63 || type.Scale > type.Precision && type.Kind is SqlQueryKind.Decimal or SqlQueryKind.Numeric || type.Length is < 0 or > 32740)
                throw new CpfException("SQL0301", "Invalid query parameter type attributes.");
            var kind = type.Kind switch {
                SqlQueryKind.Null => SqlKind.Null, SqlQueryKind.SmallInt or SqlQueryKind.Integer or SqlQueryKind.BigInt => SqlKind.Integer,
                SqlQueryKind.Decimal or SqlQueryKind.Numeric => SqlKind.Decimal, SqlQueryKind.Real or SqlQueryKind.Double => SqlKind.Float,
                SqlQueryKind.Character or SqlQueryKind.VarChar => SqlKind.Text, SqlQueryKind.Date => SqlKind.Date, SqlQueryKind.Time => SqlKind.Time,
                SqlQueryKind.Timestamp => SqlKind.Timestamp, SqlQueryKind.Boolean => SqlKind.Boolean, _ => throw new CpfException("SQL0301", "Invalid query parameter kind.") };
            if (kind == SqlKind.Text && !Ipc.Core.Text.CodePage.IsSupported(type.Ccsid)) throw new CpfException("SQL0332", "Unsupported parameter CCSID.");
            var bits = type.Kind == SqlQueryKind.SmallInt ? 16 : type.Kind is SqlQueryKind.BigInt or SqlQueryKind.Double ? 64 : 32;
            var precision = kind == SqlKind.Integer ? bits == 16 ? 5 : bits == 32 ? 11 : 19 : type.Precision;
            object? value = null;
            try
            {
                if (raw is not null)
                {
                    value = kind switch {
                        SqlKind.Integer => SqlExactNumber.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!).CheckedInteger(bits),
                        SqlKind.Decimal => SqlExactNumber.Parse(raw is SqlDecimalValue dec ? dec.Text : Convert.ToString(raw, CultureInfo.InvariantCulture)!).Checked(Math.Max(1, type.Precision), type.Scale),
                        SqlKind.Float => raw is string floatText ? double.Parse(floatText, NumberStyles.Float, CultureInfo.InvariantCulture) : raw is SqlDecimalValue decimalFloat ? double.Parse(decimalFloat.Text, NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(raw, CultureInfo.InvariantCulture), SqlKind.Boolean => Convert.ToBoolean(raw, CultureInfo.InvariantCulture),
                        SqlKind.Date => raw is DateOnly day ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(raw, CultureInfo.InvariantCulture),
                        SqlKind.Time => raw switch { TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), TimeSpan time => time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), _ => Convert.ToString(raw, CultureInfo.InvariantCulture) },
                        SqlKind.Timestamp => raw switch { DateTime date => date.ToString(type.Scale == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), DateTimeOffset date => date.UtcDateTime.ToString(type.Scale == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), _ => Convert.ToString(raw, CultureInfo.InvariantCulture) },
                        SqlKind.Text => Convert.ToString(raw, CultureInfo.InvariantCulture), _ => throw new CpfException("SQL0301", "Non-null value has the NULL parameter type.") };
                    if (value is double number && (!double.IsFinite(number) || type.Kind == SqlQueryKind.Real && !float.IsFinite((float)number))) throw SqlExactNumber.Error("Floating-point parameter is out of range.");
                    if (type.Kind == SqlQueryKind.Real && value is double real) value = (double)(float)real;
                    if (raw is TimeSpan span && (span < TimeSpan.Zero || span >= TimeSpan.FromDays(1) || span.Ticks % TimeSpan.TicksPerSecond != 0) || raw is TimeOnly timeOnly && timeOnly.Ticks % TimeSpan.TicksPerSecond != 0)
                        throw new CpfException("SQL0301", "TIME parameter exceeds its range/precision.");
                    if (kind == SqlKind.Text)
                    {
                        var length = StrictTextLength((string)value!, type.Ccsid); if (length > type.Length) throw new CpfException("SQL0404", "Character parameter exceeds its declared byte width.");
                        if (type.Kind == SqlQueryKind.Character) value = (string)value! + new string(' ', type.Length - length);
                    }
                    if (kind is SqlKind.Date or SqlKind.Time or SqlKind.Timestamp)
                        value = SqlFieldValue(new() { Name = "parameter", Type = kind switch { SqlKind.Date => FieldType.Date, SqlKind.Time => FieldType.Time, _ => FieldType.Timestamp }, Length = kind switch { SqlKind.Date => 10, SqlKind.Time => 8, _ => type.Scale == 0 ? 19 : 26 }, NullCapable = true }, value);
                }
            }
            catch (CpfException) { throw; }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw new CpfException("SQL0301", "Query parameter cannot be converted to its declared type."); }
            return new(kind, _ => value, Precision: precision, Scale: type.Scale, Bits: bits, Width: type.Length, Ccsid: type.Ccsid, Literal: true, Nullable: raw is null,
                Varying: type.Kind == SqlQueryKind.VarChar, Storage: type.Kind == SqlQueryKind.Numeric ? FieldType.Zoned : kind == SqlKind.Decimal ? FieldType.Packed : null);
        }
        private static SqlQueryType DecimalParameter(string text)
        {
            var number = SqlExactNumber.Parse(text); var digits = BigInteger.Abs(number.Units).ToString(CultureInfo.InvariantCulture).Length;
            return new(SqlQueryKind.Decimal, Math.Max(digits, number.Scale), number.Scale);
        }
        private static int StrictTextLength(string text, int code)
        {
            try { var encoding = (System.Text.Encoding)Ipc.Core.Text.CodePage.FromCcsid(code).Clone(); encoding.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback; return encoding.GetByteCount(text); }
            catch (ArgumentException) { throw new CpfException("SQL0332", "Query parameter cannot be represented in its CCSID."); }
        }
    }
}
