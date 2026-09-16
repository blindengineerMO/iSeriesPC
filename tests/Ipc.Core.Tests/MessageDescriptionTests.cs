using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class MessageDescriptionTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly MessageDescriptionStore _store;
    public MessageDescriptionTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "MessageFileFixture22");
        _store = new(_system.Connections); _store.Create("QGPL", "TEXTS");
    }
    private static MessageDescription Description(string text, string format, int ccsid = 37)
        => new(text, "Detail &1", 40, MessageDescriptionFormat.Parse(format), ccsid);

    [Theory]
    [InlineData("*DEC 2", "058C", "58")]
    [InlineData("*DEC 4 2", "05810C", "58.10")]
    [InlineData("*DEC 5 2", "01234D", "-12.34")]
    [InlineData("*BIN 2", "8000", "-32768")]
    [InlineData("*BIN 4", "FFFFFFFF", "-1")]
    [InlineData("*BIN 8", "8000000000000000", "-9223372036854775808")]
    [InlineData("*UBIN 2", "FFFF", "65535")]
    [InlineData("*UBIN 4", "FFFFFFFF", "4294967295")]
    [InlineData("*UBIN 8", "FFFFFFFFFFFFFFFF", "18446744073709551615")]
    [InlineData("*HEX 2", "FF00", "X'FF00'")]
    public void Independent_replacement_fixtures_match_documented_formats(string format, string bytes, string expected)
    {
        var result = MessageDescriptionFormat.Format(Description("Value &1.", format), new(Convert.FromHexString(bytes), 37), 37);
        Assert.Equal("Value " + expected + ".", result.Text.ToText()); Assert.Equal("Detail " + expected, result.SecondLevel.ToText());
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Character_varying_fields_and_repeated_substitutions_preserve_bytes(int ccsid)
    {
        var data = CodePage.ToBytes(ccsid, "AB  ").Concat(new byte[] { 0, 3 }).Concat(CodePage.ToBytes(ccsid, "CD ")).Concat(new byte[] { 0, 0, 0, 2, 255, 0 }).ToArray();
        var description = Description("&1/&2/&3/&1", "(*CHAR 4) (*QTDCHAR *VARY 2) (*HEX *VARY 4)", ccsid);
        var result = MessageDescriptionFormat.Format(description, new(data, ccsid), ccsid);
        Assert.Equal("AB/'CD'/X'FF00'/AB", result.Text.ToText());
        var opaque = MessageDescriptionFormat.Format(Description("&1", "*CHAR 2", ccsid), new(new byte[] { 255, 0 }, ccsid), ccsid);
        Assert.Equal("FF00", Convert.ToHexString(opaque.Text.ToArray()));
    }

    [Fact]
    public void Only_CCHAR_replacement_bytes_are_transcoded()
    {
        var description = Description("&1/&2", "(*CHAR 1) (*CCHAR *VARY 2)", 37);
        var result = MessageDescriptionFormat.Format(description, new(new byte[] { 0xC1, 0, 1, 0xC1 }, 37), 1208);
        Assert.Equal("C12F41", Convert.ToHexString(result.Text.ToArray()));
    }

    [Theory]
    [InlineData("*CHAR 3", "C1")]
    [InlineData("*CHAR *VARY 2", "00")]
    [InlineData("*CHAR *VARY 4", "00000100C1")]
    [InlineData("*DEC 5 2", "12")]
    public void Short_replacement_fields_become_empty_without_out_of_bounds_reads(string format, string data)
    {
        var result = MessageDescriptionFormat.Format(Description("Missing [&1]", format), new(Convert.FromHexString(data), 37), 37);
        Assert.Equal("Missing []", result.Text.ToText());
    }

    [Theory]
    [InlineData("*CHAR *VARY 3")]
    [InlineData("*DEC")]
    [InlineData("*DEC 25 0")]
    [InlineData("*BIN 3")]
    [InlineData("*CCHAR 10")]
    [InlineData("*SPP 16")]
    [InlineData("*CHAR 513")]
    public void Invalid_or_unimplemented_field_formats_are_rejected(string format)
        => Assert.Throws<CpfException>(() => MessageDescriptionFormat.Parse(format));

    [Theory]
    [InlineData("*DEC 5 2", "012A4C")]
    [InlineData("*DEC 4 2", "15810C")]
    [InlineData("*CHAR *VARY 2", "FFFF")]
    public void Invalid_packed_and_varying_data_fails(string format, string data)
        => Assert.Throws<CpfException>(() => MessageDescriptionFormat.Format(Description("&1", format), new(Convert.FromHexString(data), 37), 37));

    [Fact]
    public void Changes_remove_and_ordered_listing_are_atomic_and_use_data_authority()
    {
        _store.Add("QGPL", "TEXTS", "USR0001", Description("Name &1", "*CHAR 4"));
        _store.Add("QGPL", "TEXTS", "USRA001", "Earlier");
        Assert.Equal(new[] { "USRA001", "USR0001" }, _store.List("QGPL", "TEXTS").Select(pair => pair.Key));
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "TEXTS", "USR0001", text: "Invalid &2", severity: 99));
        Assert.Equal(40, _store.GetDescription("QGPL", "TEXTS", "USR0001").Severity);
        _system.Security.Authority.Grant("QGPL", "TEXTS", ObjectType.MessageFile, "QUSER", Authorities.UseBits | AuthorityBit.Update);
        using (OperationIdentity.Enter("QUSER"))
        {
            _store.Change("QGPL", "TEXTS", "USR0001", secondLevel: "New detail &1", severity: 55);
            Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", "USR0002", "Denied"));
            Assert.Throws<CpfException>(() => _store.Remove("QGPL", "TEXTS", "USR0001"));
        }
        Assert.Equal("New detail &1", _store.GetDescription("QGPL", "TEXTS", "USR0001").SecondLevel);
        _store.Remove("QGPL", "TEXTS", "*ALL"); Assert.Empty(_store.List("QGPL", "TEXTS"));
    }

    [Fact]
    public void Legacy_text_is_preserved_when_another_description_upgrades_the_file()
    {
        var original = new string('A', 200) + " &1";
        var descriptor = _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile);
        descriptor.Source = global::System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Messages = new Dictionary<string, string> { ["OLDZZZZ"] = original } });
        _system.Objects.Update(descriptor); _store.Add("QGPL", "TEXTS", "USR0001", "New");
        Assert.Equal(original, _store.Get("QGPL", "TEXTS", "OLDZZZZ"));
        Assert.Equal(original, MessageDescriptionFormat.Format(_store.GetDescription("QGPL", "TEXTS", "OLDZZZZ"), new(Array.Empty<byte>(), 37), 37).Text.ToText());
        Assert.Contains("\"Version\":2", _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile).Source);
    }

    [Fact]
    public void Duplicate_json_and_unknown_versions_cannot_be_read_or_mutated()
    {
        foreach (var payload in new[] { "{\"Version\":2,\"Version\":1,\"Messages\":{}}", "{\"Version\":99,\"Messages\":{}}", "{\"Version\":2,\"Messages\":{},\"Ignored\":true}" })
        {
            var descriptor = _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile); descriptor.Source = payload; _system.Objects.Update(descriptor);
            Assert.Throws<CpfException>(() => _store.List("QGPL", "TEXTS"));
            Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", "USR0001", "New"));
            Assert.Equal(payload, _system.Objects.GetRequired("QGPL", "TEXTS", ObjectType.MessageFile).Source);
        }
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Commands_and_CL_retrieval_return_formatted_text_lengths_and_severity(int ccsid)
    {
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        void Execute(string command) { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
        Execute("ADDMSGD USR0001 QGPL/TEXTS 'Amount &1' SECLVL('Balance &1') SEV(40) FMT((*DEC 5 2))");
        ClExternalCallTests.Create(_system, "READTEXT", "CLP", """
            PGM
            DCL &TEXT *CHAR LEN(30)
            DCL &DETAIL *CHAR LEN(40)
            DCL &LENGTH *DEC LEN(5 0)
            DCL &SEV *DEC LEN(2 0)
            RTVMSG USR0001 QGPL/TEXTS MSGDTA(X'01234D') MSG(&TEXT) SECLVL(&DETAIL) MSGLEN(&LENGTH) SEV(&SEV)
            IF (&TEXT *EQ 'Amount -12.34' *AND &DETAIL *EQ 'Balance -12.34' *AND &LENGTH *EQ 13 *AND &SEV *EQ 40) THEN(SNDPGMMSG MSG('RETRIEVAL ACCEPTED'))
            ENDPGM
            """);
        var result = session.Execute("CALL QGPL/READTEXT"); Assert.False(result.IsError, result.Message); Assert.Contains("RETRIEVAL ACCEPTED", result.Message);
        Execute("CHGMSGD USR0001 QGPL/TEXTS MSG('Total &1') SEV(55)");
        Assert.Equal("Balance &1", _store.GetDescription("QGPL", "TEXTS", "USR0001").SecondLevel);
        Assert.Contains("Total &1", string.Join('\n', session.Execute("DSPMSGD USR0001 QGPL/TEXTS").Listing!));
        Execute("RMVMSGD USR0001 QGPL/TEXTS"); Execute("DLTMSGF QGPL/TEXTS");
        Assert.False(_system.Objects.Exists("QGPL", "TEXTS", ObjectType.MessageFile));
    }

    [Fact]
    public void Retrieval_validates_every_output_and_conversion_before_assignment()
    {
        _store.Add("QGPL", "TEXTS", "USR0001", Description("&1", "*DEC 5 2"));
        ClExternalCallTests.Create(_system, "BADTEXT", "CLP", """
            PGM
            DCL &TEXT *CHAR LEN(10) VALUE('UNCHANGED')
            DCL &BAD *CHAR LEN(2)
            RTVMSG USR0001 QGPL/TEXTS MSGDTA(X'01234C') MSG(&TEXT) SEV(&BAD)
            MONMSG IPC0003
            IF (&TEXT *NE 'UNCHANGED') THEN(RETURN)
            RTVMSG USR0001 QGPL/TEXTS MSGDTA(X'012A4C') MSG(&TEXT)
            MONMSG IPC0127
            IF (&TEXT *EQ 'UNCHANGED') THEN(SNDPGMMSG MSG('ATOMIC RETRIEVAL'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/BADTEXT"); Assert.False(result.IsError, result.Message); Assert.Contains("ATOMIC RETRIEVAL", result.Message);
    }

    [Fact]
    public async Task Concurrent_description_updates_do_not_lose_other_messages()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => _store.Add("QGPL", "TEXTS", "USR" + index.ToString("X4"), "Message " + index))));
        Assert.Equal(12, _store.List("QGPL", "TEXTS").Count);
    }

    [Theory]
    [InlineData("USR00ZZ", "Text")]
    [InlineData("1SR0001", "Text")]
    [InlineData("USR0001", "Undefined &1")]
    public void Invalid_ids_and_undefined_substitution_fields_do_not_change_the_file(string id, string text)
    {
        Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", id, text)); Assert.Empty(_store.List("QGPL", "TEXTS"));
    }

    [Fact]
    public void New_text_and_metadata_limits_are_enforced_before_mutation()
    {
        Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", "USR0001", new string('A', 133)));
        Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", "USR0001", new MessageDescription("Text", new string('A', 3001), 0, Array.Empty<MessageDataField>(), 37)));
        Assert.Throws<CpfException>(() => _store.Add("QGPL", "TEXTS", "USR0001", new MessageDescription("Text", "", 100, Array.Empty<MessageDataField>(), 37)));
        _store.Add("QGPL", "TEXTS", "USR0001", new MessageDescription("Text", "", 0, Array.Empty<MessageDataField>(), 37, "YES"));
        Assert.Throws<CpfException>(() => _store.Change("QGPL", "TEXTS", "USR0001", defaultReply: new string('A', 133), replaceDefault: true));
        Assert.Equal("YES", _store.GetDescription("QGPL", "TEXTS", "USR0001").DefaultReply);
    }

    public void Dispose() => _system.Dispose();
}
