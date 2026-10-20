using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests
{
    [Theory]
    [InlineData("SUBSTRING('abc' FROM 2 FOR 20)", "bc", SqlQueryKind.VarChar, 3)]
    [InlineData("SUBSTRING('abc',9,5)", "", SqlQueryKind.VarChar, 3)]
    [InlineData("SUBSTR('abc',2,5)", "bc   ", SqlQueryKind.Character, 5)]
    [InlineData("SUBSTR('abc',2)", "bc", SqlQueryKind.Character, 2)]
    public void Substring_padding_and_metadata_distinguish_byte_and_character_functions(string expression, string expected, SqlQueryKind kind, int length)
    {
        Seed(); var result = Query("SELECT " + expression + " FROM QGPL.DATA WHERE ID=1");
        Assert.Equal(expected, Assert.Single(result.Rows)[0]); Assert.Equal(kind, result.Columns[0].Type.Kind); Assert.Equal(length, result.Columns[0].Type.Length);
    }

    [Theory]
    [InlineData("SECOND(TIMESTAMP '2024-01-01 12:30:45.123456')", "45", SqlQueryKind.Integer, 0)]
    [InlineData("SECOND(TIMESTAMP '2024-01-01 12:30:45.123456',3)", "45.123", SqlQueryKind.Decimal, 3)]
    [InlineData("EXTRACT(SECOND FROM TIMESTAMP '2024-01-01 12:30:45.123456')", "45.123456", SqlQueryKind.Decimal, 6)]
    [InlineData("EXTRACT(SECOND FROM TIME '12:30:45')", "45.000000", SqlQueryKind.Decimal, 6)]
    [InlineData("HOUR(DATE '2024-01-01')", "0", SqlQueryKind.Integer, 0)]
    public void Datetime_extraction_distinguishes_SECOND_from_EXTRACT_fractional_results(string expression, string expected, SqlQueryKind kind, int scale)
    {
        Seed(); var result = Query("SELECT " + expression + " FROM QGPL.DATA WHERE ID=1");
        Assert.Equal(expected, Convert.ToString(Assert.Single(result.Rows)[0], global::System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(kind, result.Columns[0].Type.Kind); Assert.Equal(scale, result.Columns[0].Type.Scale);
    }
    [Fact]
    public void Typed_parameter_descriptors_validate_precision_CCSID_and_timestamp_shape()
    {
        Seed();
        Assert.Equal("07006", Assert.Throws<Ipc.Core.Messages.CpfException>(() => Query("SELECT ? FROM QGPL.DATA", new SqlQueryParameter(1m, new(SqlQueryKind.Decimal)))).SqlState);
        Assert.Equal("57017", Assert.Throws<Ipc.Core.Messages.CpfException>(() => Query("SELECT ? FROM QGPL.DATA", new SqlQueryParameter(null, new(SqlQueryKind.VarChar, Length: 1, Ccsid: 1252)))).SqlState);
        var result = Query("SELECT ?,EXTRACT(SECOND FROM ?) FROM QGPL.DATA WHERE ID=1", new SqlQueryParameter(new DateTime(2024,1,1,2,3,4), new(SqlQueryKind.Timestamp)), new SqlQueryParameter(new DateTime(2024,1,1,2,3,4), new(SqlQueryKind.Timestamp)));
        Assert.Equal("2024-01-01 02:03:04", Assert.Single(result.Rows)[0]); Assert.Equal("4", Assert.Single(result.Rows)[1]!.ToString()); Assert.Equal(0, result.Columns[1].Type.Scale);
    }

}
