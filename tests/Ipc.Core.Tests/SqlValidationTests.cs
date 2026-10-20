using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class SqlValidationTests : IDisposable
{
    private readonly IpcSystem _system=IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options=>new(SqlNaming.Sql,"QGPL","QGPL",["QGPL"]);
    public SqlValidationTests() { _system.Start();_files=new(_system.Connections,_system.Objects);_files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER,AMOUNT DECIMAL(12,2),D DATE,T TIME)",Options);_files.ExecuteSql("INSERT INTO QGPL.DATA(ID) VALUES(1)",Options); }
    public void Dispose()=>_system.Dispose();
    [Theory]
    [InlineData("CREATE TABLE QGPL.NEWFILE(ID INTEGER)")]
    [InlineData("DROP TABLE QGPL.DATA")]
    [InlineData("INSERT INTO QGPL.DATA(ID) VALUES(2)")]
    [InlineData("UPDATE QGPL.DATA SET ID=ID+1")]
    [InlineData("DELETE FROM QGPL.DATA")]
    [InlineData("SELECT 1/0 FROM QGPL.DATA")]
    public void Validation_prepares_implemented_statements_without_running_them(string sql)
    { _files.ValidateSql(sql,Options);Assert.False(_files.FileExists("QGPL","NEWFILE"));Assert.Equal(1,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(1L,Assert.Single(_files.ReadAll("QGPL","DATA","DATA"))["ID"]); }
    [Theory]
    [InlineData("SELECT BAD FROM QGPL.DATA","SQL0206")]
    [InlineData("UPDATE QGPL.DATA SET BAD=1","SQL0206")]
    [InlineData("INSERT INTO QGPL.DATA(BAD) VALUES(2)","SQL0206")]
    [InlineData("INSERT INTO QGPL.DATA(ID) VALUES('bad')","SQL0406")]
    [InlineData("DELETE FROM QGPL.MISSING","SQL0204")]
    public void Validation_binds_names_and_literal_types_before_any_execution(string sql,string id)
    { Assert.Equal(id,Assert.Throws<CpfException>(()=>_files.ValidateSql(sql,Options)).MessageId); }
    [Theory]
    [InlineData("*YMD","26/10/19","2026-10-19")]
    [InlineData("*DMY","19/10/26","2026-10-19")]
    [InlineData("*MDY","10/19/26","2026-10-19")]
    [InlineData("*JUL","26/292","2026-10-19")]
    public void Configured_date_inputs_normalize_in_insert_update_CAST_and_comparison(string format,string input,string expected)
    {
        var options=Options with { Conversion=new(format,"/") };
        _files.ExecuteSql("UPDATE QGPL.DATA SET D='"+input+"'",options);
        using var cursor=_files.OpenSqlQuery("SELECT CAST('"+input+"' AS DATE) FROM QGPL.DATA WHERE D='"+input+"'",options);
        Assert.True(cursor.Read());Assert.Equal(expected,cursor.Current![0]);cursor.Dispose();
        _files.ExecuteSql("INSERT INTO QGPL.DATA(ID,D) VALUES(2,'"+input+"')",options);Assert.Equal(2,_files.RowCount("QGPL","DATA","DATA"));
    }
    [Fact]
    public void Decimal_comma_literals_keep_list_separators_and_casts_unambiguous()
    {
        var options=Options with { Conversion=new(DecimalComma:true) };
        _files.ExecuteSql("UPDATE QGPL.DATA SET AMOUNT=1,25",options);
        using var cursor=_files.OpenSqlQuery("SELECT AMOUNT, CAST('2,50' AS DECIMAL(12, 2)), CAST('3,5' AS DOUBLE), CAST(1,25 AS VARCHAR(10)) FROM QGPL.DATA",options);
        Assert.True(cursor.Read());Assert.Equal("1.25",cursor.Current![0]!.ToString());Assert.Equal("2.50",cursor.Current[1]!.ToString());Assert.Equal(3.5d,cursor.Current[2]);Assert.Equal("1,25",cursor.Current[3]);cursor.Dispose();
        _files.ExecuteSql("INSERT INTO QGPL.DATA(ID,AMOUNT) VALUES(2, 4,50)",options);Assert.Equal(2,_files.RowCount("QGPL","DATA","DATA"));
    }
    [Theory]
    [InlineData(" 1.25 ",true,"1.25")]
    [InlineData("1.2.3",false,"")]
    [InlineData("+-1",false,"")]
    public void Numeric_character_casts_trim_spaces_and_reject_malformed_numbers(string text,bool valid,string expected)
    {
        if(!valid) { Assert.Equal("22018",Assert.Throws<CpfException>(()=> { using var query=_files.OpenSqlQuery("SELECT CAST('"+text+"' AS DECIMAL(12,2)) FROM QGPL.DATA",Options);query.Read(); }).SqlState);return; }
        using var cursor=_files.OpenSqlQuery("SELECT CAST('"+text+"' AS DECIMAL(12,2)) FROM QGPL.DATA",Options);Assert.True(cursor.Read());Assert.Equal(expected,cursor.Current![0]!.ToString());
    }
    [Fact]
    public void Locale_formats_apply_to_character_casts_of_dates_and_times()
    {
        var options=Options with { Conversion=new("*DMY","/","*USA",":") };
        using var cursor=_files.OpenSqlQuery("SELECT CAST(DATE '2026-10-19' AS VARCHAR(20)),CAST(TIME '13:30:00' AS VARCHAR(20)) FROM QGPL.DATA",options);
        Assert.True(cursor.Read());Assert.Equal("19/10/26",cursor.Current![0]);Assert.Equal("01:30 PM",cursor.Current[1]);
    }

    [Fact]
    public void Floating_character_casts_require_the_configured_decimal_point_and_preserve_signed_zero()
    {
        Assert.Equal("22018",Assert.Throws<CpfException>(()=> { using var invalid=_files.OpenSqlQuery("SELECT CAST('1,2' AS DOUBLE) FROM QGPL.DATA",Options);invalid.Read(); }).SqlState);
        using var cursor=_files.OpenSqlQuery("SELECT CAST('-0.0' AS DOUBLE) FROM QGPL.DATA",Options);Assert.True(cursor.Read());Assert.Equal(long.MinValue,BitConverter.DoubleToInt64Bits((double)cursor.Current![0]!));
    }
    [Fact]
    public void Floating_parameter_text_cannot_be_misread_as_grouped_digits()
    {
        Assert.Equal("07006",Assert.Throws<CpfException>(()=> { using var invalid=_files.OpenSqlQuery("SELECT ? FROM QGPL.DATA",Options,[new("1,2",new(SqlQueryKind.Double))]); }).SqlState);
        using var cursor=_files.OpenSqlQuery("SELECT ? FROM QGPL.DATA",Options,[new(new SqlDecimalValue("1.25",3,2),new(SqlQueryKind.Double))]);Assert.True(cursor.Read());Assert.Equal(1.25d,cursor.Current![0]);
    }

}
