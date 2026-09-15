using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClDatabaseFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public ClDatabaseFileTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ClFilesFixture22");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        var format = new RecordFormat { Name = "REC", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4, Sequence = 1 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 7, Decimals = 2, NullCapable = true },
            new() { Name = "LABEL", Type = FieldType.Alpha, Length = 8, NullCapable = true }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "DATA", new() { Name = "DATA", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
    }
    private void Add(int id, decimal? value = 1, string? label = "ROW") => _files.Insert("QGPL", "DATA", "DATA", "REC", new Dictionary<string, object?> { ["ID"] = id, ["AMOUNT"] = value, ["LABEL"] = label });
    private CommandResult Create(string name, string source)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", name, source, null);
        return new CommandService(_system).Execute($"CRTCLPGM PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE)");
    }
    private void Compile(string name, string source) { var result = Create(name, source); Assert.False(result.IsError, result.Message); }
    private ExecutionSession Session(string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user), CancellationToken.None);
    [Theory]
    [InlineData(37, false, "0002C1C2404040", "01234567890123456D")]
    [InlineData(1208, false, "00024142202020", "01234567890123456D")]
    [InlineData(37, true, "00004040404040", "00000000000000000C")]
    [InlineData(1208, true, "00002020202020", "00000000000000000C")]
    public void Variable_length_and_large_numeric_fields_preserve_independent_reference_bytes(int ccsid, bool nulls, string textHex, string numberHex)
    {
        var format = new RecordFormat { Name = "WIDEREC", Fields = new() {
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 5, VariableLength = true, NullCapable = true },
            new() { Name = "PACKED", Type = FieldType.Packed, Length = 16, Decimals = 2, NullCapable = true },
            new() { Name = "ZONED", Type = FieldType.Zoned, Length = 16, Decimals = 2, NullCapable = true },
            new() { Name = "BINARY", Type = FieldType.Binary, Length = 8, DeclaredDigits = 16, NullCapable = true }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "WIDE", new() { Name = "WIDE", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        _files.Insert("QGPL", "WIDE", "WIDE", "WIDEREC", new Dictionary<string, object?> {
            ["TEXT"] = nulls ? null : "AB", ["PACKED"] = nulls ? null : -12345678901234.56m,
            ["ZONED"] = nulls ? null : -12345678901234.56m, ["BINARY"] = nulls ? null : -1234567890123456L
        });
        Assert.True(Create("DENYVAR", "DCLF FILE(QGPL/WIDE)").IsError);
        Compile("READWIDE", "DCLF FILE(QGPL/WIDE) ALWVARLEN(*YES) ALWNULL(*YES)\nRCVF\n" +
            string.Join('\n', new[] { "TEXT", "PACKED", "ZONED", "BINARY" }.Select(n => $"CRTDTAARA DTAARA(QGPL/{n}) TYPE(*CHAR) VALUE(&{n})")));
        using var session = Session(); session.Job.Ccsid = ccsid;
        var result = session.Execute("CALL QGPL/READWIDE"); Assert.False(result.IsError, result.Message);
        var store = new Ipc.Services.Work.DataAreaStore(_system.Connections);
        foreach (var field in format.Fields)
            Assert.Equal(field.Name == "TEXT" ? textHex : numberHex,
                Convert.ToHexString(Assert.IsType<Ipc.Core.Work.ProgramBuffer>(store.Read("QGPL", field.Name).Value).ToArray()));
    }
    [Fact]
    public void Large_binary_decimal_presentation_rejects_excess_digits()
    {
        var format = new RecordFormat { Name = "REC", Fields = new() { new() { Name = "BIG", Type = FieldType.Binary, Length = 8 } } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "BIGBIN", new() { Name = "BIGBIN", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        _files.Insert("QGPL", "BIGBIN", "BIGBIN", "REC", new Dictionary<string, object?> { ["BIG"] = long.MaxValue });
        Compile("OVERFLOW", "DCLF FILE(QGPL/BIGBIN)\nRCVF\nMONMSG CPF0863 EXEC(SNDPGMMSG MSG('overflow'))");
        using var session = Session(); var result = session.Execute("CALL QGPL/OVERFLOW");
        Assert.False(result.IsError, result.Message); Assert.Equal("overflow", result.Message);
    }
    [Fact]
    public void Compiled_fields_read_key_order_preserve_values_at_eof_and_close_reopens()
    {
        Add(2, 2.25m); Add(1, 1.25m);
        Compile("READFILE", """
            PGM
            DCLF FILE(QGPL/DATA) OPNID(INPUT)
            DCL &TOTAL *DEC LEN(9 2)
            DOWHILE ('1')
              RCVF OPNID(INPUT)
              MONMSG CPF0864 EXEC(LEAVE)
              CHGVAR &TOTAL (&TOTAL + &INPUT_AMOUNT)
            ENDDO
            SNDPGMMSG MSG(&TOTAL *CAT ':' *CAT &INPUT_ID)
            RCVF OPNID(INPUT)
            MONMSG CPF0864
            CLOSE OPNID(INPUT)
            RCVF OPNID(INPUT)
            SNDPGMMSG MSG(&INPUT_ID)
            ENDPGM
            """);
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/READFILE)");
        Assert.False(result.IsError, result.Message);
        Assert.Equal("3.50:2\n1", result.Message?.Replace("\r", ""));
        Assert.Equal(0, _system.JobRuntime.Environment(session.Job.Key)!.OpenPathCount);
        var stored = _system.Objects.GetRequired("QGPL", "READFILE", "*PGM"); Assert.Contains(ClFileBindings.AttributeName, stored.ExtendedAttributes!.Keys);
    }
    [Fact]
    public void Stored_layout_is_not_rebound_when_database_definition_changes()
    {
        Add(1);
        Compile("LEVELCHK", "DCLF FILE(QGPL/DATA)\nRCVF\nMONMSG CPF4131 EXEC(DO)\nSNDPGMMSG MSG('LEVEL CHECK')\nRETURN\nENDDO\nSNDPGMMSG MSG('WRONG')");
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sys_file_defs SET def=json_set(def,'$.formats[0].fields[1].length',8) WHERE lib='QGPL' AND name='DATA'";
            command.ExecuteNonQuery();
        }
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/LEVELCHK)");
        Assert.False(result.IsError, result.Message); Assert.Equal("LEVEL CHECK", result.Message);
    }
    [Theory]
    [InlineData("*YES", "1\n2\n3")]
    [InlineData("*NO", "1\n1\n2")]
    public void Overrides_share_CL_cursor_position_only_when_requested(string share, string expected)
    {
        Add(1); Add(2); Add(3);
        Compile("CHILD", "DCLF FILE(QGPL/DATA)\nRCVF\nSNDPGMMSG MSG(&ID)");
        Compile("PARENT", "DCLF FILE(QGPL/DATA)\nRCVF\nSNDPGMMSG MSG(&ID)\nCALL PGM(QGPL/CHILD)\nRCVF\nSNDPGMMSG MSG(&ID)\nCLOSE");
        using var session = Session();
        var overridden = session.Execute($"OVRDBF FILE(DATA) TOFILE(QGPL/DATA) SHARE({share}) OVRSCOPE(*JOB)");
        Assert.False(overridden.IsError, overridden.Message);
        var result = session.Execute("CALL PGM(QGPL/PARENT)");
        Assert.False(result.IsError, result.Message); Assert.Equal(expected, result.Message?.Replace("\r", ""));
        Assert.Equal(0, _system.JobRuntime.Environment(session.Job.Key)!.OpenPathCount);
    }
    [Theory]
    [InlineData("*NO", "NULL\n1:0.00")]
    [InlineData("*YES", "1:0.00")]
    public void Null_fields_supply_defaults_and_only_disallowed_nulls_raise_an_escape(string allowed, string expected)
    {
        Add(1, null, null);
        Compile("NULLS", $"DCLF FILE(QGPL/DATA) ALWNULL({allowed})\nRCVF\nMONMSG CPF0886 EXEC(SNDPGMMSG MSG('NULL'))\nSNDPGMMSG MSG(&ID *CAT ':' *CAT &AMOUNT)");
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/NULLS)");
        Assert.False(result.IsError, result.Message); Assert.Equal(expected, result.Message?.Replace("\r", ""));
    }
    [Fact]
    public void Binary_fields_use_native_decimal_width_or_explicit_integer_declaration()
    {
        var format = new RecordFormat { Name = "BINREC", Fields = new() { new() { Name = "SMALL", Type = FieldType.Binary, Length = 2, DeclaredDigits = 4 } } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "BINARY", new() { Name = "BINARY", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        _files.Insert("QGPL", "BINARY", "BINARY", "BINREC", new Dictionary<string, object?> { ["SMALL"] = 32767 });
        Compile("DECIMAL", "DCLF FILE(QGPL/BINARY)\nRCVF\nMONMSG CPF0863 EXEC(SNDPGMMSG MSG('OVERFLOW'))");
        Compile("INTEGER", "DCLF FILE(QGPL/BINARY) DCLBINFLD(*INT)\nRCVF\nSNDPGMMSG MSG(&SMALL)");
        using var session = Session(); Assert.Equal("OVERFLOW", session.Execute("CALL PGM(QGPL/DECIMAL)").Message);
        var result = session.Execute("CALL PGM(QGPL/INTEGER)"); Assert.False(result.IsError, result.Message); Assert.Equal("32767", result.Message);
    }
    [Fact]
    public void Compiled_program_loads_its_layout_but_receive_enforces_live_file_authority()
    {
        Add(1); Compile("SECURE", "DCLF FILE(QGPL/DATA)\nRCVF\nMONMSG CPF9802 EXEC(SNDPGMMSG MSG('DENIED'))");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QGPL", "DATA", "*FILE", "READER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "SECURE", "*PGM", "READER", Authorities.UseBits);
        using var session = Session("READER"); var result = session.Execute("CALL PGM(QGPL/SECURE)");
        Assert.False(result.IsError, result.Message); Assert.Equal("DENIED", result.Message);
    }
    [Fact]
    public void Explicit_declarations_must_match_file_generated_variables()
    {
        Add(1);
        Compile("MATCH", "DCL &ID *DEC LEN(9 0)\nDCLF FILE(QGPL/DATA)\nRCVF\nSNDPGMMSG MSG(&ID)");
        Assert.True(Create("MISMATCH", "DCLF FILE(QGPL/DATA)\nDCL &ID *DEC LEN(8 0)").IsError);
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/MATCH)"); Assert.False(result.IsError, result.Message); Assert.Equal("1", result.Message);
    }
    [Theory]
    [InlineData("RCVF")]
    [InlineData("CLOSE")]
    [InlineData("DCLF FILE(QGPL/DATA)\nDCLF FILE(QGPL/DATA)")]
    [InlineData("DCLF FILE(QGPL/DATA)\nRCVF OPNID(OTHER)")]
    [InlineData("DCLF FILE(QGPL/DATA)\nRCVF WAIT(*NO)")]
    [InlineData("DCLF FILE(QGPL/DATA) RCDFMT(MISSING)")]
    [InlineData("SNDPGMMSG MSG('late')\nDCLF FILE(QGPL/DATA)")]
    public void Invalid_file_declarations_and_operations_fail_before_program_creation(string source)
    {
        var result = Create("BAD", source); Assert.True(result.IsError); Assert.Contains("SOURCE(BAD)", result.Message); Assert.False(_system.Objects.Exists("QGPL", "BAD", "*PGM"));
    }

    [Fact]
    public void Five_open_identifiers_use_independent_variables_and_sixth_is_rejected()
    {
        Add(1);
        var declarations = string.Join('\n', Enumerable.Range(1, 5).Select(n => "DCLF FILE(QGPL/DATA) OPNID(FILE" + n + ")"));
        Compile("FIVE", declarations + "\nRCVF OPNID(FILE1)\nRCVF OPNID(FILE5)\nSNDPGMMSG MSG(&FILE1_ID *CAT ':' *CAT &FILE5_ID)");
        Assert.True(Create("SIX", declarations + "\nDCLF FILE(QGPL/DATA) OPNID(SIXTH)").IsError);
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/FIVE)");
        Assert.False(result.IsError, result.Message); Assert.Equal("1:1", result.Message);
    }
    [Fact]
    public void Stale_or_duplicate_compiled_binding_metadata_is_rejected()
    {
        Compile("BINDINGS", "DCLF FILE(QGPL/DATA)\nRCVF");
        var descriptor = _system.Objects.GetRequired("QGPL", "BINDINGS", "*PGM");
        var json = descriptor.ExtendedAttributes![ClFileBindings.AttributeName];
        Assert.Throws<InvalidDataException>(() => ClFileBindings.Restore(descriptor.Source! + "\nRETURN", json));
        Assert.Throws<InvalidDataException>(() => ClFileBindings.Restore(descriptor.Source!, json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
        descriptor.ExtendedAttributes = new Dictionary<string, string>(descriptor.ExtendedAttributes) { [ClFileBindings.AttributeName] = json.Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal) };
        _system.Objects.Update(descriptor);
        using var session = Session(); Assert.True(session.Execute("CALL PGM(QGPL/BINDINGS)").IsError);
    }
    [Fact]
    public void Date_time_and_timestamp_fields_map_to_their_external_character_layouts()
    {
        var format = new RecordFormat { Name = "DATES", Fields = new() {
            new() { Name = "DAY", Type = FieldType.Date, Length = 10 },
            new() { Name = "CLOCK", Type = FieldType.Time, Length = 8 },
            new() { Name = "STAMP", Type = FieldType.Timestamp, Length = 26 }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "DATES", new() { Name = "DATES", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        _files.Insert("QGPL", "DATES", "DATES", "DATES", new Dictionary<string, object?> {
            ["DAY"] = new DateTime(2024, 2, 29), ["CLOCK"] = new TimeSpan(12, 34, 56), ["STAMP"] = new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560)
        });
        Compile("DATETIME", "DCLF FILE(QGPL/DATES)\nRCVF\nSNDPGMMSG MSG(&DAY *CAT '|' *CAT &CLOCK *CAT '|' *CAT &STAMP)");
        using var session = Session(); var result = session.Execute("CALL PGM(QGPL/DATETIME)");
        Assert.False(result.IsError, result.Message); Assert.Equal("2024-02-29|12.34.56|2024-02-29-12.34.56.123456", result.Message);
    }
    public void Dispose() => _system.Dispose();
}
