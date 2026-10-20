namespace Ipc.Db.Sql;

public sealed partial class SqlStatementParser
{
    internal static SqlExpression ParseCheckCondition(string text, int ccsid)
    {
        var parser = new SqlStatementParser(text, new(SqlNaming.System, "QGPL", "QGPL", [], ccsid));
        var expression = parser.Expression();
        if (parser.Current.Kind != 'E') throw parser.Invalid("Unexpected CHECK condition token.");
        return expression;
    }
    private Ipc.Db.Definitions.SqlCheckConstraint CheckDefinition(string? name, string? column = null, string? qualifier = null)
    {
        Need("CHECK"); Need("("); var start = Current.Offset; _ = Expression(); var end = Current.Offset; Need(")");
        return new(name, _source[start..end].Trim(), _options.Ccsid, _options.Conversion, column, qualifier);
    }
}
