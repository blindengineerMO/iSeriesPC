using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class DateTimeApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public DateTimeApiTests() => _system.Start();
    private sealed class Cell
    {
        internal ProgramBuffer Buffer;
        internal ProgramArgument Argument;
        internal byte[] Bytes => Buffer.ToArray();
        internal Cell(int size = 40, int ccsid = 37, Func<ProgramBuffer, bool>? accept = null)
        {
            Buffer = new(Enumerable.Repeat((byte)0xFE, size).ToArray(), ccsid);
            Argument = new(() => Buffer, v => Buffer = (ProgramBuffer)v!, v => v is ProgramBuffer b && (accept?.Invoke(b) ?? true) ? b : throw new CpfException("CPF3C3A", "Rejected output."), "*CHAR", size, buffer: () => Buffer);
        }
    }
    private static ProgramBuffer Raw(string hex, int ccsid = 37) => new(global::System.Convert.FromHexString(hex), ccsid);
    private static ProgramBuffer Zero(int ccsid = 37) => new(new byte[4], ccsid);
    private static string Text(byte[] bytes, int count, int ccsid = 37) => new ProgramBuffer(bytes.AsSpan(0, count), ccsid).ToText();
    private byte[] Convert(string inputFormat, object? input, string outputFormat, int ccsid = 37, string precision = "0",
        string inputZone = "*SYS", string outputZone = "*SYS", string segment = "1", Func<DateTimeOffset>? clock = null, TimeZoneInfo? zone = null)
    {
        var output = new Cell(ccsid: ccsid);
        new DateTimeApi(_system.Connections, ccsid, clock: clock, systemZone: zone).Convert(
            [inputFormat, input, outputFormat, output.Argument, Zero(ccsid), inputZone, outputZone, "unused", 0, precision, segment]);
        return output.Bytes;
    }
    [Theory]
    [InlineData("0000000000000000", "19280823120306314752", "19280823120306314752")]
    [InlineData("4A2FEC4C82000000", "19700101000000000000", "19700101000000000000")]
    [InlineData("8000000000000000", "20000101000000000000", "20000101000000000000")]
    [InlineData("DFFFFFFFFFFF8000", "20530707205740263928", "20530707205740263928")]
    [InlineData("EFFFFFFFFFFFF000", "20620608162716974591", "20620608162716974584")]
    [InlineData("FFFFFFFFFFFFF000", "20710510115653685247", "20710510115653685240")]
    public void IBM_MI_reference_vectors_preserve_full_precision_while_API_uses_native_eight_microsecond_conversion(string raw, string standard, string expected)
    {
        var date = NativeTimestamp.Decode(global::System.Convert.FromHexString(raw));
        Assert.Equal(standard, date.ToString("yyyyMMddHHmmssffffff", global::System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(raw, global::System.Convert.ToHexString(NativeTimestamp.Encode(date)));
        Assert.Equal(expected, Text(Convert("*DTS", Raw(raw), "*YYMD", precision: "1"), 20));
        var bytes = global::System.Convert.FromHexString(raw); bytes[7] |= 0xFF;
        Assert.Equal(expected, Text(Convert("*DTS", new ProgramBuffer(bytes, 37), "*YYMD", precision: "1"), 20));
    }
    [Theory]
    [InlineData("*YMD", "1240229")]
    [InlineData("*MDY", "1022924")]
    [InlineData("*DMY", "1290224")]
    [InlineData("*JUL", "124060 ")]
    [InlineData("*YYMD", "20240229")]
    [InlineData("*MDYY", "02292024")]
    [InlineData("*DMYY", "29022024")]
    [InlineData("*LONGJUL", "2024060 ")]
    public void All_character_layouts_convert_to_each_other_and_preserve_time_without_validation(string format, string prefix)
    {
        var input = prefix + "BADTIMxxx";
        var outputFormat = format == "*YYMD" ? "*YMD" : "*YYMD";
        var expected = (outputFormat == "*YMD" ? "1240229" : "20240229") + "BADTIMxxx";
        Assert.Equal(expected, Text(Convert(format, input, outputFormat), expected.Length));
        var sourceFormat = outputFormat; var source = (sourceFormat == "*YMD" ? "1240229" : "20240229") + "235958123";
        Assert.Equal(prefix + "235958123", Text(Convert(sourceFormat, source, format), prefix.Length + 9));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Millisecond_and_microsecond_vectors_preserve_tails_and_known_timestamp_bytes(int ccsid)
    {
        var raw = Convert("*YYMD", "20000101000000000000", "*DTS", ccsid, "1");
        Assert.Equal("8000000000000000", global::System.Convert.ToHexString(raw.AsSpan(0, 8))); Assert.All(raw[8..], b => Assert.Equal(0xFE, b));
        Assert.Equal("1000101000000000", Text(Convert("*DTS", Raw("8000000000000000", ccsid), "*YMD", ccsid), 16, ccsid));
        Assert.Equal("20000101000000000", Text(Convert("*DTS", Raw("8000000000000000", ccsid), "*YYMD", ccsid), 17, ccsid));
        var micros = Convert("*YYMD", "20000101000000123457", "*DTS", ccsid, "1");
        Assert.Equal("800000001E240000", global::System.Convert.ToHexString(micros.AsSpan(0, 8)));
        Assert.Equal("20000101000000123456", Text(Convert("*DTS", new ProgramBuffer(micros, ccsid), "*YYMD", ccsid, "1"), 20, ccsid));
        Assert.Equal("19280823120306315", Text(Convert("*DTS", Raw("0000000000000000", ccsid), "*YYMD", ccsid), 17, ccsid));
    }
    [Fact]
    public void Identical_formats_copy_arbitrary_bytes_and_character_conversion_keeps_raw_time_and_century()
    {
        var input = Raw("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
        Assert.Equal(input.ToArray(), Convert("*YMD", input, "*YMD")[..16]);
        var bytes = global::System.Text.Encoding.UTF8.GetBytes("1240229").Concat(Enumerable.Repeat((byte)0xFF, 9)).ToArray(); bytes[0] = 0xFF;
        var converted = Convert("*YMD", new ProgramBuffer(bytes, 1208), "*DMY", 1208);
        Assert.Equal(0xFF, converted[0]); Assert.Equal("290224", Text(converted[1..], 6, 1208)); Assert.All(converted[7..16], b => Assert.Equal(0xFF, b));
    }
    [Theory]
    [InlineData("*YYMD", "20230229000000000", "*YMD", "CPF1060")]
    [InlineData("*YYMD", "00000229000000000", "*YMD", "CPF1060")]
    [InlineData("*LONGJUL", "2023366 000000000", "*YYMD", "CPF1060")]
    [InlineData("*LONGJUL", "2024000 000000000", "*YYMD", "CPF1060")]
    [InlineData("*LONGJUL", "2024060X000000000", "*YYMD", "CPF1060")]
    [InlineData("*YMD", "X240229000000000", "*YYMD", "CPF1848")]
    [InlineData("*YYMD", "20240229240000000", "*DTS", "CPF1061")]
    [InlineData("*YYMD", "20240229235960000", "*DTS", "CPF1061")]
    [InlineData("*YYMD", "20240229235959xxx", "*DTS", "CPF1849")]
    [InlineData("*YYMD", "18000101000000000", "*YMD", "CPF1060")]
    [InlineData("*YYMD", "19280823000000000", "*DTS", "CPF1060")]
    [InlineData("*YYMD", "20710510000000000", "*DTS", "CPF1060")]
    public void Invalid_fields_use_native_exception_ids(string format, string input, string output, string expected)
        => Assert.Equal(expected, Assert.Throws<CpfException>(() => Convert(format, input, output)).MessageId);
    [Fact]
    public void Four_digit_dates_support_the_full_Gregorian_range_and_two_digit_dates_wrap_only_for_DTS()
    {
        Assert.Equal("01010001000000000", Text(Convert("*YYMD", "00010101000000000", "*MDYY"), 17));
        Assert.Equal("31129999235959999", Text(Convert("*YYMD", "99991231235959999", "*DMYY"), 17));
        var dts = Convert("*YMD", "0000101000000000", "*DTS");
        var date = NativeTimestamp.Decode(dts); Assert.InRange(date, new DateTime(1928, 8, 23), new DateTime(2071, 5, 11));
        Assert.Equal(new DateTime(1900, 1, 1).Ticks + (1L << 52) * 10, date.Ticks);
    }
    [Theory]
    [InlineData("1", "20241103063000000")]
    [InlineData("0", "20241103073000000")]
    public void DST_repeated_times_select_first_or_second_segment(string segment, string expected)
        => Assert.Equal(expected, Text(Convert("*YYMD", "20241103013000000", "*YYMD", inputZone: "*SYS", outputZone: "*UTC", segment: segment,
            zone: TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")), 17));
    [Fact]
    public void Invalid_DST_times_and_zone_conversion_ranges_are_rejected()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        Assert.Equal("CPF1060", Assert.Throws<CpfException>(() => Convert("*YYMD", "20240310023000000", "*YYMD", inputZone: "*SYS", outputZone: "*UTC", zone: zone)).MessageId);
        Assert.Equal("CPF1060", Assert.Throws<CpfException>(() => Convert("*YYMD", "19280824000000000", "*YYMD", inputZone: "*SYS", outputZone: "*UTC", zone: zone)).MessageId);
        Assert.Equal("20240310030000000", Text(Convert("*YYMD", "20240310080000000", "*YYMD", inputZone: "*UTC", outputZone: "*SYS", zone: zone), 17));
    }
    [Fact]
    public void Current_time_ignores_input_and_captures_clock_once_in_the_selected_zone_with_DOS_binary_fields()
    {
        var calls = 0; DateTimeOffset Clock() { calls++; return DateTimeOffset.Parse("2024-11-03T06:30:45.1200000Z"); }
        var output = Convert("*CURRENT", null, "*DOS", inputZone: "IGNORED", outputZone: "*SYS", clock: Clock, zone: TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));
        Assert.Equal(1, calls); Assert.Equal("011E2D0C030B07E8012C00", global::System.Convert.ToHexString(output.AsSpan(0, 11))); Assert.All(output[11..], b => Assert.Equal(0xFE, b));
    }
    [Fact]
    public void System_format_observes_other_connections_and_does_not_reuse_a_stale_default()
    {
        var values = new SystemValueStore(_system.Connections); values.Set("QDATFMT", "*MDY");
        Assert.Equal("20240229000000000", Text(Convert("*SYSVAL", "1022924000000000", "*YYMD"), 17));
        values.Set("QDATFMT", "*JUL"); Assert.Equal("124060 000000000", Text(Convert("*YYMD", "20240229000000000", "*SYSVAL"), 16));
    }
    [Fact]
    public void Millisecond_rounding_crosses_year_and_DST_boundaries_on_the_instant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var instant = DateTimeOffset.Parse("2024-03-10T07:59:59.9999999Z");
        Assert.Equal("20240310030000000", Text(Convert("*CURRENT", null, "*YYMD", outputZone: "*SYS", clock: () => instant, zone: zone), 17));
        var raw = NativeTimestamp.Encode(instant.UtcDateTime);
        Assert.Equal("20240310030000000", Text(Convert("*DTS", new ProgramBuffer(raw, 37), "*YYMD", inputZone: "*UTC", outputZone: "*SYS", zone: zone), 17));
        Assert.Equal("030000000A0307E8012C00", global::System.Convert.ToHexString(Convert("*CURRENT", null, "*DOS", outputZone: "*SYS", clock: () => instant, zone: zone).AsSpan(0, 11)));
        var yearEnd = NativeTimestamp.Encode(DateTime.Parse("1999-12-31T23:59:59.999999", global::System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1000101000000000", Text(Convert("*DTS", new ProgramBuffer(yearEnd, 37), "*YMD"), 16));
    }
    [Fact]
    public void UTC_information_is_zero_binary_blank_character_and_truncates_without_modifying_tails()
    {
        var output = new Cell(); var info = new Cell(132);
        new DateTimeApi(_system.Connections).Convert(["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero(), "*UTC", "*UTC", info.Argument, 116, "0"]);
        Assert.All(info.Bytes[..8], b => Assert.Equal(0, b)); Assert.All(info.Bytes.AsSpan(8, 12).ToArray(), b => Assert.Equal(0x40, b));
        Assert.All(info.Bytes.AsSpan(20, 4).ToArray(), b => Assert.Equal(0, b)); Assert.All(info.Bytes.AsSpan(24, 88).ToArray(), b => Assert.Equal(0x40, b)); Assert.All(info.Bytes[116..], b => Assert.Equal(0xFE, b));
    }
    [Fact]
    public void Rejected_information_and_late_cancellation_leave_all_output_unmodified()
    {
        var output = new Cell(); var info = new Cell(116, accept: _ => false);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new DateTimeApi(_system.Connections).Convert(
            ["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero(), "*UTC", "*UTC", info.Argument, 116, "0"])).MessageId);
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(info.Bytes, b => Assert.Equal(0xFE, b));
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => new DateTimeApi(_system.Connections, cancellationToken: stop.Token, clock: () => { stop.Cancel(); return DateTimeOffset.UtcNow; }).Convert(["*CURRENT", null, "*DTS", output.Argument, Zero()]));
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    public void Incomplete_groups_fail_before_receivers_are_read(int count)
        => Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => new DateTimeApi(_system.Connections).Convert(new object?[count])).MessageId);
    public void Dispose() => _system.Dispose();
}
