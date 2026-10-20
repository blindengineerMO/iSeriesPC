using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

public enum SqlNaming { System, Sql }
public sealed record SqlExecutionOptions(SqlNaming Naming, string CurrentLibrary, string DefaultSchema,
    IReadOnlyList<string> LibraryList, int Ccsid = 37, string? DefaultCollection = null, SqlConversionOptions? Conversion = null);
public sealed record SqlObjectName(string? Schema, string Name);
public abstract record SqlStatement(SqlObjectName Table);
public sealed record SqlCreateTable(SqlObjectName Name, FileDefinition Definition) : SqlStatement(Name);
public sealed record SqlDropTable(SqlObjectName Name) : SqlStatement(Name);
public sealed record SqlAlterTable(SqlObjectName Name, IReadOnlyList<SqlTableChange> Changes) : SqlStatement(Name);
public abstract record SqlTableChange;
public sealed record SqlAddColumn(FieldSpec Field, string? Before, bool HistoricalDatetimeDefault = false) : SqlTableChange;
public sealed record SqlDropColumn(string Name, bool Cascade) : SqlTableChange;
public sealed record SqlAlterColumn(string Name, FieldSpec? DataType, SqlValue? Default, bool DropDefault, bool? Nullable) : SqlTableChange;
public sealed record SqlAddKey(SqlUniqueConstraint Constraint) : SqlTableChange;
public sealed record SqlAddCheck(SqlCheckConstraint Constraint) : SqlTableChange;
public sealed record SqlAddForeign(SqlForeignConstraint Constraint) : SqlTableChange;
public sealed record SqlDropForeign(string Name) : SqlTableChange;
public sealed record SqlDropKey(string? Name, bool Primary, bool Cascade, bool UniqueOnly = false, bool CheckOnly = false) : SqlTableChange
{
    public SqlDropKey(string? name, bool primary, bool cascade, bool uniqueOnly) : this(name, primary, cascade, uniqueOnly, false) { }
    public void Deconstruct(out string? name, out bool primary, out bool cascade, out bool uniqueOnly)
    { name = Name; primary = Primary; cascade = Cascade; uniqueOnly = UniqueOnly; }
}
public sealed record SqlIndexKey(string Column, bool Descending = false);
public sealed record SqlCreateIndex(SqlObjectName Name, SqlObjectName Source, IReadOnlyList<SqlIndexKey> Keys,
    bool Unique = false, bool ExcludeNullKeys = false, string? Format = null, IReadOnlyList<string>? Columns = null) : SqlStatement(Source);
public sealed record SqlDropIndex(SqlObjectName Name) : SqlStatement(Name);
public sealed record SqlCreateView(SqlObjectName Name, SqlSelect Query, string QueryText, IReadOnlyList<string>? Columns = null,
    string? Format = null, bool Replace = false, SqlViewCheckOption CheckOption = SqlViewCheckOption.None) : SqlStatement(Name);
public sealed record SqlDropView(SqlObjectName Name) : SqlStatement(Name);
public sealed record SqlInsert(SqlObjectName Name, IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<SqlValue>> Rows) : SqlStatement(Name);
public sealed record SqlUpdate(SqlObjectName Name, IReadOnlyList<SqlAssignment> Assignments, SqlExpression? Where, string? Alias = null) : SqlStatement(Name);
public sealed record SqlDelete(SqlObjectName Name, SqlExpression? Where, string? Alias = null) : SqlStatement(Name);
public sealed record SqlAssignment(string Column, SqlExpression Value);
public abstract record SqlExpression;
public sealed record SqlConstant(SqlValue Value) : SqlExpression;
public sealed record SqlColumn(string Name, string? Qualifier = null) : SqlExpression;
public sealed record SqlUnary(string Operator, SqlExpression Value) : SqlExpression;
public sealed record SqlBinary(string Operator, SqlExpression Left, SqlExpression Right) : SqlExpression;
public sealed record SqlIsNull(SqlExpression Value, bool Negated) : SqlExpression;
public sealed record SqlIn(SqlExpression Value, IReadOnlyList<SqlExpression> Values, bool Negated) : SqlExpression;
public sealed record SqlCoalesce(IReadOnlyList<SqlExpression> Values) : SqlExpression;
public sealed record SqlParameter(int Index) : SqlExpression;
public sealed record SqlStar(string? Qualifier = null) : SqlExpression;
public sealed record SqlFunction(string Name, IReadOnlyList<SqlExpression> Arguments, bool Distinct = false, bool All = false) : SqlExpression;
public sealed record SqlCast(SqlExpression Value, SqlDataType Type) : SqlExpression;
public sealed record SqlExtract(string Unit, SqlExpression Value) : SqlExpression;
public sealed record SqlDuration(SqlExpression Value, string Unit) : SqlExpression;
public sealed record SqlLike(SqlExpression Value, SqlExpression Pattern, SqlExpression? Escape, bool Negated) : SqlExpression;
public sealed record SqlDataType(FieldType Type, int Length, int Scale = 0, bool Varying = false, int Ccsid = 0);
public sealed record SqlSelect(IReadOnlyList<SqlProjection> Columns, SqlFrom From, SqlExpression? Where,
    IReadOnlyList<SqlExpression> GroupBy, SqlExpression? Having, IReadOnlyList<SqlOrdering> OrderBy,
    SqlExpression? FetchFirst, bool Distinct, int ParameterCount) : SqlStatement(new SqlObjectName(null, "*QUERY"));
public sealed record SqlProjection(SqlExpression Value, string? Alias = null);
public sealed record SqlOrdering(SqlExpression Value, bool Descending, bool? NullsFirst = null);
public abstract record SqlFrom;
public sealed record SqlTableSource(SqlObjectName Table, string? Alias = null) : SqlFrom;
public sealed record SqlJoinSource(SqlFrom Left, SqlFrom Right, string Kind, SqlExpression? On) : SqlFrom;
public enum SqlValueKind { Literal, Default, CurrentDate, CurrentTime, CurrentTimestamp, Number }
public sealed record SqlValue(SqlValueKind Kind, object? Value = null);
