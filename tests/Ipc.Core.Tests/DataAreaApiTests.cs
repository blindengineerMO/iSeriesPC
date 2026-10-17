using System.Buffers.Binary;
using System.Globalization;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class DataAreaApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private DataAreaStore Areas => new(_system.Connections);
    public DataAreaApiTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DataAreaApiFixture26");
    }
    private DataAreaApi Api(int ccsid = 37, JobKey? job = null, CancellationToken token = default) =>
        new(_system.Connections, selector => _system.SearchLibraries(null, selector), ccsid, job, token);
    private static string Name(string name = "AREA", string library = "QGPL") => name.PadRight(10) + library.PadRight(10);
    private static ProgramBuffer Hex(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
    private sealed class Cell
    {
        public ProgramBuffer Buffer { get; private set; }
        public ProgramArgument Argument { get; }
        public Cell(int length, int ccsid = 37, byte fill = 0xAA)
        {
            Buffer = new(Enumerable.Repeat(fill, length).ToArray(), ccsid);
            Argument = new(() => Buffer, value => Buffer = (ProgramBuffer)value!, value => value is ProgramBuffer b && b.Length == length && b.Ccsid == ccsid
                ? b : throw new CpfException("CPF3C3A", "Invalid test receiver."), "*CHAR", length, buffer: () => Buffer);
        }
        public int Int(int offset) => BinaryPrimitives.ReadInt32BigEndian(Buffer.ToArray().AsSpan(offset));
        public string Text(int offset, int count) => new ProgramBuffer(Buffer.ToArray().AsSpan(offset, count), Buffer.Ccsid).ToText();
        public byte[] Data => Buffer.ToArray();
    }
    private static Cell Error(int ccsid = 37)
    {
        var cell = new Cell(20, ccsid); var bytes = cell.Data; BinaryPrimitives.WriteInt32BigEndian(bytes, 16);
        cell.Argument.Value = new ProgramBuffer(bytes, ccsid); return cell;
    }
    private Cell Retrieve(string qualified, int start = -1, int length = 1, int receiverLength = 2040, int ccsid = 37, DataAreaApi? api = null)
    {
        var output = new Cell(receiverLength, ccsid);
        (api ?? Api(ccsid)).Retrieve([output.Argument, receiverLength, qualified, start, length, Hex("00000000", ccsid)]);
        return output;
    }

    [Theory]
    [InlineData(37, "5CC3C8C1D94040404040", "D8C7D7D3404040404040")]
    [InlineData(1208, "2A434841522020202020", "5147504C202020202020")]
    public void Header_and_raw_payload_match_independent_native_vectors(int ccsid, string type, string library)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4, value: Hex("FF00FE01", ccsid), ccsid: ccsid);
        var output = Retrieve(Name(), receiverLength: 44, ccsid: ccsid);
        Assert.Equal("0000002800000028" + type + library + "0000000400000000FF00FE01AAAAAAAA", Convert.ToHexString(output.Data));
    }
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(28)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(38)]
    public void Truncated_receivers_report_available_before_returned_and_preserve_tails(int length)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4, value: "ABCD");
        var output = new Cell(44); Api().Retrieve([output.Argument, length, Name(), -1, 1, Hex("00000000")]);
        Assert.Equal(40, output.Int(0)); Assert.Equal(length, output.Int(4)); Assert.All(output.Data[length..], b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(5, 2, "-123.45", "12345D")]
    [InlineData(6, 2, "1234.56", "0123456C")]
    [InlineData(1, 0, "0", "0C")]
    [InlineData(24, 9, "123456789012345.123456789", "0123456789012345123456789C")]
    public void Decimal_values_use_packed_bytes_and_report_digit_precision(int digits, int decimals, string value, string hex)
    {
        Areas.Create("QGPL", "AREA", "*DEC", digits, decimals, decimal.Parse(value, CultureInfo.InvariantCulture));
        var output = Retrieve(Name(), receiverLength: 64);
        Assert.Equal(digits, output.Int(28)); Assert.Equal(decimals, output.Int(32)); Assert.Equal("*DEC      ", output.Text(8, 10));
        var expected = Convert.FromHexString(hex); Assert.Equal(36 + expected.Length, output.Int(0));
        Assert.Equal(expected, output.Data.AsSpan(36, expected.Length).ToArray()); Assert.All(output.Data[(36 + expected.Length)..], b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(37, true, 0xF1)]
    [InlineData(37, false, 0xF0)]
    [InlineData(1208, true, 0x31)]
    [InlineData(1208, false, 0x30)]
    public void Logical_values_are_encoded_in_the_callers_ccsid(int ccsid, bool value, int expected)
    {
        Areas.Create("QGPL", "AREA", "*LGL", 1, value: value);
        var output = Retrieve(Name(), receiverLength: 38, ccsid: ccsid);
        Assert.Equal(37, output.Int(0)); Assert.Equal(1, output.Int(28)); Assert.Equal(0, output.Int(32));
        Assert.Equal("*LGL      ", output.Text(8, 10)); Assert.Equal(expected, output.Data[36]); Assert.Equal(0xAA, output.Data[37]);
    }
    [Fact]
    public void Substrings_are_one_based_and_all_ignores_length_for_value_selection()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4, value: Hex("FF00FE01"));
        var part = Retrieve(Name(), 2, 2, 44); Assert.Equal("00FE", Convert.ToHexString(part.Data[36..38]));
        Assert.Equal(2, part.Int(28)); Assert.Equal(38, part.Int(0));
        var all = Retrieve(Name(), -1, int.MaxValue, 44); Assert.Equal(40, all.Int(0)); Assert.Equal(40, all.Int(4));
        Assert.Equal("FF00FE0140404040", Convert.ToHexString(all.Data[36..]));
    }
    [Theory]
    [InlineData(0, 1, "CPF1088")]
    [InlineData(-2, 1, "CPF1088")]
    [InlineData(2001, 1, "CPF1088")]
    [InlineData(5, 1, "CPF1088")]
    [InlineData(4, 2, "CPF1089")]
    [InlineData(1, int.MaxValue, "CPF1089")]
    [InlineData(1, 0, "CPF1863")]
    [InlineData(-1, -1, "CPF1863")]
    public void Invalid_ranges_leave_receivers_unchanged(int start, int length, string message)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); var output = new Cell(40);
        Assert.Equal(message, Assert.Throws<CpfException>(() => Api().Retrieve([output.Argument, 40, Name(), start, length, Hex("00000000")])).MessageId);
        Assert.All(output.Data, b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(7)]
    [InlineData(41)]
    [InlineData(int.MaxValue)]
    public void Invalid_receiver_lengths_fail_before_assignment(int length)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); var output = new Cell(40);
        Assert.Equal("CPF3C24", Assert.Throws<CpfException>(() => Api().Retrieve([output.Argument, length, Name(), -1, 1, Hex("00000000")])).MessageId);
        Assert.All(output.Data, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Error_headers_and_output_validation_preserve_data_and_untouched_bytes()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); var error = Error(); var output = new Cell(40);
        Api().Retrieve([output.Argument, 40, Name(), 4, 2, error.Argument]);
        Assert.Equal(16, error.Int(4)); Assert.Equal("CPF1089", error.Text(8, 7)); Assert.All(output.Data, b => Assert.Equal(0xAA, b));
        Assert.All(error.Data[16..], b => Assert.Equal(0xAA, b));
        Api().Retrieve([output.Argument, 40, Name(), -1, 1, error.Argument]); Assert.Equal(0, error.Int(4));
        var alias = Error(); Api().Retrieve([alias.Argument, 20, Name(), -1, 1, alias.Argument]); Assert.Equal("CPF3C3A", alias.Text(8, 7));
        Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => Api().Retrieve([Hex(new string('0', 80)), 40, Name(), -1, 1, Hex("00000000")])).MessageId);
        var bad = new ProgramArgument(() => output.Buffer, _ => throw new InvalidOperationException(), _ => throw new CpfException("CPF3C19", "Rejected normalization"), "*CHAR", 40, buffer: () => output.Buffer);
        var before = output.Data;
        Assert.Equal("CPF3C19", Assert.Throws<CpfException>(() => Api().Retrieve([bad, 40, Name(), -1, 1, Hex("00000000")])).MessageId);
        Assert.Equal(before, output.Data);
    }
    [Fact]
    public void Cross_ccsid_text_conversion_is_checked_and_raw_same_ccsid_bytes_are_preserved()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 3, value: "éAB", ccsid: 37);
        var output = Retrieve(Name(), receiverLength: 44, ccsid: 1208); Assert.Equal("C3A94142", Convert.ToHexString(output.Data[36..40])); Assert.Equal(4, output.Int(28));
        Areas.Create("QGPL", "UTF8", "*CHAR", 2, value: Hex("C3A9", 1208), ccsid: 1208);
        var bad = new Cell(40); var error = Error(); Api().Retrieve([bad.Argument, 40, Name("UTF8"), 1, 1, error.Argument]);
        Assert.Equal("CPF3C19", error.Text(8, 7)); Assert.All(bad.Data, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Library_selection_stops_at_denied_first_match_and_reports_native_missing_ids()
    {
        _system.Libraries.CreateLibrary("FIRST"); _system.Libraries.CreateLibrary("SECOND");
        Areas.Create("FIRST", "AREA", "*CHAR", 2, value: "AB", authority: "*EXCLUDE");
        Areas.Create("SECOND", "AREA", "*CHAR", 2, value: "CD");
        var api = new DataAreaApi(_system.Connections, _ => ["FIRST", "SECOND"]);
        using (OperationIdentity.Enter("QUSER"))
        {
            Assert.Equal("CPF1016", Assert.Throws<CpfException>(() => Retrieve(Name(library: "*LIBL"), api: api)).MessageId);
        }
        var output = Retrieve(Name(library: "*LIBL"), api: api); Assert.Equal("FIRST     ", output.Text(18, 10));
        Assert.Equal("CPF1015", Assert.Throws<CpfException>(() => Retrieve(Name("MISSING"))).MessageId);
        Assert.Equal("CPF1021", Assert.Throws<CpfException>(() => Retrieve(Name(library: "MISSING"))).MessageId);
        _system.Security.Authority.Grant("QSYS", "FIRST", ObjectType.Library, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF1022", Assert.Throws<CpfException>(() => Retrieve(Name(library: "FIRST"))).MessageId);
    }
    [Theory]
    [InlineData(JobLockMode.Exclusive, false)]
    [InlineData(JobLockMode.ExclusiveRead, true)]
    [InlineData(JobLockMode.SharedUpdate, true)]
    public void Retrieval_uses_native_shared_read_allocation(JobLockMode mode, bool readable)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); var holder = _system.Jobs.CreateInteractive("QSECOFR"); var reader = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections);
        using var held = locks.Acquire(holder.Key, new("QGPL", "AREA", ObjectType.DataArea), mode, TimeSpan.Zero);
        using var identity = OperationIdentity.Enter("QSECOFR", reader.Key); using var command = locks.EnterCommand(reader.Key);
        if (readable) Assert.Equal(40, Retrieve(Name()).Int(0));
        else Assert.Equal("CPF1063", Assert.Throws<CpfException>(() => Retrieve(Name())).MessageId);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Special_areas_and_qtemp_are_job_owned_and_library_fields_are_logical(int ccsid)
    {
        var own = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid); var other = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid);
        var group = _system.Jobs.CreateGroupJob(own.Key, "GROUP");
        using (OperationIdentity.Enter("QSECOFR", own.Key))
        {
            new JobDataAreaStore(_system.Connections).Write(own.Key, JobDataArea.Local, 0, [0xFE]);
            new JobDataAreaStore(_system.Connections).Write(own.Key, JobDataArea.Group, 0, [0xAB]);
            Areas.Create("QTEMP", "AREA", "*CHAR", 4, ccsid: ccsid);
            var local = Retrieve(Name("*LDA", ""), 1, 1, 40, ccsid); Assert.Equal(0xFE, local.Data[36]); Assert.Equal(new string(' ', 10), local.Text(18, 10));
            var temp = Retrieve(Name(library: "QTEMP"), receiverLength: 40, ccsid: ccsid); Assert.Equal("QTEMP     ", temp.Text(18, 10));
            Assert.Throws<CpfException>(() => Retrieve(Name("*LDA", "QGPL"), ccsid: ccsid));
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Retrieve(Name("*LDA", ""), ccsid: ccsid, api: Api(ccsid, other.Key))).MessageId);
        }
        using (OperationIdentity.Enter("QSECOFR", group.Key)) Assert.Equal(0xAB, Retrieve(Name("*GDA", ""), 1, 1, 40, ccsid).Data[36]);
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        {
            Assert.Equal("CPF1015", Assert.Throws<CpfException>(() => Retrieve(Name(library: "QTEMP"), ccsid: ccsid)).MessageId);
            Assert.Equal("CPF1046", Assert.Throws<CpfException>(() => Retrieve(Name("*GDA", ""), ccsid: ccsid)).MessageId);
            Assert.Equal("CPF1072", Assert.Throws<CpfException>(() => Retrieve(Name("*PDA", ""), ccsid: ccsid)).MessageId);
        }
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_uses_native_INT_fields_and_receives_packed_decimal_and_local_values(int ccsid)
    {
        Areas.Create("QGPL", "NUMBER", "*DEC", 5, 2, -123.45m);
        ClExternalCallTests.Create(_system, "DAAPI", "CLP", """
            PGM
            DCL &RESULT *CHAR LEN(40)
            CALL QSYS/QWCRDTAA PARM(&RESULT (39 (*INT 4)) 'NUMBER    QGPL      ' (-1 (*INT 4)) (1 (*INT 4)) X'00000000')
            IF (%BIN(&RESULT 1 4) *EQ 39 *AND %BIN(&RESULT 29 4) *EQ 5 *AND %SST(&RESULT 37 3) *EQ X'12345D') THEN(SNDPGMMSG MSG('DECIMAL API ACCEPTED'))
            CHGDTAARA DTAARA(*LDA (1 4)) VALUE('TEST')
            CALL QSYS/QWCRDTAA PARM(&RESULT (40 (*INT 4)) '*LDA                ' (1 (*INT 4)) (4 (*INT 4)) X'00000000')
            IF (%SST(&RESULT 37 4) *EQ 'TEST') THEN(SNDPGMMSG MSG('LOCAL API ACCEPTED'))
            CALL QSYS/QWCRDTAA PARM(&RESULT (40 (*INT 4)) 'MISSING   QGPL      ' (-1 (*INT 4)) (1 (*INT 4)) X'00000000')
            MONMSG CPF1015 EXEC(SNDPGMMSG MSG('API ERROR ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/DAAPI"); Assert.False(result.IsError, result.Message);
        Assert.Contains("DECIMAL API ACCEPTED", result.Message); Assert.Contains("LOCAL API ACCEPTED", result.Message); Assert.Contains("API ERROR ACCEPTED", result.Message);
    }
    [Fact]
    public void Cancellation_does_not_assign_outputs()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); using var stop = new CancellationTokenSource(); stop.Cancel(); var output = new Cell(40);
        Assert.Throws<OperationCanceledException>(() => Api(token: stop.Token).Retrieve([output.Argument, 40, Name(), -1, 1, Hex("00000000")]));
        Assert.All(output.Data, b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void RPG_calls_the_API_directly_with_borrowed_character_binary_fields(int ccsid)
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4, value: "TEST", ccsid: ccsid);
        (string Name, int Length)[] fields = [("RESULT", 40), ("RLEN", 4), ("QUAL", 20), ("START", 4), ("COUNT", 4), ("ERROR", 4)];
        var source = string.Concat(fields.Select(f => {
            var line = ClExternalCallTests.Declaration(f.Name, "A", f.Length.ToString(CultureInfo.InvariantCulture)).ToCharArray();
            line[40] = ' '; return new string(line);
        })) +
            ClExternalCallTests.Calculation("*ENTRY", "PLIST") + string.Concat(fields.Select(f => ClExternalCallTests.Calculation(f.Name, "PARM"))) +
            "/free\ncallp QWCRDTAA(RESULT:RLEN:QUAL:START:COUNT:ERROR);\ndsply %subst(RESULT:37:4);\n/end-free\n";
        ClExternalCallTests.Create(_system, "RPGAREA", "RPG", source);
        ClExternalCallTests.Create(_system, "CALLRPGDA", "CLP", """
            PGM
            DCL &RESULT *CHAR LEN(40)
            CALL QGPL/RPGAREA PARM(&RESULT X'00000028' 'AREA      QGPL      ' X'00000001' X'00000004' X'00000000')
            IF (%SST(&RESULT 37 4) *EQ 'TEST') THEN(SNDPGMMSG MSG('RPG DATA AREA API ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/CALLRPGDA"); Assert.False(result.IsError, result.Message); Assert.Contains("RPG DATA AREA API ACCEPTED", result.Message);
    }
    [Fact]
    public void API_authority_object_authority_and_signing_trust_remain_live()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4, value: "TEST", authority: "*EXCLUDE");
        _system.Security.Authority.Grant("QGPL", "AREA", ObjectType.DataArea, "QUSER", Authorities.UseBits);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DataAreaReader26");
        var ca = _system.Certificates.CreateAuthority("DAAPIROOT", "Data area signer");
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("DAAPISIGN", "Data area API", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QWCRDTAA", ObjectType.Program, signer.Id);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        var output = new Cell(40); object?[] values = [output.Argument, Hex("00000028"), Name(), Hex("FFFFFFFF"), Hex("00000001"), Hex("00000000")];
        var result = session.ExecuteProgram("QSYS/QWCRDTAA", values); Assert.False(result.IsError, result.Message);
        var before = output.Data;
        _system.Security.Authority.Grant("QGPL", "AREA", ObjectType.DataArea, "QUSER", AuthorityBit.None);
        result = session.ExecuteProgram("QSYS/QWCRDTAA", values); Assert.True(result.IsError); Assert.Equal("CPF1016", result.MessageId); Assert.Equal(before, output.Data);
        _system.Security.Authority.Grant("QGPL", "AREA", ObjectType.DataArea, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, false);
        Assert.True(session.ExecuteProgram("QSYS/QWCRDTAA", values).IsError); Assert.Equal(before, output.Data);
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        _system.Security.Authority.Grant("QSYS", "QWCRDTAA", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QWCRDTAA", values).MessageId); Assert.Equal(before, output.Data);
    }
    [Fact]
    public void Parameter_count_and_binary_types_are_checked_without_receiving_data()
    {
        Areas.Create("QGPL", "AREA", "*CHAR", 4); var output = new Cell(40);
        object?[] values = [output.Argument, 40, Name(), -1, 1, Hex("00000000")];
        Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Retrieve(values[..5])).MessageId);
        Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Retrieve(values.Append<object?>(0).ToArray())).MessageId);
        values[1] = new ProgramConstant("*DEC", 5, 0, Hex("00040C"), 40m);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Retrieve(values)).MessageId);
        values[1] = Hex("000028"); Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Retrieve(values)).MessageId);
        Assert.All(output.Data, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Maximum_character_data_and_routing_initialization_extension_return_exact_bytes()
    {
        var data = Enumerable.Range(0, 2000).Select(i => (byte)(i % 256)).ToArray();
        Areas.Create("QGPL", "AREA", "*CHAR", 2000, value: new ProgramBuffer(data, 37));
        var output = Retrieve(Name(), receiverLength: 2040); Assert.Equal(2036, output.Int(0)); Assert.Equal(data, output.Data[36..2036]);
        var job = _system.Jobs.Submit("ROUTED", "", "QBATCH", profile: "QSECOFR", command: "DSPJOB", initializationParameters: data);
        Assert.Equal(job.Key, new BatchQueue(_system.Connections, _system.Jobs).ClaimNext()!.Value.Job.Key);
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        {
            var routed = Retrieve(Name("*PDA", ""), receiverLength: 2040); Assert.Equal(data, routed.Data[36..2036]); Assert.Equal(new string(' ', 10), routed.Text(18, 10));
        }
    }
    public void Dispose() => _system.Dispose();
}
