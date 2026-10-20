using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlTableTests
{
    [Fact]
    public void Searched_UPDATE_uses_original_values_for_every_assignment_and_defers_unique_checks()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, OTHER INTEGER, NAME VARCHAR(8))");
        Run("INSERT INTO QGPL.DATA VALUES(1,2,'a'),(2,1,'b'),(3,3,'c')");
        Assert.Equal(2, Run("UPDATE QGPL.DATA AS D SET ID=D.OTHER, OTHER=D.ID, NAME=NAME||'x' WHERE D.ID BETWEEN 1 AND 2"));
        var rows = _files.ReadAll("QGPL", "DATA", "DATA");
        Assert.Equal(2L, rows[0]["ID"]); Assert.Equal(1L, rows[0]["OTHER"]); Assert.Equal("ax", rows[0]["NAME"]);
        Assert.Equal(1L, rows[1]["ID"]); Assert.Equal(2L, rows[1]["OTHER"]); Assert.Equal(3L, rows[2]["ID"]);
        Assert.Equal(3, Run("UPDATE QGPL.DATA SET ID=ID+1"));
        Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }

    [Theory]
    [InlineData("ID IN(1,NULL)", 1)]
    [InlineData("ID NOT IN(1,NULL)", 0)]
    [InlineData("NOT(ID=1 OR ID=2)", 1)]
    [InlineData("VALUE IS NULL OR ID=3 AND ID<>2", 2)]
    [InlineData("NOT VALUE IS NULL AND ID>=2", 2)]
    [InlineData("ID NOT BETWEEN 1 AND 2", 1)]
    [InlineData("ID=1 OR ID=2 AND ID=3", 1)]
    [InlineData("NULL=1", 0)]
    public void DELETE_implements_SQL_three_valued_predicates_and_boolean_precedence(string predicate, int expected)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER, VALUE INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1,NULL),(2,5),(3,6)");
        Assert.Equal(expected, Run("DELETE FROM QGPL.DATA D WHERE " + predicate));
        Assert.Equal(3 - expected, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }

    [Theory]
    [InlineData("VALUE/3", "3.33")]
    [InlineData("VALUE+0.01", "10.01")]
    [InlineData("-VALUE*1.125", "-11.25")]
    [InlineData("COALESCE(NULL,VALUE)/-3", "-3.33")]
    [InlineData("10/3", "3.00")]
    [InlineData("VALUE+0000000001", "11.00")]
    public void Decimal_arithmetic_and_assignment_truncate_toward_zero_without_REAL_rounding(string expression, string expected)
    {
        Run("CREATE TABLE QGPL.DATA(VALUE DECIMAL(12,2))"); Run("INSERT INTO QGPL.DATA VALUES(10)");
        Assert.Equal(1, Run("UPDATE QGPL.DATA SET VALUE=" + expression));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["VALUE"]);
    }

    [Fact]
    public void Decimal_intermediates_beyond_SystemDecimal_remain_exact_before_assignment()
    {
        Run("CREATE TABLE QGPL.DATA(VALUE DECIMAL(29,0))"); Run("INSERT INTO QGPL.DATA VALUES(10000000000000000000000000000)");
        Assert.Equal(1, Run("UPDATE QGPL.DATA SET VALUE=VALUE*100/100"));
        Assert.Equal(10000000000000000000000000000m, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["VALUE"]);
    }

    [Theory]
    [InlineData("ID=ID+2147483647", "SQL0802")]
    [InlineData("ID=ID/(ID-2)", "SQL0802")]
    [InlineData("ID=1", "SQL0803")]
    [InlineData("ID=NULL", "SQL0545")]
    [InlineData("NAME='too long text'", "SQL0404")]
    [InlineData("ID=2147483648", "SQL0406")]
    public void A_late_UPDATE_failure_rolls_back_rows_indexes_versions_and_journal_entries(string assignment, string expected)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, NAME CHAR(8))"); Run("INSERT INTO QGPL.DATA VALUES(1,'first'),(2,'second')");
        _system.Journals.CreateReceiver(new("QGPL", "SQLRCV")); _system.Journals.Create(new("QGPL", "SQLJRN"), new("QGPL", "SQLRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "SQLJRN"), "*BOTH");
        Assert.Equal(expected, Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET " + assignment)).MessageId);
        Assert.Equal(new long[] { 1, 2 }, _files.ReadAll("QGPL", "DATA", "DATA").Select(row => (long)row["ID"]!));
        Assert.Empty(_system.Journals.Read(new("QGPL", "SQLJRN")));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(1,'dup')")).MessageId);
    }

    [Theory]
    [InlineData("UPDATE QGPL.DATA SET BAD=1", "SQL0206")]
    [InlineData("UPDATE QGPL.DATA SET ID=MISSING", "SQL0206")]
    [InlineData("UPDATE QGPL.DATA SET ID=CURRENT_DATE", "SQL0408")]
    [InlineData("UPDATE QGPL.DATA D SET ID=DATA.ID", "SQL0206")]
    [InlineData("DELETE FROM QGPL.DATA WHERE ID", "SQL0104")]
    [InlineData("DELETE FROM QGPL.DATA WHERE ID='text'", "SQL0104")]
    [InlineData("UPDATE QGPL.DATA SET ID=ID+DEFAULT", "SQL0104")]
    [InlineData("UPDATE QGPL.DATA SET ID=1,ID=2", "SQL0104")]
    [InlineData("UPDATE QGPL.DATA SET ID=.0.0", "SQL0104")]
    [InlineData("DELETE FROM QGPL.DATA; DROP TABLE QGPL.DATA", "SQL0104")]
    public void Binding_and_syntax_errors_are_reported_even_when_no_records_exist(string statement, string expected)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        Assert.Equal(expected, Assert.Throws<CpfException>(() => Run(statement)).MessageId);
        Assert.True(_files.FileExists("QGPL", "DATA")); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Defaults_COALESCE_integer_minimums_and_fixed_character_padding_are_typed()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER, BIG BIGINT, NAME CHAR(4), OTHER VARCHAR(8), VALUE INTEGER DEFAULT 7)");
        Run("INSERT INTO QGPL.DATA VALUES(1,1,'A',NULL,NULL)");
        Assert.Equal(1, Run("UPDATE QGPL.DATA SET ID=-2147483648,BIG=-9223372036854775808,OTHER=NAME||'Z',VALUE=DEFAULT WHERE NAME='A '"));
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"));
        Assert.Equal((long)int.MinValue, row["ID"]); Assert.Equal(long.MinValue, row["BIG"]); Assert.Equal("A   Z", row["OTHER"]); Assert.Equal(7L, row["VALUE"]);
        Assert.Equal(1, Run("UPDATE QGPL.DATA SET VALUE=COALESCE(NULL,VALUE,1/0)"));
    }

    [Fact]
    public void DELETE_retains_sparse_RRN_extent_and_INSERT_reuses_the_deleted_slot()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3)");
        Assert.Equal(1, Run("DELETE FROM QGPL.DATA WHERE ID=2")); Run("INSERT INTO QGPL.DATA VALUES(4)");
        Assert.Equal(new long[] { 1, 4, 3 }, _files.ReadAll("QGPL", "DATA", "DATA").Select(row => (long)row["ID"]!));
        Assert.Equal(3, Run("DELETE FROM QGPL.DATA")); Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }

    [Fact]
    public void Searched_mutations_require_READ_only_when_expressions_reference_columns()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1)");
        _system.Security.Profiles.Create(new() { Name = "SQLWRITER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SQLWRITER"), "SqlMutationWriterFixture55");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SQLWRITER", AuthorityBit.ObjectOperate | AuthorityBit.Update | AuthorityBit.Delete);
        var job = _system.Jobs.CreateInteractive("SQLWRITER");
        using (OperationIdentity.Enter("SQLWRITER", job.Key))
        {
            Assert.Equal(1, Run("UPDATE QGPL.DATA SET ID=2"));
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET ID=ID+1")).MessageId);
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL.DATA WHERE ID=2")).MessageId);
            Assert.Equal(1, Run("DELETE FROM QGPL.DATA"));
        }
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Fact]
    public void A_locked_selected_record_rejects_the_whole_mutation_but_unselected_records_do_not_block()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2),(3)");
        var a = _system.Jobs.CreateInteractive("QSECOFR"); var b = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections);
        using (locks.Acquire(a.Key, new("QGPL", "DATA", ObjectType.File, "DATA", 2), JobLockMode.Exclusive, TimeSpan.Zero))
        using (OperationIdentity.Enter("QSECOFR", b.Key))
        {
            Assert.Equal(1, Run("UPDATE QGPL.DATA SET ID=10 WHERE ID=1"));
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET ID=ID+1")).MessageId);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL.DATA")).MessageId);
        }
        Assert.Equal(new long[] { 10, 2, 3 }, _files.ReadAll("QGPL", "DATA", "DATA").Select(row => (long)row["ID"]!));
        Assert.Empty(locks.Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Fact]
    public void Left_associative_expression_depth_is_bounded_before_execution()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET ID=" + string.Join('+', Enumerable.Repeat("1", 300)))).MessageId);
    }

    [Fact]
    public void Successful_mutations_capture_exact_before_after_journal_images_and_preserve_extents()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, VALUE INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1,5),(2,6),(3,7)");
        _system.Journals.CreateReceiver(new("QGPL", "SQLRCV")); _system.Journals.Create(new("QGPL", "SQLJRN"), new("QGPL", "SQLRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "SQLJRN"), "*BOTH");
        Assert.Equal(2, Run("UPDATE QGPL.DATA SET VALUE=VALUE+1 WHERE ID IN(1,3)"));
        Assert.Equal(1, Run("DELETE FROM QGPL.DATA WHERE ID=2"));
        Assert.Equal(new[] { "UB", "UP", "UB", "UP", "DL" }, _system.Journals.Read(new("QGPL", "SQLJRN")).Select(entry => entry.Type));
        Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_trigger_failure_after_an_earlier_record_rolls_back_the_whole_mutation(bool update)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL.DATA VALUES(1),(2)");
        Scalar("CREATE TRIGGER reject_second BEFORE " + (update ? "UPDATE" : "DELETE") + " ON \"QGPL.DATA.DATA\" WHEN OLD.ID=2 BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Equal("SQL0901", Assert.Throws<CpfException>(() => Run(update ? "UPDATE QGPL.DATA SET ID=ID+10" : "DELETE FROM QGPL.DATA")).MessageId);
        Assert.Equal(new long[] { 1, 2 }, _files.ReadAll("QGPL", "DATA", "DATA").Select(row => (long)row["ID"]!));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(1)")).MessageId);
    }

    [Fact]
    public void Updating_a_referenced_unique_key_rejects_before_dropping_any_index()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER NOT NULL PRIMARY KEY, VALUE INTEGER)"); Run("INSERT INTO QGPL.DATA VALUES(1,1)");
        Scalar("CREATE UNIQUE INDEX native_parent_key ON \"QGPL.DATA.DATA\"(\"ID\")");
        Scalar("CREATE TABLE child(ID INTEGER REFERENCES \"QGPL.DATA.DATA\"(ID))"); Scalar("INSERT INTO child VALUES(1)");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET ID=ID+1")).MessageId);
        Assert.Equal(1, Run("UPDATE QGPL.DATA SET VALUE=VALUE+1"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='native_parent_key'"));
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }

    [Fact]
    public void Oversized_numeric_expressions_reject_before_unbounded_numeric_conversion()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        Assert.Equal("SQL0802", Assert.Throws<CpfException>(() => Run("UPDATE QGPL.DATA SET ID=" + new string('9', 100000))).MessageId);
    }

    [Fact]
    public void One_SQL_statement_updates_more_records_than_the_normal_holder_budget()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        Scalar("WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<9000) INSERT INTO \"QGPL.DATA.DATA\"(ID) SELECT x FROM n");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Equal(9000, Run("UPDATE QGPL.DATA SET ID=ID+1"));
        Assert.Equal(2L, Scalar("SELECT min(ID) FROM \"QGPL.DATA.DATA\""));
        Assert.Equal(9001L, Scalar("SELECT max(ID) FROM \"QGPL.DATA.DATA\""));
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }
}
