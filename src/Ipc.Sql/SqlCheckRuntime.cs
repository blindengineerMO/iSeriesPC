using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;
using static Ipc.Db.Sql.SqlExpressionRuntime;

namespace Ipc.Db.Sql;

internal static class SqlCheckRuntime
{
    private sealed record Program(int Version, IReadOnlyList<FieldSpec> Fields, SqlCheckConstraint Constraint);
    private sealed record Plan(SqlBound Predicate, IReadOnlyList<FieldSpec> Fields);
    private sealed record Failure(string Id, string Message, string? State);
    private const string ErrorPrefix = "IPC_SQL_CHECK_ERROR_";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    internal static IEnumerable<SqlExpression> Children(SqlExpression expression) => expression switch {
        SqlUnary value => [value.Value], SqlBinary value => [value.Left, value.Right], SqlIsNull value => [value.Value],
        SqlIn value => new[] { value.Value }.Concat(value.Values), SqlCoalesce value => value.Values, SqlFunction value => value.Arguments,
        SqlCast value => [value.Value], SqlExtract value => [value.Value], SqlDuration value => [value.Value],
        SqlLike value => value.Escape is null ? [value.Value, value.Pattern] : [value.Value, value.Pattern, value.Escape], _ => [] };

    internal static IReadOnlyList<string> Validate(RecordFormat format, SqlCheckConstraint constraint)
    {
        if (constraint.Condition is null || !CodePage.IsSupported(constraint.Ccsid)) throw Invalid("Invalid CHECK definition.");
        var expression = SqlStatementParser.ParseCheckCondition(constraint.Condition, constraint.Ccsid); var columns = new HashSet<string>();
        Inspect(expression, 0);
        var fields = format.Fields.Where(field => columns.Contains(field.Name)).ToArray();
        if (fields.Length != columns.Count) throw Invalid("CHECK references an unknown column.");
        if (fields.Select(field => field.Name).Distinct().Count() != fields.Length || fields.Any(field => !ValidField(field)) ||
            fields.Sum(field => (long)field.StorageLength) > 32766) throw Invalid("Invalid CHECK column metadata.");
        _ = Bind(fields, constraint, expression); return fields.Select(field => field.Name).ToArray();
        void Inspect(SqlExpression value, int depth)
        {
            if (depth > 128) throw new CpfException("SQL0904", "CHECK expression is too deep.");
            if (value is SqlColumn column)
            {
                if (column.Qualifier is not null && column.Qualifier != constraint.Qualifier) throw Invalid("CHECK column qualifier is not allowed.");
                if (constraint.Column is not null && constraint.Column != column.Name) throw Invalid("A column CHECK can only reference its declaring column.");
                columns.Add(column.Name);
            }
            if (value is SqlParameter or SqlStar || value is SqlConstant { Value.Kind: SqlValueKind.CurrentDate or SqlValueKind.CurrentTime or SqlValueKind.CurrentTimestamp } ||
                value is SqlFunction { Name: "COUNT" or "COUNT_BIG" or "SUM" or "AVG" or "MIN" or "MAX" })
                throw Invalid("CHECK cannot contain parameters, aggregates or special registers.");
            foreach (var child in Children(value)) Inspect(child, depth+1);
        }
    }
    private static bool ValidField(FieldSpec field) => ObjectName.IsValid(field.Name) && CodePage.IsSupported(field.Ccsid) &&
        (!field.VariableLength || field.Type == FieldType.Alpha) && field.Type switch {
            FieldType.Alpha => field.Length is >= 1 and <= 32740,
            FieldType.Packed or FieldType.Zoned => field.Length is >= 1 and <= 29 && field.Decimals >= 0 && field.Decimals <= Math.Min(28, field.Length),
            FieldType.Binary => field.Length is 2 or 4 or 8, FieldType.Float => field.Length is 4 or 8,
            FieldType.Logic => field.Length == 1, FieldType.Date => field.Length == 10,
            FieldType.Time => field.Length == 8, FieldType.Timestamp => field.Length is 19 or 26, _ => false };
    private static SqlBound Bind(IReadOnlyList<FieldSpec> fields, SqlCheckConstraint constraint, SqlExpression? expression = null)
    {
        var format = new RecordFormat { Name = "SQLCHECK", Fields = fields.ToList() };
        try
        {
            var predicate = new SqlBinder(format, constraint.Qualifier ?? "", constraint.Ccsid, DateTimeOffset.UnixEpoch, conversion: constraint.Conversion)
                .Bind(expression ?? SqlStatementParser.ParseCheckCondition(constraint.Condition, constraint.Ccsid));
            SqlBinder.Predicate(predicate); return predicate;
        }
        catch (CpfException error) when (error.MessageId is "SQL0104" or "SQL0206" or "SQL0440" or "SQL0408")
        { throw Invalid("Invalid CHECK expression: " + error.Message); }
    }
    internal static string Sql(RecordFormat format, SqlCheckConstraint constraint)
    {
        var columns = Validate(format, constraint); var fields = columns.Select(name => format.Find(name)!).ToArray();
        var program = JsonSerializer.Serialize(new Program(1, fields, constraint with { Name = null, Disabled = false, CheckPending = false }), JsonOptions);
        if (program.Length > 2097152) throw new CpfException("SQL0904", "Compiled CHECK definition exceeds two MiB.");
        var chunks = fields.Chunk(64).Select(chunk => "json_array(" + string.Join(',', chunk.Select(field => Quote(field.Name))) + ")");
        var frame = "json_array(" + string.Join(',', chunks) + ")";
        return (constraint.Name is null ? "" : "CONSTRAINT " + Quote(constraint.Name) + " ") + "CHECK(ipc_sql_check_v1('" + program.Replace("'", "''") + "'," + frame + "))";
    }
    internal static void Register(SqliteConnection connection)
    {
        var cache = new Dictionary<string, Plan>(StringComparer.Ordinal); var order = new Queue<string>(); var bytes = 0;
        connection.CreateFunction<string?, string?, long?>("ipc_sql_check_v1", (programText, frameText) => {
            try
            {
                JobExecutionBudget.Checkpoint();
                if (programText is null || programText.Length > 2097152 || frameText is null || frameText.Length > 2097152) throw Invalid("Invalid CHECK evaluation input.");
                if (!cache.TryGetValue(programText, out var plan))
                {
                    var program = JsonSerializer.Deserialize<Program>(programText, JsonOptions) ?? throw Invalid("Invalid CHECK program.");
                    if (program.Version != 1 || program.Fields is null || program.Constraint is null || program.Fields.Count > 8000 ||
                        program.Fields.Any(field => field is null || !ObjectName.IsValid(field.Name) || !Enum.IsDefined(field.Type)) || program.Fields.Select(field => field.Name).Distinct().Count() != program.Fields.Count)
                        throw Invalid("Unsupported CHECK program.");
                    var format = new RecordFormat { Name = "SQLCHECK", Fields = program.Fields.ToList() }; _ = Validate(format, program.Constraint);
                    plan = new(Bind(program.Fields, program.Constraint), program.Fields);
                    while (cache.Count >= 16 || bytes + programText.Length > 2097152) { var expired = order.Dequeue(); bytes -= expired.Length; cache.Remove(expired); }
                    cache.Add(programText, plan); order.Enqueue(programText); bytes += programText.Length;
                }
                using var frame = JsonDocument.Parse(frameText); var values = new object?[plan.Fields.Count]; var ordinal = 0;
                if (frame.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("Invalid CHECK row frame.");
                foreach (var part in frame.RootElement.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Array) throw Invalid("Invalid CHECK row chunk.");
                    foreach (var value in part.EnumerateArray())
                    {
                        if (ordinal >= values.Length) throw Invalid("CHECK row has too many values.");
                        values[ordinal] = value.ValueKind switch {
                            JsonValueKind.Null => null, JsonValueKind.String => value.GetString(),
                            JsonValueKind.Number => plan.Fields[ordinal].Type == FieldType.Float ? (object)value.GetDouble() : value.GetRawText(),
                            _ => throw Invalid("Invalid CHECK cell type.") };
                        ordinal++;
                    }
                }
                if (ordinal != values.Length) throw Invalid("CHECK row has missing values.");
                return plan.Predicate.Evaluate(values) switch { true => 1, false => 0, null => null, _ => throw Invalid("CHECK did not return a predicate.") };
            }
            catch (CpfException error)
            {
                var value = SqlDiagnostics.Enrich(error);
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Failure(value.MessageId, value.Message, value.SqlState))));
                throw new SqliteException(ErrorPrefix + payload + "_END", 1);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            { throw new SqliteException("SQL0546: Invalid persisted CHECK program or row frame.", 1); }
        }, isDeterministic: true);
    }
    internal static CpfException? DecodeError(SqliteException error)
    {
        var start = error.Message.IndexOf(ErrorPrefix, StringComparison.Ordinal); if (start < 0) return null; start += ErrorPrefix.Length;
        var end = error.Message.IndexOf("_END", start, StringComparison.Ordinal); if (end < 0 || end-start > 65536) return null;
        try
        {
            var failure = JsonSerializer.Deserialize<Failure>(Encoding.UTF8.GetString(Convert.FromBase64String(error.Message[start..end])));
            return failure is null ? null : new(failure.Id, failure.Message.StartsWith(failure.Id + ": ", StringComparison.Ordinal) ? failure.Message[(failure.Id.Length+2)..] : failure.Message, sqlState: failure.State);
        }
        catch (Exception invalid) when (invalid is JsonException or FormatException or ArgumentException) { return null; }
    }
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
    private static CpfException Invalid(string message) => new("SQL0546", message);
}
