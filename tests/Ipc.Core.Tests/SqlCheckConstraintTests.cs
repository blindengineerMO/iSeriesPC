using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlCheckConstraintTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options(int ccsid = 37) => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"], ccsid);
    public SqlCheckConstraintTests() { _system.Start(); _files = new(_system.Connections, _system.Objects); }
    public void Dispose() => _system.Dispose();
    private long Run(string sql, int ccsid = 37) => _files.ExecuteSql(sql, Options(ccsid));
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    [Fact]
    public void Column_and_table_checks_allow_UNKNOWN_and_reject_false_for_inserts_and_updates()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER CONSTRAINT POS CHECK(A>0), B INTEGER,CONSTRAINT PAIR CHECK(A<B))");
        Assert.Equal(2, _files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks!.Count);
        Run("INSERT INTO QGPL/DATA VALUES(NULL,0),(1,2)");
        var failure = Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(-1,2)"));
        Assert.Equal("SQL0545", failure.MessageId); Assert.Equal("23513", failure.SqlState);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/DATA SET B=0")).MessageId);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"QGPL.DATA.DATA\"")); Assert.Equal(2L, Scalar("SELECT B FROM \"QGPL.DATA.DATA\" WHERE A=1"));
        Assert.Equal(2L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
    }
    [Fact]
    public void Checks_use_exact_decimal_and_padded_CCSID_comparison_rules()
    {
        Run("CREATE TABLE QGPL/DATA(V DECIMAL(29,2),CODE CHAR(4),CHECK(V>10000000000000000000000000.00 AND CODE='A'))");
        Run("INSERT INTO QGPL/DATA VALUES(10000000000000000000000000.01,'A')");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(10000000000000000000000000.00,'A')")).MessageId);
        Run("CREATE TABLE QGPL/TEXTDATA(VALUE VARCHAR(12),CHECK(CHAR_LENGTH(VALUE)=2))", 1208);
        Run("INSERT INTO QGPL/TEXTDATA VALUES('東京')", 1208);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/TEXTDATA VALUES('A')", 1208)).MessageId);
    }
    [Theory]
    [InlineData("A INTEGER CHECK(B>0),B INTEGER")]
    [InlineData("A INTEGER,CHECK(MISSING>0)")]
    [InlineData("A INTEGER,CHECK(CURRENT_DATE IS NOT NULL)")]
    [InlineData("A INTEGER,CHECK(COUNT(A)>0)")]
    [InlineData("A INTEGER,CHECK(A>?)")]
    [InlineData("A INTEGER,CHECK(RAND()>0)")]
    [InlineData("A INTEGER,CHECK(A+1)")]
    public void Invalid_check_definitions_never_publish_an_object(string fields)
    {
        Assert.Equal("SQL0546", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL/DATA(" + fields + ")")).MessageId);
        Assert.False(_files.FileExists("QGPL", "DATA"));
    }
    [Fact]
    public void ALTER_checks_existing_rows_and_rolls_back_before_replacing_the_definition()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER,B INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(1,2),(-1,2)");
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(A>0)")).MessageId);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Run("DELETE FROM QGPL/DATA WHERE A<0"); Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(A>0) ADD CONSTRAINT PAIR CHECK(A<B)");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/DATA SET A=3")).MessageId);
        Run("ALTER TABLE QGPL/DATA DROP CHECK PAIR DROP CONSTRAINT POS"); Run("UPDATE QGPL/DATA SET A=-1");
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks);
    }
    [Fact]
    public void Expression_errors_preserve_the_original_SQL_diagnostic()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER,CHECK(10/A>0))");
        var error = Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(0)"));
        Assert.Equal("SQL0802", error.MessageId); Assert.Equal("22012", error.SqlState);
    }
    [Fact]
    public void Checks_with_more_than_127_referenced_columns_use_bounded_row_chunks()
    {
        var names = Enumerable.Range(0,130).Select(index => "C" + index).ToArray();
        Run("CREATE TABLE QGPL/DATA(" + string.Join(',', names.Select(name => name + " INTEGER" + (name == "C129" ? " DEFAULT 1" : ""))) + ",CHECK(COALESCE(" + string.Join(',', names) + ")>0))");
        Run("INSERT INTO QGPL/DATA DEFAULT VALUES");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA(C129) VALUES(-1)")).MessageId);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void New_connections_enforce_checks_without_constructing_a_file_store()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER,CHECK(A>0))");
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO \"QGPL.DATA.DATA\" VALUES(-1)";
        Assert.Equal(275, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteExtendedErrorCode);
    }
}
