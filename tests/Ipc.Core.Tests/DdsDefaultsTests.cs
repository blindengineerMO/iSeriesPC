using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Microsoft.Data.Sqlite;
using static Ipc.Core.Tests.NativePhysicalDdsTests;

namespace Ipc.Core.Tests;

public sealed class DdsDefaultsTests
{
    [Fact]
    public void Literal_hex_null_and_datetime_defaults_survive_catalog_and_supply_PF_LF_and_copy_outputs()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        var source = string.Join('\n', Line('R', "REC"), Line(' ', "ID", "9", 'B', "0"),
            Line(' ', "AMOUNT", "5", 'P', "2", "DFT(12.34)"), Line(' ', "LABEL", "5", 'A', keywords: "DFT(X'C1C2C34040')"),
            Line(' ', "NOTE", "10", 'A', keywords: "VARLEN DFT('')"), Line(' ', "EMPTY", "5", 'A', keywords: "ALWNULL DFT(*NULL)"),
            Line(' ', "DAY", type: 'L', keywords: "DFT('2024-02-29')"), Line(' ', "CLOCK", type: 'T', keywords: "DFT('23.59.58')"),
            Line(' ', "STAMP", type: 'Z', keywords: "DFT('2024-02-29-23.59.58.123456')"), Line(' ', "NULLDAY", type: 'L', keywords: "ALWNULL DFT(*NULL)"));
        var definition = new DdsCompiler().CompilePhysical("DATA", source);
        files.CreatePhysicalFile("QGPL", "DATA", definition, source);
        files.Insert("QGPL", "DATA", "DATA", "REC", new Dictionary<string, object?> { ["ID"] = 1 });
        var logicalSource = LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/DATA)") + "\n" + LogicalFileTests.Line(' ', "ID");
        var logical = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", logicalSource);
        files.CreateLogicalFile("QGPL", "VIEW", logical, logicalSource);
        files.Insert("QGPL", "VIEW", "VIEW", "VIEWREC", new Dictionary<string, object?> { ["ID"] = 2 });
        files.AddMember("QGPL", "DATA", "COPY");
        Assert.Equal(2, files.CopyRecords(new("QGPL", "VIEW"), "VIEW", new("QGPL", "DATA"), "COPY", map: true));
        var restored = files.GetDefinition("QGPL", "DATA")!;
        Assert.NotNull(restored.PrimaryFormat.Find("NULLDAY")!.DefaultValue);
        Assert.False(restored.PrimaryFormat.Find("DAY")!.CurrentDatetimeDefault);
        foreach (var member in new[] { "DATA", "COPY" })
        foreach (var row in files.ReadAll("QGPL", "DATA", member))
        {
            Assert.Equal(12.34m, row["AMOUNT"]); Assert.Equal("ABC  ", row["LABEL"]); Assert.Equal("", row["NOTE"]);
            Assert.Null(row["EMPTY"]); Assert.Null(row["NULLDAY"]);
            Assert.Equal(new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), row["DAY"]);
            Assert.Equal(new TimeSpan(23, 59, 58), row["CLOCK"]);
            Assert.Equal(new DateTimeOffset(2024, 2, 29, 23, 59, 58, TimeSpan.Zero).AddTicks(1234560), row["STAMP"]);
        }
    }

    [Theory]
    [InlineData('A', "5", "", "DFT('')")]
    [InlineData('A', "5", "", "DFT(ABC)")]
    [InlineData('A', "5", "", "DFT(*NULL)")]
    [InlineData('A', "5", "", "DFT(X'C1')")]
    [InlineData('A', "2", "", "CCSID(1208) DFT('€')")]
    [InlineData('P', "3", "2", "DFT(1.234)")]
    [InlineData('P', "3", "2", "DFT(12.34)")]
    [InlineData('P', "3", "2", "DFT('1.23')")]
    [InlineData('B', "4", "0", "DFT(32768)")]
    [InlineData('L', "", "", "DFT('2023-02-29')")]
    [InlineData('T', "", "", "DFT('24.00.00')")]
    [InlineData('Z', "", "", "DFT('2024-02-29-23.59.58.1234567')")]
    public void Invalid_default_is_a_source_error_before_file_creation(char type, string length, string decimals, string keywords)
    {
        var source = Line('R', "REC") + "\n" + Line(' ', "VALUE", length, type, decimals, keywords);
        var error = Assert.Throws<DdsCompileException>(() => new DdsCompiler().CompilePhysical("BAD", source, sourceName: "QGPL/DDS(BAD)"));
        Assert.Contains("QGPL/DDS(BAD):2:1", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_unique_constraints_survive_member_add_copy_and_logical_deletion(bool excludeNulls)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        var source = string.Join('\n', Line(' ', keywords: excludeNulls ? "UNIQUE(*EXCNULL)" : "UNIQUE"), Line('R', "REC"),
            Line(' ', "VALUE", "9", 'B', "0", "ALWNULL"), Line('K', "VALUE"));
        var definition = new DdsCompiler().CompilePhysical("DATA", source); files.CreatePhysicalFile("QGPL", "DATA", definition, source);
        void Add(string file, string member, object? value) => files.Insert("QGPL", file, member, "REC", new Dictionary<string, object?> { ["VALUE"] = value });
        Add("DATA", "DATA", 1); Add("DATA", "DATA", null);
        if (excludeNulls) Add("DATA", "DATA", null); else Assert.Throws<SqliteException>(() => Add("DATA", "DATA", null));
        var logicalSource = string.Join('\n', LogicalFileTests.Line(' ', keywords: excludeNulls ? "UNIQUE(*EXCNULL)" : "UNIQUE"),
            LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/DATA)"), LogicalFileTests.Line('K', "VALUE"));
        var logical = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", logicalSource);
        files.CreateLogicalFile("QGPL", "VIEW", logical, logicalSource); files.DeleteFile("QGPL", "VIEW");
        Assert.Throws<SqliteException>(() => Add("DATA", "DATA", 1));
        files.AddMember("QGPL", "DATA", "SECOND"); Add("DATA", "SECOND", 1); Assert.Throws<SqliteException>(() => Add("DATA", "SECOND", 1));
        system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Assert.True(files.GetDefinition("QGPL", "COPIED")!.Unique);
        Assert.Throws<SqliteException>(() => Add("COPIED", "DATA", 1));
        files.DeleteFile("QGPL", "DATA"); Add("COPIED", "DATA", 2);
        Assert.Throws<SqliteException>(() => Add("COPIED", "DATA", 2));
    }
}
