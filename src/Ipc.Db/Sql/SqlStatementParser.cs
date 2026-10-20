using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

/// <summary>Parse SQL before storage is touched. No input SQL is passed to SQLite.</summary>
public sealed partial class SqlStatementParser
{
    private sealed record Token(string Text, int Offset, char Kind = 'W');
    private readonly List<Token> _tokens = [];
    private readonly SqlExecutionOptions _options;
    private int _position;
    private readonly string _source;
    private bool _viewQuery;
    private SqlStatementParser(string source, SqlExecutionOptions options)
    {
        _options = options;
        _source = source;
        if (source.Length > 2097152) throw Error("Statement exceeds two MiB.", 0);
        for (var offset = 0; offset < source.Length;)
        {
            var start = offset; var c = source[offset++];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '-' && offset < source.Length && source[offset] == '-')
            { while (offset < source.Length && source[offset] is not ('\r' or '\n')) offset++; continue; }
            if (c == '/' && offset < source.Length && source[offset] == '*')
            {
                offset++; var depth = 1;
                while (offset < source.Length && depth != 0)
                {
                    if (offset + 1 < source.Length && source[offset] == '/' && source[offset + 1] == '*') { depth++; offset += 2; }
                    else if (offset + 1 < source.Length && source[offset] == '*' && source[offset + 1] == '/') { depth--; offset += 2; }
                    else offset++;
                }
                if (depth != 0) throw Error("Unclosed SQL comment.", start);
                continue;
            }
            if (c is '\'' or '"')
            {
                var value = new System.Text.StringBuilder(); var closed = false;
                while (offset < source.Length)
                {
                    var item = source[offset++];
                    if (item != c) { value.Append(item); continue; }
                    if (offset < source.Length && source[offset] == c) { value.Append(c); offset++; continue; }
                    closed = true; break;
                }
                if (!closed) throw Error("Unclosed quoted SQL value.", start);
                _tokens.Add(new(value.ToString(), start, c)); continue;
            }
            if (char.IsAsciiLetter(c) || c is '_' or '#' or '$' or '@')
            {
                while (offset < source.Length && (char.IsAsciiLetterOrDigit(source[offset]) || source[offset] is '_' or '#' or '$' or '@')) offset++;
                _tokens.Add(new(source[start..offset].ToUpperInvariant(), start)); continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' || c == ',' && options.Conversion?.DecimalComma == true) && offset < source.Length && char.IsAsciiDigit(source[offset]))
            {
                while (offset < source.Length && char.IsAsciiDigit(source[offset])) offset++;
                if (c is not ('.' or ',') && offset < source.Length && (source[offset] == '.' || source[offset] == ',' && options.Conversion?.DecimalComma == true && (offset + 1 == source.Length || !char.IsWhiteSpace(source[offset + 1]))))
                { offset++; while (offset < source.Length && char.IsAsciiDigit(source[offset])) offset++; }
                if (offset < source.Length && source[offset] is 'e' or 'E')
                {
                    offset++; if (offset < source.Length && source[offset] is '+' or '-') offset++;
                    var digits = offset; while (offset < source.Length && char.IsAsciiDigit(source[offset])) offset++;
                    if (digits == offset) throw Error("Invalid exponent.", start);
                }
                _tokens.Add(new(source[start..offset].Replace(',', '.'), start, 'N')); continue;
            }
            if (c is '<' or '>' or '!' or '|')
            {
                if (offset < source.Length && (source[offset] == '=' && c is '<' or '>' or '!' || source[offset] == '>' && c == '<' || source[offset] == '|' && c == '|')) offset++;
                var operation = source[start..offset];
                if (operation is "!" or "|") throw Error("Invalid SQL operator.", start);
                _tokens.Add(new(operation, start, 'P')); continue;
            }
            if (c is '(' or ')' or ',' or '.' or '/' or ';' or '+' or '-' or '*' or '=' or '?') _tokens.Add(new(c.ToString(), start, 'P'));
            else throw Error("Unsupported SQL character.", start);
        }
        _tokens.Add(new("", source.Length, 'E'));
    }
    public static SqlStatement Parse(string source, SqlExecutionOptions options)
    {
        var parser = new SqlStatementParser(source, options);
        var statement = parser.Statement(); parser.Take(";");
        if (parser.Current.Kind != 'E') throw parser.Invalid("Only one statement is allowed; unexpected " + parser.Current.Text + ".");
        return statement;
    }
    private Token Current => _tokens[_position];
    private bool Is(string text) => Current.Text == text && Current.Kind is 'W' or 'P';
    private bool Take(string text) { if (!Is(text)) return false; _position++; return true; }
    private void Need(string text) { if (!Take(text)) throw Invalid("Expected " + text + "."); }
    private static CpfException Error(string message, int offset) => new("SQL0104", message + " At SQL character " + (offset + 1) + ".");
    private CpfException Invalid(string message) => Error(message, Current.Offset);
    private string Identifier()
    {
        var token = Current;
        if (token.Kind is not ('W' or '"') || !ObjectName.IsValid(token.Text) || token.Text != token.Text.ToUpperInvariant())
            throw Invalid("This SQL object/column name requires a system-name mapping not yet implemented.");
        _position++; return token.Text;
    }
    private SqlObjectName ObjectNameValue()
    {
        var first = Identifier(); var separator = _options.Naming == SqlNaming.System ? "/" : ".";
        return Take(separator) ? new(first, Identifier()) : new(null, first);
    }
    private SqlStatement Statement()
    {
        if (Take("CREATE"))
        {
            if (Take("OR")) { Need("REPLACE"); Need("VIEW"); return CreateView(true); }
            if (Take("VIEW")) return CreateView(false);
            if (Take("TABLE")) return Create();
            var unique = Take("UNIQUE"); var excludeNulls = false;
            if (unique && Take("WHERE")) { Need("NOT"); Need("NULL"); excludeNulls = true; }
            Need("INDEX"); return CreateIndex(unique, excludeNulls);
        }
        if (Take("DROP"))
        {
            if (Take("TABLE")) return new SqlDropTable(ObjectNameValue());
            if (Take("VIEW")) return new SqlDropView(ObjectNameValue());
            Need("INDEX"); return new SqlDropIndex(ObjectNameValue());
        }
        if (Take("ALTER")) { Need("TABLE"); return AlterTable(); }
        if (Take("INSERT")) { Need("INTO"); return Insert(); }
        if (Take("SELECT")) return Select();
        if (Take("UPDATE")) return Update();
        if (Take("DELETE")) { Need("FROM"); var table = ObjectNameValue(); var alias = Correlation("WHERE"); return new SqlDelete(table, Take("WHERE") ? Expression() : null, alias); }
        throw Invalid("Unsupported SQL statement.");
    }
    private SqlCreateView CreateView(bool replace)
    {
        var name = ObjectNameValue(); IReadOnlyList<string>? columns = null;
        if (Take("(")) { columns = Identifiers(); Need(")"); }
        Need("AS"); var start = Current.Offset; Need("SELECT"); _viewQuery = true;
        var query = Select(); _viewQuery = false; var text = _source[start..Current.Offset].Trim();
        if (query.ParameterCount != 0 || query.OrderBy.Count != 0 || query.FetchFirst is not null)
            throw Invalid("A view definition cannot have parameter markers or outer ORDER BY/FETCH clauses.");
        var check = SqlViewCheckOption.None;
        if (Take("WITH"))
        {
            check = Take("LOCAL") ? SqlViewCheckOption.Local : SqlViewCheckOption.Cascaded;
            if (check == SqlViewCheckOption.Cascaded) Take("CASCADED");
            Need("CHECK"); Need("OPTION");
        }
        var format = Take("RCDFMT") ? Identifier() : null;
        return new(name, query, text, columns, format, replace, check);
    }
    private SqlCreateIndex CreateIndex(bool unique, bool excludeNulls)
    {
        var name = ObjectNameValue(); Need("ON"); var source = ObjectNameValue(); Need("(");
        var keys = new List<SqlIndexKey>();
        do
        {
            if (keys.Count >= 120) throw Invalid("An index supports at most 120 keys.");
            var column = Identifier();
            if (keys.Any(key => key.Column == column)) throw new CpfException("SQL0612", "Repeated index key requires the remaining duplicate-key metadata implementation.");
            var descending = Take("DESC"); if (!descending) Take("ASC"); keys.Add(new(column, descending));
        } while (Take(","));
        Need(")"); string? format = null; IReadOnlyList<string>? columns = null;
        if (Take("RCDFMT"))
        {
            format = Identifier();
            if (Take("ADD"))
            {
                if (Take("ALL")) Need("COLUMNS");
                else if (Take("KEYS")) { Need("ONLY"); columns = keys.Select(key => key.Column).ToArray(); }
                else
                {
                    var added = Identifiers();
                    if (added.Distinct(StringComparer.Ordinal).Count() != added.Count || added.Any(column => keys.Any(key => key.Column == column)))
                        throw new CpfException("SQL0612", "Repeated index format column.");
                    columns = keys.Select(key => key.Column).Concat(added).ToArray();
                }
            }
        }
        return new(name, source, keys, unique, excludeNulls, format, columns);
    }
    private SqlCreateTable Create()
    {
        var table = ObjectNameValue(); Need("(");
        var fields = new List<FieldSpec>(); var constraints = new List<SqlUniqueConstraint>(); var checks = new List<SqlCheckConstraint>(); var foreign = new List<SqlForeignConstraint>();
        do
        {
            if (fields.Count >= 8000) throw Invalid("Too many table columns.");
            string? constraintName = Take("CONSTRAINT") ? Identifier() : null;
            if (Is("PRIMARY") || Is("UNIQUE"))
            {
                var primary = Take("PRIMARY"); if (primary) Need("KEY"); else Need("UNIQUE");
                Need("("); var columns = Identifiers(); Need(")"); constraints.Add(new(constraintName, primary, columns)); continue;
            }
            if (Is("CHECK")) { checks.Add(CheckDefinition(constraintName, qualifier: table.Name)); continue; }
            if (Is("FOREIGN")) { foreign.Add(ForeignDefinition(constraintName)); continue; }
            if (constraintName is not null) throw Invalid("Unsupported table constraint.");
            var field = ColumnDefinition(constraints, false, checks, foreign, table.Name);
            if (fields.Any(existing => existing.Name == field.Name)) throw new CpfException("SQL0612", "Repeated column " + field.Name + ".");
            fields.Add(field);
        } while (Take(","));
        Need(")");
        if (fields.Count == 0) throw Invalid("A table requires columns.");
        var formatName = Take("RCDFMT") ? Identifier() : table.Name;
        if (constraints.Count(constraint => constraint.Primary) > 1 || constraints.Select(constraint => constraint.Name).Concat(checks.Select(check => check.Name)).Concat(foreign.Select(item => item.Name)).Where(name => name is not null).GroupBy(name => name).Any(group => group.Count() > 1))
            throw Invalid("Repeated primary key or constraint name.");
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in constraints)
        {
            if (constraint.Columns.Count > 120) throw Invalid("A constraint supports at most 120 columns.");
            if (constraint.Columns.Distinct(StringComparer.Ordinal).Count() != constraint.Columns.Count || !signatures.Add(string.Join(',', constraint.Columns.Order()))) throw Invalid("Repeated constraint columns.");
            foreach (var column in constraint.Columns)
            {
                if (fields.All(field => field.Name != column)) throw new CpfException("SQL0206", "Unknown constraint column " + column + ".");
            }
        }
        var format = new RecordFormat { Name = formatName, Fields = fields }; format.AssignPositions();
        if (format.RecordLength > 32766) throw Invalid("Table record exceeds the supported external record length.");
        return new(table, new() { Name = table.Name, Attribute = FileAttribute.Physical, Formats = [format], Ccsid = _options.Ccsid,
            MaximumMembers = 1, MemberSize = new(Unlimited: true), ReuseDeletedRecords = true, SqlTable = new(constraints, checks.Count == 0 ? null : checks, foreign.Count == 0 ? null : foreign) });
    }
    private bool ColumnDefinitionEnd(bool altering) => Is(",") || Is(")") || altering &&
        (Current.Kind == 'E' || Is(";") || Is("ADD") || Is("ALTER") || Is("DROP") || Is("BEFORE"));
    private FieldSpec ColumnDefinition(List<SqlUniqueConstraint> constraints, bool altering, List<SqlCheckConstraint> checks, List<SqlForeignConstraint> foreign, string? qualifier = null)
    {
        var name = Identifier();
        var (type, length, scale, varying) = DataType();
        var nullable = true; var nullSpecified = false; SqlValue? defaultValue = null; var ccsid = _options.Ccsid; var ccsidSpecified = false;
        var inlineConstraints = new List<SqlUniqueConstraint>();
        while (!ColumnDefinitionEnd(altering))
        {
            if (Take("NOT")) { Need("NULL"); if (nullSpecified) throw Invalid("Repeated nullability."); nullable = false; nullSpecified = true; }
            else if (Take("NULL")) { if (nullSpecified) throw Invalid("Repeated nullability."); nullable = true; nullSpecified = true; }
            else if (Take("DEFAULT"))
            {
                if (defaultValue is not null) throw Invalid("Repeated default.");
                defaultValue = ColumnDefinitionEnd(altering) || Is("NOT") || Is("PRIMARY") || Is("UNIQUE") || Is("CHECK") || Is("CONSTRAINT") || Is("REFERENCES") ? new(SqlValueKind.Default) : Value(false);
            }
            else if (Take("WITH")) { Need("DEFAULT"); if (defaultValue is not null) throw Invalid("Repeated default."); defaultValue = ColumnDefinitionEnd(altering) || Is("NOT") || Is("PRIMARY") || Is("UNIQUE") || Is("CHECK") || Is("CONSTRAINT") || Is("REFERENCES") ? new(SqlValueKind.Default) : Value(false); }
            else if (Take("CCSID")) { if (type != FieldType.Alpha || ccsidSpecified) throw Invalid("CCSID requires character data and cannot be repeated."); ccsidSpecified = true; ccsid = Integer(1, 65535); if (!CodePage.IsSupported(ccsid)) throw Invalid("Unsupported CCSID."); }
            else
            {
                var inlineName = Take("CONSTRAINT") ? Identifier() : null;
                if (Is("CHECK")) { checks.Add(CheckDefinition(inlineName, name, qualifier)); continue; }
                if (Is("REFERENCES")) { foreign.Add(ForeignDefinition(inlineName, name)); continue; }
                var primary = Take("PRIMARY");
                if (primary) Need("KEY"); else Need("UNIQUE");
                inlineConstraints.Add(new(inlineName, primary, new[] { name }));
            }
        }
        if (defaultValue is { Kind: SqlValueKind.Default } || altering && defaultValue is null) defaultValue = nullable ? new(SqlValueKind.Literal) : TypeDefault(type);
        if (defaultValue is { Kind: SqlValueKind.CurrentDate } && type != FieldType.Date ||
            defaultValue is { Kind: SqlValueKind.CurrentTime } && type != FieldType.Time ||
            defaultValue is { Kind: SqlValueKind.CurrentTimestamp } && type != FieldType.Timestamp) throw Invalid("Current datetime default does not match column type.");
        if (defaultValue is { Kind: SqlValueKind.Literal, Value: string literal }) defaultValue = defaultValue with { Value = SqlInputConversion.Normalize(type, literal, _options.Conversion) };
        var field = new FieldSpec { Name = name, Type = type, Length = length, Decimals = scale, VariableLength = varying, NullCapable = nullable, Ccsid = ccsid,
            DefaultValue = defaultValue is { Kind: SqlValueKind.Literal or SqlValueKind.Number } ? new(defaultValue.Value is null ? null : Convert.ToString(defaultValue.Value, CultureInfo.InvariantCulture)) : null,
            CurrentDatetimeDefault = defaultValue is { Kind: SqlValueKind.CurrentDate or SqlValueKind.CurrentTime or SqlValueKind.CurrentTimestamp } };
        constraints.AddRange(inlineConstraints); return field;
    }
    private (FieldType Type, int Length, int Scale, bool Varying) DataType(bool expression = false)
    {
        if (Take("SMALLINT")) return (FieldType.Binary, 2, 0, false);
        if (Take("INTEGER") || Take("INT")) return (FieldType.Binary, 4, 0, false);
        if (Take("BIGINT")) return (FieldType.Binary, 8, 0, false);
        if (Take("DECIMAL") || Take("DEC") || Take("NUMERIC"))
        {
            var type = _tokens[_position - 1].Text == "NUMERIC" ? FieldType.Zoned : FieldType.Packed;
            var precision = 5; var scale = 0;
            if (Take("(")) { precision = Integer(1, expression ? 63 : 29); if (Take(",")) scale = Integer(0, Math.Min(expression ? 63 : 28, precision)); Need(")"); }
            return (type, precision, scale, false);
        }
        if (Take("CHAR") || Take("CHARACTER") || Is("VARCHAR"))
        {
            var varying = Take("VARCHAR") || Take("VARYING"); var length = 1;
            if (Take("(")) { length = Integer(1, 32740); Need(")"); } else if (varying) throw Invalid("VARCHAR requires a length.");
            return (FieldType.Alpha, length, 0, varying);
        }
        if (Take("REAL")) return (FieldType.Float, 4, 0, false);
        if (Take("DOUBLE")) { Take("PRECISION"); return (FieldType.Float, 8, 0, false); }
        if (Take("DATE")) return (FieldType.Date, 10, 0, false);
        if (Take("TIME")) return (FieldType.Time, 8, 0, false);
        if (Take("TIMESTAMP"))
        { if (Take("(")) { if (Integer(0, 12) != 6) throw Invalid("Only TIMESTAMP(6) is implemented."); Need(")"); } return (FieldType.Timestamp, 26, 0, false); }
        throw Invalid("Unsupported SQL data type.");
    }
    private int Integer(int minimum, int maximum)
    {
        if (Current.Kind != 'N' || !int.TryParse(Current.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum) throw Invalid("Invalid type length, precision or CCSID.");
        _position++; return value;
    }
    private List<string> Identifiers()
    { var result = new List<string> { Identifier() }; while (Take(",")) result.Add(Identifier()); return result; }
    private SqlInsert Insert()
    {
        var table = ObjectNameValue(); IReadOnlyList<string>? columns = null;
        if (Take("(")) { columns = Identifiers(); Need(")"); if (columns.Distinct().Count() != columns.Count) throw Invalid("Repeated INSERT column."); }
        if (Take("DEFAULT")) { Need("VALUES"); return new(table, columns, new[] { (IReadOnlyList<SqlValue>)Array.Empty<SqlValue>() }); }
        Need("VALUES"); var rows = new List<IReadOnlyList<SqlValue>>();
        do
        {
            Need("("); var row = new List<SqlValue> { Value(true) }; while (Take(",")) row.Add(Value(true)); Need(")"); rows.Add(row);
        } while (Take(","));
        return new(table, columns, rows);
    }
    private SqlValue Value(bool allowDefault)
    {
        if (allowDefault && Take("DEFAULT")) return new(SqlValueKind.Default);
        if (Take("NULL")) return new(SqlValueKind.Literal);
        if (Take("CURRENT_DATE")) return new(SqlValueKind.CurrentDate);
        if (Take("CURRENT_TIME")) return new(SqlValueKind.CurrentTime);
        if (Take("CURRENT_TIMESTAMP")) return new(SqlValueKind.CurrentTimestamp);
        if (Take("CURRENT"))
        {
            if (Take("DATE")) return new(SqlValueKind.CurrentDate);
            if (Take("TIME")) return new(SqlValueKind.CurrentTime);
            Need("TIMESTAMP"); return new(SqlValueKind.CurrentTimestamp);
        }
        if (Take("DATE") || Take("TIME") || Take("TIMESTAMP"))
        { if (Current.Kind != '\'') throw Invalid("Typed datetime requires a string literal."); var value = Current.Text; _position++; return new(SqlValueKind.Literal, value); }
        if (Current.Kind == '\'') { var text = Current.Text; _position++; return new(SqlValueKind.Literal, text); }
        var sign = Take("-") ? "-" : Take("+") ? "+" : "";
        if (Current.Kind != 'N') throw Invalid("Expected a literal value.");
        var number = sign + Current.Text; _position++;
        // Retain numeric text: Decimal.TryParse can round excess fractional digits.
        return new(SqlValueKind.Number, number);
    }
    private static SqlValue TypeDefault(FieldType type) => type switch {
        FieldType.Date => new(SqlValueKind.CurrentDate), FieldType.Time => new(SqlValueKind.CurrentTime), FieldType.Timestamp => new(SqlValueKind.CurrentTimestamp),
        FieldType.Alpha => new(SqlValueKind.Literal, ""), _ => new(SqlValueKind.Literal, "0") };
}
