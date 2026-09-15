using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class DataAreaTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly DataAreaStore _store;
    public DataAreaTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DataAreaFixture9");
        _store = new(_system.Connections);
    }
    private ExecutionSession Session(string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user), CancellationToken.None);
    private static void Execute(ExecutionSession session, string command)
    { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
    private ProgramBuffer Bytes(string name) => Assert.IsType<ProgramBuffer>(_store.Read("QGPL", name).Value);
    [Fact]
    public void Character_values_have_exact_ccsid_padding_and_partial_updates()
    {
        _store.Create("QGPL", "TEXT", "*CHAR", 5, value: "AB");
        Assert.Equal("C1C2404040", Convert.ToHexString(Bytes("TEXT").ToArray()));
        _store.Change("QGPL", "TEXT", "Z", 2, 2);
        Assert.Equal("C1E9404040", Convert.ToHexString(Bytes("TEXT").ToArray()));
        Assert.Equal("Z ", Assert.IsType<ProgramBuffer>(_store.Read("QGPL", "TEXT", 2, 2).Value).ToText());
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "TEXT", "TOOLONG", 2, 2));
        Assert.Equal("AZ   ", Bytes("TEXT").ToText());
        var copy = Bytes("TEXT").ToArray(); copy[0] = 0; Assert.Equal(0xC1, Bytes("TEXT").ToArray()[0]);
    }
    [Fact]
    public void Large_decimal_values_remain_exact_and_failed_changes_leave_them_unchanged()
    {
        const decimal number = 123456789012345678901234m;
        _store.Create("QGPL", "NUMBER", "*DEC", 24, value: number);
        Assert.Equal(number, _store.Read("QGPL", "NUMBER").Value);
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "NUMBER", 1234567890123456789012345m));
        Assert.Equal(number, _store.Read("QGPL", "NUMBER").Value);
        _store.Create("QGPL", "SCALED", "*DEC", 24, 9, 123456789012345.123456789m);
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "SCALED", 1.1234567891m));
        Assert.Equal(123456789012345.123456789m, _store.Read("QGPL", "SCALED").Value);
        Assert.Throws<CpfException>(() => _store.Read("QGPL", "NUMBER", 1, 1));
    }
    [Theory]
    [InlineData("*CHAR", 0, 0)]
    [InlineData("*CHAR", 2001, 0)]
    [InlineData("*CHAR", 5, 1)]
    [InlineData("*DEC", 25, 0)]
    [InlineData("*DEC", 5, 6)]
    [InlineData("*DEC", 24, 10)]
    [InlineData("*LGL", 2, 0)]
    [InlineData("*DDM", 10, 0)]
    public void Invalid_definition_does_not_create_an_object(string type, int length, int decimals)
    {
        Assert.Throws<CpfException>(() => _store.Create("QGPL", "BAD", type, length, decimals));
        Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.DataArea));
    }
    [Fact]
    public void Logical_values_reject_nonlogical_input_and_substrings()
    {
        _store.Create("QGPL", "FLAG", "*LGL", 1); Assert.Equal(false, _store.Read("QGPL", "FLAG").Value);
        _store.Change("QGPL", "FLAG", "1"); Assert.Equal(true, _store.Read("QGPL", "FLAG").Value);
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "FLAG", "2"));
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "FLAG", false, 1, 1));
        Assert.Equal(true, _store.Read("QGPL", "FLAG").Value);
    }
    [Fact]
    public void Authority_lists_and_explicit_object_locks_apply_to_data_area_operations()
    {
        _system.Security.Authority.AddAuthLMember("DAAUTH", "QUSER", Authorities.ChangeBits);
        _store.Create("QGPL", "SECURE", "*CHAR", 4, authority: "DAAUTH");
        using (OperationIdentity.Enter("QUSER")) { _store.Change("QGPL", "SECURE", "READ"); Assert.Equal("READ", Bytes("SECURE").ToText()); }
        _system.Security.Authority.RemoveAuthLMember("DAAUTH", "QUSER");
        using (OperationIdentity.Enter("QUSER")) { Assert.Throws<CpfException>(() => _store.Read("QGPL", "SECURE")); Assert.Throws<CpfException>(() => _store.Change("QGPL", "SECURE", "FAIL")); }
        using var owner = Session(); using var reader = Session(); var locks = new JobLockStore(_system.Connections);
        using (locks.Acquire(owner.Job.Key, new("QGPL", "SECURE", ObjectType.DataArea), JobLockMode.Exclusive, TimeSpan.Zero))
        {
            Assert.True(reader.Execute("DSPDTAARA QGPL/SECURE").IsError);
            Assert.True(reader.Execute("CHGDTAARA QGPL/SECURE FAIL").IsError);
        }
        Execute(reader, "CHGDTAARA QGPL/SECURE 'GOOD'"); Assert.Equal("GOOD", Bytes("SECURE").ToText());
    }
    [Fact]
    public async Task Concurrent_substring_updates_preserve_each_others_bytes()
    {
        _store.Create("QGPL", "SHARED", "*CHAR", 8);
        await Task.WhenAll(Enumerable.Range(1, 8).Select(position => Task.Run(() => _store.Change("QGPL", "SHARED", position.ToString(), position, 1))));
        Assert.Equal("12345678", Bytes("SHARED").ToText());
    }
    [Fact]
    public void Native_commands_and_CL_retrieval_enforce_types_padding_and_decimal_alignment()
    {
        using var session = Session();
        Execute(session, "CRTDTAARA QGPL/PRICE *DEC (5 2) 12.39");
        Execute(session, "CRTDTAARA QGPL/TEXT *CHAR 3 'ABC'");
        var displayed = session.Execute("DSPDTAARA QGPL/TEXT"); Assert.False(displayed.IsError, displayed.Message);
        Assert.Contains("Type: *CHAR  Length: 3", displayed.Listing![1]); Assert.Equal("ABC", displayed.Listing[^1]);
        Execute(session, "CRTDTAARA QGPL/FLAG *LGL 1 '1'");
        ClExternalCallTests.Create(_system, "GETAREA", "CLP", "PGM\nDCL &N *DEC LEN(5 1)\nDCL &S *CHAR LEN(5)\nDCL &B *LGL\n" +
            "RTVDTAARA QGPL/PRICE &N\nRTVDTAARA (QGPL/TEXT (2 1)) &S\nRTVDTAARA QGPL/FLAG &B\n" +
            "SNDPGMMSG MSG(&N *CAT ':' *CAT &S *CAT ':' *CAT &B)\nENDPGM");
        var result = session.Execute("CALL QGPL/GETAREA"); Assert.False(result.IsError, result.Message); Assert.Equal("12.3:B    :1", result.Message?.Trim());
        Assert.True(session.Execute("CRTDTAARA QGPL/BAD *DEC (5 0) '12'").IsError);
        Assert.True(session.Execute("CRTDTAARA QGPL/BAD *CHAR 2 12").IsError);
        Execute(session, "DLTDTAARA QGPL/TEXT"); Assert.False(_system.Objects.Exists("QGPL", "TEXT", ObjectType.DataArea));
    }
    [Fact]
    public void CL_character_variable_remains_character_data_when_it_contains_digits()
    {
        ClExternalCallTests.Create(_system, "MAKEAREA", "CLP", "PGM\nDCL &TEXT *CHAR LEN(3) VALUE('123')\n" +
            "CRTDTAARA DTAARA(QGPL/DIGITS) TYPE(*CHAR) VALUE(&TEXT)\nCHGVAR &TEXT '456'\nCHGDTAARA DTAARA(QGPL/DIGITS) VALUE(&TEXT)\nENDPGM");
        using var session = Session(); Execute(session, "CALL QGPL/MAKEAREA"); Assert.Equal("456", Bytes("DIGITS").ToText());
    }
    [Fact]
    public void Cross_ccsid_retrieval_does_not_truncate_expanded_multibyte_text()
    {
        _store.Create("QGPL", "ACCENT", "*CHAR", 2, value: "éA", ccsid: 37);
        ClExternalCallTests.Create(_system, "NLSAREA", "CLP", "PGM\nDCL &TEXT *CHAR LEN(2) VALUE('OK')\nRTVDTAARA QGPL/ACCENT &TEXT\nMONMSG IPC0003\nSNDPGMMSG MSG(&TEXT)\nENDPGM");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: 1208), CancellationToken.None);
        var result = session.Execute("CALL QGPL/NLSAREA"); Assert.False(result.IsError, result.Message); Assert.Equal("OK", result.Message?.Trim());
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void CL_transfers_all_bytes_between_named_and_local_areas_with_byte_substrings(int ccsid)
    {
        var source = Enumerable.Range(0, 256).Select(n => (byte)n).ToArray();
        _store.Create("QGPL", "BYTES", "*CHAR", 256, value: new ProgramBuffer(source, ccsid), ccsid: ccsid);
        ClExternalCallTests.Create(_system, "RAWDA", "CLP", """
            PGM
            DCL &RAW *CHAR LEN(260)
            DCL &TAIL *CHAR LEN(2)
            RTVDTAARA QGPL/BYTES &RAW
            CHGDTAARA DTAARA(*LDA (1 260)) VALUE(&RAW)
            CHGVAR &RAW ' '
            RTVDTAARA DTAARA(*LDA (1 260)) RTNVAR(&RAW)
            CRTDTAARA DTAARA(QGPL/COPYBYTES) TYPE(*CHAR) LEN(260) VALUE(&RAW)
            RTVDTAARA DTAARA(QGPL/BYTES (255 2)) RTNVAR(&TAIL)
            CHGDTAARA DTAARA(QGPL/COPYBYTES (1 2)) VALUE(&TAIL)
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        Execute(session, "CALL QGPL/RAWDA");
        var padded = source.Concat(Enumerable.Repeat(ccsid == 37 ? (byte)0x40 : (byte)0x20, 4)).ToArray();
        Assert.Equal(padded, new JobDataAreaStore(_system.Connections).Read(session.Job.Key, JobDataArea.Local).Take(260));
        var expected = (byte[])padded.Clone(); expected[0] = 254; expected[1] = 255;
        Assert.Equal(expected, Bytes("COPYBYTES").ToArray()); Assert.Equal(source, Bytes("BYTES").ToArray());
        var display = session.Execute("DSPDTAARA QGPL/COPYBYTES"); Assert.False(display.IsError, display.Message);
        if (ccsid == 1208) Assert.Contains(display.Listing!, line => line.Contains("bytes FEFF0203"));
    }
    [Fact]
    public void Invalid_cross_ccsid_data_does_not_change_the_CL_receiver()
    {
        _store.Create("QGPL", "BINARY", "*CHAR", 2, value: new ProgramBuffer(new byte[] { 255, 0 }, 1208), ccsid: 1208);
        ClExternalCallTests.Create(_system, "BADENC", "CLP", "PGM\nDCL &TEXT *CHAR LEN(2) VALUE('OK')\nRTVDTAARA QGPL/BINARY &TEXT\nMONMSG IPC0136\nSNDPGMMSG MSG(&TEXT)\nENDPGM");
        using var session = Session(); var result = session.Execute("CALL QGPL/BADENC");
        Assert.False(result.IsError, result.Message); Assert.Equal("OK", result.Message?.Trim());
        Assert.Equal("FF00", Convert.ToHexString(Bytes("BINARY").ToArray()));
    }
    [Fact]
    public void Null_change_is_rejected_without_resetting_the_area()
    {
        _store.Create("QGPL", "KEEP", "*CHAR", 4, value: "KEEP");
        Assert.Throws<ArgumentNullException>(() => _store.Change("QGPL", "KEEP", null!));
        Assert.Equal("KEEP", Bytes("KEEP").ToText());
    }
    [Fact]
    public void Malformed_stored_payload_fails_before_read_or_change()
    {
        _store.Create("QGPL", "BROKEN", "*CHAR", 4, value: "OLD");
        var descriptor = _system.Objects.GetRequired("QGPL", "BROKEN", ObjectType.DataArea); var source = descriptor.Source!;
        foreach (var invalid in new[] { source.Replace("\"version\":1", "\"version\":2"), source.Replace("\"version\":1", "\"version\":1,\"version\":1"), source.Replace("\"length\":4", "\"length\":5") })
        {
            descriptor.Source = invalid; _system.Objects.Update(descriptor);
            Assert.Throws<CpfException>(() => _store.Read("QGPL", "BROKEN")); Assert.Throws<CpfException>(() => _store.Change("QGPL", "BROKEN", "NEW"));
            Assert.Equal(invalid, _system.Objects.GetRequired("QGPL", "BROKEN", ObjectType.DataArea).Source);
        }
    }
    [Fact]
    public void Copy_and_rename_preserve_independent_data_area_values()
    {
        _store.Create("QGPL", "ORIGINAL", "*CHAR", 4, value: "ORIG");
        using var session = Session(); Execute(session, "CRTDUPOBJ OBJ(ORIGINAL) FROMLIB(QGPL) OBJTYPE(*DTAARA) TOLIB(QGPL) NEWOBJ(COPY)");
        Execute(session, "RNMOBJ OBJ(QGPL/COPY) OBJTYPE(*DTAARA) NEWOBJ(RENAMED)");
        _store.Change("QGPL", "RENAMED", "NEW"); Assert.Equal("ORIG", Bytes("ORIGINAL").ToText()); Assert.Equal("NEW ", Bytes("RENAMED").ToText());
    }
    [Fact]
    public void Binary_character_buffers_survive_without_text_conversion()
    {
        var input = new ProgramBuffer(new byte[] { 0, 255, 128, 127 }, 1208);
        _store.Create("QGPL", "RAW", "*CHAR", 4, value: input, ccsid: 1208);
        Assert.Equal(input.ToArray(), Bytes("RAW").ToArray()); Assert.Throws<CpfException>(() => Bytes("RAW").ToText());
        _store.Change("QGPL", "RAW", new ProgramBuffer(new byte[] { 254 }, 1208), 2, 1);
        Assert.Equal(new byte[] { 0, 254, 128, 127 }, Bytes("RAW").ToArray());
    }
    [Fact]
    public void Values_survive_catalog_reopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-data-area-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = IpcSystem.Create(directory)) { first.Start(); new DataAreaStore(first.Connections).Create("QGPL", "DURABLE", "*DEC", 24, 9, -123456789012345.123456789m); }
            using var second = IpcSystem.Create(directory); second.Start();
            Assert.Equal(-123456789012345.123456789m, new DataAreaStore(second.Connections).Read("QGPL", "DURABLE").Value);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    public void Dispose() => _system.Dispose();
}
