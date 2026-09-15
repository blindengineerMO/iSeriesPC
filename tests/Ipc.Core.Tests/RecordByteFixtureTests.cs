using System.Globalization;
using System.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Records;

namespace Ipc.Core.Tests;

public sealed class RecordByteFixtureTests
{
    [Theory]
    [InlineData(1, 0, "9", "9C")]
    [InlineData(2, 0, "12", "012C")]
    [InlineData(4, 0, "-1234", "01234D")]
    [InlineData(5, 2, "12.34", "01234C")]
    [InlineData(20, 0, "12345678901234567890", "012345678901234567890C")]
    [InlineData(29, 0, "79228162514264337593543950335", "79228162514264337593543950335C")]
    public void Packed_fields_match_independent_BCD_fixtures(int digits, int scale, string number, string hex)
    {
        var field = Field(FieldType.Packed, digits, scale); var codec = new RecordCodec();
        var expected = Convert.FromHexString(hex); var buffer = new byte[expected.Length];
        codec.WriteValue(buffer, field, decimal.Parse(number, CultureInfo.InvariantCulture)); Assert.Equal(expected, buffer);
        Assert.Equal(decimal.Parse(number, CultureInfo.InvariantCulture), codec.ReadValue(expected, field));
    }

    [Theory]
    [InlineData(2, "-32768", "8000")]
    [InlineData(2, "32767", "7FFF")]
    [InlineData(2, "-1", "FFFF")]
    [InlineData(4, "-2147483648", "80000000")]
    [InlineData(8, "-9223372036854775808", "8000000000000000")]
    public void Signed_binary_fields_match_twos_complement_fixtures(int width, string number, string hex)
    {
        var codec = new RecordCodec(); var field = Field(FieldType.Binary, width); var expected = Convert.FromHexString(hex); var buffer = new byte[width];
        var value = long.Parse(number, CultureInfo.InvariantCulture); codec.WriteValue(buffer, field, value);
        Assert.Equal(expected, buffer); Assert.Equal(value, codec.ReadValue(expected, field));
    }

    [Fact]
    public void Zoned_digits_and_signs_are_checked_independently()
    {
        var codec = new RecordCodec(); var field = Field(FieldType.Zoned, 4, 2); var expected = Convert.FromHexString("F1F2F3D4");
        var buffer = new byte[4]; codec.WriteValue(buffer, field, -12.34m); Assert.Equal(expected, buffer);
        Assert.Equal(-12.34m, codec.ReadValue(expected, field));
        Assert.Equal(12.34m, codec.ReadValue(Convert.FromHexString("F1F2F3C4"), field));
        Assert.Throws<FormatException>(() => codec.ReadValue(Convert.FromHexString("F1A2F3D4"), field));
        Assert.Throws<FormatException>(() => codec.ReadValue(Convert.FromHexString("F1F2FAD4"), field));
    }

    [Theory]
    [InlineData("01234F", 1234)]
    [InlineData("01234B", -1234)]
    [InlineData("01234A", 1234)]
    [InlineData("01234E", 1234)]
    public void Alternate_decimal_sign_nibbles_decode_correctly(string hex, int expected)
        => Assert.Equal((decimal)expected, new RecordCodec().ReadValue(Convert.FromHexString(hex), Field(FieldType.Packed, 4)));

    [Theory]
    [InlineData("11234C")]
    [InlineData("012A4C")]
    [InlineData("012349")]
    public void Invalid_packed_padding_digits_and_signs_are_rejected(string hex)
        => Assert.Throws<FormatException>(() => new RecordCodec().ReadValue(Convert.FromHexString(hex), Field(FieldType.Packed, 4)));

