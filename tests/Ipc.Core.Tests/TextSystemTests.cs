using Ipc.Core.Catalog;
using Ipc.Core.Messages;
using Ipc.Core.System;
using Ipc.Core.Text;

namespace Ipc.Core.Tests;

public class CodePageTests
{
    [Fact]
    public void Ebcdic_037_encodes_uppercase_A_as_c1()
    {
        var bytes = CodePage.ToBytes(CodePage.EbcdicUsCanada, "A");
        Assert.Equal(new byte[] { 0xC1 }, bytes);
    }

    [Theory]
    [InlineData("ABC123")]
    [InlineData("HELLO WORLD")]
    [InlineData("PRICE=1,234.56")]
    [InlineData("RPG & CL")]
    public void Ebcdic_round_trips_ascii_text(string text)
    {
        var bytes = CodePage.ToBytes(CodePage.EbcdicUsCanada, text);
        var decoded = CodePage.FromBytes(CodePage.EbcdicUsCanada, bytes, 0, bytes.Length);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void Ebcdic_500_round_trips()
    {
        var text = "café ß ü";
        var bytes = CodePage.ToBytes(CodePage.EbcdicInternational, text);
        Assert.Equal(text, CodePage.FromBytes(CodePage.EbcdicInternational, bytes, 0, bytes.Length));
    }

    [Fact]
    public void Utf8_ccsid_maps_to_utf8()
    {
        var bytes = CodePage.ToBytes(CodePage.Utf8, "♥");
        Assert.Equal("♥", CodePage.FromBytes(CodePage.Utf8, bytes, 0, bytes.Length));
    }

    [Fact]
    public void Unsupported_ccsid_is_rejected() =>
        Assert.False(CodePage.IsSupported(99999));

    [Fact]
    public void Stream_encode_pads_to_fixed_width()
    {
        var stream = new EbcdicTextStream(CodePage.EbcdicUsCanada);
        var bytes = stream.Encode("AB", 5);
        Assert.Equal(5, bytes.Length);
        Assert.Equal(0x40, bytes[^1]);
    }

    [Fact]
    public void Stream_encode_truncates_to_fixed_width()
    {
        var stream = new EbcdicTextStream(CodePage.EbcdicUsCanada);
        var bytes = stream.Encode("ABCDEFGH", 4);
        Assert.Equal(4, bytes.Length);
        Assert.Equal("ABCD", CodePage.FromBytes(CodePage.EbcdicUsCanada, bytes, 0, 4));
    }
}

public class QDateTests
{
    [Theory]
    [InlineData("*YMD", "230915", new[] { 2023, 9, 15 })]
    [InlineData("*DMY", "150923", new[] { 2023, 9, 15 })]
    [InlineData("*MDY", "091523", new[] { 2023, 9, 15 })]
    public void Short_formats_parse(string fmt, string text, int[] ymd)
    {
        var date = QDate.Parse(text, QDate.ParseFormat(fmt), QDateSeparator.Slash);
        Assert.Equal(new DateTimeOffset(new DateTime(ymd[0], ymd[1], ymd[2]), TimeSpan.Zero), date);
    }

    [Theory]
    [InlineData("*YMD", "/", "23/09/15")]
    [InlineData("*YMD", "-", "23-09-15")]
    [InlineData("*DMY", ".", "15.09.23")]
    [InlineData("*MDY", ",", "09,15,23")]
    public void Short_formats_render(string fmt, string sep, string expected)
    {
        var date = new DateTimeOffset(new DateTime(2023, 9, 15), TimeSpan.Zero);
        Assert.Equal(expected, QDate.Format(date, QDate.ParseFormat(fmt), QDate.ParseSeparator(sep)));
    }

    [Fact]
    public void Julian_round_trip()
    {
        var date = new DateTimeOffset(new DateTime(2023, 9, 15), TimeSpan.Zero);
        Assert.Equal("2023258", QDate.Format(date, QDateFormat.Julian, QDateSeparator.NoneApplied));
        Assert.Equal(date, QDate.Parse("2023258", QDateFormat.Julian, QDateSeparator.NoneApplied));
    }

    [Fact]
    public void Iso_round_trip()
    {
        var date = new DateTimeOffset(new DateTime(2023, 9, 15), TimeSpan.Zero);
        Assert.Equal("2023-09-15", QDate.Format(date, QDateFormat.Iso, QDateSeparator.NoneApplied));
        Assert.Equal(date, QDate.Parse("2023-09-15", QDateFormat.Iso, QDateSeparator.NoneApplied));
    }

    [Fact]
    public void Century_rule_maps_0_39_to_2000s()
    {
        var date = QDate.Parse("390915", QDateFormat.YmD, QDateSeparator.Slash);
        Assert.Equal(2039, date.Year);
        Assert.Equal(2015, QDate.Parse("150915", QDateFormat.YmD, QDateSeparator.Slash).Year);
        Assert.Equal(1990, QDate.Parse("900915", QDateFormat.YmD, QDateSeparator.Slash).Year);
    }

    [Fact]
    public void Invalid_input_returns_null_from_tryparse() =>
        Assert.Null(QDate.TryParse("not-a-date", QDateFormat.YmD, QDateSeparator.Slash));
}

public class SystemValueTests
{
    [Fact]
    public void Registry_has_core_defaults()
    {
        var registry = new SystemValueRegistry();
        Assert.Equal("40", registry.Get(SystemValueNames.SecurityLevel).Value);
        Assert.Equal("37", registry.Get(SystemValueNames.Ccsid).Value);
        Assert.Equal("*MAIN", registry.Get(SystemValueNames.InitialMenu).Value);
    }

    [Fact]
    public void Set_and_read_round_trip()
    {
        var registry = new SystemValueRegistry();
        registry.Set(SystemValueNames.SystemName, "MYSYS");
        Assert.Equal("MYSYS", registry.Get(SystemValueNames.SystemName).Value);
    }

    [Fact]
    public void Unknown_value_throws() =>
        Assert.Throws<KeyNotFoundException>(
            () => new SystemValueRegistry().Get("QNOTREAL"));
}

public class MessagingTests
{
    [Fact]
    public void Cpf_exception_carries_id_and_message()
    {
        var ex = new CpfException("CPF1234", "Something bad.");
        Assert.Equal("CPF1234", ex.MessageId);
        Assert.Contains("Something bad", ex.Message);
    }

    [Fact]
    public void Message_render_includes_id()
    {
        var msg = new IpcMessage { MessageId = "CPF0002", Text = "Failure", Type = IpcMessageType.Escape };
        Assert.StartsWith("CPF0002", msg.ToString());
    }
}

public class CatalogTests
{
    [Fact]
    public void Catalog_ddl_is_valid_sqlite()
    {
        var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        db.Open();
        foreach (var ddl in SystemCatalog.All)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        object? count;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master";
            count = cmd.ExecuteScalar();
        }

        Assert.NotNull(count);
        Assert.True(Convert.ToInt64(count) >= 6);
    }
}