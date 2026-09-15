using System.Globalization;
using System.Text;
using Ipc.Console.Session;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests.Db;

public class DbTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;

    public DbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ipcdb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _system = IpcSystem.Create(_tempDir, "test.db");
        _system.Start();
        _files = new SqliteFileStore(_system.Connections, _system.Objects);
    }

    public void Dispose()
    {
        _system.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static string SalesSource =>
        Line('A', ' ', 'R', "SALES", ' ', " ", " ") +
        Line('A', ' ', ' ', "ORDER#", 'P', "7", "0") +
        Line('A', ' ', ' ', "CUSTOMER", 'A', "20", "", "TEXT('Customer name')") +
        Line('A', ' ', ' ', "AMOUNT", 'P', "9", "2") +
        Line('A', ' ', ' ', "STATUS", 'A', "10", "", "ALWNULL") +
        Line('A', ' ', ' ', "ACTIVE", 'L', "1", "") +
        Line('A', 'K', ' ', "ORDER#", ' ', " ", " ", "DESC");

    private static FileDefinition Sales() =>
        new DdsCompiler().CompilePhysical("SALES", SalesSource);

    [Fact]
    public void Dds_compiler_parses_all_field_types_and_keywords()
    {
        var definition = Sales();

        Assert.Equal("SALES", definition.Name);
        Assert.Equal(FileAttribute.Physical, definition.Attribute);
        Assert.Equal("SALES", definition.PrimaryFormat.Name);

        var order = definition.PrimaryFormat.Find("ORDER#")!;
        Assert.Equal(FieldType.Packed, order.Type);
        Assert.Equal(7, order.Length);
        Assert.Equal(0, order.Decimals);
        Assert.Equal(1, order.Sequence);
        Assert.True(order.Descending);

        var customer = definition.PrimaryFormat.Find("CUSTOMER")!;
        Assert.Equal(FieldType.Alpha, customer.Type);
        Assert.Equal(20, customer.Length);
        Assert.Equal("Customer name", customer.Text);

        var amount = definition.PrimaryFormat.Find("AMOUNT")!;
        Assert.Equal(FieldType.Packed, amount.Type);
        Assert.Equal(9, amount.Length);
        Assert.Equal(2, amount.Decimals);

        Assert.True(definition.PrimaryFormat.Find("STATUS")!.NullCapable);
        Assert.Equal(FieldType.Logic, definition.PrimaryFormat.Find("ACTIVE")!.Type);
        Assert.Equal(40, definition.PrimaryFormat.RecordLength);
    }

    [Fact]
    public void Dds_compiler_rejects_bad_source()
    {
        var compiler = new DdsCompiler();
        Assert.Throws<DdsCompileException>(() => compiler.CompilePhysical("BAD", "no record formats here"));
        Assert.Throws<DdsCompileException>(() =>
            compiler.CompilePhysical("BAD", Line('A', ' ', 'R', "REC", ' ', " ", " ")));
    }

    [Fact]
    public void Record_codec_round_trips_every_type()
    {
        var definition = new DdsCompiler().CompilePhysical("ALL", AllTypesSource);
        var format = definition.PrimaryFormat;
        var codec = new RecordCodec(37);

        var values = new Dictionary<string, object?>
        {
            ["TEXT"] = "HELLO WORLD",
            ["ZONED"] = -1234.56m,
            ["PACKED"] = 12.34m,
            ["BIN"] = 424242L,
            ["F4"] = 1.5f,
            ["F8"] = -2.25,
            ["FLAG"] = true,
            ["WHEN"] = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            ["TIME"] = TimeSpan.FromHours(14).Add(TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(5))),
            ["STAMP"] = new DateTimeOffset(2026, 9, 13, 14, 30, 5, TimeSpan.Zero),
        };

        var buffer = codec.Encode(format, values);
        Assert.Equal(format.RecordLength, buffer.Length);

        var decoded = codec.Decode(format, buffer);
        Assert.Equal("HELLO WORLD", AssertType<string>(decoded, format, "TEXT"));
        Assert.Equal(-1234.56m, AssertType<decimal>(decoded, format, "ZONED"));
        Assert.Equal(12.34m, AssertType<decimal>(decoded, format, "PACKED"));
        Assert.Equal(424242L, AssertType<long>(decoded, format, "BIN"));
        Assert.Equal(1.5f, AssertType<float>(decoded, format, "F4"));
        Assert.Equal(-2.25, AssertType<double>(decoded, format, "F8"), 12);
        Assert.True(AssertType<bool>(decoded, format, "FLAG"));
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero), AssertType<DateTimeOffset>(decoded, format, "WHEN"));
        Assert.Equal(new TimeSpan(14, 30, 5), AssertType<TimeSpan>(decoded, format, "TIME"));
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 14, 30, 5, TimeSpan.Zero), AssertType<DateTimeOffset>(decoded, format, "STAMP"));
    }

    [Fact]
    public void Record_codec_float32_bits_are_real()
    {
        var definition = new DdsCompiler().CompilePhysical("FLT", FloatSource);
        var format = definition.PrimaryFormat;
        var codec = new RecordCodec(37);

        var buffer = codec.Encode(format, new Dictionary<string, object?> { ["F4"] = 1.0f });
        Assert.Equal(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, buffer);

        var negative = codec.Encode(format, new Dictionary<string, object?> { ["F4"] = -1.0f });
        Assert.Equal(0xBF, negative[0]);

        var decoded = codec.Decode(format, negative);
        Assert.Equal(-1.0f, AssertType<float>(decoded, format, "F4"));
    }

    [Fact]
    public void Create_physical_file_registers_object_and_default_member()
    {
        var definition = Sales();
        _files.CreatePhysicalFile("QGPL", "SALES", definition, SalesSource, "Sales master.");

        Assert.True(_files.FileExists("QGPL", "SALES"));
        Assert.True(_files.MemberExists("QGPL", "SALES", "SALES"));
        var stored = _files.GetDefinition("QGPL", "SALES")!;
        Assert.Equal("SALES", stored.PrimaryFormat.Name);
        Assert.Equal(5, stored.PrimaryFormat.Fields.Count);

        var descriptor = _system.Objects.Get("QGPL", "SALES", "*FILE")!;
        Assert.Equal("*PF", descriptor.Attribute);
        Assert.Equal("SALES", descriptor.Format);
    }

    [Fact]
    public void Insert_and_read_all_round_trip_values()
    {
        _files.CreatePhysicalFile("QGPL", "SALES", Sales(), SalesSource, "Sales master.");

        _files.Insert("QGPL", "SALES", "SALES", "SALES", new Dictionary<string, object?>
        {
            ["ORDER#"] = 1001L,
            ["CUSTOMER"] = "Acme Corp",
            ["AMOUNT"] = 123.45m,
            ["STATUS"] = "OPEN",
            ["ACTIVE"] = true,
        });
        _files.Insert("QGPL", "SALES", "SALES", "SALES", new Dictionary<string, object?>
        {
            ["ORDER#"] = 1002L,
            ["CUSTOMER"] = "Globex",
            ["AMOUNT"] = 0.99m,
            ["ACTIVE"] = false,
        });

        Assert.Equal(2, _files.RowCount("QGPL", "SALES", "SALES"));
        var rows = _files.ReadAll("QGPL", "SALES", "SALES");
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal(1001m, first["ORDER#"]);
        Assert.Equal("Acme Corp", first["CUSTOMER"]);
        Assert.Equal(123.45m, first["AMOUNT"]);
        Assert.True((bool)first["ACTIVE"]!);

        var second = rows[1];
        Assert.Null(second["STATUS"]);
        Assert.False((bool)second["ACTIVE"]!);
    }

    [Fact]
    public void Keyed_read_orders_by_keys_with_decimal_cast()
    {
        var definition = new DdsCompiler().CompilePhysical("LEDGER", LedgerSource);
        _files.CreatePhysicalFile("QGPL", "LEDGER", definition, LedgerSource);

        _files.Insert("QGPL", "LEDGER", "LEDGER", "LEDGER", LedgerRow(2, 3.50m, "B"));
        _files.Insert("QGPL", "LEDGER", "LEDGER", "LEDGER", LedgerRow(1, 12.00m, "A"));
        _files.Insert("QGPL", "LEDGER", "LEDGER", "LEDGER", LedgerRow(3, 0.99m, "A"));

        var rows = _files.ReadKeyed("QGPL", "LEDGER", "LEDGER");
        Assert.Equal(new[] { 12.00m, 3.50m, 0.99m }, rows.Select(r => (decimal)r["BALANCE"]!));
        Assert.Equal(new[] { 1L, 2L, 3L }, rows.Select(r => (long)(decimal)r["ENTRY"]!));
    }

    [Fact]
    public void Key_prefix_lookup_returns_matching_rows()
    {
        _files.CreatePhysicalFile("QGPL", "SALES", Sales(), SalesSource, "Sales master.");
        _files.Insert("QGPL", "SALES", "SALES", "SALES", Row(1001, "Acme", 10.00m, "OPEN", true));
        _files.Insert("QGPL", "SALES", "SALES", "SALES", Row(1002, "Globex", 20.00m, "OPEN", true));

        var matches = _files.ReadKeyPrefix("QGPL", "SALES", "SALES", new Dictionary<string, object?> { ["ORDER#"] = 1002L });
        var row = Assert.Single(matches);
        Assert.Equal("Globex", row["CUSTOMER"]);
    }

    [Fact]
    public void Member_lifecycle_and_file_delete()
    {
        _files.CreatePhysicalFile("QGPL", "SALES", Sales(), SalesSource, "Sales master.");
        _files.AddMember("QGPL", "SALES", "ARCHIVE");
        Assert.True(_files.MemberExists("QGPL", "SALES", "ARCHIVE"));

        _files.Insert("QGPL", "SALES", "ARCHIVE", "SALES", Row(999, "Old", 1.00m, "CLOSED", false));
        Assert.Equal(1, _files.RowCount("QGPL", "SALES", "ARCHIVE"));

        _files.ClearMember("QGPL", "SALES", "ARCHIVE");
        Assert.Equal(0, _files.RowCount("QGPL", "SALES", "ARCHIVE"));

        _files.RemoveMember("QGPL", "SALES", "ARCHIVE");
        Assert.False(_files.MemberExists("QGPL", "SALES", "ARCHIVE"));

        _files.DeleteFile("QGPL", "SALES");
        Assert.False(_files.FileExists("QGPL", "SALES"));
        Assert.Null(_files.GetDefinition("QGPL", "SALES"));
        Assert.Empty(_files.ListMembers("QGPL", "SALES"));
        Assert.Null(_system.Objects.Get("QGPL", "SALES", "*FILE"));
    }

    [Fact]
    public void Source_file_member_can_hold_source_records()
    {
        _files.CreateSourceFile("QGPL", "QCLSRC", "CL source.");
        Assert.True(_files.FileExists("QGPL", "QCLSRC"));
        Assert.True(_files.MemberExists("QGPL", "QCLSRC", "QCLSRC"));

        _files.Insert("QGPL", "QCLSRC", "QCLSRC", "QCLSRC", new Dictionary<string, object?>
        {
            ["SRCSEQ"] = 10L,
            ["SRCDAT"] = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            ["SRCDTA"] = "PGM",
        });
        _files.Insert("QGPL", "QCLSRC", "QCLSRC", "QCLSRC", new Dictionary<string, object?>
        {
            ["SRCSEQ"] = 20L,
            ["SRCDAT"] = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            ["SRCDTA"] = "SNDPGMMSG MSG('Hello from CL')",
        });

        var rows = _files.ReadAll("QGPL", "QCLSRC", "QCLSRC");
        Assert.Equal(2, rows.Count);
        Assert.Equal(10m, rows[0]["SRCSEQ"]);
        Assert.Equal("PGM", rows[0]["SRCDTA"]);
        Assert.Contains("SNDPGMMSG", (string)rows[1]["SRCDTA"]!);

        var descriptor = _system.Objects.Get("QGPL", "QCLSRC", "*FILE")!;
        Assert.Equal("*SRCPF", descriptor.Attribute);
    }

    [Fact]
    public void Db_formatting_produces_header_and_columnized_rows()
    {
        _files.CreatePhysicalFile("QGPL", "SALES", Sales(), SalesSource, "Sales master.");
        _files.Insert("QGPL", "SALES", "SALES", "SALES", Row(7, "Acme", 99.50m, "OPEN", true));

        var definition = _files.GetDefinition("QGPL", "SALES")!;
        var header = DbFormatting.ColumnHeader(definition.PrimaryFormat);
        Assert.Contains("ORDER#", header);
        Assert.Contains("AMOUNT", header);

        var row = _files.ReadAll("QGPL", "SALES", "SALES")[0];
        var cells = DbFormatting.FormatColumn(definition.PrimaryFormat, row);
        Assert.Equal(5, cells.Count);
        Assert.Equal("7", cells[0].TrimEnd());
        Assert.Equal("99.50", cells[2].TrimEnd());
    }

    [Fact]
    public void Command_loop_creates_compiles_and_runs_from_source_member()
    {
        var commands = new CommandService(_system);

        var createSource = commands.Execute("CRTSRCPF FILE(QGPL/QCLSRC) TEXT('CL source')");
        Assert.False(createSource.IsError);

        commands.Execute("ADDSRCPFM FILE(QGPL/QCLSRC) MBR(QCLSRC) DATA('PGM')");
        commands.Execute("ADDSRCPFM FILE(QGPL/QCLSRC) MBR(QCLSRC) DATA('SNDPGMMSG MSG(''Hello from source member'')')");
        commands.Execute("ADDSRCPFM FILE(QGPL/QCLSRC) MBR(QCLSRC) DATA('ENDPGM')");

        var createProgram = commands.Execute("CRTCLPGM PGM(SAY) SRCFILE(QGPL/QCLSRC) SRCMBR(QCLSRC)");
        Assert.False(createProgram.IsError);
        Assert.Contains("SAY", createProgram.Message);

        var call = commands.Execute("CALL PGM(SAY)");
        Assert.False(call.IsError);
        Assert.Contains("Hello from source member", call.Message);
    }

    [Fact]
    public void Command_creates_physical_file_from_dds_member_and_copies()
    {
        var commands = new CommandService(_system);
        commands.Execute("CRTSRCPF FILE(QGPL/QDDS) TEXT('DDS source')");
        commands.Execute($"ADDSRCPFM FILE(QGPL/QDDS) MBR(QDDS) DATA('{Line('A', ' ', 'R', "CUST", ' ', " ", " ")}')");
        commands.Execute($"ADDSRCPFM FILE(QGPL/QDDS) MBR(QDDS) DATA('{DdsLine("CUSTID", "P", "5 0", "")}')");
        commands.Execute($"ADDSRCPFM FILE(QGPL/QDDS) MBR(QDDS) DATA('{DdsLine("CNAME", "A", "20", "")}')");
        commands.Execute($"ADDSRCPFM FILE(QGPL/QDDS) MBR(QDDS) DATA('{DdsLine("BALANCE", "P", "7 2", "")}')");

        var create = commands.Execute("CRTPF FILE(QGPL/CUST) SRCFILE(QGPL/QDDS) SRCMBR(QDDS) MAXMBRS(2) TEXT('Customers')");
        Assert.False(create.IsError);
        Assert.Contains("CUST", create.Message);

        _files.Insert("QGPL", "CUST", "CUST", "CUST", new Dictionary<string, object?>
        {
            ["CUSTID"] = 1L,
            ["CNAME"] = "Acme Corp",
            ["BALANCE"] = 250.50m,
        });
        _files.Insert("QGPL", "CUST", "CUST", "CUST", new Dictionary<string, object?>
        {
            ["CUSTID"] = 2L,
            ["CNAME"] = "Globex",
            ["BALANCE"] = 15.00m,
        });

        var display = commands.Execute("DSPPFM FILE(QGPL/CUST) MBR(CUST)");
        Assert.False(display.IsError);
        Assert.NotNull(display.Listing);
        Assert.Contains("CUSTID", display.Listing[0]);
        Assert.Contains("BALANCE", display.Listing[0]);
        Assert.Contains(display.Listing, l => l.Contains("Acme Corp"));

        var addMember = commands.Execute("ADDPFM FILE(QGPL/CUST) MBR(DEMO)");
        Assert.False(addMember.IsError);

        var copy = commands.Execute("CPYF FROMFILE(QGPL/CUST) TOFILE(QGPL/CUST) FROMMBR(CUST) TOMBR(DEMO) MBROPT(*ADD)");
        Assert.False(copy.IsError);
        Assert.Contains("2 records", copy.Message);
        Assert.Equal(2, _files.RowCount("QGPL", "CUST", "DEMO"));
    }

    private static string Line(char col7, char col8, char col17, string name, char type, string length, string decimals,
        string keywords = "")
    {
        var sb = new StringBuilder(new string(' ', 100));
        sb[6] = col7;
        sb[7] = col8;
        sb[16] = col17;
        WriteAt(sb, 18, name, 10);
        if (type != ' ')
        {
            sb[34] = type;
            WriteAt(sb, 35, length, 4);
            WriteAt(sb, 39, decimals, 2);
        }

        WriteAt(sb, 44, keywords, 40);
        return sb.ToString() + "\n";
    }

    private static string DdsLine(string name, string type, string lengthAndDecimals, string keywords)
    {
        var parts = lengthAndDecimals.Split(' ');
        var length = parts[0];
        var decimals = parts.Length > 1 ? parts[1] : "";
        return Line('A', ' ', ' ', name, type[0], length, decimals, keywords);
    }

    private static void WriteAt(StringBuilder sb, int start, string text, int width)
    {
        for (var index = 0; index < width && index < text.Length; index++)
        {
            sb[start + index] = text[index];
        }
    }

    private static string AllTypesSource =>
        Line('A', ' ', 'R', "ALL", ' ', " ", " ") +
        Line('A', ' ', ' ', "TEXT", 'A', "20", "") +
        Line('A', ' ', ' ', "ZONED", 'S', "9", "2") +
        Line('A', ' ', ' ', "PACKED", 'P', "9", "2") +
        Line('A', ' ', ' ', "BIN", 'B', "4", "") +
        Line('A', ' ', ' ', "F4", 'F', "4", "") +
        Line('A', ' ', ' ', "F8", 'F', "8", "") +
        Line('A', ' ', ' ', "FLAG", 'L', "1", "") +
        Line('A', ' ', ' ', "WHEN", 'D', "8", "") +
        Line('A', ' ', ' ', "TIME", 'T', "6", "") +
        Line('A', ' ', ' ', "STAMP", 'Z', "14", "");

    private static string FloatSource =>
        Line('A', ' ', 'R', "FLT", ' ', " ", " ") +
        Line('A', ' ', ' ', "F4", 'F', "4", "");

    private static string LedgerSource =>
        Line('A', ' ', 'R', "LEDGER", ' ', " ", " ") +
        Line('A', ' ', ' ', "ENTRY", 'P', "5", "0") +
        Line('A', ' ', ' ', "BALANCE", 'P', "7", "2") +
        Line('A', 'K', ' ', "BALANCE", ' ', " ", " ", "DESC") +
        Line('A', ' ', ' ', "CODE", 'A', "2", "");

    private static Dictionary<string, object?> LedgerRow(int entry, decimal balance, string code) => new()
    {
        ["ENTRY"] = (long)entry,
        ["BALANCE"] = balance,
        ["CODE"] = code,
    };

    private static Dictionary<string, object?> Row(long order, string customer, decimal amount, string status, bool active) =>
        new()
        {
            ["ORDER#"] = order,
            ["CUSTOMER"] = customer,
            ["AMOUNT"] = amount,
            ["STATUS"] = status,
            ["ACTIVE"] = active,
        };

    private static T AssertType<T>(object?[] decoded, RecordFormat format, string name)
    {
        var index = format.Fields.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        Assert.InRange(index, 0, decoded.Length - 1);
        return Assert.IsType<T>(decoded[index]);
    }
}
