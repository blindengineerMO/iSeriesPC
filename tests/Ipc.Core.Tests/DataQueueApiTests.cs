using System.Buffers.Binary;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class DataQueueApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public DataQueueApiTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DataQueueApiFixture25");
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR", CancellationToken token = default)
        => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), token);
    private DataQueueStore Store => new(_system.Connections);
    private void Queue(bool keyed = false, bool sender = false) => Store.Create(new("QGPL", "APIQUEUE"), 64,
        keyed ? DataQueueSequence.Keyed : DataQueueSequence.Fifo, keyed ? 2 : 0, sender);
    private static void Ok(Ipc.Cl.Commands.CommandResult result) => Assert.False(result.IsError, result.Message);
    private static ProgramBuffer Hex(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
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
    private static Cell Bytes(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
    private static Cell Error(int provided = 16, int size = 20)
    {
        var bytes = Enumerable.Repeat((byte)0xAA, size).ToArray(); BinaryPrimitives.WriteInt32BigEndian(bytes, provided); return new(bytes);
    }
    private static object?[] Receive(Cell length, Cell data, Cell? key = null, Cell? sender = null, int senderLength = 0,
        string remove = "*YES", int size = 0, object? error = null, string order = "EQ")
        => ["APIQUEUE", "QGPL", length.Argument, data.Argument, 0m, order, key is null ? 0m : 2m,
            key?.Argument ?? (object)"", (decimal)senderLength, sender?.Argument ?? (object)"", remove, (decimal)size, error ?? Hex("00000000")];

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_uses_exact_packed_layouts_and_preserves_trailing_receiver_bytes(int ccsid)
    {
        Queue(keyed: true, sender: true);
        ClExternalCallTests.Create(_system, "DQAPI", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(8) VALUE(X'FF00FE0102030405')
            DCL &OUT *CHAR LEN(8) VALUE(X'0707070707070707')
            DCL &KEY *CHAR LEN(2) VALUE(X'0011')
            DCL &LENGTH *DEC LEN(5 0) VALUE(99)
            DCL &SENDER *CHAR LEN(44)
            CALL QSYS/QSNDDTAQ PARM('APIQUEUE' 'QGPL' (4 (*DEC 5 0)) &DATA (2 (*DEC 3 0)) &KEY '*NO' '*NO')
            CALL QSYS/QRCVDTAQ PARM('APIQUEUE' 'QGPL' &LENGTH &OUT (0 (*DEC 5 0)) 'GE' (2 (*DEC 3 0)) &KEY (44 (*DEC 3 0)) &SENDER '*NO' (8 (*DEC 5 0)) X'00000000' '*MSEC')
            IF (&LENGTH *NE 4 *OR &OUT *NE X'FF00FE0107070707' *OR %SST(&SENDER 1 8) *NE X'0000044C0000044C') THEN(RETURN)
            CALL QSYS/QCLRDTAQ PARM('APIQUEUE' 'QGPL' 'EQ' (2 (*DEC 3 0)) &KEY X'00000000')
            CALL QSYS/QRCVDTAQ PARM('APIQUEUE' 'QGPL' &LENGTH &OUT (0 (*DEC 5 0)) 'EQ' (2 (*DEC 3 0)) &KEY (0 (*DEC 3 0)) '')
            IF (&LENGTH *EQ 0 *AND &OUT *EQ X'FF00FE0107070707') THEN(SNDPGMMSG MSG('NATIVE QUEUE API ACCEPTED'))
            ENDPGM
            """);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/DQAPI"); Ok(result);
        Assert.Contains("NATIVE QUEUE API ACCEPTED", result.Message); Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Explicit_receiver_size_truncates_and_returns_full_length_while_peek_retains_entry(int size)
    {
        Queue(keyed: true); Store.Send(new("QGPL", "APIQUEUE"), Hex("FF00FE01"), Hex("0022"));
        using var session = Session(); var length = Bytes("00999C"); var data = Bytes("070707070707"); var key = Bytes("001100");
        var before = data.Buffer.ToArray(); var values = Receive(length, data, key, remove: "*NO", size: size, order: "GE");
        Ok(session.ExecuteProgram("QRCVDTAQ", values)); Assert.Equal("00004C", length.Hex); Assert.Equal("002200", key.Hex);
        var expected = before; Hex("FF00FE01").ToArray().AsSpan(0, size).CopyTo(expected);
        Assert.Equal(expected, data.Buffer.ToArray()); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        values[10] = "*YES"; Ok(session.ExecuteProgram("QRCVDTAQ", values)); Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        var last = data.Hex; Ok(session.ExecuteProgram("QRCVDTAQ", values)); Assert.Equal("00000C", length.Hex); Assert.Equal(last, data.Hex); Assert.Equal("002200", key.Hex);
    }

    [Fact]
    public void Short_receiver_and_immutable_or_aliased_outputs_leave_entry_and_outputs_unchanged()
    {
        Queue(); Store.Send(new("QGPL", "APIQUEUE"), Hex("FF00FE01")); using var session = Session();
        var length = Bytes("00999C"); var data = Bytes("0707");
        Assert.Equal("CPF9506", session.ExecuteProgram("QRCVDTAQ", ["APIQUEUE", "QGPL", length.Argument, data.Argument, 0m]).MessageId);
        Assert.Equal("00999C", length.Hex); Assert.Equal("0707", data.Hex);
        Assert.Equal("CPF3C90", session.ExecuteProgram("QRCVDTAQ", ["APIQUEUE", "QGPL", Hex("00000C"), data.Argument, 0m]).MessageId);
        Assert.Equal("CPF3C3A", session.ExecuteProgram("QRCVDTAQ", ["APIQUEUE", "QGPL", length.Argument, length.Argument, 0m]).MessageId);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(44)]
    [InlineData(50)]
    public void Sender_receiver_has_independent_packed_headers_and_native_field_offsets(int requested)
    {
        Queue(sender: true);
        using (OperationIdentity.Enter("QSECOFR", new(123456, "SENDJOB", "QUSER"))) Store.Send(new("QGPL", "APIQUEUE"), Hex("0102"));
        using var session = Session(); var length = Bytes("00000C"); var data = Bytes("0707"); var sender = new Cell(Enumerable.Repeat((byte)0xAA, 52).ToArray());
        Ok(session.ExecuteProgram("QRCVDTAQ", Receive(length, data, sender: sender, senderLength: requested, size: 2)));
        var expected = new byte[44]; Hex("0000044C0000044C").ToArray().CopyTo(expected, 0);
        CodePage.ToBytes(37, "SENDJOB   QUSER     123456QSECOFR   ").CopyTo(expected, 8);
        var returned = requested switch { 8 => "0000008C", 12 => "0000012C", _ => "0000044C" };
        Hex(returned).ToArray().CopyTo(expected, 0);
        Assert.Equal(expected.AsSpan(0, Math.Min(44, requested)).ToArray(), sender.Buffer.ToArray().AsSpan(0, Math.Min(44, requested)).ToArray());
        Assert.All(sender.Buffer.ToArray().Skip(Math.Min(44, requested)), b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void Missing_sender_metadata_returns_only_headers_and_bad_sender_layout_does_not_dequeue()
    {
        Queue(); Store.Send(new("QGPL", "APIQUEUE"), Hex("0102")); using var session = Session();
        var length = Bytes("00999C"); var data = Bytes("0707"); var sender = new Cell(Enumerable.Repeat((byte)0xAA, 44).ToArray());
        Ok(session.ExecuteProgram("QRCVDTAQ", Receive(length, data, sender: sender, senderLength: 44, size: 2)));
        Assert.StartsWith("0000008C0000008C", sender.Hex); Assert.All(sender.Buffer.ToArray().Skip(8), b => Assert.Equal(0xAA, b));
        Store.Send(new("QGPL", "APIQUEUE"), Hex("0102"));
        Assert.Equal("CPF3C3A", session.ExecuteProgram("QRCVDTAQ", Receive(length, data, sender: sender, senderLength: 7, size: 2)).MessageId);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(37, "UNENCODE😀", 1)]
    [InlineData(1208, "QUSER", 1000000)]
    public void Entry_dependent_sender_marshalling_failure_preserves_entry_and_every_output(int ccsid, string principal, int jobNumber)
    {
        Queue(sender: true);
        // Insert a valid stored entry, then model historical metadata that cannot fit the native sender format.
        Store.Send(new("QGPL", "APIQUEUE"), Hex("0102", ccsid));
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sys_data_queue_entries SET sender=$user,sender_job=$job,sender_job_name='SENDER',sender_job_user='QUSER'";
            command.Parameters.AddWithValue("$user", principal); command.Parameters.AddWithValue("$job", jobNumber); command.ExecuteNonQuery();
        }
        using var session = Session(ccsid); var length = Bytes("00999C", ccsid); var data = Bytes("0707", ccsid); var sender = new Cell(new byte[44], ccsid);
        var values = Receive(length, data, sender: sender, senderLength: 44, size: 2); values[12] = Hex("00000000", ccsid);
        Assert.True(session.ExecuteProgram("QRCVDTAQ", values).IsError);
        Assert.Equal("00999C", length.Hex); Assert.Equal("0707", data.Hex); Assert.All(sender.Buffer.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void Error_receiver_reports_available_bytes_and_partial_message_id_without_changing_other_storage(int provided)
    {
        Queue(); Store.Send(new("QGPL", "APIQUEUE"), Hex("0102")); using var session = Session();
        var length = Bytes("00999C"); var data = Bytes("0707"); var error = Error(provided);
        Ok(session.ExecuteProgram("QRCVDTAQ", Receive(length, data, size: 3, error: error.Argument)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(error.Buffer.ToArray().AsSpan(4)));
        Assert.Equal(CodePage.ToBytes(37, "CPF3C3A").Take(Math.Min(7, provided - 8)), error.Buffer.ToArray().Skip(8).Take(Math.Min(7, provided - 8)));
        Assert.All(error.Buffer.ToArray().Skip(provided), b => Assert.Equal(0xAA, b));
        Assert.Equal("00999C", length.Hex); Assert.Equal("0707", data.Hex); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        var success = Error(provided); Ok(session.ExecuteProgram("QCLRDTAQ", ["APIQUEUE", "QGPL", "", 0m, "", success.Argument]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(success.Buffer.ToArray().AsSpan(4))); Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(21)]
    public void Invalid_error_receiver_is_rejected_before_clear(int provided)
    {
        Queue(); Store.Send(new("QGPL", "APIQUEUE"), Hex("0102")); using var session = Session(); var error = Error(provided);
        Assert.Equal("CPF3CF1", session.ExecuteProgram("QCLRDTAQ", ["APIQUEUE", "QGPL", "", 0m, "", error.Argument]).MessageId);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries); Assert.All(error.Buffer.ToArray().Skip(4), b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void Zero_error_length_escapes_and_error_alias_is_rejected_without_writes()
    {
        Queue(); using var session = Session(); var error = Error(); var initial = error.Hex;
        Assert.Equal("CPF9801", session.ExecuteProgram("QCLRDTAQ", ["MISSING", "QGPL", "", 0m, "", Hex("00000000")]).MessageId);
        Assert.Equal("CPF3C3A", session.ExecuteProgram("QCLRDTAQ", ["APIQUEUE", "QGPL", "", 0m, error.Argument, error.Argument]).MessageId);
        Assert.Equal(initial, error.Hex);
    }

    [Theory]
    [InlineData("QSNDDTAQ", 3)]
    [InlineData("QSNDDTAQ", 5)]
    [InlineData("QSNDDTAQ", 9)]
    [InlineData("QRCVDTAQ", 4)]
    [InlineData("QRCVDTAQ", 6)]
    [InlineData("QRCVDTAQ", 9)]
    [InlineData("QRCVDTAQ", 11)]
    [InlineData("QRCVDTAQ", 12)]
    [InlineData("QRCVDTAQ", 15)]
    [InlineData("QCLRDTAQ", 3)]
    [InlineData("QCLRDTAQ", 5)]
    public void Partial_optional_groups_fail_with_parameter_count_error(string api, int count)
    {
        using var session = Session(); Assert.Equal("CPF3C36", session.ExecuteProgram(api, Enumerable.Repeat<object?>(null, count).ToArray()).MessageId);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("(3 (*DEC 3 0))")]
    [InlineData("(3 (*DEC 5 1))")]
    [InlineData("X'000037'")]
    [InlineData("X'000A3C'")]
    [InlineData("X'003C'")]
    [InlineData("X'000000'")]
    public void Send_rejects_mismatched_and_malformed_packed_layouts(string value)
    {
        Queue(); using var session = Session(); Assert.True(session.Execute("CALL QSNDDTAQ PARM('APIQUEUE' 'QGPL' " + value + " 'ABC')").IsError);
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Independent_packed_input_fixtures_and_clear_key_selection_share_the_durable_queue()
    {
        Queue(keyed: true); using var session = Session();
        foreach (var key in new[] { "0001", "0002", "0003" })
            Ok(session.ExecuteProgram("QSNDDTAQ", ["APIQUEUE", "*LIBL", Hex("00003C"), Hex("FF00FE"), Hex("002C"), Hex(key)]));
        Ok(session.ExecuteProgram("QCLRDTAQ", ["APIQUEUE", "*CURLIB", "LE", Hex("002C"), Hex("0002"), Hex("00000000")]));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        Assert.Equal("0003", Convert.ToHexString(Store.Receive(new("QGPL", "APIQUEUE"), Hex("0000"), DataQueueKeyOrder.Greater)!.Key.ToArray()));
    }

    [Theory]
    [InlineData("*YES", "*NO")]
    [InlineData("*NO", "*YES")]
    public void Unsupported_async_and_journal_modes_fail_before_send(string asyncRequest, string journal)
    {
        Queue(); using var session = Session();
        Assert.Equal("CPF3C3A", session.ExecuteProgram("QSNDDTAQ", ["APIQUEUE", "QGPL", 2m, Hex("0102"), 0m, "", asyncRequest, journal]).MessageId);
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(4, "fraction")]
    [InlineData(5, "BAD")]
    [InlineData(6, "negative")]
    [InlineData(8, "negative")]
    [InlineData(10, "BAD")]
    [InlineData(11, "negative")]
    [InlineData(13, "*MIN")]
    public void Invalid_receive_controls_leave_entry_and_receivers_unchanged(int index, string bad)
    {
        Queue(); Store.Send(new("QGPL", "APIQUEUE"), Hex("0102")); using var session = Session();
        var length = Bytes("00999C"); var data = Bytes("0707");
        var values = Receive(length, data, size: 2).Append<object?>("*SEC").ToArray();
        values[index] = bad == "negative" ? -1m : bad == "fraction" ? 0.1m : bad;
        // Key order is ignored with a zero key length, as specified by the API.
        if (index == 5) { values[6] = 2m; values[7] = Bytes("0102").Argument; }
        Assert.True(session.ExecuteProgram("QRCVDTAQ", values).IsError); Assert.Equal("00999C", length.Hex); Assert.Equal("0707", data.Hex);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Maximum_entry_and_large_explicit_receiver_are_checked_by_native_packed_lengths()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 64512); using var session = Session();
        var bytes = Enumerable.Range(0, 64513).Select(i => (byte)i).ToArray();
        Ok(session.ExecuteProgram("QSNDDTAQ", ["APIQUEUE", "QGPL", Hex("64512C"), new ProgramBuffer(bytes, 37)]));
        Assert.True(session.ExecuteProgram("QSNDDTAQ", ["APIQUEUE", "QGPL", Hex("64513C"), new ProgramBuffer(bytes, 37)]).IsError);
        var length = Bytes("00000C"); var data = new Cell(new byte[99999]);
        Ok(session.ExecuteProgram("QRCVDTAQ", Receive(length, data, size: 99999)));
        Assert.Equal("64512C", length.Hex); Assert.Equal(bytes.Take(64512), data.Buffer.ToArray().Take(64512));
        Assert.All(data.Buffer.ToArray().Skip(64512), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Revoking_signing_trust_prevents_native_queue_API_execution()
    {
        Queue(); var ca = _system.Certificates.CreateAuthority("DQAPIROOT", "Data queue API tests");
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("DQAPISIGN", "Data queue API signer", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QSNDDTAQ", ObjectType.Program, signer.Id);
        using var session = Session(); object?[] values = ["APIQUEUE", "QGPL", 2m, Hex("0102")];
        Ok(session.ExecuteProgram("QSNDDTAQ", values));
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, false);
        Assert.True(session.ExecuteProgram("QSNDDTAQ", values).IsError); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Send_only_authority_library_lookup_and_live_api_and_queue_revocation_apply()
    {
        Queue(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "ApiUserFixture25");
        _system.Security.Authority.Grant("QGPL", "APIQUEUE", ObjectType.DataQueue, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var session = Session(user: "QUSER"); object?[] send = ["APIQUEUE", "*LIBL", 2m, Hex("0102")];
        Ok(session.ExecuteProgram("QSNDDTAQ", send));
        Assert.Equal("CPF9802", session.ExecuteProgram("QCLRDTAQ", ["APIQUEUE", "*LIBL"]).MessageId);
        _system.Security.Authority.Grant("QGPL", "APIQUEUE", ObjectType.DataQueue, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSNDDTAQ", send).MessageId);
        _system.Security.Authority.Grant("QGPL", "APIQUEUE", ObjectType.DataQueue, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        _system.Security.Authority.Grant("QSYS", "QSNDDTAQ", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSNDDTAQ", send).MessageId);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public async Task Negative_wait_cancels_with_no_output_changes_and_millisecond_wait_can_receive_from_another_job()
    {
        Queue(); var length = Bytes("00999C"); var data = Bytes("07070707"); using var cancellation = new CancellationTokenSource();
        using (var receiver = Session(token: cancellation.Token))
        {
            cancellation.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => receiver.ExecuteProgram("QRCVDTAQ", ["APIQUEUE", "QGPL", length.Argument, data.Argument, -1m])));
        }
        Assert.Equal("00999C", length.Hex); Assert.Equal("07070707", data.Hex);
        using var reader = Session(); using var sender = Session();
        var values = Receive(length, data, size: 4).Append<object?>("*MSEC").ToArray(); values[4] = 5000m;
        var running = Task.Run(() => reader.ExecuteProgram("QRCVDTAQ", values));
        try { Ok(sender.ExecuteProgram("QSNDDTAQ", ["APIQUEUE", "QGPL", 2m, Hex("FF00")])); Ok(await running.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancellation.Cancel(); }
        Assert.Equal("00002C", length.Hex); Assert.Equal("FF000707", data.Hex);
    }

    [Fact]
    public void CL_monitors_escape_and_RPG_can_call_a_CL_wrapper_with_native_API_marshalling()
    {
        Queue();
        ClExternalCallTests.Create(_system, "DQWRAP", "CLP", """
            PGM
            DCL &LENGTH *DEC LEN(5 0)
            DCL &DATA *CHAR LEN(8)
            CALL QRCVDTAQ PARM('MISSING' 'QGPL' &LENGTH &DATA (0 (*DEC 5 0)))
            MONMSG CPF9801
            CALL QSNDDTAQ PARM('APIQUEUE' 'QGPL' (5 (*DEC 5 0)) 'READY')
            CALL QRCVDTAQ PARM('APIQUEUE' 'QGPL' &LENGTH &DATA (0 (*DEC 5 0)))
            CALL QCLRDTAQ PARM('APIQUEUE' 'QGPL')
            IF (&LENGTH *EQ 5 *AND &DATA *EQ 'READY') THEN(SNDPGMMSG MSG('RPG QUEUE BRIDGE ACCEPTED'))
            ENDPGM
            """);
        ClExternalCallTests.Create(_system, "RPGDQ", "RPG", ClExternalCallTests.Calculation("'DQWRAP'", "CALL"));
        using var session = Session(); var result = session.Execute("CALL RPGDQ"); Ok(result); Assert.Contains("RPG QUEUE BRIDGE ACCEPTED", result.Message);
    }

    [Theory]
    [InlineData("QSNDDTAQ")]
    [InlineData("QRCVDTAQ")]
    [InlineData("QCLRDTAQ")]
    public void Native_adapter_manifests_cannot_be_relocated_tampered_or_overwrite_local_programs(string api)
    {
        var descriptor = _system.Objects.GetRequired("QSYS", api, ObjectType.Program);
        descriptor.Source = "{}"; Assert.Throws<CpfException>(() => _system.Objects.Update(descriptor));
        Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", api), ObjectType = ObjectType.Program, Attribute = BuiltinProgramService.Attribute, Source = "{}" }));
        descriptor.Attribute = "CLP"; descriptor.Source = "SNDPGMMSG MSG('LOCAL PROGRAM')"; _system.Objects.Update(descriptor);
        BuiltinProgramService.SeedDefaults(_system.Connections); using var session = Session(); Assert.Equal("LOCAL PROGRAM", session.Execute("CALL QSYS/" + api).Message);
    }
    public void Dispose() => _system.Dispose();
}
