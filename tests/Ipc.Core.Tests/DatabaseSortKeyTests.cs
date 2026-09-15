using System.Text;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class DatabaseSortKeyTests
{
    [Theory]
    [InlineData("0", "1", '0', "00")]
    [InlineData("0.01", "1", '0', "01")]
    [InlineData("-0.01", "0", '9', "98")]
    public void Decimal_keys_match_independent_fixed_width_fixtures(string value, string sign, char integerDigit, string cents)
    {
        var expected = sign + new string(integerDigit, 29) + cents + new string(integerDigit, 26);
        Assert.Equal(Encoding.ASCII.GetBytes(expected), DatabaseSortKeys.Decimal(value));
    }

    [Fact]
    public void Numeric_equivalents_and_extremes_have_exact_order()
    {
        Assert.Equal(DatabaseSortKeys.Decimal("0"), DatabaseSortKeys.Decimal("-0.000"));
        Assert.Equal(DatabaseSortKeys.Decimal("1.2"), DatabaseSortKeys.Decimal("+0001.200"));
        string[] values = ["-79228162514264337593543950335", "-100", "-0.01", "0", "0.0000000000000000000000000001", "0.01", "100", "79228162514264337593543950335"];
        var keys = values.Select(v => Convert.ToHexString(DatabaseSortKeys.Decimal(v)!)).ToArray();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1e2")]
    public void Unsupported_precision_and_notation_are_rejected(string value) => Assert.Throws<ArgumentException>(() => DatabaseSortKeys.Decimal(value));

    [Fact]
    public void Character_fixtures_preserve_CCSID_bytes_padding_and_byte_width()
    {
        Assert.Equal(new byte[] { 0x81, 0xC1, 0xF9, 0x40 }, DatabaseSortKeys.Text("aA9", 37, 4));
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x20 }, DatabaseSortKeys.Text("é", 1208, 3));
        Assert.Throws<ArgumentException>(() => DatabaseSortKeys.Text("é", 1208, 1));
        Assert.Throws<ArgumentException>(() => DatabaseSortKeys.Text("😀", 37, 4));
        Assert.Null(DatabaseSortKeys.Text(null, 37, 4));
        Assert.Null(DatabaseSortKeys.Decimal(null));
    }
}
