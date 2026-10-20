using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewTests
{
    [Fact]
    public void First_native_view_read_does_not_materialize_twenty_thousand_records()
    {
        Scalar("WITH RECURSIVE n(x) AS(VALUES(4) UNION ALL SELECT x+1 FROM n WHERE x<20003) INSERT INTO \"QGPL.DATA.DATA\"(ID,AMOUNT) SELECT x,0 FROM n");
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CODE FROM QGPL/DATA");
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var cursor = _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT"); Assert.Equal(2L, cursor.Read()!["ID"]);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4L * 1024 * 1024);
    }
    [Fact]
    public void Outer_join_views_preserve_nullable_result_columns()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT A.ID,B.ID AS MATCHED FROM QGPL/DATA A LEFT JOIN QGPL/DATA B ON A.ID=B.ID+10");
        Assert.True(_files.GetDefinition("QGPL", "REPORT")!.PrimaryFormat.Find("MATCHED")!.NullCapable);
        var rows = Query("SELECT ID,MATCHED FROM QGPL/REPORT"); Assert.Equal(3, rows.Count); Assert.All(rows, row => Assert.Null(row[1]));
    }
    [Fact]
    public void UTF8_view_columns_keep_their_CCSID_byte_width_and_native_record_value()
    {
        var options = Options with { Ccsid = 1208 };
        _files.ExecuteSql("CREATE TABLE QGPL/TEXTDATA(VALUE VARCHAR(16) NOT NULL)", options);
        _files.ExecuteSql("INSERT INTO QGPL/TEXTDATA VALUES('東京')", options);
        _files.ExecuteSql("CREATE VIEW QGPL/REPORT AS SELECT VALUE,CHAR_LENGTH(VALUE) AS N FROM QGPL/TEXTDATA", options);
        Assert.Equal(1208, _files.GetDefinition("QGPL", "REPORT")!.PrimaryFormat.Find("VALUE")!.Ccsid);
        using var cursor = _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT"); var row = cursor.Read()!;
        Assert.Equal("東京", row["VALUE"]); Assert.Equal(2L, row["N"]);
    }
    [Fact]
    public void A_native_view_cursor_rejects_late_enlistment_and_releases_its_snapshot()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        _system.Journals.CreateReceiver(new("QGPL", "VWRCV")); _system.Journals.Create(new("QGPL", "VWJRN"), new("QGPL", "VWRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "VWJRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT"); Assert.NotNull(cursor.Read());
        _system.RecordTransactions.Begin(_session.Job.Key); _system.RecordTransactions.Enlist(_session.Job.Key, new("QGPL", "DATA"));
        try { Assert.Equal("IPC0211", Assert.Throws<CpfException>(() => cursor.Read()).MessageId); }
        finally { _system.RecordTransactions.Rollback(_session.Job.Key); _system.RecordTransactions.End(_session.Job.Key); }
    }
    [Fact]
    public void SQL_view_queries_read_pending_record_data_and_native_cursors_reject_unimplemented_unit_boundaries()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        _system.Journals.CreateReceiver(new("QGPL", "VWRCV")); _system.Journals.Create(new("QGPL", "VWJRN"), new("QGPL", "VWRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "VWJRN"), "*BOTH");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key))
        {
            _system.RecordTransactions.Begin(_session.Job.Key); _system.RecordTransactions.Enlist(_session.Job.Key, new("QGPL", "DATA"));
            try
            {
                using (var change = _system.RecordTransactions.Open(_session.Job.Key)) { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='9.50' WHERE ID=1"); change.Complete(); }
                Assert.Equal("9.50", Query("SELECT AMOUNT FROM QGPL/REPORT WHERE ID=1")[0][0]!.ToString());
                Assert.Equal("IPC0211", Assert.Throws<CpfException>(() => _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT")).MessageId);
                Assert.Equal("IPC0211", Assert.Throws<CpfException>(() => Run("DROP VIEW QGPL/REPORT")).MessageId);
            }
            finally { _system.RecordTransactions.Rollback(_session.Job.Key); _system.RecordTransactions.End(_session.Job.Key); }
        }
        Assert.Equal("2.00", Query("SELECT AMOUNT FROM QGPL/REPORT WHERE ID=1")[0][0]!.ToString());
    }
    [Fact]
    public void Star_columns_are_frozen_and_source_names_remain_bound_across_library_list_changes_and_relocation()
    {
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        Run("CREATE VIEW QGPL/BASE AS SELECT ID,CODE FROM DATA"); Run("CREATE VIEW QGPL/CHILD AS SELECT * FROM BASE");
        Run("CREATE OR REPLACE VIEW QGPL/BASE AS SELECT ID,CODE,AMOUNT FROM DATA");
        Assert.Equal(2, Query("SELECT * FROM QGPL/CHILD")[0].Count);
        _system.Libraries.CreateLibrary("OTHER");
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("OTHER", "RENAMED"));
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CODE CHAR(1))"); Run("INSERT INTO QGPL/DATA VALUES(99,'x')");
        Assert.Equal(new long[] { 1, 2, 3 }, Query("SELECT ID FROM QGPL/CHILD ORDER BY ID").Select(row => (long)row[0]!));
        _system.ObjectOperations.Relocate(new("QGPL", "BASE"), ObjectType.File, new("OTHER", "MOVED"));
        Assert.Equal(3, Query("SELECT ID FROM QGPL/CHILD").Count);
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("OTHER", "COPY"), copy: true);
        Assert.Equal(3, Query("SELECT ID FROM OTHER/COPY").Count);
    }
    [Fact]
    public void System_naming_inherits_the_first_source_schema_and_SQL_naming_uses_the_default_schema()
    {
        _system.Libraries.CreateLibrary("OTHER");
        _files.ExecuteSql("CREATE VIEW REPORT AS SELECT ID FROM QGPL/DATA", Options with { CurrentLibrary = "OTHER" });
        Assert.True(_files.FileExists("QGPL", "REPORT"));
        _files.ExecuteSql("CREATE VIEW SQLVIEW AS SELECT ID FROM QGPL.DATA", Options with { Naming = SqlNaming.Sql, DefaultSchema = "OTHER" });
        Assert.True(_files.FileExists("OTHER", "SQLVIEW")); Assert.Equal(AuthorityBit.None, _system.Objects.GetRequired("OTHER", "SQLVIEW", ObjectType.File).PublicAuthority);
    }
    [Theory]
    [InlineData("SELECT ID,ID FROM QGPL/DATA", "SQL0612")]
    [InlineData("SELECT ID+1 FROM QGPL/DATA", "SQL0104")]
    [InlineData("SELECT ID FROM QGPL/DATA ORDER BY ID", "SQL0104")]
    [InlineData("SELECT ID FROM QGPL/DATA FETCH FIRST 1 ROW ONLY", "SQL0104")]
    [InlineData("SELECT ID FROM QGPL/DATA WHERE ID=?", "SQL0104")]
    [InlineData("SELECT BAD FROM QGPL/DATA", "SQL0206")]
    [InlineData("SELECT ID FROM QGPL/DATA; DROP TABLE QGPL/DATA", "SQL0104")]
    public void Invalid_or_remaining_view_clauses_never_publish_objects(string query, string error)
    {
        Assert.Equal(error, Assert.Throws<CpfException>(() => Run("CREATE VIEW QGPL/REPORT AS " + query)).MessageId);
        Assert.False(_files.FileExists("QGPL", "REPORT")); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Explicit_columns_name_derived_results_and_mismatched_lists_reject()
    {
        Run("CREATE VIEW QGPL/REPORT(NEXTID,TOTAL) AS SELECT ID+1,AMOUNT+AMOUNT FROM QGPL/DATA");
        Assert.Equal(2L, Query("SELECT NEXTID FROM QGPL/REPORT ORDER BY NEXTID")[0][0]);
        Assert.Equal("SQL0117", Assert.Throws<CpfException>(() => Run("CREATE VIEW QGPL/BAD(ONE,TWO) AS SELECT ID FROM QGPL/DATA")).MessageId);
    }
    [Fact]
    public void Native_record_reads_scroll_a_bounded_snapshot_and_revalidate_after_EOF()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CODE,AMOUNT FROM QGPL/DATA");
        Assert.Equal(3, _files.ReadAll("QGPL", "REPORT", "REPORT").Count); Assert.Equal(3, _files.RowCount("QGPL", "REPORT", "REPORT"));
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT");
        Assert.Equal(2L, cursor.Read()!["ID"]); Assert.Equal(1L, cursor.Read()!["ID"]);
        Assert.Equal(2L, cursor.ReadPrior()!["ID"]); Assert.Equal(1L, cursor.Read()!["ID"]); Assert.Equal(3L, cursor.Read()!["ID"]); Assert.Null(cursor.Read());
        Assert.Equal(3L, cursor.ReadPrior()!["ID"]);
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("DROP VIEW QGPL/REPORT")).MessageId);
    }
    [Fact]
    public void Wide_exact_decimal_results_remain_queryable_and_native_record_open_rejects_without_rounding()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT AMOUNT*AMOUNT AS TOTAL FROM QGPL/DATA");
        var value = Assert.IsType<SqlDecimalValue>(Query("SELECT SUM(TOTAL) FROM QGPL/REPORT")[0][0]); Assert.Equal("10104.0000", value.Text);
        Assert.Equal("CPF4131", Assert.Throws<CpfException>(() => _files.OpenSequentialCursor("QGPL", "REPORT", "REPORT")).MessageId);
    }
    [Fact]
    public void Nested_views_and_the_outer_statement_share_one_timestamp_and_warning_diagnostics_survive_EOF()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT CURRENT_TIMESTAMP AS STAMP,CAST('long' AS VARCHAR(2)) AS TEXT FROM QGPL/DATA");
        using var query = _files.OpenSqlQuery("SELECT STAMP,CURRENT_TIMESTAMP AS LIVE,TEXT FROM QGPL/REPORT", Options);
        Assert.True(query.Read()); Assert.Equal(query.Current![0], query.Current[1]); Assert.Equal("lo", query.Current[2]);
        while (query.Read()) { }
        Assert.Contains(query.Diagnostics, item => item.SqlState == "01004");
    }
    [Fact]
    public void Validation_checks_the_view_query_without_creating_or_replacing_it()
    {
        _files.ValidateSql("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA", Options); Assert.False(_files.FileExists("QGPL", "REPORT"));
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        _files.ValidateSql("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT CODE FROM QGPL/DATA", Options);
        Assert.Equal(3, Query("SELECT ID FROM QGPL/REPORT").Count);
        _files.ValidateSql("DROP VIEW QGPL/REPORT", Options); Assert.True(_files.FileExists("QGPL", "REPORT"));
    }
    [Fact]
    public void Replacing_a_view_with_a_recursive_dependency_rolls_back_both_catalog_and_query_shape()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA"); Run("CREATE VIEW QGPL/CHILD AS SELECT ID FROM QGPL/REPORT");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/CHILD")).MessageId);
        Assert.Equal(3, Query("SELECT ID FROM QGPL/CHILD").Count);
    }
    [Fact]
    public void DROP_TABLE_cascades_multiple_levels_of_views_and_indexes_without_leaving_dependencies()
    {
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)"); Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/IDX"); Run("CREATE VIEW QGPL/CHILD AS SELECT ID FROM QGPL/REPORT");
        Run("DROP TABLE QGPL/DATA");
        foreach (var name in new[] { "DATA", "IDX", "REPORT", "CHILD" }) Assert.False(_files.FileExists("QGPL", name));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_object_dependencies WHERE source_name IN('IDX','REPORT','CHILD')"));
    }
}
