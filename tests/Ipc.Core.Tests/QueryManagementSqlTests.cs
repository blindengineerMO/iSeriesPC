using Ipc.Core.Messages;
using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed class QueryManagementSqlTests
{
    private static string Records(string source, int ccsid = 37) => QueryManagementSql.SourceRecords(source, ccsid);
    [Fact]
    public void Variables_are_case_sensitive_non_recursive_and_not_expanded_in_quotes_or_comments()
    {
        var source = Records("SELECT '&NO',\"&NAME\",&X,&x FROM &LIB/&FILE -- &IGNORED\nWHERE ID=&X /* &BLOCK */");
        Assert.Equal(new[] { "X", "x", "LIB", "FILE" }, QueryManagementSql.Variables(source));
        var sql = QueryManagementSql.Substitute(source, new Dictionary<string, string> { ["X"] = "1", ["x"] = "'&X'", ["LIB"] = "QGPL", ["FILE"] = "DATA" }, 37);
        Assert.Contains("SELECT '&NO',\"&NAME\",1,'&X' FROM QGPL/DATA", sql);
        Assert.Contains("WHERE ID=1", sql); Assert.DoesNotContain("IGNORED", sql); Assert.DoesNotContain("BLOCK", sql);
    }
    [Fact]
    public void Adjacent_variables_and_native_column_79_continuations_do_not_insert_extra_blanks()
    {
        var line = "SELECT ".PadRight(77) + "&L";
        var source = Records(line + "\nIB/&T WHERE N=&D.&C");
        var values = new Dictionary<string, string> { ["LIB"] = "QGPL", ["T"] = "DATA", ["D"] = "10", ["C"] = "50" };
        Assert.Contains("QGPL/DATA WHERE N=10.50", QueryManagementSql.Substitute(source, values, 37));
    }
    [Fact]
    public void Continued_quoted_values_retain_native_record_padding_and_escaped_quotes()
    {
        var source = Records("SELECT 'a\n''--&X' FROM DATA");
        var sql = QueryManagementSql.Substitute(source, new Dictionary<string, string>(), 37);
        Assert.Equal("SELECT 'a" + new string(' ', 70) + "''--&X' FROM DATA", sql);
        Assert.Empty(QueryManagementSql.Variables(source));
    }
    [Theory]
    [InlineData("&1")]
    [InlineData("&_")]
    [InlineData("&")]
    [InlineData("&ABCDEFGHIJKLMNOPQRSTUVWXYZABCDE")]
    public void Malformed_variable_names_fail_before_execution(string variable)
        => Assert.Equal("QWM2701", Assert.Throws<CpfException>(() => QueryManagementSql.Variables(Records("SELECT " + variable + " FROM DATA"))).MessageId);
    [Theory]
    [InlineData("SELECT 'unclosed")]
    [InlineData("SELECT \"unclosed")]
    [InlineData("SELECT /*unclosed")]
    public void Unclosed_query_source_is_diagnosed_before_substitution(string source)
        => Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => QueryManagementSql.Variables(Records(source))).MessageId);
    [Fact]
    public void Source_records_use_encoded_byte_width_and_substituted_queries_enforce_the_native_limit()
    {
        Assert.Equal(41, Records(new string('é', 39), 1208).Length);
        Assert.Equal("QWM2701", Assert.Throws<CpfException>(() => Records(new string('é', 40), 1208)).MessageId);
        Assert.Equal("SQL0332", Assert.Throws<CpfException>(() => Records("😀")).MessageId);
        var source = Records(string.Join('\n', Enumerable.Repeat("'" + new string('x', 70) + "'", 500)));
        Assert.Equal("SQL0904", Assert.Throws<CpfException>(() => QueryManagementSql.Substitute(source, new Dictionary<string, string>(), 37)).MessageId);
    }
    [Fact]
    public void Missing_variables_reserved_names_and_oversized_values_reject()
    {
        var source = Records("SELECT &X FROM DATA");
        Assert.Equal("QWM2701", Assert.Throws<CpfException>(() => QueryManagementSql.Substitute(source, new Dictionary<string, string>(), 37)).MessageId);
        Assert.Equal("QWM2701", Assert.Throws<CpfException>(() => QueryManagementSql.Substitute(source, new Dictionary<string, string> { ["X"] = new('x', 56) }, 37)).MessageId);
        Assert.Equal("QWM2701", Assert.Throws<CpfException>(() => QueryManagementSql.Substitute(source, new Dictionary<string, string> { ["DSQUSER"] = "x" }, 37)).MessageId);
    }
}
