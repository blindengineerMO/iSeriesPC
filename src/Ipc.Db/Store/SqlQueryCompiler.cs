using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record QueryTable(SqlTableSource Source, string Library, string Name, string Member, FileDefinition Definition, int Ordinal)
    {
        internal string Qualifier => Source.Alias ?? Source.Table.Name;
        internal string Alias => "__t" + Ordinal;
        internal bool Nullable { get; set; }
        internal string? LogicalSql { get; set; }
        internal QueryPlan? View { get; init; }
        internal string? ViewOwner { get; init; }
    }
    private sealed record QueryReference(string Sql, SqlBound Type, Func<object?, object?> Decode);
    private sealed record QueryExpression(string Sql, SqlBound Type);
    private sealed class QueryBudget(SqlQueryLimits limits)
    {
        private long _evaluations, _bytes, _cells;
        internal void Step(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
            if (++_evaluations > limits.MaximumEvaluations) throw new CpfException("SQL0904", "Query evaluation budget exceeded.");
        }
        internal void Memory(long cells, long bytes)
        { if ((_cells += cells) > limits.MaximumCells || (_bytes += bytes) > limits.MaximumBytes) throw new CpfException("SQL0904", "Query grouping/distinct memory budget exceeded."); }
    }
    private sealed partial class SqlQueryCompiler(SqliteConnection connection, SqlSelect query, IReadOnlyList<QueryTable> tables,
        SqlExecutionOptions options, IReadOnlyList<SqlQueryParameter> parameters, SqlQueryLimits limits, CancellationToken token, QueryBudget? sharedBudget = null, DateTimeOffset? statementTime = null) : IDisposable
    {
        private readonly string _prefix = "ipc_q_" + Guid.NewGuid().ToString("N") + "_";
        private readonly List<string> _functions = [];
        private readonly List<string> _aggregateFunctions = [];
        private readonly Dictionary<string, QueryReference> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, QueryReference> _aggregates = new(StringComparer.Ordinal);
        private readonly List<string> _groupSelect = [];
        private readonly List<string> _aggregateSelect = [];
        private readonly QueryBudget _budget = sharedBudget ?? new(limits);
        private readonly DateTimeOffset _now = statementTime ?? DateTimeOffset.UtcNow;
        private readonly List<SqlQueryColumn> _columns = [];
        private readonly List<SqlBound> _outputTypes = [];
        private Exception? _failure;
        private SqlBound[] _parameters = [];
        private readonly List<SqlQueryDiagnostic> _diagnostics = [];
        internal IReadOnlyList<SqlQueryDiagnostic> Diagnostics => _diagnostics.AsReadOnly();
        private void Warn(string state, string message)
        {
            var index = _diagnostics.FindIndex(item => item.SqlState == state && item.Message == message);
            if (index >= 0) _diagnostics[index] = _diagnostics[index] with { Occurrences = _diagnostics[index].Occurrences + 1 };
            else if (_diagnostics.Count < 100) _diagnostics.Add(new("SQL0445", state, 445, message));
        }
        internal IReadOnlyList<SqlQueryColumn> Columns => _columns;
        internal void RethrowFailure() { if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw(); }
        private T Guard<T>(Func<T> operation)
        {
            try { _budget.Step(token); return operation(); }
            catch (Exception error) when (error is CpfException or OperationCanceledException or ArgumentException or FormatException or OverflowException)
            {
                _failure ??= error is CpfException or OperationCanceledException ? error : new CpfException("SQL0802", "Invalid or out-of-range query expression value.");
                throw;
            }
        }
        internal string Compile()
        {
            var check = new Stack<(SqlExpression Value, int Depth)>();
            foreach (var expression in query.Columns.Select(p => p.Value).Concat(query.GroupBy).Concat(query.OrderBy.Select(o => o.Value)).Concat(query.Where is null ? [] : new[] { query.Where }).Concat(query.Having is null ? [] : new[] { query.Having })) check.Push((expression, 0));
            while (check.TryPop(out var node)) { if (node.Depth > 128) throw Invalid("Query expression exceeds 128 levels."); foreach (var child in Children(node.Value)) check.Push((child, node.Depth + 1)); }
            if (parameters.Count != query.ParameterCount) throw new CpfException("SQL0313", "Query parameter count does not match its markers.");
            long parameterBytes = 0;
            foreach (var binding in parameters)
            {
                parameterBytes += binding.Value switch { string text => Encoding.UTF8.GetByteCount(text), SqlDecimalValue number => Encoding.UTF8.GetByteCount(number.Text), BigInteger integer => integer.GetByteCount(), _ => 32L };
                if (parameterBytes > limits.MaximumBytes) throw new CpfException("SQL0904", "Query parameter byte limit exceeded.");
            }
            _parameters = parameters.Select(Parameter).ToArray();
            var projections = new List<SqlProjection>();
            foreach (var projection in query.Columns)
            {
                if (projection.Value is not SqlStar star) { projections.Add(projection); continue; }
                if (projection.Alias is not null) throw Invalid("A wildcard cannot have an alias.");
                var expanded = tables.Where(table => star.Qualifier is null || table.Qualifier == star.Qualifier).ToArray();
                if (expanded.Length == 0) throw new CpfException("SQL0206", "Unknown wildcard qualifier.");
                foreach (var table in expanded) foreach (var field in table.Definition.PrimaryFormat.Fields)
                {
                    if (projections.Count >= 1000) throw new CpfException("SQL0904", "Query projection limit is 1000 columns.");
                    projections.Add(new(new SqlColumn(field.Name, table.Qualifier)));
                }
            }
            if (projections.Count is < 1 or > 1000) throw new CpfException("SQL0904", "Query projection limit is 1000 columns.");
            var grouped = query.GroupBy.Count != 0 || query.Having is not null || projections.Any(p => HasAggregate(p.Value)) || query.OrderBy.Any(o => HasAggregate(o.Value));
            var from = From(query.From);
            var where = query.Where is null ? "" : " WHERE " + Predicate(Bind(query.Where, false));
            var groupKeys = new List<string>();
            foreach (var expression in query.GroupBy)
            {
                var key = Key(expression); if (_groups.ContainsKey(key)) continue;
                var value = Bind(expression, false); var alias = "g" + _groups.Count;
                _groupSelect.Add(value.Sql + " AS " + Quote(alias)); groupKeys.Add(SortKey(value));
                _groups.Add(key, Reference("__g." + Quote(alias), value.Type));
            }
            var values = new List<QueryExpression>(); var names = new List<string>();
            foreach (var projection in projections)
            {
                var value = Bind(projection.Value, grouped); values.Add(value);
                var name = projection.Alias ?? (projection.Value is SqlColumn column ? column.Name : (names.Count + 1).ToString("D5", CultureInfo.InvariantCulture));
                names.Add(name); _columns.Add(new(name, PublicType(value.Type)) { Calculated = projection.Alias is null && projection.Value is not SqlColumn }); _outputTypes.Add(value.Type);
            }
            var having = query.Having is null ? "" : " WHERE " + Predicate(Bind(query.Having, true));
            var hidden = new List<QueryExpression>(); var order = new List<string>();
            foreach (var term in query.OrderBy)
            {
                var ordinal = -1;
                if (term.Value is SqlConstant { Value.Kind: SqlValueKind.Number, Value.Value: string numeric } && int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                { if (position < 1 || position > values.Count) throw new CpfException("SQL0125", "ORDER BY position is outside the result columns."); ordinal = position - 1; }
                if (ordinal < 0 && term.Value is SqlColumn { Qualifier: null } alias)
                {
                    var matches = names.Select((name, index) => (name, index)).Where(p => p.name == alias.Name).ToArray();
                    if (matches.Length > 1) throw new CpfException("SQL0203", "Ambiguous ORDER BY name.");
                    if (matches.Length == 1) ordinal = matches[0].index;
                }
                if (ordinal < 0)
                    for (var index = 0; index < projections.Count; index++) if (Key(projections[index].Value) == Key(term.Value)) { ordinal = index; break; }
                QueryExpression sort;
                if (ordinal >= 0) sort = new("__r." + Quote("p" + ordinal), values[ordinal].Type);
                else
                {
                    if (query.Distinct) throw new CpfException("SQL0214", "DISTINCT ORDER BY must identify a result expression.");
                    var value = Bind(term.Value, grouped); var index = hidden.Count; hidden.Add(value); sort = new("__r." + Quote("o" + index), value.Type);
                }
                var key = SortKey(sort); var nullFirst = term.NullsFirst ?? term.Descending;
                order.Add("(" + key + " IS NULL) " + (nullFirst ? "DESC" : "ASC")); order.Add(key + (term.Descending ? " DESC" : " ASC"));
            }
            string input;
            if (grouped)
            {
                var columns = _groupSelect.Concat(_aggregateSelect).ToList();
                if (_aggregateSelect.Count == 0 && query.GroupBy.Count == 0) columns.Add("count(*) AS __global_group");
                input = "(SELECT " + string.Join(',', columns) + " FROM " + from + where + (groupKeys.Count == 0 ? "" : " GROUP BY " + string.Join(',', groupKeys)) + ") AS __g";
            }
            else input = from;
            var projected = values.Select((value, index) => value.Sql + " AS " + Quote("p" + index)).Concat(hidden.Select((value, index) => value.Sql + " AS " + Quote("o" + index)));
            var sql = "SELECT " + string.Join(',', values.Select((_, index) => "__r." + Quote("p" + index))) + " FROM (SELECT " + string.Join(',', projected) + " FROM " + input + (grouped ? having : where) + ") AS __r";
            if (query.Distinct) sql += " GROUP BY " + string.Join(',', values.Select((value, index) => SortKey(new("__r." + Quote("p" + index), value.Type))));
            if (order.Count != 0) sql += " ORDER BY " + string.Join(',', order);
            if (query.FetchFirst is { } count)
            {
                var bound = count is SqlParameter parameter ? _parameters[parameter.Index] : new SqlBinder(new() { Name = "QUERY" }, "", options.Ccsid, _now).Bind(count);
                if (bound.Kind != SqlKind.Integer || bound.Evaluate([]) is not SqlExactNumber number || number.Scale != 0 || number.Units < 0 || number.Units > int.MaxValue) throw new CpfException("SQL0802", "FETCH FIRST requires a non-null, nonnegative integer.");
                sql += " LIMIT " + number.Units.ToString(CultureInfo.InvariantCulture);
            }
            if (sql.Length > 4194304) throw new CpfException("SQL0904", "Compiled query exceeds four MiB.");
            return sql;
        }
        private string From(SqlFrom source)
        {
            if (source is SqlTableSource table)
            {
                var bound = tables.Single(candidate => ReferenceEquals(candidate.Source, table));
                return (bound.LogicalSql ?? Quote(MemberTable(bound.Library, bound.Name, bound.Member))) + " AS " + Quote(bound.Alias);
            }
            var join = (SqlJoinSource)source; var left = From(join.Left); var right = From(join.Right);
            var allowed = TableSet(join).ToHashSet();
            return "(" + left + " " + join.Kind + " JOIN " + right + (join.On is null ? "" : " ON " + Predicate(Bind(join.On, false, allowed))) + ")";
        }
        private static IEnumerable<SqlTableSource> TableSet(SqlFrom source) => source is SqlTableSource table ? [table] : TableSet(((SqlJoinSource)source).Left).Concat(TableSet(((SqlJoinSource)source).Right));
        private QueryReference Column(SqlColumn column, HashSet<SqlTableSource>? allowed = null)
        {
            var candidates = tables.Where(table => (allowed is null || allowed.Contains(table.Source)) && (column.Qualifier is null || table.Qualifier == column.Qualifier))
                .Select(table => (Table: table, Field: table.Definition.PrimaryFormat.Find(column.Name))).Where(item => item.Field is not null).ToArray();
            if (candidates.Length == 0) throw new CpfException("SQL0206", "Unknown query column " + column.Name + ".");
            if (candidates.Length > 1) throw new CpfException("SQL0203", "Ambiguous query column " + column.Name + ".");
            var actual = candidates[0]; var field = actual.Field!; var type = SqlBinder.Column(field, 0) with { Nullable = field.NullCapable || actual.Table.Nullable };
            if (actual.Table.Definition.SqlView is { } view)
            {
                var columnType = view.Columns.Single(item => item.Name == field.Name).Type;
                type = ViewColumnType(columnType) with { Nullable = columnType.Nullable || actual.Table.Nullable };
                return Reference(Quote(actual.Table.Alias) + "." + Quote(field.Name), type);
            }
            return new(Quote(actual.Table.Alias) + "." + Quote(field.Name), type, raw => {
                if (raw is null && !type.Nullable) throw new CpfException("SQL0407", "Null stored in a non-null query column.");
                if (raw is not null)
                {
                    if (field.Type is FieldType.Packed or FieldType.Zoned && raw is double) throw new CpfException("SQL0406", "Decimal column contains inexact storage.");
                    _ = RecordSqlValue(field, raw);
                }
                return SqlBinder.Read(field, raw);
            });
        }
        private static SqlBound ViewColumnType(SqlQueryType type)
        {
            var kind = type.Kind switch {
                SqlQueryKind.Null => SqlKind.Null, SqlQueryKind.SmallInt or SqlQueryKind.Integer or SqlQueryKind.BigInt => SqlKind.Integer,
                SqlQueryKind.Decimal or SqlQueryKind.Numeric => SqlKind.Decimal, SqlQueryKind.Real or SqlQueryKind.Double => SqlKind.Float,
                SqlQueryKind.Character or SqlQueryKind.VarChar => SqlKind.Text, SqlQueryKind.Date => SqlKind.Date,
                SqlQueryKind.Time => SqlKind.Time, SqlQueryKind.Timestamp => SqlKind.Timestamp, SqlQueryKind.Boolean => SqlKind.Boolean,
                _ => throw Invalid("Invalid stored view column type.") };
            return new(kind, _ => null, Reads: true, Precision: type.Precision, Scale: type.Scale, Width: type.Length, Ccsid: type.Ccsid,
                Bits: type.Kind == SqlQueryKind.SmallInt ? 16 : type.Kind is SqlQueryKind.BigInt or SqlQueryKind.Double ? 64 : 32,
                Nullable: type.Nullable, Varying: type.Kind == SqlQueryKind.VarChar,
                Storage: type.Kind == SqlQueryKind.Numeric ? FieldType.Zoned : kind == SqlKind.Decimal ? FieldType.Packed : null);
        }
        private QueryExpression Bind(SqlExpression expression, bool postGroup, HashSet<SqlTableSource>? allowed = null)
        {
            var references = new List<QueryReference>();
            SqlBound Use(QueryReference reference)
            {
                var index = references.FindIndex(item => item.Sql == reference.Sql);
                if (index < 0) { index = references.Count; references.Add(reference); }
                return reference.Type with { Evaluate = row => reference.Decode(row[index]) };
            }
            SqlBound Resolve(SqlColumn column)
            {
                if (postGroup) throw new CpfException("SQL0122", "Column " + column.Name + " is not a grouping expression.");
                return Use(Column(column, allowed));
            }
            SqlBound? Extend(SqlExpression value)
            {
                if (value is SqlParameter parameter) return _parameters[parameter.Index];
                if (value is SqlFunction call && IsAggregate(call.Name))
                {
                    if (!postGroup) throw new CpfException("SQL0120", "Aggregate is not valid in this query clause.");
                    return Use(Aggregate(call));
                }
                return postGroup && _groups.TryGetValue(Key(value), out var group) ? Use(group) : null;
            }
            var binder = new SqlBinder(new() { Name = "QUERY" }, "", options.Ccsid, _now, Resolve, Extend, Warn, options.Conversion);
            var result = binder.Bind(expression);
            if (references.Count > 100) throw new CpfException("SQL0904", "One query expression exceeds 100 input references.");
            var function = NewFunction(); connection.CreateFunction<object?>(function, args => Guard(() => Native(result.Evaluate(args))));
            return new(function + "(" + string.Join(',', references.Select(reference => reference.Sql)) + ")", result);
        }
        private string Predicate(QueryExpression expression) { SqlBinder.Predicate(expression.Type); return expression.Sql; }
        private string SortKey(QueryExpression expression)
        {
            if (expression.Type.Kind == SqlKind.Duration) throw Invalid("A duration cannot be a grouping/order value.");
            var function = NewFunction(); connection.CreateFunction<object?>(function, args => Guard(() => TypedKey(expression.Type, Stored(expression.Type, args[0]))));
            return function + "(" + expression.Sql + ")";
        }
        private string NewFunction()
        {
            if (_functions.Count + _aggregateFunctions.Count >= 4096) throw new CpfException("SQL0904", "Query function budget exceeded.");
            var name = _prefix + _functions.Count; _functions.Add(name); return name;
        }
        private QueryReference Aggregate(SqlFunction call)
        {
            var key = Key(call); if (_aggregates.TryGetValue(key, out var existing)) return existing;
            if (call.Arguments.Count != 1) throw Invalid("Aggregate requires one argument.");
            var star = call.Arguments[0] is SqlStar;
            if (star && (call.Name is not ("COUNT" or "COUNT_BIG") || call.Distinct || call.All || call.Arguments[0] is SqlStar { Qualifier: not null })) throw Invalid("Invalid aggregate wildcard.");
            var argument = star ? null : Bind(call.Arguments[0], false);
            var type = AggregateType(call.Name, argument?.Type);
            var name = _prefix + "a" + _aggregateFunctions.Count; _aggregateFunctions.Add(name);
            connection.CreateAggregate<QueryAggregateState?, object?>(name, null, (state, args) => Guard(() => {
                state ??= new QueryAggregateState(call.Name, call.Distinct, argument?.Type, type, _budget);
                state.Add(star ? new SqlExactNumber(1, 0) : Stored(argument!.Type, args[0])); return state;
            }), state => Guard(() => Native(state is null ? call.Name is "COUNT" or "COUNT_BIG" ? new SqlExactNumber(0, 0) : null : state.Finish())));
            var alias = "a" + _aggregates.Count;
            _aggregateSelect.Add(name + "(" + (argument?.Sql ?? "") + ") AS " + Quote(alias));
            var reference = Reference("__g." + Quote(alias), type); _aggregates.Add(key, reference); return reference;
        }
        private static bool IsAggregate(string name) => name is "COUNT" or "COUNT_BIG" or "SUM" or "AVG" or "MIN" or "MAX";
        private static bool HasAggregate(SqlExpression expression) => expression is SqlFunction function && IsAggregate(function.Name) || Children(expression).Any(HasAggregate);
        private string Key(SqlExpression expression) => expression switch {
            SqlColumn column => "C:" + Column(column).Sql,
            SqlConstant value => "K:" + value.Value.Kind + ":" + System.Text.Json.JsonSerializer.Serialize(value.Value.Value),
            SqlParameter parameter => "P:" + parameter.Index, SqlStar star => "*:" + star.Qualifier,
            SqlUnary unary => "U:" + unary.Operator + "(" + Key(unary.Value) + ")",
            SqlBinary binary => "B:" + binary.Operator + "(" + Key(binary.Left) + "," + Key(binary.Right) + ")",
            SqlFunction function => "F:" + function.Name + ":" + function.Distinct + ":" + function.All + "(" + string.Join(',', function.Arguments.Select(Key)) + ")",
            SqlCast cast => "T:" + cast.Type + "(" + Key(cast.Value) + ")", SqlExtract extract => "E:" + extract.Unit + "(" + Key(extract.Value) + ")",
            SqlDuration duration => "D:" + duration.Unit + "(" + Key(duration.Value) + ")",
            SqlIsNull test => "N:" + test.Negated + "(" + Key(test.Value) + ")",
            SqlIn list => "I:" + list.Negated + "(" + Key(list.Value) + "," + string.Join(',', list.Values.Select(Key)) + ")",
            SqlCoalesce list => "CO(" + string.Join(',', list.Values.Select(Key)) + ")",
            SqlLike like => "LIKE:" + like.Negated + "(" + Key(like.Value) + "," + Key(like.Pattern) + "," + (like.Escape is null ? "" : Key(like.Escape)) + ")",
            _ => throw Invalid("Unsupported query expression.") };
        private static IEnumerable<SqlExpression> Children(SqlExpression expression) => expression switch {
            SqlUnary unary => [unary.Value], SqlBinary binary => [binary.Left, binary.Right], SqlFunction function => function.Arguments,
            SqlCast cast => [cast.Value], SqlExtract extract => [extract.Value], SqlDuration duration => [duration.Value], SqlIsNull test => [test.Value],
            SqlIn list => new[] { list.Value }.Concat(list.Values), SqlCoalesce list => list.Values,
            SqlLike like => like.Escape is null ? [like.Value, like.Pattern] : [like.Value, like.Pattern, like.Escape], _ => [] };
        private static QueryReference Reference(string sql, SqlBound type) => new(sql, type, raw => Stored(type, raw));
        internal IReadOnlyList<object?> ReadRow(SqliteDataReader reader)
        {
            var values = new object?[_outputTypes.Count];
            for (var index = 0; index < values.Length; index++)
            {
                if (reader.IsDBNull(index)) continue;
                var type = _outputTypes[index]; var raw = Stored(type, reader.GetValue(index));
                values[index] = type.Kind switch {
                    SqlKind.Integer => (long)((SqlExactNumber)raw!).CheckedInteger(type.Bits == 0 ? 32 : type.Bits).Units,
                    SqlKind.Decimal => new SqlDecimalValue(((SqlExactNumber)raw!).Rescale(type.Scale).ToString(), type.Precision, type.Scale),
                    _ => raw };
            }
            return Array.AsReadOnly(values);
        }
        private static object? Native(object? value) => value is SqlExactNumber exact ? exact.ToString() : value;
        private static object? Stored(SqlBound type, object? raw) => raw is null || raw is DBNull ? null : type.Kind switch {
            SqlKind.Integer or SqlKind.Decimal or SqlKind.Duration => SqlExactNumber.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!),
            SqlKind.Float => Convert.ToDouble(raw, CultureInfo.InvariantCulture), SqlKind.Boolean => Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0,
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture) };
        private static object? TypedKey(SqlBound type, object? raw)
        {
            if (raw is null) return null;
            if (raw is SqlExactNumber exact)
            {
                var scaled = exact.Rescale(63); var digits = BigInteger.Abs(scaled.Units).ToString(CultureInfo.InvariantCulture).PadLeft(126, '0');
                if (digits.Length > 126) throw SqlExactNumber.Error("Query numeric key exceeds 63 integral digits.");
                if (scaled.Units.Sign < 0) digits = new string(digits.Select(c => (char)('9' - (c - '0'))).ToArray());
                return Encoding.ASCII.GetBytes((scaled.Units.Sign < 0 ? "0" : "1") + digits);
            }
            if (raw is double zero && zero == 0) raw = 0d;
            return type.Kind == SqlKind.Text ? DatabaseSortKeys.Text((string)raw, type.Ccsid, Math.Max(1, type.Width)) : raw;
        }
        internal static SqlQueryType PublicType(SqlBound type) => new(type.Kind switch {
            SqlKind.Null => SqlQueryKind.Null, SqlKind.Integer => type.Bits <= 16 ? SqlQueryKind.SmallInt : type.Bits <= 32 ? SqlQueryKind.Integer : SqlQueryKind.BigInt,
            SqlKind.Decimal => type.Storage == FieldType.Zoned ? SqlQueryKind.Numeric : SqlQueryKind.Decimal, SqlKind.Float => type.Bits == 32 ? SqlQueryKind.Real : SqlQueryKind.Double,
            SqlKind.Text => type.Varying ? SqlQueryKind.VarChar : SqlQueryKind.Character, SqlKind.Date => SqlQueryKind.Date,
            SqlKind.Time => SqlQueryKind.Time, SqlKind.Timestamp => SqlQueryKind.Timestamp, SqlKind.Boolean => SqlQueryKind.Boolean,
            _ => throw Invalid("A labeled duration cannot be a result column.") },
            type.Kind == SqlKind.Integer ? type.Bits <= 16 ? 5 : type.Bits <= 32 ? 10 : 19 : type.Kind == SqlKind.Float ? type.Bits == 32 ? 7 : 15 : type.Precision,
            type.Scale, type.Width, type.Ccsid, type.Nullable);
        private static CpfException Invalid(string text) => new("SQL0104", text);
        public void Dispose()
        {
            foreach (var name in _functions) connection.CreateFunction<object?>(name, (Func<object?[], object?>?)null);
            foreach (var name in _aggregateFunctions) connection.CreateAggregate<QueryAggregateState?, object?>(name, null, (Func<QueryAggregateState?, object?[], QueryAggregateState?>?)null, (Func<QueryAggregateState?, object?>?)null);
        }
    }
}
