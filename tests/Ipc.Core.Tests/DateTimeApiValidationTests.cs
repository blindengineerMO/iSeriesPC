using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Work;

namespace Ipc.Core.Tests;

public sealed partial class DateTimeApiTests
{
    [Theory]
    [InlineData(0, "*JOB", "CPF1850")]
    [InlineData(0, "*ISO", "CPF1850")]
    [InlineData(2, "*CURRENT", "CPF1850")]
    [InlineData(2, "*DOS", "CPF1850")]
    [InlineData(5, "*JOB", "CPF3C3C")]
    [InlineData(6, "QN0600CST2", "CPF3C3C")]
    [InlineData(9, "2", "CPF3C3C")]
    [InlineData(10, "2", "CPF3C3C")]
    public void Invalid_controls_report_to_native_error_storage_before_other_outputs(int index, string value, string expected)
    {
        var output = new Cell(); var info = new Cell(116); var error = new Cell(20); var storage = error.Bytes;
        BinaryPrimitives.WriteInt32BigEndian(storage, 16); error.Buffer = new(storage, 37);
        object?[] p = ["*YYMD", "20240229000000000", "*YMD", output.Argument, error.Argument, "*UTC", "*UTC", info.Argument, 116, "0", "1"];
        p[index] = value; new DateTimeApi(_system.Connections).Convert(p);
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(error.Bytes.AsSpan(4))); Assert.Equal(expected, Text(error.Bytes[8..], 7));
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(info.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(117)]
    public void Invalid_zone_lengths_preserve_output(int length)
    {
        var output = new Cell(); var info = new Cell(116);
        Assert.Equal("CPF3C3C", Assert.Throws<CpfException>(() => new DateTimeApi(_system.Connections).Convert(
            ["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero(), "*UTC", "*UTC", info.Argument, length, "0"])).MessageId);
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(info.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(19)]
    [InlineData(115)]
    [InlineData(132)]
    public void Time_zone_information_can_be_truncated_at_any_byte_and_leaves_tails(int length)
    {
        var output = new Cell(); var info = new Cell(140);
        new DateTimeApi(_system.Connections).Convert(["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero(), "*UTC", "*UTC", info.Argument, length, "0"]);
        var returned = Math.Min(length, 116); Assert.All(info.Bytes[returned..], b => Assert.Equal(0xFE, b));
        Assert.All(info.Bytes[..Math.Min(8, returned)], b => Assert.Equal(0, b));
    }
    [Fact]
    public void Receiver_constants_aliases_and_short_storage_are_rejected_before_writes()
    {
        var output = new Cell(8); var api = new DateTimeApi(_system.Connections);
        Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => api.Convert(["*CURRENT", null, "*DTS", output.Buffer, Zero()])).MessageId);
        Assert.Equal("CPF3C3C", Assert.Throws<CpfException>(() => api.Convert(["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero()])).MessageId);
        Assert.Equal("CPF3C3C", Assert.Throws<CpfException>(() => api.Convert(["*DTS", Raw("8000000000000000"), "*YMD", new Cell().Argument, Zero(), "*UTC", "*UTC", output.Argument, 116, "0"])).MessageId);
        var shared = new Cell(116);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => api.Convert(["*CURRENT", null, "*DTS", shared.Argument, Zero(), "*UTC", "*UTC", shared.Argument, 116, "0"])).MessageId);
        var bytes = shared.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); shared.Buffer = new(bytes, 37);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => api.Convert(["*CURRENT", null, "*DTS", shared.Argument, shared.Argument])).MessageId);
        Assert.Equal(bytes, shared.Bytes); Assert.All(output.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Fact]
    public void Invalid_UTF8_time_bytes_are_copied_until_a_conversion_requires_validation()
    {
        var source = global::System.Text.Encoding.UTF8.GetBytes("20240229").Concat(Enumerable.Repeat((byte)0xFF, 9)).ToArray();
        var output = Convert("*YYMD", new ProgramBuffer(source, 1208), "*YMD", 1208);
        Assert.All(output[7..16], b => Assert.Equal(0xFF, b));
        Assert.Equal("CPF1061", Assert.Throws<CpfException>(() => Convert("*YYMD", new ProgramBuffer(source, 1208), "*DTS", 1208)).MessageId);
    }
    [Fact]
    public void Same_zone_metadata_outside_native_range_is_blank_and_unbacked_host_metadata_is_explicit()
    {
        var output = new Cell(); var info = new Cell(116); var api = new DateTimeApi(_system.Connections);
        api.Convert(["*YYMD", "19000101000000000", "*YMD", output.Argument, Zero(), "*SYS", "*SYS", info.Argument, 116, "0"]);
        Assert.Equal("0000101000000000", Text(output.Bytes, 16)); Assert.All(info.Bytes[..8], b => Assert.Equal(0, b));
        var before = output.Bytes;
        Assert.Equal("CPF3C3C", Assert.Throws<CpfException>(() => api.Convert(["*YYMD", "20240229000000000", "*YMD", output.Argument, Zero(), "*SYS", "*SYS", info.Argument, 116, "0"])).MessageId);
        Assert.Equal(before, output.Bytes);
    }
}
