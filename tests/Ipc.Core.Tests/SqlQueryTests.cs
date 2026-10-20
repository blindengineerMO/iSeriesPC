using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options => new(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"]);
    public SqlQueryTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SqlQueryFixtureAdmin83");
    }
    private void Run(string text) => _files.ExecuteSql(text, Options);
    private (IReadOnlyList<SqlQueryColumn> Columns, IReadOnlyList<object?[]> Rows) Query(string text, params SqlQueryParameter[] parameters)
    {
        using var cursor = _files.OpenSqlQuery(text, Options, parameters);
        var rows = new List<object?[]>(); while (cursor.Read()) rows.Add(cursor.Current!.ToArray());
        return (cursor.Columns, rows);
    }
    private static decimal Number(object? value) => value is SqlDecimalValue number ? number.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private void Seed()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER NOT NULL, DEPT CHAR(4), AMOUNT DECIMAL(12,2), NAME VARCHAR(20))");
        Run("INSERT INTO QGPL.DATA VALUES(1,'A',1.25,'Ada'),(2,'A',2.75,'Grace'),(3,'B',NULL,NULL),(4,NULL,-4,'Zoë')");
    }
    public void Dispose() => _system.Dispose();

    [Fact]
    public void Projection_filter_order_and_fetch_use_typed_shared_file_rows()
    {
        Seed(); var result = Query("SELECT ID AS CODE, AMOUNT+0.01 AS VALUE FROM QGPL.DATA WHERE ID IN(1,2,4) ORDER BY VALUE DESC FETCH FIRST 2 ROWS ONLY");
        Assert.Equal(new[] { "CODE", "VALUE" }, result.Columns.Select(c => c.Name));
        Assert.Equal(2L, result.Rows[0][0]); Assert.Equal(2.76m, Number(result.Rows[0][1]));
        Assert.Equal(1L, result.Rows[1][0]); Assert.Equal(1.26m, Number(result.Rows[1][1]));
        Assert.Equal(SqlQueryKind.Integer, result.Columns[0].Type.Kind); Assert.Equal(SqlQueryKind.Decimal, result.Columns[1].Type.Kind);
    }

    [Theory]
    [InlineData("ASC", 1, 2, 4, 3)]
    [InlineData("DESC", 3, 4, 2, 1)]
    [InlineData("ASC NULLS FIRST", 3, 1, 2, 4)]
    [InlineData("DESC NULLS LAST", 4, 2, 1, 3)]
    public void Null_ordering_matches_DB2_defaults_and_explicit_overrides(string order, params int[] expected)
    {
        Seed(); var result = Query("SELECT ID FROM QGPL.DATA ORDER BY NAME " + order);
        Assert.Equal(expected.Select(i => (long)i), result.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void Wildcards_keep_column_order_duplicates_and_outer_join_nullability()
    {
        Run("CREATE TABLE QGPL.LEFTDATA(ID INTEGER NOT NULL, NAME CHAR(3) NOT NULL)");
        Run("CREATE TABLE QGPL.RIGHTDATA(ID INTEGER NOT NULL, VALUE INTEGER NOT NULL)");
        Run("INSERT INTO QGPL.LEFTDATA VALUES(1,'a'),(2,'b')"); Run("INSERT INTO QGPL.RIGHTDATA VALUES(1,7),(3,9)");
        var result = Query("SELECT A.*, B.* FROM QGPL.LEFTDATA A LEFT JOIN QGPL.RIGHTDATA B ON A.ID=B.ID ORDER BY A.ID");
        Assert.Equal(new[] { "ID", "NAME", "ID", "VALUE" }, result.Columns.Select(c => c.Name));
        Assert.Equal("a  ", result.Rows[0][1]); Assert.Null(result.Rows[1][2]);
        Assert.False(result.Columns[0].Type.Nullable); Assert.True(result.Columns[2].Type.Nullable);
    }

    [Theory]
    [InlineData("INNER", 1)]
    [InlineData("LEFT", 2)]
    [InlineData("RIGHT", 2)]
    [InlineData("FULL", 3)]
    [InlineData("CROSS", 4)]
    public void Joins_use_native_NULL_predicates_and_preserve_unmatched_sides(string kind, int expected)
    {
        Run("CREATE TABLE QGPL.A(ID INTEGER)"); Run("CREATE TABLE QGPL.B(ID INTEGER)");
        Run("INSERT INTO QGPL.A VALUES(1),(2)"); Run("INSERT INTO QGPL.B VALUES(1),(3)");
        var result = Query("SELECT A.ID AS L, B.ID AS R FROM QGPL.A A " + kind + " JOIN QGPL.B B" + (kind == "CROSS" ? "" : " ON A.ID=B.ID"));
        Assert.Equal(expected, result.Rows.Count);
    }

    [Fact]
    public void Parenthesized_joins_and_comma_sources_share_predicate_binding()
    {
        Run("CREATE TABLE QGPL.A(ID INTEGER)"); Run("CREATE TABLE QGPL.B(ID INTEGER)"); Run("CREATE TABLE QGPL.C(ID INTEGER)");
        Run("INSERT INTO QGPL.A VALUES(1),(2)"); Run("INSERT INTO QGPL.B VALUES(2)"); Run("INSERT INTO QGPL.C VALUES(2)");
        Assert.Equal(2L, Assert.Single(Query("SELECT A.ID FROM (QGPL.A A INNER JOIN QGPL.B B ON A.ID=B.ID), QGPL.C C WHERE B.ID=C.ID").Rows)[0]);
    }

    [Fact]
    public void Grouping_HAVING_and_aggregates_preserve_exact_decimals_and_ignore_NULL_values()
    {
        Seed(); var result = Query("SELECT DEPT, COUNT(*) AS ROWS, COUNT(AMOUNT) AS ITEMS, SUM(AMOUNT) AS TOTAL, AVG(AMOUNT) AS MEAN, MIN(ID) AS LO, MAX(ID) AS HI FROM QGPL.DATA GROUP BY DEPT HAVING COUNT(*)>1 ORDER BY TOTAL DESC");
        var row = Assert.Single(result.Rows); Assert.Equal("A   ", row[0]); Assert.Equal(2L, row[1]); Assert.Equal(2L, row[2]); Assert.Equal(4m, Number(row[3])); Assert.Equal(2m, Number(row[4])); Assert.Equal(1L, row[5]); Assert.Equal(2L, row[6]);
        Assert.Equal(31, result.Columns[3].Type.Precision); Assert.False(result.Columns[1].Type.Nullable);
    }

    [Fact]
    public void Aggregate_empty_sets_return_one_row_with_zero_counts_and_NULL_totals()
    {
        Seed(); var row = Assert.Single(Query("SELECT COUNT(*),COUNT(AMOUNT),SUM(AMOUNT),AVG(AMOUNT),MIN(ID),MAX(ID) FROM QGPL.DATA WHERE 1=0").Rows);
        Assert.Equal(0L, row[0]); Assert.Equal(0L, row[1]); Assert.All(row.Skip(2), value => Assert.Null(value));
        Assert.Empty(Query("SELECT DEPT,COUNT(*) FROM QGPL.DATA WHERE 1=0 GROUP BY DEPT").Rows);
    }

    [Fact]
    public void DISTINCT_grouping_and_distinct_aggregates_use_padded_character_and_exact_numeric_equality()
    {
        Run("CREATE TABLE QGPL.DATA(TEXT VARCHAR(5), VALUE DECIMAL(8,2))"); Run("INSERT INTO QGPL.DATA VALUES('A',1),('A ',1),(NULL,NULL),('B',2)");
        Assert.Equal(3, Query("SELECT DISTINCT TEXT FROM QGPL.DATA").Rows.Count);
        var row = Assert.Single(Query("SELECT COUNT(DISTINCT TEXT),COUNT(DISTINCT VALUE),SUM(DISTINCT VALUE),AVG(DISTINCT VALUE) FROM QGPL.DATA").Rows);
        Assert.Equal(2L, row[0]); Assert.Equal(2L, row[1]); Assert.Equal(3m, Number(row[2])); Assert.Equal(1.5m, Number(row[3]));
        Assert.Equal(3, Query("SELECT TEXT,COUNT(*) FROM QGPL.DATA GROUP BY TEXT").Rows.Count);
    }

    [Fact]
    public void Group_expressions_and_nested_aggregate_scalar_expressions_bind_to_the_group_result()
    {
        Seed(); var result = Query("SELECT ID/2 AS BUCKET, ABS(SUM(AMOUNT)) AS TOTAL FROM QGPL.DATA GROUP BY ID/2 HAVING SUM(AMOUNT) IS NOT NULL ORDER BY BUCKET");
        Assert.Equal(3, result.Rows.Count); Assert.Equal(1.25m, Number(result.Rows[0][1])); Assert.Equal(4m, Number(result.Rows[2][1]));
    }

    [Fact]
    public void Parameters_are_data_and_markers_bind_in_source_order_including_FETCH()
    {
        Seed(); var result = Query("SELECT ? AS INPUT,ID FROM QGPL.DATA WHERE ID>? ORDER BY ID FETCH FIRST ? ROWS ONLY", new("x'); DELETE FROM sys_objects; --"), new(1), new(2));
        Assert.Equal(2, result.Rows.Count); Assert.Equal("x'); DELETE FROM sys_objects; --", result.Rows[0][0]); Assert.Equal(2L, result.Rows[0][1]); Assert.True(_files.FileExists("QGPL", "DATA"));
        Assert.Equal(1L, Assert.Single(Query("SELECT ID FROM QGPL.DATA WHERE AMOUNT=CAST(? AS DECIMAL(12,2))", new SqlQueryParameter(1.25m)).Rows)[0]);
    }

    [Fact]
    public void Typed_NULL_markers_and_NULLIF_COALESCE_follow_three_valued_logic()
    {
        Seed(); Assert.Empty(Query("SELECT ID FROM QGPL.DATA WHERE AMOUNT=?", new SqlQueryParameter(null, new(SqlQueryKind.Decimal, 12, 2))).Rows);
        var rows = Query("SELECT COALESCE(NULLIF(ID,1),99), COALESCE(NAME,'none') FROM QGPL.DATA ORDER BY ID").Rows;
        Assert.Equal(99L, rows[0][0]); Assert.Equal("none", rows[2][1]);
        Assert.Equal(1L, Assert.Single(Query("SELECT COALESCE(ID,1/0) FROM QGPL.DATA WHERE ID=1").Rows)[0]);
    }

    [Theory]
    [InlineData("CHARACTER_LENGTH('Jürgen')", "6")]
    [InlineData("LENGTH('Jürgen')", "7")]
    [InlineData("TRIM(BOTH 'x' FROM 'xxabxx')", "ab")]
    [InlineData("TRIM(LEADING FROM '  ab  ')", "ab  ")]
    [InlineData("UPPER('café')", "CAFÉ")]
    [InlineData("LOWER('ABC')", "abc")]
    [InlineData("SUBSTR('abcdef',2,3)", "bcd")]
    [InlineData("SUBSTRING('Jürgen' FROM 2 FOR 3)", "ürg")]
    [InlineData("ROUND(-12.345,2)", "-12.350")]
    [InlineData("ABS(-12.50)", "12.50")]
    [InlineData("EXTRACT(YEAR FROM DATE '2024-02-29')", "2024")]
    [InlineData("MONTH(DATE '2024-02-29')", "2")]
    [InlineData("DATE '2024-01-31'+1 MONTH", "2024-02-29")]
    [InlineData("DATE '2024-02-29'-1 YEAR", "2023-02-28")]
    [InlineData("TIMESTAMP '2024-02-29 23:59:59.000000'+2 SECONDS", "2024-03-01 00:00:01.000000")]
    public void Scalar_functions_use_SQL_character_numeric_and_datetime_rules(string expression, string expected)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1)");
        using var cursor = _files.OpenSqlQuery("SELECT " + expression + " FROM QGPL.DATA", Options with { Ccsid = 1208 });
        Assert.True(cursor.Read()); Assert.Equal(expected, Convert.ToString(cursor.Current![0], CultureInfo.InvariantCulture)); Assert.False(cursor.Read());
    }

    [Fact]
    public void Query_decimal_results_can_exceed_SystemDecimal_without_rounding()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1)");
        var row = Assert.Single(Query("SELECT CAST(123456789012345678901234567890123456789 AS DECIMAL(40,0))+1 FROM QGPL.DATA").Rows);
        var value = Assert.IsType<SqlDecimalValue>(row[0]); Assert.Equal("123456789012345678901234567890123456790", value.Text); Assert.Throws<OverflowException>(() => value.ToDecimal());
    }

    [Theory]
    [InlineData("SELECT ID FROM QGPL.DATA A JOIN QGPL.DATA B ON A.ID=B.ID", "SQL0203")]
    [InlineData("SELECT A.ID FROM QGPL.DATA B", "SQL0206")]
    [InlineData("SELECT BAD FROM QGPL.DATA", "SQL0206")]
    [InlineData("SELECT ID,COUNT(*) FROM QGPL.DATA", "SQL0122")]
    [InlineData("SELECT SUM(COUNT(*)) FROM QGPL.DATA", "SQL0120")]
    [InlineData("SELECT ID FROM QGPL.DATA WHERE COUNT(*)>0", "SQL0120")]
    [InlineData("SELECT ID FROM QGPL.DATA ORDER BY 2", "SQL0125")]
    [InlineData("SELECT DISTINCT ID FROM QGPL.DATA ORDER BY NAME", "SQL0214")]
    [InlineData("SELECT ID FROM sys_jobs", "SQL0204")]
    [InlineData("SELECT ID FROM QGPL.DATA; DROP TABLE QGPL.DATA", "SQL0104")]
    [InlineData("SELECT SQLITE_VERSION() FROM QGPL.DATA", "SQL0440")]
    public void Invalid_queries_fail_before_any_data_is_exposed_even_on_empty_files(string sql, string error)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER,NAME VARCHAR(20))"); Assert.Equal(error, Assert.Throws<CpfException>(() => Query(sql)).MessageId); Assert.True(_files.FileExists("QGPL", "DATA"));
    }
}
