using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;

namespace Ipc.Db.Sql;

internal static partial class SqlExpressionRuntime
{
    internal enum SqlKind { Null, Integer, Decimal, Float, Text, Date, Time, Timestamp, Boolean, Duration }
    internal sealed record SqlBound(SqlKind Kind, Func<object?[], object?> Evaluate, bool Reads = false,
        int Precision = 0, int Scale = 0, int Bits = 0, int Width = 0, int Ccsid = 37, bool Literal = false, bool Nullable = true, bool Varying = false, string? Unit = null, FieldType? Storage = null)
    {
        internal bool Numeric => Kind is SqlKind.Integer or SqlKind.Decimal or SqlKind.Float;
    }
    internal sealed partial class SqlBinder(RecordFormat format, string qualifier, int ccsid, DateTimeOffset now,
        Func<SqlColumn, SqlBound>? resolveColumn = null, Func<SqlExpression, SqlBound?>? extension = null, Action<string, string>? warning = null, SqlConversionOptions? conversion = null)
    {
        internal SqlBound Bind(SqlExpression expression, int depth = 0)
        {
            if (depth > 128) throw Invalid("SQL expression exceeds the supported nesting limit.");
            if (extension?.Invoke(expression) is { } custom) return custom;
            SqlBound Child(SqlExpression value) => Bind(value, depth + 1);
            switch (expression)
            {
                case SqlColumn column:
                {
                    if (resolveColumn is not null) return resolveColumn(column);
                    if (column.Qualifier is not null && column.Qualifier != qualifier) throw new CpfException("SQL0206", "Unknown table qualifier " + column.Qualifier + ".");
                    var index = format.Fields.FindIndex(field => field.Name == column.Name);
                    if (index < 0) throw new CpfException("SQL0206", "Unknown column " + column.Name + ".");
                    return Column(format.Fields[index], index);
                }
                case SqlFunction function: return Function(function, depth);
                case SqlCast cast: return Cast(Child(cast.Value), cast.Type);
                case SqlExtract extract: return Extract(Child(extract.Value), extract.Unit);
                case SqlDuration duration:
                {
                    var value = Child(duration.Value);
                    if (value.Kind is not (SqlKind.Integer or SqlKind.Decimal or SqlKind.Null)) throw Invalid("A labeled duration requires an exact numeric value.");
                    return value with { Kind = SqlKind.Duration, Unit = duration.Unit };
                }
                case SqlLike like: return Like(Child(like.Value), Child(like.Pattern), like.Escape is null ? null : Child(like.Escape), like.Negated);
                case SqlConstant constant: return Constant(constant.Value);
                case SqlUnary { Operator: "+" or "-", Value: SqlConstant { Value.Kind: SqlValueKind.Number } number } unary:
                    return Constant(number.Value with { Value = (unary.Operator == "-" ? "-" : "") + number.Value.Value });
                case SqlUnary unary:
                {
                    var value = Child(unary.Value);
                    if (unary.Operator == "NOT") { Predicate(value); return new(SqlKind.Boolean, row => value.Evaluate(row) is bool flag ? !flag : null, value.Reads, Nullable: value.Nullable); }
                    if (!value.Numeric && value.Kind != SqlKind.Null) throw Invalid("Unary arithmetic requires a numeric value.");
                    if (value.Kind == SqlKind.Null) return value;
                    var result = value.Kind == SqlKind.Integer && unary.Operator == "-" ? value with { Bits = Math.Max(32, value.Bits), Precision = value.Bits <= 32 ? 11 : 19 } : value;
                    return result with { Evaluate = row => {
                        var raw = value.Evaluate(row); if (raw is null) return null;
                        if (value.Kind == SqlKind.Float) return Finite(unary.Operator == "-" ? -(double)raw : (double)raw);
                        var exact = (SqlExactNumber)raw; if (unary.Operator == "-") exact = exact with { Units = -exact.Units };
                        return result.Kind == SqlKind.Integer ? exact.CheckedInteger(result.Bits) : exact.Checked(result.Precision, result.Scale);
                    }};
                }
                case SqlIsNull test:
                { var value = Child(test.Value); return new(SqlKind.Boolean, row => (value.Evaluate(row) is null) != test.Negated, value.Reads, Nullable: false); }
                case SqlIn list:
                {
                    var value = Child(list.Value); var choices = list.Values.Select(Child).ToArray();
                    foreach (var choice in choices) Comparable(value, choice);
                    return new(SqlKind.Boolean, row => {
                        var raw = value.Evaluate(row); if (raw is null) return null; var unknown = false;
                        foreach (var choice in choices) { var other = choice.Evaluate(row); if (other is null) unknown = true;
                            else if (Compare(value, raw, choice, other, conversion) == 0) return !list.Negated; }
                        return unknown ? null : list.Negated;
                    }, value.Reads || choices.Any(item => item.Reads), Nullable: value.Nullable || choices.Any(item => item.Nullable));
                }
                case SqlCoalesce coalesce:
                {
                    var values = coalesce.Values.Select(Child).ToArray(); var type = values.Aggregate(Common);
                    return type with { Reads = values.Any(value => value.Reads), Nullable = values.All(value => value.Nullable), Evaluate = row => {
                        foreach (var value in values) if (value.Evaluate(row) is { } result) return ConvertCommon(type, result);
                        return null;
                    }};
                }
                case SqlBinary binary:
                {
                    var left = Child(binary.Left); var right = Child(binary.Right); var reads = left.Reads || right.Reads;
                    if (binary.Operator is "AND" or "OR")
                    {
                        Predicate(left); Predicate(right);
                        return new(SqlKind.Boolean, row => {
                            var l = (bool?)left.Evaluate(row); var r = (bool?)right.Evaluate(row);
                            return binary.Operator == "AND" ? l & r : l | r;
                        }, reads, Nullable: left.Nullable || right.Nullable);
                    }
                    if (binary.Operator is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
                    {
                        Comparable(left, right);
                        return new(SqlKind.Boolean, row => {
                            var l = left.Evaluate(row); var r = right.Evaluate(row); if (l is null || r is null) return null;
                            var comparison = Compare(left, l, right, r, conversion);
                            return binary.Operator switch { "=" => comparison == 0, "<>" or "!=" => comparison != 0,
                                "<" => comparison < 0, ">" => comparison > 0, "<=" => comparison <= 0, _ => comparison >= 0 };
                        }, reads, Nullable: left.Nullable || right.Nullable);
                    }
                    if (binary.Operator == "||")
                    {
                        if (left.Kind is not (SqlKind.Text or SqlKind.Null) || right.Kind is not (SqlKind.Text or SqlKind.Null)) throw Invalid("Concatenation requires character operands.");
                        var width = left.Width + right.Width; if (width > 32740) throw new CpfException("SQL0404", "Concatenation result is too long.");
                        var code = TextCcsid(left, right);
                        return new(SqlKind.Text, row => left.Evaluate(row) is string l && right.Evaluate(row) is string r ? l + r : null, reads, Width: width, Ccsid: code, Nullable: left.Nullable || right.Nullable, Varying: left.Varying || right.Varying);
                    }
                    return left.Kind == SqlKind.Duration || right.Kind == SqlKind.Duration || left.Kind is SqlKind.Date or SqlKind.Time or SqlKind.Timestamp || right.Kind is SqlKind.Date or SqlKind.Time or SqlKind.Timestamp
                        ? DatetimeArithmetic(binary.Operator, left, right) : Arithmetic(binary.Operator, left, right);
                }
                default: throw Invalid("Unsupported SQL expression.");
            }
        }
        private SqlBound Constant(SqlValue value)
        {
            if (value.Kind == SqlValueKind.Default) throw Invalid("DEFAULT is supported only as a complete SET value.");
            if (value.Kind != SqlValueKind.Number)
            {
                var raw = value.Kind switch {
                    SqlValueKind.CurrentDate => SqlNow(FieldType.Date, now), SqlValueKind.CurrentTime => SqlNow(FieldType.Time, now),
                    SqlValueKind.CurrentTimestamp => SqlNow(FieldType.Timestamp, now), _ => value.Value };
                var width = raw is string textValue ? EncodedLength(textValue, ccsid) : 0;
                if (width > 32740) throw new CpfException("SQL0404", "Character literal exceeds the supported byte width.");
                return raw is null ? new(SqlKind.Null, _ => null, Literal: true) :
                    new(value.Kind switch { SqlValueKind.CurrentDate => SqlKind.Date, SqlValueKind.CurrentTime => SqlKind.Time, SqlValueKind.CurrentTimestamp => SqlKind.Timestamp, _ => SqlKind.Text },
                        _ => raw, Width: width, Ccsid: ccsid, Literal: true, Nullable: false, Scale: value.Kind == SqlValueKind.CurrentTimestamp ? 6 : 0);
            }
            var text = (string)value.Value!;
            if (text.IndexOfAny(['e', 'E']) >= 0)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)) throw SqlExactNumber.Error("Invalid floating-point literal.");
                floating = Finite(floating); return new(SqlKind.Float, _ => floating, Literal: true, Nullable: false, Bits: 64);
            }
            var digits = text.TrimStart('+', '-').Replace(".", "");
            var significant = text.Contains('.') ? digits.TrimStart('0').Length : digits.Length;
            var point = text.IndexOf('.'); var scale = point < 0 ? 0 : text.Length - point - 1;
            var precision = Math.Max(Math.Max(1, significant), scale);
            if (precision > 63 || scale > 63) throw SqlExactNumber.Error("Decimal literal exceeds 63 digits.");
            var exact = SqlExactNumber.Parse(text);
            var integral = !text.Contains('.') && exact.Units >= long.MinValue && exact.Units <= long.MaxValue;
            var bits = exact.Units >= int.MinValue && exact.Units <= int.MaxValue ? 32 : 64;
            return new(integral ? SqlKind.Integer : SqlKind.Decimal, _ => exact, Precision: precision, Scale: exact.Scale, Bits: bits, Literal: true, Nullable: false);
        }
        internal static SqlBound Column(FieldSpec field, int index)
        {
            var type = field.Type switch {
                FieldType.Binary => SqlKind.Integer, FieldType.Packed or FieldType.Zoned => SqlKind.Decimal,
                FieldType.Float => SqlKind.Float, FieldType.Date => SqlKind.Date, FieldType.Time => SqlKind.Time,
                FieldType.Timestamp => SqlKind.Timestamp, FieldType.Logic => SqlKind.Boolean, _ => SqlKind.Text };
            return new(type, row => Read(field, row[index]), true,
                field.Type == FieldType.Binary ? field.Length switch { 2 => 5, 4 => 11, _ => 19 } : field.Length,
                field.Type == FieldType.Timestamp && field.Length == 26 ? 6 : field.Decimals, field.Length * 8, field.Length, field.Ccsid, Nullable: field.NullCapable, Varying: field.VariableLength, Storage: field.Type);
        }
        internal static void Predicate(SqlBound value)
        { if (value.Kind is not (SqlKind.Boolean or SqlKind.Null)) throw Invalid("WHERE/boolean operands require a predicate."); }
        internal static object? Read(FieldSpec field, object? raw) => raw is null ? null : field.Type switch {
            FieldType.Binary or FieldType.Packed or FieldType.Zoned => SqlExactNumber.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!),
            FieldType.Alpha when !field.VariableLength => ((string)raw).PadRight(((string)raw).Length + Math.Max(0, field.Length - EncodedLength((string)raw, field.Ccsid))),
            FieldType.Float => field.Length == 4 ? (double)(float)Convert.ToDouble(raw, CultureInfo.InvariantCulture) : Convert.ToDouble(raw, CultureInfo.InvariantCulture), FieldType.Logic => Convert.ToInt64(raw) != 0, _ => raw };
        private static SqlBound Arithmetic(string operation, SqlBound left, SqlBound right)
        {
            if ((!left.Numeric && left.Kind != SqlKind.Null) || (!right.Numeric && right.Kind != SqlKind.Null)) throw Invalid("Arithmetic requires numeric operands.");
            if (left.Kind == SqlKind.Null) left = left with { Kind = right.Kind, Precision = right.Precision, Scale = right.Scale, Bits = right.Bits };
            if (right.Kind == SqlKind.Null) right = right with { Kind = left.Kind, Precision = left.Precision, Scale = left.Scale, Bits = left.Bits };
            var kind = left.Kind == SqlKind.Float || right.Kind == SqlKind.Float ? SqlKind.Float :
                left.Kind == SqlKind.Decimal || right.Kind == SqlKind.Decimal ? SqlKind.Decimal : SqlKind.Integer;
            var bits = Math.Max(32, Math.Max(left.Bits, right.Bits)); var precision = bits == 64 ? 19 : 11; var scale = 0;
            if (kind == SqlKind.Decimal)
            {
                var maximum = Math.Max(left.Precision, right.Precision) > 31 ? 63 : 31;
                scale = operation switch { "+" or "-" => Math.Max(left.Scale, right.Scale), "*" => Math.Min(31, left.Scale + right.Scale),
                    _ => Math.Max(0, Math.Min(31, maximum - left.Precision + left.Scale - right.Scale)) };
                precision = operation switch { "+" or "-" => Math.Min(maximum, Math.Max(left.Precision - left.Scale, right.Precision - right.Scale) + scale + 1),
                    "*" => Math.Min(maximum, left.Precision + right.Precision), _ => left.Precision - left.Scale + right.Scale + scale };
                if (precision > 63 || scale > precision) throw Invalid("Decimal arithmetic result scale exceeds its precision.");
            }
            return new(kind, row => {
                var l = left.Evaluate(row); var r = right.Evaluate(row); if (l is null || r is null) return null;
                if (kind == SqlKind.Float)
                {
                    var a = Double(l); var b = Double(r); if (operation == "/" && b == 0) throw SqlExactNumber.Error("Division by zero.");
                    return Finite(operation switch { "+" => a + b, "-" => a - b, "*" => a * b, _ => a / b });
                }
                var result = SqlExactNumber.Calculate(operation, (SqlExactNumber)l, (SqlExactNumber)r, scale);
                return kind == SqlKind.Integer ? result.CheckedInteger(bits) : result.Checked(precision, scale);
            }, left.Reads || right.Reads, precision, scale, bits, Nullable: left.Nullable || right.Nullable);
        }
        private static SqlBound Common(SqlBound left, SqlBound right)
        {
            if (left.Kind == SqlKind.Null) return right;
            if (right.Kind == SqlKind.Null) return left;
            if (left.Numeric && right.Numeric)
            {
                if (left.Kind == SqlKind.Float || right.Kind == SqlKind.Float) return left with { Kind = SqlKind.Float };
                var scale = Math.Max(left.Scale, right.Scale);
                return left with { Kind = left.Kind == SqlKind.Decimal || right.Kind == SqlKind.Decimal ? SqlKind.Decimal : SqlKind.Integer,
                    Precision = Math.Max(left.Precision - left.Scale, right.Precision - right.Scale) + scale, Scale = scale, Bits = Math.Max(left.Bits, right.Bits) };
            }
            if (left.Kind != right.Kind) throw Invalid("COALESCE operands have incompatible types.");
            return left with { Width = Math.Max(left.Width, right.Width), Ccsid = TextCcsid(left, right), Literal = left.Literal && right.Literal };
        }
        private static object ConvertCommon(SqlBound type, object raw) => type.Kind == SqlKind.Float ? Double(raw) : raw;
        internal static void Comparable(SqlBound left, SqlBound right)
        {
            if (left.Kind == SqlKind.Null || right.Kind == SqlKind.Null || left.Numeric && right.Numeric) return;
            if (left.Kind == right.Kind) { if (left.Kind == SqlKind.Text) _ = TextCcsid(left, right); return; }
            if ((left.Kind is SqlKind.Date or SqlKind.Time or SqlKind.Timestamp && right.Kind == SqlKind.Text && right.Literal) ||
                (right.Kind is SqlKind.Date or SqlKind.Time or SqlKind.Timestamp && left.Kind == SqlKind.Text && left.Literal)) return;
            throw Invalid("Comparison operands have incompatible types.");
        }
        internal static int Compare(SqlBound left, object l, SqlBound right, object r, SqlConversionOptions? conversion = null)
        {
            if (left.Numeric && right.Numeric) return left.Kind == SqlKind.Float || right.Kind == SqlKind.Float ? Double(l).CompareTo(Double(r)) : ((SqlExactNumber)l).CompareTo((SqlExactNumber)r);
            if (left.Kind == SqlKind.Boolean) return ((bool)l).CompareTo((bool)r);
            if (left.Kind != SqlKind.Text || right.Kind != SqlKind.Text)
            {
                var type = left.Kind == SqlKind.Text ? right.Kind : left.Kind;
                var field = new FieldSpec { Name = "comparison", Type = type switch { SqlKind.Date => FieldType.Date, SqlKind.Time => FieldType.Time, _ => FieldType.Timestamp }, Length = type == SqlKind.Timestamp ? 26 : 10 };
                return string.CompareOrdinal((string)SqlFieldValue(field, l, conversion)!, (string)SqlFieldValue(field, r, conversion)!);
            }
            var code = TextCcsid(left, right); var width = Math.Max(1, Math.Max(Math.Max(left.Width, right.Width), Math.Max(EncodedLength((string)l, code), EncodedLength((string)r, code))));
            try { return DatabaseSortKeys.Text((string)l, code, width)!.AsSpan().SequenceCompareTo(DatabaseSortKeys.Text((string)r, code, width)); }
            catch (ArgumentException) { throw new CpfException("SQL0332", "Character comparison cannot be represented in the selected CCSID."); }
        }
        private static int TextCcsid(SqlBound left, SqlBound right)
        {
            if (left.Kind == SqlKind.Null || left.Literal) return right.Ccsid;
            if (right.Kind == SqlKind.Null || right.Literal || left.Ccsid == right.Ccsid) return left.Ccsid;
            throw new CpfException("SQL0332", "Comparison/concatenation between different column CCSIDs is not supported.");
        }
        private static int EncodedLength(string text, int code)
        {
            try { var encoding = (Encoding)CodePage.FromCcsid(code).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; return encoding.GetByteCount(text); }
            catch (EncoderFallbackException) { throw new CpfException("SQL0332", "Character expression cannot be represented in its CCSID."); }
        }
        private static double Double(object raw) => raw is SqlExactNumber exact ? exact.Double() : (double)raw;
        private static double Finite(double number) => double.IsFinite(number) ? number : throw SqlExactNumber.Error("Floating-point arithmetic overflow.");
        private static CpfException Invalid(string message) => new("SQL0104", message);
    }
}
