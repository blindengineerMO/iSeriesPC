using Ipc.Core.Messages;
using Ipc.Db.Dds;
using Ipc.Db.Sql;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests
{
    [Theory]
    [InlineData("SUM(ID)")]
    [InlineData("AVG(ID)")]
    public void Integer_aggregate_intermediate_overflow_is_not_hidden_by_the_final_average(string aggregate)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(2147483647),(1),(-1)");
        Assert.Equal("22003", Assert.Throws<CpfException>(() => Query("SELECT " + aggregate + " FROM QGPL.DATA")).SqlState);
    }

    [Fact]
    public void Aggregate_metadata_and_decimal_intermediates_keep_native_widths()
    {
        Run("CREATE TABLE QGPL.DATA(ID SMALLINT,VALUE DECIMAL(29,2))");
        Run("INSERT INTO QGPL.DATA VALUES(1,700000000000000000000000000.99),(2,700000000000000000000000000.99)");
        var result = Query("SELECT COUNT(*),COUNT_BIG(*),SUM(ID),AVG(ID),SUM(VALUE),AVG(CAST(VALUE AS DECIMAL(31,2))) FROM QGPL.DATA");
        Assert.Equal(new[] { SqlQueryKind.Integer,SqlQueryKind.BigInt,SqlQueryKind.Integer,SqlQueryKind.Integer,SqlQueryKind.Decimal,SqlQueryKind.Decimal }, result.Columns.Select(c => c.Type.Kind));
        var row = Assert.Single(result.Rows); Assert.Equal(1L, row[3]);
        Assert.Equal("1400000000000000000000000001.98", ((SqlDecimalValue)row[4]!).Text);
        Assert.Equal(31, result.Columns[4].Type.Precision); Assert.Equal(2, result.Columns[5].Type.Scale);
        Assert.Equal("700000000000000000000000000.99", ((SqlDecimalValue)row[5]!).Text);
    }

    [Theory]
    [InlineData("SELECT COUNT(*),COUNT(ALL *) FROM QGPL.DATA")]
    [InlineData("SELECT SUM(*) FROM QGPL.DATA")]
    [InlineData("SELECT LENGTH(DISTINCT NAME) FROM QGPL.DATA")]
    [InlineData("SELECT A.ID FROM QGPL.DATA A JOIN QGPL.DATA B ON C.ID=A.ID JOIN QGPL.DATA C ON B.ID=C.ID")]
    public void Invalid_function_forms_and_forward_join_references_do_not_reuse_valid_bindings(string sql)
    {
        Seed(); Assert.Throws<CpfException>(() => Query(sql));
    }

    [Theory]
    [InlineData("left-chain")]
    [InlineData("tables")]
    [InlineData("groups")]
    [InlineData("projection")]
    public void Query_structure_limits_reject_pathological_inputs_before_recursive_compilation(string shape)
    {
        Seed(); var sql = shape switch {
            "left-chain" => "SELECT " + string.Join('+', Enumerable.Repeat("ID", 2000)) + " FROM QGPL.DATA",
            "tables" => "SELECT A0.ID FROM " + string.Join(',', Enumerable.Range(0, 33).Select(i => "QGPL.DATA A" + i)),
            "groups" => "SELECT ID FROM QGPL.DATA GROUP BY " + string.Join(',', Enumerable.Repeat("ID", 129)),
            _ => "SELECT " + string.Join(',', Enumerable.Repeat("ID", 1001)) + " FROM QGPL.DATA" };
        var error = Assert.Throws<CpfException>(() => Query(sql)); Assert.Contains(error.MessageId, new[] { "SQL0104", "SQL0904" });
    }

    [Theory]
    [InlineData("NAME LIKE 'A%'", 1)]
    [InlineData("NAME NOT LIKE 'A%'", 2)]
    [InlineData("NAME LIKE 'Zo_'", 1)]
    [InlineData("NAME LIKE 'A!%' ESCAPE '!'", 0)]
    [InlineData("NAME LIKE '%e' OR NAME IS NULL", 2)]
    public void LIKE_uses_character_wildcards_escape_and_null_predicates(string predicate, int expected)
    { Seed(); Assert.Equal(expected, Query("SELECT ID FROM QGPL.DATA WHERE " + predicate).Rows.Count); }

    [Fact]
    public void Markers_keep_exact_values_typed_nulls_and_per_query_validation()
    {
        Seed(); var exact = new SqlDecimalValue("1234567890123456789012345678901234567890.25", 42, 2);
        var result = Query("SELECT ?,CAST(? AS REAL),? FROM QGPL.DATA WHERE ID=?", new(exact), new(0.1d), new(DBNull.Value, new(SqlQueryKind.VarChar, Length: 10)), new(1));
        var row = Assert.Single(result.Rows); Assert.Equal(exact.Text, ((SqlDecimalValue)row[0]!).Text); Assert.Equal((double)(float)0.1, row[1]); Assert.Null(row[2]);
        Assert.Equal("22001", Assert.Throws<CpfException>(() => Query("SELECT ? FROM QGPL.DATA WHERE 1=0", new SqlQueryParameter("long", new(SqlQueryKind.Character, Length: 1)))).SqlState);
        Assert.Equal("07006", Assert.Throws<CpfException>(() => Query("SELECT ? FROM QGPL.DATA", new SqlQueryParameter(new object()))).SqlState);
        Assert.Equal("57011", Assert.Throws<CpfException>(() => { using var query = _files.OpenSqlQuery("SELECT ? FROM QGPL.DATA", Options, [new("four")], new(MaximumBytes: 3)); }).SqlState);
    }

    [Fact]
    public void Logical_query_sources_preserve_projection_selection_and_source_authority()
    {
        Seed();
        var source = string.Join('\n', new[] { LogicalFileTests.Line('R', "QUERYREC", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "ID"), LogicalFileTests.Line(' ', "DEPT"), LogicalFileTests.Line('S', "DEPT", "COMP(EQ 'A')") });
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("ACTIVE", source);
        _files.CreateLogicalFile("QGPL", "ACTIVE", definition, source);
        Assert.Equal(new long[] { 1,2 }, Query("SELECT ID FROM QGPL.ACTIVE ORDER BY ID").Rows.Select(row => (long)row[0]!));
        Assert.Equal("42703", Assert.Throws<CpfException>(() => Query("SELECT NAME FROM QGPL.ACTIVE")).SqlState);
        Run("UPDATE QGPL.DATA SET DEPT='B' WHERE ID=1"); Assert.Equal(2L, Assert.Single(Query("SELECT ID FROM QGPL.ACTIVE").Rows)[0]);
    }

    [Fact]
    public void QTEMP_queries_are_job_private_and_cleanup_closes_their_read_transactions()
    {
        var owner = _system.Jobs.CreateInteractive("QSECOFR"); var foreign = _system.Jobs.CreateInteractive("QSECOFR"); SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", owner.Key))
        {
            Run("CREATE TABLE QTEMP.DATA(ID INTEGER)"); Run("INSERT INTO QTEMP.DATA VALUES(7)");
            cursor = _files.OpenSqlQuery("SELECT ID FROM QTEMP.DATA", Options); Assert.True(cursor.Read()); Assert.Equal(7L, cursor.Current![0]);
        }
        using (OperationIdentity.Enter("QSECOFR", foreign.Key)) Assert.Equal("42501", Assert.Throws<CpfException>(() => Query("SELECT ID FROM QTEMP.DATA")).SqlState);
        _system.Jobs.Complete(owner.Key, Ipc.Core.Work.JobCompletion.Normal);
        Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read()); cursor.Dispose();
    }

    [Fact]
    public void New_scalar_binding_is_shared_by_mutations_without_exposing_result_sets_to_RUNSQL()
    {
        Seed(); Run("UPDATE QGPL.DATA SET NAME=UPPER(SUBSTR(NAME,1,3)),AMOUNT=ROUND(AMOUNT,1) WHERE NAME LIKE 'A%'");
        var row = Assert.Single(Query("SELECT NAME,AMOUNT FROM QGPL.DATA WHERE ID=1").Rows); Assert.Equal("ADA", row[0]); Assert.Equal(1.30m, Number(row[1]));
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("SELECT ID FROM QGPL.DATA")).MessageId);
    }
}