    [Fact]
    public void Overflow_and_malformed_values_fail_before_modifying_a_field()
    {
        var codec = new RecordCodec(); var buffer = new byte[] { 0xAA, 0xAA, 0xAA };
        Assert.Throws<OverflowException>(() => codec.WriteValue(buffer, Field(FieldType.Packed, 5, 2), 1.001m));
        Assert.Throws<OverflowException>(() => codec.WriteValue(buffer, Field(FieldType.Binary, 2), 32768));
        Assert.Throws<FormatException>(() => codec.WriteValue(buffer, Field(FieldType.Binary, 2), "bad"));
        Assert.Throws<ArgumentException>(() => codec.WriteValue(new byte[1], Field(FieldType.Binary, 2), 1));
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA }, buffer);
    }

    [Fact]
    public void CCSID_fixtures_validate_padding_utf8_boundaries_and_logical_bytes()
    {
        var codec = new RecordCodec(); var buffer = new byte[4];
        codec.WriteValue(buffer, Field(FieldType.Alpha, 4), "aA9"); Assert.Equal(Convert.FromHexString("81C1F940"), buffer);
        var utf8 = new FieldSpec { Name = "VALUE", Type = FieldType.Alpha, Length = 4, Position = 1, Ccsid = 1208 };
        codec.WriteValue(buffer, utf8, "é"); Assert.Equal(Convert.FromHexString("C3A92020"), buffer);
        Assert.Throws<DecoderFallbackException>(() => codec.ReadValue(Convert.FromHexString("C3FF2020"), utf8));
        Assert.Throws<OverflowException>(() => codec.WriteValue(buffer, utf8, "ééé"));
        var flag = new byte[1]; codec.WriteValue(flag, Field(FieldType.Logic, 1), true); Assert.Equal(new byte[] { 0xF1 }, flag);
        Assert.True((bool)codec.ReadValue(new byte[] { 0xF1 }, Field(FieldType.Logic, 1))!);
    }

    [Fact]
    public void Legacy_basic_date_bytes_validate_leap_days()
    {
        var codec = new RecordCodec(); var field = Field(FieldType.Date, 8); var expected = Convert.FromHexString("F2F0F2F4F0F2F2F9");
        var buffer = new byte[8]; var day = new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero);
        codec.WriteValue(buffer, field, day); Assert.Equal(expected, buffer); Assert.Equal(day, codec.ReadValue(expected, field));
        Assert.Throws<FormatException>(() => codec.ReadValue(Convert.FromHexString("F2F0F2F3F0F2F2F9"), field));
    }
    [Fact]
    public void Record_offsets_include_packed_storage_and_variable_length_prefixes()
    {
        var format = new RecordFormat { Name = "FIXTURE", Fields = new() {
            new() { Name = "PACKED", Type = FieldType.Packed, Length = 4 },
            new() { Name = "FIXED", Type = FieldType.Alpha, Length = 2 },
            new() { Name = "VARYING", Type = FieldType.Alpha, Length = 4, VariableLength = true, Ccsid = 1208 }
        } }; format.AssignPositions();
        Assert.Equal(new[] { 1, 4, 6 }, format.Fields.Select(f => f.Position)); Assert.Equal(11, format.RecordLength);
        var codec = new RecordCodec(); var bytes = codec.Encode(format, new Dictionary<string, object?> { ["PACKED"] = 1234m, ["FIXED"] = "AB", ["VARYING"] = "é " });
        Assert.Equal(Convert.FromHexString("01234CC1C20003C3A92020"), bytes);
        Assert.Equal("é ", codec.Decode(format, bytes)[2]);
        bytes[6] = 5; Assert.Throws<FormatException>(() => codec.Decode(format, bytes));
    }

    [Fact]
    public void Null_indicators_are_explicit_and_do_not_change_record_offsets()
    {
        var format = new RecordFormat { Name = "NULLREC", Fields = new() { new() { Name = "NUMBER", Type = FieldType.Packed, Length = 4, NullCapable = true } } }; format.AssignPositions();
        var codec = new RecordCodec(); var values = new Dictionary<string, object?> { ["NUMBER"] = null };
        Assert.Throws<ArgumentException>(() => codec.Encode(format, values));
        var bytes = codec.Encode(format, values, out var indicators);
        Assert.Equal(new short[] { -1 }, indicators); Assert.Equal(Convert.FromHexString("00000C"), bytes);
        Assert.Null(Assert.Single(codec.Decode(format, bytes, indicators)));
        Assert.Equal(0m, Assert.Single(codec.Decode(format, bytes)));
        Assert.Throws<ArgumentException>(() => codec.Decode(format, bytes, new short[] { 2 }));
    }

    [Fact]
    public void Legacy_catalog_layout_is_adapted_without_accepting_unknown_versions()
    {
        const string json = """{"name":"LEGACY","attribute":"Physical","formats":[{"name":"REC","recordLength":7,"fields":[{"name":"N","type":"Packed","length":5,"position":1},{"name":"A","type":"Alpha","length":2,"position":6}]}]}""";
        var definition = FileDefinition.FromJson(json);
        Assert.Equal(2, definition.PrimaryFormat.BufferLayoutVersion); Assert.Equal(5, definition.PrimaryFormat.RecordLength);
        Assert.Equal(4, definition.PrimaryFormat.Fields[1].Position);
        Assert.Throws<InvalidDataException>(() => FileDefinition.FromJson(definition.ToJson().Replace("\"bufferLayoutVersion\":2", "\"bufferLayoutVersion\":99", StringComparison.Ordinal)));
    }
    [Fact]
    public void ISO_datetime_external_buffers_match_EBCDIC_fixtures_and_preserve_microseconds()
    {
        var codec = new RecordCodec(); var buffer = new byte[26]; var stamp = new DateTimeOffset(2024, 2, 29, 14, 3, 5, TimeSpan.Zero).AddTicks(1234560);
        var expected = Convert.FromHexString("F2F0F2F460F0F260F2F960F1F44BF0F34BF0F54BF1F2F3F4F5F6");
        codec.WriteValue(buffer, Field(FieldType.Timestamp, 26), stamp); Assert.Equal(expected, buffer);
        Assert.Equal(stamp, codec.ReadValue(expected, Field(FieldType.Timestamp, 26)));
        Assert.Throws<OverflowException>(() => codec.WriteValue(buffer, Field(FieldType.Timestamp, 26), stamp.AddTicks(1)));
        var day = new byte[10]; codec.WriteValue(day, Field(FieldType.Date, 10), new DateOnly(2024, 2, 29));
        Assert.Equal(Convert.FromHexString("F2F0F2F460F0F260F2F9"), day);
        var time = new byte[8]; codec.WriteValue(time, Field(FieldType.Time, 8), new TimeOnly(14, 3, 5));
        Assert.Equal(Convert.FromHexString("F1F44BF0F34BF0F5"), time);
        Assert.Equal(new TimeSpan(14, 3, 5), codec.ReadValue(time, Field(FieldType.Time, 8)));
    }
    private static FieldSpec Field(FieldType type, int length, int decimals = 0) => new() { Name = "VALUE", Type = type, Length = length, Decimals = decimals, Position = 1 };
}
