using Ipc.Console.Session;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class NativePhysicalDdsTests
{
    internal static string Line(char level, string name = "", string length = "", char type = ' ', string decimals = "", string keywords = "")
    {
        var line = new string(' ', 44).ToCharArray(); line[5] = 'A'; line[16] = level;
        name.PadRight(10).CopyTo(0, line, 18, 10); length.PadLeft(5).CopyTo(0, line, 29, 5); line[34] = type;
        decimals.PadLeft(2).CopyTo(0, line, 35, 2); return new string(line) + keywords;
    }

    [Fact]
    public void Native_columns_types_keys_and_current_datetime_defaults_reach_real_file_IO()
    {
        var source = string.Join('\n', Line('R', "NATREC", keywords: "TEXT('Native record')"),
            Line(' ', "ID", "9", 'B', "0"), Line(' ', "AMOUNT", "7", decimals: "2"),
            Line(' ', "NAME", "20", 'A', keywords: "ALWNULL VARLEN CCSID(1208)"),
            Line(' ', "RATE", "17", 'F', "10", "FLTPCN(*DOUBLE)"),
            Line(' ', "STAMP", type: 'Z'), Line(' ', "DAY", type: 'L'), Line(' ', "CLOCK", type: 'T'),
            Line('K', "AMOUNT", keywords: "DESCEND"));
        var definition = new DdsCompiler().CompilePhysical("NATIVE", source, 1208, "DDS(NATIVE)");
        Assert.Equal(4, definition.PrimaryFormat.Fields[0].Length); Assert.Equal(9, definition.PrimaryFormat.Fields[0].DeclaredDigits);
        Assert.Equal(FieldType.Packed, definition.PrimaryFormat.Fields[1].Type); Assert.Equal(82, definition.PrimaryFormat.RecordLength);
        Assert.True(definition.PrimaryFormat.Fields[1].Descending);
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreatePhysicalFile("QGPL", "NATIVE", definition, source);
        var before = DateTimeOffset.UtcNow;
        files.Insert("QGPL", "NATIVE", "NATIVE", "NATREC", new Dictionary<string, object?> { ["ID"] = int.MaxValue, ["AMOUNT"] = 12.34m, ["RATE"] = 0.1d });
        var row = Assert.Single(files.ReadKeyed("QGPL", "NATIVE", "NATIVE")); Assert.Null(row["NAME"]);
        var stamp = Assert.IsType<DateTimeOffset>(row["STAMP"]); Assert.InRange(stamp, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        var codec = new RecordCodec(1208); var bytes = codec.Encode(definition.PrimaryFormat, row, out var nulls);
        Assert.Equal(-1, nulls[2]); Assert.Equal(82, bytes.Length);
        Assert.Equal(12.34m, codec.Decode(definition.PrimaryFormat, bytes, nulls)[1]);
    }

    [Fact]
    public void CRTPF_uses_the_target_name_as_default_source_member_and_reports_source_locations()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "DDS");
        files.SaveSourceMember("QGPL", "DDS", "NATIVE", Line('R', "REC") + "\n" + Line(' ', "TEXT", "12"), null);
        var commands = new CommandService(system); var result = commands.Execute("CRTPF FILE(QGPL/NATIVE) SRCFILE(QGPL/DDS)"); Assert.False(result.IsError, result.Message);
        Assert.Equal(FieldType.Alpha, files.GetDefinition("QGPL", "NATIVE")!.PrimaryFormat.Fields[0].Type);
        files.SaveSourceMember("QGPL", "DDS", "BAD", Line('R', "REC") + "\n" + Line(' ', "NUMBER", "63", 'P', "0"), null);
        var failed = commands.Execute("CRTPF FILE(QGPL/BAD) SRCFILE(QGPL/DDS)"); Assert.True(failed.IsError); Assert.Contains("QGPL/DDS(BAD):2:1", failed.Message);
        Assert.False(files.FileExists("QGPL", "BAD"));
    }

    [Theory]
    [InlineData("CCSID(99999)")]
    [InlineData("REF(OTHER)")]
    [InlineData("UNKNOWN")]
    public void Unsupported_field_keywords_fail_at_their_source_line(string keywords)
    {
        var error = Assert.Throws<DdsCompileException>(() => new DdsCompiler().CompilePhysical("BAD", Line('R', "REC") + "\n" + Line(' ', "TEXT", "12", 'A', keywords: keywords), sourceName: "DDS(BAD)"));
        Assert.Contains("DDS(BAD):2:1", error.Message);
    }

    [Fact]
    public void Native_storage_limit_accounts_for_varying_and_null_overhead()
    {
        var valid = Line('R', "REC") + "\n" + Line(' ', "TEXT", "32739", 'A', keywords: "ALWNULL VARLEN");
        Assert.Equal(32741, new DdsCompiler().CompilePhysical("VALID", valid).PrimaryFormat.RecordLength);
        Assert.Throws<DdsCompileException>(() => new DdsCompiler().CompilePhysical("BAD", valid.Replace("32739", "32740", StringComparison.Ordinal)));
        Assert.Throws<DdsCompileException>(() => new DdsCompiler().CompilePhysical("BAD", Line('R', "REC") + "\n" + Line(' ', "BIG", "32766", 'A') + "\n" + Line(' ', "EXTRA", "1", 'A')));
    }
}
