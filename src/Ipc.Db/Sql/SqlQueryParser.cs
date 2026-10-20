namespace Ipc.Db.Sql;

public sealed partial class SqlStatementParser
{
    private int _parameterOrdinal;
    private int _queryTableCount;
    private static readonly HashSet<string> QueryBoundaries = new(StringComparer.Ordinal) {
        "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "FETCH", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "JOIN", "ON", "USING", "UNION", "EXCEPT", "INTERSECT" };
    private SqlSelect Select()
    {
        var distinct = Take("DISTINCT"); if (!distinct) Take("ALL");
        var projections = new List<SqlProjection>();
        do
        {
            var value = Expression();
            var alias = Take("AS") ? Identifier() : QueryAlias();
            projections.Add(new(value, alias));
            if (projections.Count > 1000) throw Invalid("SELECT exceeds 1000 projections.");
        } while (Take(","));
        Need("FROM"); var from = QueryFrom();
        var where = Take("WHERE") ? Expression() : null;
        var group = new List<SqlExpression>();
        if (Take("GROUP")) { Need("BY"); do { group.Add(Expression()); if (group.Count > 128) throw new Ipc.Core.Messages.CpfException("SQL0904", "GROUP BY exceeds 128 terms."); } while (Take(",")); }
        var having = Take("HAVING") ? Expression() : null;
        var order = new List<SqlOrdering>();
        if (Take("ORDER"))
        {
            Need("BY");
            do
            {
                var value = Expression(); var descending = Take("DESC"); if (!descending) Take("ASC"); bool? first = null;
                if (Take("NULLS")) { if (Take("FIRST")) first = true; else { Need("LAST"); first = false; } }
                order.Add(new(value, descending, first));
                if (order.Count > 128) throw Invalid("ORDER BY exceeds 128 terms.");
            } while (Take(","));
        }
        SqlExpression? fetch = null;
        if (Take("FETCH"))
        {
            Need("FIRST");
            fetch = Take("?") ? new SqlParameter(_parameterOrdinal++) : new SqlConstant(new(SqlValueKind.Number, (Is("ROW") || Is("ROWS") ? 1 : Integer(0, int.MaxValue)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (!Take("ROW")) Need("ROWS"); Need("ONLY");
        }
        return new(projections, from, where, group, having, order, fetch, distinct, _parameterOrdinal);
    }
    private string? QueryAlias() => _viewQuery && (Is("RCDFMT") || Is("WITH")) ? null :
        Current.Kind == '"' || Current.Kind == 'W' && !QueryBoundaries.Contains(Current.Text) ? Identifier() : null;
    private SqlFrom QueryFrom(int depth = 0)
    {
        if (depth > 32) throw Invalid("FROM exceeds 32 nesting levels.");
        var left = Joined();
        while (Take(",")) left = new SqlJoinSource(left, Joined(), "CROSS", null);
        return left;

        SqlFrom Factor()
        {
            if (Take("(")) { var grouped = QueryFrom(depth + 1); Need(")"); return grouped; }
            if (++_queryTableCount > 32) throw new Ipc.Core.Messages.CpfException("SQL0904", "Query exceeds 32 table references.");
            var table = ObjectNameValue(); var alias = Take("AS") ? Identifier() : QueryAlias(); return new SqlTableSource(table, alias);
        }
        SqlFrom Joined()
        {
            var value = Factor();
            while (Is("JOIN") || Is("INNER") || Is("LEFT") || Is("RIGHT") || Is("FULL") || Is("CROSS"))
            {
                var kind = "INNER";
                if (!Is("JOIN")) { kind = Current.Text; _position++; if (kind is "LEFT" or "RIGHT" or "FULL") Take("OUTER"); }
                Need("JOIN"); var right = Factor(); SqlExpression? on = null;
                if (kind != "CROSS") { Need("ON"); on = Expression(); }
                value = new SqlJoinSource(value, right, kind, on);
            }
            return value;
        }
    }
    private SqlExpression FunctionExpression()
    {
        var name = Current.Text; _position++; Need("(");
        if (name == "CAST")
        {
            var value = Expression(); Need("AS"); var (type, length, scale, varying) = DataType(true); Need(")");
            return new SqlCast(value, new(type, length, scale, varying, type == Definitions.FieldType.Alpha ? _options.Ccsid : 0));
        }
        if (name == "EXTRACT")
        { var unit = Current.Text; _position++; Need("FROM"); var value = Expression(); Need(")"); return new SqlExtract(unit, value); }
        if (name == "SUBSTRING")
        {
            var value = Expression();
            if (Take("FROM")) { var start = Expression(); var length = Take("FOR") ? Expression() : null; Need(")"); return new SqlFunction(name, length is null ? [value, start] : [value, start, length]); }
            Need(","); var position = Expression(); var count = Take(",") ? Expression() : null; Need(")"); return new SqlFunction(name, count is null ? [value, position] : [value, position, count]);
        }
        if (name == "TRIM")
        {
            var direction = "BOTH"; var explicitDirection = false;
            if (Is("BOTH") || Is("LEADING") || Is("TRAILING")) { direction = Current.Text; _position++; explicitDirection = true; }
            SqlExpression value; SqlExpression? strip = null;
            if (Take("FROM")) value = Expression();
            else { var first = Expression(); if (Take("FROM")) { strip = first; value = Expression(); } else { if (explicitDirection) throw Invalid("TRIM direction requires FROM."); value = first; } }
            Need(")"); return new SqlFunction("TRIM_" + direction, strip is null ? [value] : [value, strip]);
        }
        var distinct = Take("DISTINCT"); var all = !distinct && Take("ALL");
        if (all && name is not ("COUNT" or "COUNT_BIG" or "SUM" or "AVG" or "MIN" or "MAX")) throw Invalid("ALL is only valid for an aggregate argument.");
        var arguments = new List<SqlExpression>();
        if (!Is(")")) { do { arguments.Add(Expression()); } while (Take(",")); }
        Need(")"); return new SqlFunction(name, arguments, distinct, all);
    }
}
