namespace Ipc.Db.Sql;

public sealed partial class SqlStatementParser
{
    private int _expressionDepth;
    private int _expressionNodes;
    private SqlUpdate Update()
    {
        var table = ObjectNameValue(); var alias = Correlation("SET"); Need("SET"); var assignments = new List<SqlAssignment>();
        do
        {
            var column = Identifier(); Need("=");
            if (assignments.Any(item => item.Column == column)) throw Invalid("Repeated UPDATE column.");
            assignments.Add(new(column, Expression()));
        } while (Take(","));
        return new(table, assignments, Take("WHERE") ? Expression() : null, alias);
    }
    private string? Correlation(string next)
    {
        if (Take("AS")) return Identifier();
        return Current.Kind is 'W' or '"' && !Is(next) ? Identifier() : null;
    }
    private SqlExpression Expression(int minimum = 0)
    {
        if (++_expressionDepth > 128 || ++_expressionNodes > 8192) throw Invalid("SQL expression exceeds the supported nesting/node limit.");
        try
        {
            SqlExpression left;
            if (Take("?")) left = new SqlParameter(_parameterOrdinal++);
            else if (Take("*")) left = new SqlStar();
            else if (Take("(")) { left = Expression(); Need(")"); }
            else if (Take("NOT")) left = new SqlUnary("NOT", Expression(25));
            else if (Take("+")) left = new SqlUnary("+", Expression(70));
            else if (Take("-")) left = new SqlUnary("-", Expression(70));
            else if (Current.Kind == 'W' && _tokens[_position + 1].Text == "(" && !Is("COALESCE")) left = FunctionExpression();
            else if (Take("COALESCE"))
            {
                Need("("); var values = new List<SqlExpression> { Expression() }; Need(",");
                do { values.Add(Expression()); } while (Take(",")); Need(")"); left = new SqlCoalesce(values);
            }
            else if ((Is("DATE") || Is("TIME") || Is("TIMESTAMP")) && _tokens[_position + 1].Kind == '\'')
            { var type = Current.Text; var value = Value(true); left = new SqlCast(new SqlConstant(value), new(type switch { "DATE" => Definitions.FieldType.Date, "TIME" => Definitions.FieldType.Time, _ => Definitions.FieldType.Timestamp }, type switch { "DATE" => 10, "TIME" => 8, _ => 26 })); }
            else if (Current.Kind is 'N' or '\'' || Is("NULL") || Is("DEFAULT") || Is("CURRENT") ||
                Is("CURRENT_DATE") || Is("CURRENT_TIME") || Is("CURRENT_TIMESTAMP") || Is("DATE") || Is("TIME") || Is("TIMESTAMP")) left = new SqlConstant(Value(true));
            else { var first = Identifier(); left = Take(".") ? Take("*") ? new SqlStar(first) : new SqlColumn(Identifier(), first) : new SqlColumn(first); }
            while (true)
            {
                if (minimum <= 30 && Take("IS")) { var negated = Take("NOT"); Need("NULL"); left = new SqlIsNull(left, negated); continue; }
                if (minimum <= 30 && (Is("LIKE") || Is("IN") || Is("BETWEEN") || Is("NOT")))
                {
                    var negated = Take("NOT");
                    if (Take("LIKE")) { var pattern = Expression(31); var escape = Take("ESCAPE") ? Expression(31) : null; left = new SqlLike(left, pattern, escape, negated); continue; }
                    if (Take("IN"))
                    {
                        Need("("); var values = new List<SqlExpression> { Expression(31) }; while (Take(",")) values.Add(Expression(31)); Need(")");
                        left = new SqlIn(left, values, negated); continue;
                    }
                    Need("BETWEEN"); var lower = Expression(31); Need("AND"); var upper = Expression(31);
                    var between = new SqlBinary("AND", new SqlBinary(">=", left, lower), new SqlBinary("<=", left, upper));
                    left = negated ? new SqlUnary("NOT", between) : between; continue;
                }
                if (Current.Kind == 'W' && Current.Text is "YEAR" or "YEARS" or "MONTH" or "MONTHS" or "DAY" or "DAYS" or "HOUR" or "HOURS" or "MINUTE" or "MINUTES" or "SECOND" or "SECONDS")
                { var unit = Current.Text.TrimEnd('S'); _position++; left = new SqlDuration(left, unit); continue; }
                var operation = Current.Text;
                var precedence = operation switch {
                    "OR" => 10, "AND" => 20, "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=" => 30,
                    "||" => 40, "+" or "-" => 50, "*" or "/" => 60, _ => -1 };
                if (precedence < minimum || Current.Kind is not ('W' or 'P')) break;
                _position++; left = new SqlBinary(operation, left, Expression(precedence + 1));
            }
            return left;
        }
        finally { _expressionDepth--; }
    }
}
