using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class UserSpaceApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly QualifiedName _name = new("QGPL", "SPACE");
    private const string Name = "SPACE     QGPL      ";
    private UserSpaceStore Store => new(_system.Connections);
    public UserSpaceApiTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "UserSpaceFixture25");
    }
    private UserSpaceApi Api(int ccsid = 37) => new(_system.Connections, _ => ["QGPL", "QSYS"], ccsid);
    private static ProgramBuffer Hex(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
    private static byte[] Binary(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes; }
    private sealed class Cell
    {
        public ProgramBuffer Buffer { get; private set; }
        public ProgramArgument Argument { get; }
        public Cell(byte[] bytes, int ccsid = 37)
        {
            Buffer = new(bytes, ccsid);
            Argument = new(() => Buffer, value => Buffer = (ProgramBuffer)value!, value => value is ProgramBuffer buffer && buffer.Length == bytes.Length && buffer.Ccsid == ccsid
                ? buffer : throw new CpfException("CPF3C3A", "Bad test cell write."), "*CHAR", bytes.Length, buffer: () => Buffer);
        }
        public string Hex => Convert.ToHexString(Buffer.ToArray());
    }
    private static Cell Error(int ccsid = 37) => new(Convert.FromHexString("0000001000000000AAAAAAAAAAAAAAAAAAAAAAAA"), ccsid);
    private void Create(UserSpaceApi api, int ccsid = 37) => api.Invoke("QUSCRTUS", [Name, "BINARY", Hex("00000008", ccsid), Hex("FE", ccsid), "*CHANGE", "Test"]);

    [Theory]
    [InlineData(37, "F0", "D8C7D7D3404040404040")]
    [InlineData(1208, "30", "5147504C202020202020")]
    public void Native_binary_layouts_round_trip_without_transcoding(int ccsid, string flag, string library)
    {
        var api = Api(ccsid); Create(api, ccsid);
        var error = Error(ccsid); var returnedLibrary = new Cell(Enumerable.Repeat((byte)0xAA, 12).ToArray(), ccsid);
        api.Invoke("QUSCHGUS", [Name, Hex("00000003", ccsid), Hex("00000004", ccsid), Hex("FF00FE01AB", ccsid), "2", error.Argument, returnedLibrary.Argument]);
        Assert.Equal(library + "AAAA", returnedLibrary.Hex); Assert.StartsWith("0000001000000000", error.Hex);
        var data = new Cell(Enumerable.Repeat((byte)0xAA, 10).ToArray(), ccsid);
        api.Invoke("QUSRTVUS", [Name, Hex("00000001", ccsid), Hex("00000008", ccsid), data.Argument, error.Argument]);
        Assert.Equal("FEFEFF00FE01FEFEAAAA", data.Hex);
        var attributes = new Cell(Enumerable.Repeat((byte)0xAA, 28).ToArray(), ccsid);
        api.Invoke("QUSRUSAT", [attributes.Argument, Hex("0000001C", ccsid), "SPCA0100", Name, error.Argument]);
        Assert.Equal("000000180000001800000008" + flag + "FE" + library + "AAAAAAAA", attributes.Hex);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(23)]
    public void Attribute_receivers_truncate_at_requested_length_and_preserve_tail(int length)
    {
        var api = Api(); Create(api); var output = new Cell(Enumerable.Repeat((byte)0xAA, 28).ToArray());
        api.Invoke("QUSRUSAT", [output.Argument, length, "SPCA0100", Name, Hex("00000000")]);
        var expected = Convert.FromHexString("000000000000001800000008F0FED8C7D7D3404040404040");
        BinaryPrimitives.WriteInt32BigEndian(expected, length);
        Assert.Equal(expected[..length], output.Buffer.ToArray()[..length]);
        Assert.All(output.Buffer.ToArray()[length..], value => Assert.Equal(0xAA, value));
    }

    private static ProgramBuffer Records(params (int Key, byte[] Data)[] records)
    {
        var bytes = new List<byte>(Binary(records.Length));
        foreach (var (key, data) in records)
        {
            bytes.AddRange(Binary(key)); bytes.AddRange(Binary(data.Length)); bytes.AddRange(data);
            while (bytes.Count % 4 != 0) bytes.Add(0xAA);
        }
        return new(bytes.ToArray(), 37);
    }
    [Fact]
    public void Attribute_records_use_aligned_keys_and_last_duplicate_wins()
    {
        var api = Api(); Create(api); var returnedLibrary = new Cell(new byte[10]);
        api.Invoke("QUSCUSAT", [returnedLibrary.Argument, Name, Records((1, Binary(9)), (2, [0, 255]), (3, [0xF1]), (4, Binary(32)), (1, Binary(12))), Hex("00000000")]);
        var attrs = Store.Inspect(_name); Assert.Equal(12, attrs.Size); Assert.True(attrs.AutomaticallyExtendible); Assert.Equal(0, attrs.InitialValue); Assert.Equal(32, attrs.TransferSize);
        Assert.Equal("FEFEFEFEFEFEFEFE00000000", Convert.ToHexString(Store.Read(_name, 1, 12)));
        Assert.Equal("D8C7D7D3404040404040", returnedLibrary.Hex);
    }
    [Theory]
    [InlineData("000000010000000100000003000000")]
    [InlineData("00000001000000630000000400000001")]
    [InlineData("00000002000000010000000400000010")]
    [InlineData("000000010000000300000001F2000000")]
    [InlineData("FFFFFFFF")]
    public void Malformed_attribute_lists_preserve_payload_and_returned_library(string hex)
    {
        var api = Api(); Create(api); var returnedLibrary = new Cell(Enumerable.Repeat((byte)0xAA, 10).ToArray());
        Assert.Throws<CpfException>(() => api.Invoke("QUSCUSAT", [returnedLibrary.Argument, Name, Hex(hex), Hex("00000000")]));
        Assert.Equal(8, Store.Inspect(_name).Size); Assert.Equal(new string('A', 20), returnedLibrary.Hex);
    }

    [Fact]
    public void Error_code_receivers_report_failure_without_changing_storage_or_other_outputs()
    {
        var api = Api(); Create(api); var error = Error(); var data = new Cell(new byte[] { 0xAA, 0xAA });
        api.Invoke("QUSRTVUS", [Name, 8, 2, data.Argument, error.Argument]);
        Assert.Equal("AAAA", data.Hex); Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(error.Buffer.ToArray().AsSpan(4)));
        Assert.Equal("CPF3C14", new ProgramBuffer(error.Buffer.ToArray().AsSpan(8, 7), 37).ToText());
        Assert.Equal("AAAAAAAA", error.Hex[^8..]);
        api.Invoke("QUSCHGUS", [Name, 1, 1, Hex("FF"), "0", error.Argument]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(error.Buffer.ToArray().AsSpan(4)));
        Assert.Equal(255, Store.Read(_name, 1, 1)[0]);
    }

    [Fact]
    public void Invalid_and_aliased_outputs_are_validated_before_mutating_storage()
    {
        var api = Api(); Create(api);
        Assert.Throws<CpfException>(() => api.Invoke("QUSCHGUS", [Name, 1, 1, Hex("00"), "0", Hex("00000000"), "IMMUTABLE "]));
        var error = Error(); api.Invoke("QUSCHGUS", [Name, 1, 1, Hex("00"), "0", error.Argument, error.Argument]);
        Assert.Equal(0xFE, Store.Read(_name, 1, 1)[0]);
        Assert.Throws<CpfException>(() => api.Invoke("QUSCUSAT", [Hex("00000000000000000000"), Name, Records((1, Binary(12))), Hex("00000000")]));
        Assert.Equal(8, Store.Inspect(_name).Size);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(13)]
    public void Incomplete_create_optional_groups_are_rejected(int count)
    {
        var parameters = new object?[] { Name, "", 1, Hex("00"), "*CHANGE", "", "*NO", Hex("00000000"), "*USER", 0, "1", "0", "" };
        Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Invoke("QUSCRTUS", parameters.Take(count).ToArray())).MessageId);
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }
    [Fact]
    public void Complete_create_options_set_domain_alignment_transfer_and_extension()
    {
        Api().Invoke("QUSCRTUS", [Name, "", 1, Hex("AA"), "*USE", "", "*NO", Hex("00000000"), "*SYSTEM", 32, "0", "1"]);
        var attrs = Store.Inspect(_name); Assert.False(attrs.OptimumAlignment); Assert.True(attrs.AutomaticallyExtendible); Assert.Equal(32, attrs.TransferSize); Assert.Equal("*SYSTEM", attrs.Domain);
        Api().Invoke("QUSDLTUS", [Name, Hex("00000000")]); Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
        Assert.Equal("CPF2105", Assert.Throws<CpfException>(() => Api().Invoke("QUSDLTUS", [Name, Hex("00000000")])).MessageId);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_calls_real_QSYS_programs_with_native_INT_and_raw_receivers(int ccsid)
    {
        ClExternalCallTests.Create(_system, "USAPI", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(8) VALUE(X'AAAAAAAAAAAAAAAA')
            DCL &ATTR *CHAR LEN(24)
            DCL &N *CHAR LEN(4)
            DCL &TEXT *CHAR LEN(50)
            CHGVAR %BIN(&N 1 4) 4
            CALL QSYS/QUSCRTUS PARM('SPACE     QGPL      ' '' (8 (*INT 4)) X'FE' '*CHANGE' &TEXT)
            CALL QSYS/QUSCHGUS PARM('SPACE     QGPL      ' (3 (*INT 4)) &N X'FF00FE01' '0')
            CALL QSYS/QUSRTVUS PARM('SPACE     QGPL      ' (1 (*INT 4)) (8 (*INT 4)) &DATA)
            CALL QSYS/QUSRUSAT PARM(&ATTR (24 (*INT 4)) 'SPCA0100' 'SPACE     QGPL      ' X'00000000')
            IF (&DATA *EQ X'FEFEFF00FE01FEFE' *AND %BIN(&ATTR 9 4) *EQ 8) THEN(SNDPGMMSG MSG('USER SPACE API ACCEPTED'))
            CALL QSYS/QUSDLTUS PARM('SPACE     QGPL      ' X'00000000')
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/USAPI"); Assert.False(result.IsError, result.Message);
        Assert.Contains("USER SPACE API ACCEPTED", result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }
    [Fact]
    public void Commands_create_and_delete_binary_spaces_and_reject_unknown_options()
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CRTUSRSPC QGPL/SPACE 16 VALUE(X'FE') AUTOEXT(*YES) ATTR(TEST) TEXT('Native bytes')");
        Assert.False(result.IsError, result.Message); Assert.Equal(new byte[] { 254, 254 }, Store.Read(_name, 1, 2));
        Assert.True(Store.Inspect(_name).AutomaticallyExtendible);
        Assert.True(session.Execute("CRTUSRSPC QGPL/OTHER 2 VALUE(X'0011')").IsError);
        Assert.True(session.Execute("CRTUSRSPC QGPL/OTHER 2 UNKNOWN(1)").IsError);
        result = session.Execute("DLTUSRSPC QGPL/SPACE"); Assert.False(result.IsError, result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }
    [Theory]
    [InlineData(0, 1, "CPF3C0F")]
    [InlineData(1, 0, "CPF3C12")]
    [InlineData(8, 2, "CPF3C14")]
    [InlineData(1, 4, "CPF3C19")]
    public void Retrieve_validates_native_bounds_without_touching_receiver(int start, int length, string message)
    {
        var api = Api(); Create(api); var receiver = new Cell(new byte[] { 0xAA, 0xAA });
        Assert.Equal(message, Assert.Throws<CpfException>(() => api.Invoke("QUSRTVUS", [Name, start, length, receiver.Argument])).MessageId);
        Assert.Equal("AAAA", receiver.Hex);
    }
    [Theory]
    [InlineData(7, "SPCA0100", "CPF3C24")]
    [InlineData(25, "SPCA0100", "CPF3C24")]
    [InlineData(24, "USAT0100", "CPF3C21")]
    public void Attributes_validate_length_and_format_before_writing(int length, string format, string message)
    {
        var api = Api(); Create(api); var receiver = new Cell(Enumerable.Repeat((byte)0xAA, 24).ToArray());
        Assert.Equal(message, Assert.Throws<CpfException>(() => api.Invoke("QUSRUSAT", [receiver.Argument, length, format, Name, Hex("00000000")])).MessageId);
        Assert.All(receiver.Buffer.ToArray(), value => Assert.Equal(0xAA, value));
    }
    [Fact]
    public void Binary_fields_reject_packed_layouts_and_short_storage()
    {
        var api = Api(); Create(api); var receiver = new Cell(new byte[8]);
        var packed = new ProgramConstant("*DEC", 5, 0, Hex("00001C"), 1m);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => api.Invoke("QUSRTVUS", [Name, packed, 1, receiver.Argument])).MessageId);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => api.Invoke("QUSRTVUS", [Name, Hex("000001"), 1, receiver.Argument])).MessageId);
        api.Invoke("QUSRTVUS", [Name, new ProgramConstant("*INT", 4, 0, Hex("00000001"), 1), 1, receiver.Argument]);
        Assert.Equal(0xFE, receiver.Buffer.ToArray()[0]);
    }
    [Fact]
    public void Library_search_and_delete_apply_object_exist_without_requiring_use()
    {
        Store.Create(_name, 1, authority: "*EXCLUDE");
        _system.Security.Authority.Grant("QGPL", "SPACE", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        using (Ipc.Services.Events.OperationIdentity.Enter("QUSER"))
        {
            Assert.Throws<CpfException>(() => Store.Inspect(_name));
            Api().Invoke("QUSDLTUS", ["SPACE     *LIBL     ", Hex("00000000")]);
        }
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
        Api().Invoke("QUSCRTUS", ["SPACE     *CURLIB   ", "", 1, Hex("00"), "*USE", ""]);
        Assert.Equal(1, Store.Inspect(_name).Size);
        Assert.Throws<CpfException>(() => Api().Invoke("QUSCRTUS", ["BAD       *LIBL     ", "", 1, Hex("00"), "*USE", ""]));
    }
    [Theory]
    [InlineData("QUSCHGUS")]
    [InlineData("QUSCUSAT")]
    public void Return_library_normalization_failure_prevents_persistent_changes(string apiName)
    {
        var api = Api(); Create(api); var bytes = Hex("00000000000000000000");
        var receiver = new ProgramArgument(() => bytes, _ => throw new InvalidOperationException("Setter must not run."),
            _ => throw new CpfException("CPF3C3A", "Cannot normalize receiver."), "*CHAR", 10, buffer: () => bytes);
        object?[] parameters = apiName == "QUSCHGUS"
            ? [Name, 1, 1, Hex("00"), "0", Hex("00000000"), receiver]
            : [receiver, Name, Records((1, Binary(12))), Hex("00000000")];
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => api.Invoke(apiName, parameters)).MessageId);
        Assert.Equal(8, Store.Inspect(_name).Size); Assert.All(Store.Read(_name, 1, 8), value => Assert.Equal(0xFE, value));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Generic_native_delete_commits_eligible_matches_and_returns_partial_failure(int ccsid)
    {
        Store.Create(new("QGPL", "MATCHA"), 1); Store.Create(new("QGPL", "MATCHB"), 1);
        _system.Security.Authority.Grant("QGPL", "MATCHA", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        _system.Security.Authority.Grant("QGPL", "MATCHB", ObjectType.UserSpace, "QUSER", AuthorityBit.None);
        var error = Error(ccsid);
        using (Ipc.Services.Events.OperationIdentity.Enter("QUSER")) Api(ccsid).Invoke("QUSDLTUS", ["MATCH*    QGPL      ", error.Argument]);
        Assert.Equal("CPF2117", new ProgramBuffer(error.Buffer.ToArray().AsSpan(8, 7), ccsid).ToText());
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(error.Buffer.ToArray().AsSpan(4)));
        Assert.Equal("AAAAAAAA", error.Hex[^8..]);
        Assert.False(_system.Objects.Exists("QGPL", "MATCHA", ObjectType.UserSpace));
        Assert.True(_system.Objects.Exists("QGPL", "MATCHB", ObjectType.UserSpace));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Disallowed_user_domain_returns_native_creation_error_without_creating_an_object(int ccsid)
    {
        _system.SetSystemValue(Ipc.Core.System.SystemValueNames.AllowUserDomain, "QTEMP");
        var error = Error(ccsid);
        Api(ccsid).Invoke("QUSCRTUS", [Name, "", 1, Hex("00", ccsid), "*USE", "", "*NO", error.Argument, "*USER"]);
        Assert.Equal("CPF3C49", new ProgramBuffer(error.Buffer.ToArray().AsSpan(8, 7), ccsid).ToText());
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }
    [Fact]
    public void Generic_delete_error_receiver_is_validated_before_any_object_is_deleted()
    {
        Store.Create(new("QGPL", "MATCHA"), 1); Store.Create(new("QGPL", "MATCHB"), 1);
        Assert.Throws<CpfException>(() => Api().Invoke("QUSDLTUS", ["MATCH*    QGPL      ", Hex("00000010000000000000000000000000")]));
        Assert.True(_system.Objects.Exists("QGPL", "MATCHA", ObjectType.UserSpace));
        Assert.True(_system.Objects.Exists("QGPL", "MATCHB", ObjectType.UserSpace));
        Api().Invoke("QUSDLTUS", ["MATCH*    *LIBL     ", Hex("00000000")]);
        Assert.False(_system.Objects.Exists("QGPL", "MATCHA", ObjectType.UserSpace));
        Assert.False(_system.Objects.Exists("QGPL", "MATCHB", ObjectType.UserSpace));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_monitors_native_partial_deletion_after_successful_matches_commit(int ccsid)
    {
        Store.Create(new("QGPL", "MATCHA"), 1); Store.Create(new("QGPL", "MATCHB"), 1);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DEPENDENT"), ObjectType = ObjectType.Program });
        new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections).AddDependency(new("QGPL", "DEPENDENT"), ObjectType.Program, new("QGPL", "MATCHB"), ObjectType.UserSpace);
        ClExternalCallTests.Create(_system, "USDELETE", "CLP", """
            PGM
            CALL QSYS/QUSDLTUS PARM('MATCH*    QGPL      ' X'00000000')
            MONMSG CPF2117 EXEC(SNDPGMMSG MSG('PARTIAL DELETION ACCEPTED'))
            ENDPGM
            """);
        using var execution = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = execution.Execute("CALL QGPL/USDELETE"); Assert.False(result.IsError, result.Message);
        Assert.Contains("PARTIAL DELETION ACCEPTED", result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "MATCHA", ObjectType.UserSpace));
        Assert.True(_system.Objects.Exists("QGPL", "MATCHB", ObjectType.UserSpace));
    }
    public void Dispose() => _system.Dispose();
}
