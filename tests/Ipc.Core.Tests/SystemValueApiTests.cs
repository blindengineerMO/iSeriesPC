using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SystemValueApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public SystemValueApiTests() { _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SystemValues27"); }
    private sealed class Cell
    {
        internal ProgramBuffer Buffer { get; private set; }
        internal ProgramArgument Argument { get; }
        internal byte[] Bytes => Buffer.ToArray();
        internal Cell(int length, int ccsid = 37, Func<ProgramBuffer, bool>? accept = null)
        {
            Buffer = new(Enumerable.Repeat((byte)0xAA, length).ToArray(), ccsid);
            Argument = new(() => Buffer, value => Buffer = (ProgramBuffer)value!, value => value is ProgramBuffer buffer && buffer.Length == length && buffer.Ccsid == ccsid && (accept?.Invoke(buffer) ?? true)
                ? buffer : throw new CpfException("CPF3C3A", "Receiver rejected output."), "*CHAR", length, buffer: () => Buffer);
        }
        internal int Int(int offset) => BinaryPrimitives.ReadInt32BigEndian(Bytes.AsSpan(offset));
        internal string Text(int offset, int length) => new ProgramBuffer(Bytes.AsSpan(offset, length), Buffer.Ccsid).ToText();
    }
    private static ProgramBuffer Zero(int ccsid = 37) => new(new byte[4], ccsid);
    private static string Names(params string[] names) => string.Concat(names.Select(name => name.PadRight(10)));
    private SystemValueApi Api(int ccsid = 37, Func<DateTimeOffset>? clock = null, CancellationToken token = default) => new(_system.Connections, ccsid, token, clock);
    private Cell Retrieve(string[] names, int ccsid = 37, int length = 2048, Func<DateTimeOffset>? clock = null)
    {
        var output = new Cell(length + 8, ccsid); Api(ccsid, clock).Retrieve([output.Argument, length, names.Length, Names(names), Zero(ccsid)]); return output;
    }
    private static Cell Error()
    {
        var result = new Cell(20); var bytes = result.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); result.Argument.Value = new ProgramBuffer(bytes, 37); return result;
    }

    [Theory]
    [InlineData(37, "D8C3C3E2C9C440404040C240")]
    [InlineData(1208, "514343534944202020204220")]
    public void Binary_value_receiver_matches_independent_native_bytes(int ccsid, string nameTypeStatus)
    {
        var output = Retrieve(["QCCSID"], ccsid, length: 28);
        Assert.Equal("0000000100000008" + nameTypeStatus + "0000000400000025", Convert.ToHexString(output.Bytes.AsSpan(0, 28)));
        Assert.All(output.Bytes[28..], b => Assert.Equal(0xAA, b));
    }

    [Theory]
    [InlineData(37, "D8E2C5C3E4D9C9E3E840C34000000002F4F0")]
    [InlineData(1208, "515345435552495459204320000000023430")]
    public void Security_level_is_character_data_despite_the_internal_numeric_setting(int ccsid, string table)
    {
        var output = Retrieve(["QSECURITY"], ccsid, 28);
        Assert.Equal("0000000100000008" + table, Convert.ToHexString(output.Bytes.AsSpan(0, 26)));
        Assert.All(output.Bytes[26..], b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void Mixed_entries_are_ordered_four_byte_aligned_and_preserve_tail_storage()
    {
        var output = Retrieve(["QSECURITY", "QPWDLVL", "QCCSID"], length: 80);
        Assert.Equal(3, output.Int(0)); Assert.Equal(16, output.Int(4)); Assert.Equal(36, output.Int(8)); Assert.Equal(56, output.Int(12));
        Assert.Equal("QSECURITY C ", output.Text(16, 12)); Assert.Equal(2, output.Int(28)); Assert.Equal("40", output.Text(32, 2));
        Assert.Equal("0000", Convert.ToHexString(output.Bytes.AsSpan(34, 2)));
        Assert.Equal("QPWDLVL   B ", output.Text(36, 12)); Assert.Equal(3, output.Int(52)); Assert.Equal(37, output.Int(72));
        Assert.All(output.Bytes[76..], b => Assert.Equal(0xAA, b));
    }

    [Theory]
    [InlineData(28, 1)]
    [InlineData(51, 1)]
    [InlineData(52, 2)]
    [InlineData(75, 2)]
    [InlineData(76, 3)]
    public void Only_complete_entries_fit_and_the_offset_directory_tracks_the_returned_count(int length, int count)
    {
        var output = Retrieve(["QCCSID", "QPWDLVL", "QPWDMINLEN"], length: length);
        Assert.Equal(count, output.Int(0)); Assert.Equal(4 + 4 * count, output.Int(4));
        Assert.All(output.Bytes[(4 + count * 24)..], b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void An_entry_that_does_not_fit_returns_only_zero_count_without_skipping_to_a_later_entry()
    {
        var output = Retrieve(["QALWUSRDMN", "QCCSID"], length: 28);
        Assert.Equal(0, output.Int(0)); Assert.All(output.Bytes[4..], b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Repeated_requests_keep_their_order_and_maximum_count_is_bounded()
    {
        var output = Retrieve(Enumerable.Repeat("QCCSID", 4096).ToArray(), length: 52);
        Assert.Equal(2, output.Int(0)); Assert.Equal("QCCSID", output.Text(output.Int(4), 10).TrimEnd());
        Assert.Equal("QCCSID", output.Text(output.Int(8), 10).TrimEnd());
    }

    [Theory]
    [InlineData("QDATE", "1240229")]
    [InlineData("QCENTURY", "1")]
    [InlineData("QYEAR", "24")]
    [InlineData("QMONTH", "02")]
    [InlineData("QDAY", "29 ")]
    [InlineData("QDAYOFWEEK", "*THU")]
    [InlineData("QHOUR", "23")]
    [InlineData("QMINUTE", "59")]
    [InlineData("QSECOND", "58")]
    [InlineData("QTIME", "235958123")]
    [InlineData("QDATETIME", "20240229235958123456")]
    public void Date_and_time_fields_share_one_local_clock_capture_and_native_precision(string name, string expected)
    {
        var calls = 0;
        var output = Retrieve([name], clock: () => { calls++; return DateTimeOffset.Parse("2024-02-29T23:59:58.1234567-06:00", global::System.Globalization.CultureInfo.InvariantCulture); });
        Assert.Equal(1, calls); var offset = output.Int(4); Assert.Equal(expected.Length, output.Int(offset + 12)); Assert.Equal(expected, output.Text(offset + 16, expected.Length));
    }
    [Fact]
    public void A_multi_value_read_uses_one_clock_snapshot_and_ignores_persisted_startup_dates()
    {
        new SystemValueStore(_system.Connections).Set("QDATE", "1900-01-01");
        new SystemValueStore(_system.Connections).Set("QTIME", "00:00:00");
        var calls = 0; var output = Retrieve(["QDATE", "QTIME", "QDATETIME"], clock: () => { calls++; return DateTimeOffset.Parse("2100-01-01T00:00:00.999999Z"); });
        Assert.Equal(1, calls); Assert.Equal("2000101", output.Text(output.Int(4) + 16, 7)); Assert.Equal("000000999", output.Text(output.Int(8) + 16, 9));
        Assert.Equal("21000101000000999999", output.Text(output.Int(12) + 16, 20));
    }
    [Fact]
    public void Julian_day_and_month_use_the_date_format_from_the_same_snapshot()
    {
        new SystemValueStore(_system.Connections).Set("QDATFMT", "JUL");
        var output = Retrieve(["QMONTH", "QDAY"], clock: () => DateTimeOffset.Parse("2024-12-31T12:00:00Z"));
        Assert.Equal("  ", output.Text(output.Int(4) + 16, 2)); Assert.Equal("366", output.Text(output.Int(8) + 16, 3));
    }
    [Fact]
    public void Native_library_arrays_include_all_fixed_slots_and_policy_values_are_mapped()
    {
        _system.SetSystemValue("QUSRLIBL", "QGPL QTEMP"); _system.SetSystemValue("QALWUSRDMN", "*DIR");
        _system.SetSystemValue("QPWDRQDDGT", "*YES"); _system.SetSystemValue("QPWDRQDRPT", "*YES"); _system.SetSystemValue("QPWDEXPITV", "0");
        var output = Retrieve(["QUSRLIBL", "QSYSLIBL", "QALWUSRDMN", "QPWDRQDDGT", "QPWDLMTREP", "QPWDMAXLEN", "QPWDEXPITV"]);
        Assert.Equal(7, output.Int(0)); var offset = output.Int(4); Assert.Equal(250, output.Int(offset + 12));
        Assert.Equal("QGPL      QTEMP     " + new string(' ', 230), output.Text(offset + 16, 250));
        offset = output.Int(8); Assert.Equal(150, output.Int(offset + 12)); Assert.StartsWith("QSYS      QSYS2     ", output.Text(offset + 16, 150));
        offset = output.Int(12); Assert.Equal(500, output.Int(offset + 12)); Assert.Equal("*DIR" + new string(' ', 496), output.Text(offset + 16, 500));
        Assert.Equal("1", output.Text(output.Int(16) + 16, 1)); Assert.Equal("1", output.Text(output.Int(20) + 16, 1));
        Assert.Equal(128, output.Int(output.Int(24) + 16)); Assert.Equal("*NOMAX", output.Text(output.Int(28) + 16, 6));
    }
    [Theory]
    [InlineData("0", 10)]
    [InlineData("1", 10)]
    [InlineData("2", 128)]
    [InlineData("3", 128)]
    [InlineData("4", 128)]
    public void Password_level_and_maximum_length_come_from_the_same_policy_snapshot(string level, int maximum)
    {
        _system.SetSystemValue("QPWDLVL", level); var output = Retrieve(["QPWDLVL", "QPWDMAXLEN"]);
        Assert.Equal(int.Parse(level), output.Int(output.Int(4) + 16)); Assert.Equal(maximum, output.Int(output.Int(8) + 16));
    }
    [Fact]
    public void Encoding_or_stored_width_failures_do_not_return_misleading_truncated_values()
    {
        var store = new SystemValueStore(_system.Connections);
        store.Set("QPRTTXT", new string('A', 31)); Assert.Equal("CPF3C19", Assert.Throws<CpfException>(() => Retrieve(["QPRTTXT"])).MessageId);
        store.Set("QPRTTXT", "emoji 😀"); Assert.Equal("CPF3C19", Assert.Throws<CpfException>(() => Retrieve(["QPRTTXT"])).MessageId);
        var utf8 = Retrieve(["QPRTTXT"], 1208); var offset = utf8.Int(4); Assert.StartsWith("emoji 😀", utf8.Text(offset + 16, 30));
    }

    [Theory]
    [InlineData(-1, "QCCSID", "CPF1862")]
    [InlineData(0, "QCCSID", "CPF1862")]
    [InlineData(4097, "QCCSID", "CPF1862")]
    [InlineData(1, "*ALL", "CPF1860")]
    [InlineData(1, "QSYSNAME", "CPF1860")]
    [InlineData(1, "QAUDCTL", "CPF1860")]
    [InlineData(1, "QPWDRQDRPT", "CPF1860")]
    [InlineData(1, "qccsid", "CPF1860")]
    public void Invalid_or_unimplemented_requests_report_native_errors_without_writing_the_receiver(int count, string name, string expected)
    {
        var output = new Cell(100); var error = Error();
        Api().Retrieve([output.Argument, 100, count, Names(name), error.Argument]);
        Assert.Equal(16, error.Int(4)); Assert.Equal(expected, error.Text(8, 7)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(101)]
    public void Invalid_receiver_lengths_leave_all_storage_unchanged_except_the_error_receiver(int length)
    {
        var output = new Cell(100); var error = Error(); Api().Retrieve([output.Argument, length, 1, Names("QCCSID"), error.Argument]);
        Assert.Equal("CPF1861", error.Text(8, 7)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Every_requested_name_is_validated_even_when_a_later_entry_would_not_fit()
    {
        var output = new Cell(28); var error = Error(); Api().Retrieve([output.Argument, 28, 2, Names("QCCSID", "UNKNOWN"), error.Argument]);
        Assert.Equal("CPF1860", error.Text(8, 7)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Immutable_and_aliased_error_receivers_are_rejected_without_writes()
    {
        Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => Api().Retrieve([new ProgramBuffer(new byte[28], 37), 28, 1, Names("QCCSID"), Zero()])).MessageId);
        var cell = new Cell(100); var bytes = cell.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); cell.Argument.Value = new ProgramBuffer(bytes, 37);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Retrieve([cell.Argument, 100, 1, Names("QCCSID"), cell.Argument])).MessageId);
        Assert.Equal(bytes, cell.Bytes);
    }
    [Fact]
    public void Rejected_receiver_preparation_preserves_all_outputs_and_error_reports_the_failure()
    {
        var output = new Cell(100, accept: value => value.ToArray()[0] != 0); var error = Error();
        Api().Retrieve([output.Argument, 100, 1, Names("QCCSID"), error.Argument]);
        Assert.Equal("CPF3C3A", error.Text(8, 7)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Arity_is_checked_before_accessing_any_argument(int count)
        => Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Retrieve(new object?[count])).MessageId);
    [Fact]
    public void Cancellation_precedes_receiver_writes()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var output = new Cell(100);
        Assert.ThrowsAny<OperationCanceledException>(() => Api(token: stop.Token).Retrieve([output.Argument, 100, 1, Names("QCCSID"), Zero()]));
        Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Cancellation_during_snapshot_capture_leaves_both_outputs_unchanged()
    {
        using var stop = new CancellationTokenSource(); var output = new Cell(100); var error = Error(); var before = error.Bytes;
        Assert.ThrowsAny<OperationCanceledException>(() => Api(clock: () => { stop.Cancel(); return DateTimeOffset.Now; }, token: stop.Token)
            .Retrieve([output.Argument, 100, 1, Names("QCCSID"), error.Argument]));
        Assert.All(output.Bytes, b => Assert.Equal(0xAA, b)); Assert.Equal(before, error.Bytes);
    }
    [Fact]
    public void Names_use_fixed_input_storage_and_binary_parameters_reject_packed_declarations()
    {
        var output = new Cell(100);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Retrieve([output.Argument, 100, 1,
            new ProgramBuffer(CodePage.ToBytes(37, "QCCSID"), 37), Zero()])).MessageId);
        var packedCount = new ProgramConstant("*DEC", 5, 0, new([0x00, 0x00, 0x1C], 37), 1m);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Retrieve([output.Argument, 100, packedCount, Names("QCCSID"), Zero()])).MessageId);
        Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Every_published_name_has_a_real_backed_value_and_legacy_oversized_arrays_fail_without_truncation()
    {
        var output = Retrieve(SystemValueApi.SupportedNames.ToArray(), length: 4096); Assert.Equal(27, output.Int(0));
        new SystemValueStore(_system.Connections).Set("QUSRLIBL", string.Join(',', Enumerable.Range(1, 26).Select(n => "LIB" + n)));
        Assert.Equal("CPF3C19", Assert.Throws<CpfException>(() => Retrieve(["QUSRLIBL"])).MessageId);
    }
    public void Dispose() => _system.Dispose();
}
