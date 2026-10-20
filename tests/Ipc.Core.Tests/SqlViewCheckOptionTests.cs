using Ipc.Core.Messages;
using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewMutationTests
{
    [Theory]
    [InlineData("LOCAL", "LOCAL")]
    [InlineData("LOCAL", "CASCADED")]
    [InlineData("CASCADED", "LOCAL")]
    [InlineData("CASCADED", "CASCADED")]
    public void Five_level_CHECK_OPTION_matrix_matches_the_independent_IBM_example(string x, string y)
    {
        Run("CREATE TABLE QGPL/FLAGS(F1 INTEGER,F2 INTEGER,F3 INTEGER,F4 INTEGER,F5 INTEGER)");
        for (var index = 1; index <= 5; index++)
        {
            var source = index == 1 ? "FLAGS" : "V" + (index - 1); var check = index == 2 ? x : index == 4 ? y : null;
            Run("CREATE VIEW QGPL/V" + index + " AS SELECT * FROM QGPL/" + source + " WHERE F" + index + "=1" + (check is null ? "" : " WITH " + check + " CHECK OPTION"));
        }
        for (var view = 1; view <= 5; view++) for (var changed = 1; changed <= 5; changed++)
        {
            var enforced = view < 2 ? Array.Empty<int>() : view < 4 ? x == "LOCAL" ? [2] : new[] { 1, 2 } :
                y == "CASCADED" ? new[] { 1, 2, 3, 4 } : x == "LOCAL" ? [2, 4] : new[] { 1, 2, 4 };
            var values = Enumerable.Range(1, 5).Select(index => index == changed ? "0" : "1");
            var sql = "INSERT INTO QGPL/V" + view + " VALUES(" + string.Join(',', values) + ")";
            if (enforced.Contains(changed)) Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run(sql)).MessageId);
            else Assert.Equal(1, Run(sql));
        }
    }
    [Fact]
    public void UNKNOWN_does_not_satisfy_a_view_check_and_omitted_checks_do_not_filter_inserts()
    {
        Run("CREATE TABLE QGPL/NULLDATA(ID INTEGER)"); Run("CREATE VIEW QGPL/OPENED AS SELECT ID FROM QGPL/NULLDATA WHERE ID>0");
        Run("CREATE VIEW QGPL/CHECKED AS SELECT ID FROM QGPL/NULLDATA WHERE ID>0 WITH CHECK OPTION");
        Assert.Equal(1, Run("INSERT INTO QGPL/OPENED VALUES(NULL)")); Assert.Empty(Query("SELECT ID FROM QGPL/OPENED"));
        Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHECKED VALUES(NULL)")).MessageId);
        Assert.Equal(1, _files.RowCount("QGPL", "NULLDATA", "NULLDATA"));
    }
    [Theory]
    [InlineData("SELECT SUM(AMOUNT) AS TOTAL FROM QGPL/DATA")]
    [InlineData("SELECT DISTINCT ID FROM QGPL/DATA")]
    [InlineData("SELECT A.ID FROM QGPL/DATA A JOIN QGPL/DATA B ON A.ID=B.ID")]
    [InlineData("SELECT ID FROM QGPL/DATA WHERE CURRENT_TIMESTAMP IS NOT NULL")]
    public void CHECK_OPTION_rejects_read_only_or_unstable_definitions_before_publication(string query)
    {
        Assert.Equal("SQL0160", Assert.Throws<CpfException>(() => Run("CREATE VIEW QGPL/REPORT AS " + query + " WITH CHECK OPTION")).MessageId);
        Assert.False(_files.FileExists("QGPL", "REPORT"));
    }
    [Fact]
    public void A_special_register_projected_by_an_underlying_view_cannot_be_hidden_in_a_check_predicate()
    {
        Run("CREATE VIEW QGPL/STAMPED AS SELECT ID,CURRENT_TIMESTAMP AS STAMP FROM QGPL/DATA");
        Assert.Equal("SQL0160", Assert.Throws<CpfException>(() => Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/STAMPED WHERE STAMP IS NOT NULL WITH LOCAL CHECK OPTION")).MessageId);
    }
    [Fact]
    public void DEFAULT_values_come_from_base_columns_and_missing_required_hidden_columns_fail()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        Assert.Equal(1, Run("UPDATE QGPL/REPORT SET AMOUNT=DEFAULT WHERE ID=1")); Assert.Equal("5.00", Query("SELECT AMOUNT FROM QGPL/DATA WHERE ID=1")[0][0]!.ToString());
        Assert.Equal("SQL0407", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET ID=DEFAULT")).MessageId);
        Run("CREATE VIEW QGPL/NOID AS SELECT AMOUNT FROM QGPL/DATA");
        Assert.Equal("SQL0407", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/NOID VALUES(100)")).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Repeated_base_columns_cannot_be_assigned_twice_through_distinct_view_names()
    {
        Run("CREATE VIEW QGPL/REPORT(A,B) AS SELECT ID,ID FROM QGPL/DATA");
        Assert.Equal("SQL0612", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET A=4,B=5")).MessageId);
        Assert.Equal("SQL0612", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(4,5)")).MessageId);
    }
    [Theory]
    [InlineData("INSERT INTO QGPL/REPORT VALUES(10)")]
    [InlineData("UPDATE QGPL/REPORT SET TOTAL=10")]
    [InlineData("DELETE FROM QGPL/REPORT")]
    public void Aggregate_views_reject_all_DML_with_the_read_only_view_diagnostic(string sql)
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT SUM(AMOUNT) AS TOTAL FROM QGPL/DATA");
        Assert.Equal("SQL0150", Assert.Throws<CpfException>(() => Run(sql)).MessageId); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void A_constant_only_view_remains_deletable_without_becoming_insertable_or_updatable()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT 1 AS N FROM QGPL/DATA");
        Assert.Equal("SQL0150", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(2)")).MessageId);
        Assert.Equal("SQL0150", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET N=2")).MessageId);
        Assert.Equal(3, Run("DELETE FROM QGPL/REPORT WHERE N=1"));
    }
    [Fact]
    public void Searched_unique_swaps_and_conflicts_use_the_shared_statement_end_constraint_path()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<3 WITH CHECK OPTION");
        Assert.Equal(2, Run("UPDATE QGPL/REPORT SET ID=3-ID"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET ID=1")).MessageId);
        Assert.Equal(new long[] { 1, 2, 3 }, Query("SELECT ID FROM QGPL/DATA ORDER BY ID").Select(row => (long)row[0]!));
    }
    [Fact]
    public void Stored_view_expression_CCSIDs_remain_in_effect_for_outer_mutation_predicates()
    {
        _files.ExecuteSql("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT,CAST('東京' AS VARCHAR(8)) AS MARK FROM QGPL/DATA", Options with { Ccsid = 1208 });
        Assert.Equal(3, Run("UPDATE QGPL/REPORT SET AMOUNT=7 WHERE CHAR_LENGTH(MARK)=2"));
        Assert.Equal("21.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
    [Fact]
    public void Validation_binds_view_writes_without_executing_values_or_check_conditions()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<3 WITH CHECK OPTION");
        _files.ValidateSql("INSERT INTO QGPL/REPORT VALUES(10,1)", Options);
        _files.ValidateSql("UPDATE QGPL/REPORT SET ID=10", Options); _files.ValidateSql("DELETE FROM QGPL/REPORT", Options);
        Assert.Equal("SQL0206", Assert.Throws<CpfException>(() => _files.ValidateSql("UPDATE QGPL/REPORT SET BAD=10", Options)).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
}
