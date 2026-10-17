using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgPrototypeApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public RpgPrototypeApiTests()
    { _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RpgPrototypeFixture27"); }
    private DataQueueStore Store => new(_system.Connections);
    private static RpgField Char(string name, int width) => new() { Name = name, Kind = RpgFieldKind.Character, Length = width };
    private static RpgField Packed(string name, int digits = 5, int scale = 0) => new() { Name = name, Kind = RpgFieldKind.Packed, Length = digits, Decimals = scale };
    private static RpgProgram Receiver(RpgField? formalLength = null, RpgField? actualLength = null)
    {
        var program = new RpgProgram { Name = "PROTOTYPE", Library = "QGPL" };
        program.Fields.AddRange([Char("Q", 10), Char("LIB", 10), actualLength ?? Packed("LEN"), Char("DATA", 8), Packed("WAIT")]);
        var formals = new[] { Char("FQ", 10), Char("FLIB", 10), formalLength ?? Packed("FLEN"), Char("FDATA", 8), Packed("FWAIT") };
        AddPrototype(program, "GET", "QRCVDTAQ", formals);
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "GET(Q:LIB:LEN:DATA:WAIT)", Indicator1 = 99 });
        return program;
    }
    private static RpgPrototype AddPrototype(RpgProgram program, string name, string target, params RpgField[] fields)
    {
        var prototype = new RpgPrototype { Name = name, ExternalName = target };
        foreach (var field in fields) { program.Fields.Add(field); prototype.Parameters.Add(field.Name); }
        program.Prototypes.Add(prototype); return prototype;
    }
    private RpgHost Host(int ccsid = 37, Action<CpfException>? onError = null) => new() {
        Ccsid = () => ccsid, TypedProgramCaller = (_, target, values, borrow) => {
            try { new DataQueueApi(_system.Connections, _ => ["QGPL"], ccsid).Invoke(target, borrow!()); return new() { Success = true }; }
            catch (CpfException error) { onError?.Invoke(error); return new() { Success = false, Message = error.Message }; }
        } };
    private static RpgRuntimeContext Context(RpgProgram program, RpgHost host)
    {
        var context = new RpgRuntimeContext(program, host);
        context.WriteValue("Q", "APIQUEUE"); context.WriteValue("LIB", "QGPL"); context.WriteValue("LEN", 99m); context.WriteValue("DATA", "TAILTAIL");
        return context;
    }
    private void Entry(int ccsid = 37)
    { Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(ccsid, "READY"), ccsid)); }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Prototype_outputs_write_actual_fields_and_leave_formal_temporaries_unchanged(int ccsid)
    {
        Entry(ccsid); var program = Receiver(); var host = Host(ccsid); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.False(context.Indicators[99]); Assert.Equal(5m, context.ReadValue("LEN")); Assert.Equal("READYAIL", context.ReadValue("DATA"));
        Assert.Equal(0m, context.ReadValue("FLEN")); Assert.Equal(new string(' ', 8), context.ReadValue("FDATA"));
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(3, 0, 5, 0)]
    [InlineData(5, 1, 5, 0)]
    [InlineData(5, 0, 7, 0)]
    [InlineData(5, 0, 5, 1)]
    public void Formal_and_actual_packed_shapes_must_match_before_any_receiver_or_queue_change(int formalDigits, int formalScale, int actualDigits, int actualScale)
    {
        Entry(); var program = Receiver(Packed("FLEN", formalDigits, formalScale), Packed("LEN", actualDigits, actualScale));
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Equal(0m, context.ReadValue("FLEN")); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData("CONST")]
    [InlineData("VALUE")]
    [InlineData("PARM CONST")]
    [InlineData("OPTIONS(*NOPASS)")]
    public void Unsupported_formal_options_cannot_silently_become_mutable_receivers(string options)
    {
        Entry(); var program = Receiver(); program.Prototypes[0].ParameterKeywords["FDATA"] = options;
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal("TAILTAIL", context.ReadValue("DATA")); Assert.Equal(99m, context.ReadValue("LEN"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData("GET(Q:LIB:LEN:DATA)")]
    [InlineData("GET(Q:LIB:LEN:DATA:WAIT:WAIT)")]
    public void Prototype_argument_count_is_checked_before_binding_or_dispatch(string call)
    {
        Entry(); var program = Receiver(); program.MainStatements[0].Factor1 = call;
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(0m, context.ReadValue("FLEN")); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData("GET(Q:LIB:0:DATA:WAIT)")]
    [InlineData("GET(Q:LIB:LEN:'DUMMY':WAIT)")]
    public void Typed_prototype_constants_remain_immutable_receivers(string call)
    {
        Entry(); var program = Receiver(); program.MainStatements[0].Factor1 = call;
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C90", error?.MessageId); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(37, "D6D24040")]
    [InlineData(1208, "4F4B2020")]
    public void Literal_temporaries_have_independent_formal_character_and_packed_byte_vectors(int ccsid, string characterHex)
    {
        var program = new RpgProgram { Name = "CONSTANTS", Library = "QGPL" };
        AddPrototype(program, "TEST", "FAKE", Char("FTEXT", 4), Packed("FNUMBER"));
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "TEST('OK':-12)" });
        var host = new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) => {
            var values = borrow!(); var text = Assert.IsType<ProgramConstant>(values[0]); var number = Assert.IsType<ProgramConstant>(values[1]);
            Assert.Equal("*CHAR", text.Type); Assert.Equal(4, text.Length); Assert.Equal(characterHex, Convert.ToHexString(text.Buffer.ToArray()));
            Assert.Equal("*DEC", number.Type); Assert.Equal(5, number.Length); Assert.Equal(0, number.Decimals); Assert.Equal("00012D", Convert.ToHexString(number.Buffer.ToArray()));
            return new() { Success = true };
        } };
        new RpgInterpreter(host).Run(program);
    }

    [Theory]
    [InlineData("100000")]
    [InlineData("1.25")]
    [InlineData("'12'")]
    public void Packed_literal_precision_scale_and_kind_are_checked_without_formal_assignment(string value)
    {
        var program = new RpgProgram { Name = "BADCONST", Library = "QGPL" }; AddPrototype(program, "TEST", "FAKE", Packed("FNUMBER"));
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "TEST(" + value + ")" });
        var dispatched = false; var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => { _ = borrow!(); dispatched = true; return new() { Success = true }; } };
        var context = new RpgRuntimeContext(program, host);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program, existingContext: context)).MessageId);
        Assert.False(dispatched); Assert.Equal(0m, context.ReadValue("FNUMBER"));
    }

    [Fact]
    public void Constant_declaration_is_checked_against_the_native_API_not_only_the_prototype()
    {
        Entry(); var program = Receiver(); program.Fields.RemoveAll(field => field.Name == "FWAIT"); program.Fields.Add(Packed("FWAIT", 3));
        program.MainStatements[0].Factor1 = "GET(Q:LIB:LEN:DATA:0)";
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prototype_aliases_are_live_and_factories_and_cells_expire_on_success_or_exception(bool fail)
    {
        var program = new RpgProgram { Name = "ALIASES", Library = "QGPL" }; program.Fields.Add(Packed("N"));
        AddPrototype(program, "TEST", "FAKE", Packed("F1"), Packed("F2"));
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "TEST(N:N)" });
        ProgramArgument? retained = null; Func<IReadOnlyList<object?>>? factory = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            factory = borrow; var values = borrow!(); Assert.Same(values[0], values[1]); retained = (ProgramArgument)values[0]!;
            retained.Value = 42m; Assert.Equal(42m, ((ProgramArgument)values[1]!).Value);
            if (fail) throw new OperationCanceledException(); return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host);
        if (fail) Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program, existingContext: context));
        else new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(42m, context.ReadValue("N")); Assert.Equal(0m, context.ReadValue("F1")); Assert.Equal(0m, context.ReadValue("F2"));
        Assert.Throws<ObjectDisposedException>(() => retained!.Value); Assert.Throws<ObjectDisposedException>(() => factory!());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_and_typed_hosts_keep_ordinary_prototype_copy_and_writeback(bool typed)
    {
        var program = new RpgProgram { Name = "LEGACY", Library = "QGPL" }; program.Fields.Add(Packed("N", 7, 3));
        AddPrototype(program, "TEST", "ORDINARY", Packed("FORMAL", 5, 1)); program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "TEST(N)" });
        RpgExternalCallResult Call(IReadOnlyList<object?> values) { Assert.Equal(42.1m, values[0]); return new() { Success = true, UpdatedParameters = [77.7m] }; }
        var host = typed ? new RpgHost { TypedProgramCaller = (_, _, values, _) => Call(values) } : new RpgHost { ProgramCaller = (_, _, values) => Call(values) };
        var context = new RpgRuntimeContext(program, host); context.WriteValue("N", 42.125m);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(77.7m, context.ReadValue("FORMAL")); Assert.Equal(42.125m, context.ReadValue("N"));
    }

    [Fact]
    public void Unused_semantic_arguments_cannot_bind_formals_after_the_call_returns()
    {
        var program = Receiver(); IReadOnlyList<object?>? retained = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, values, _) => { retained = values; return new() { Success = true }; } };
        var context = Context(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(0m, context.ReadValue("FLEN")); Assert.Throws<ObjectDisposedException>(() => retained!.Count);
    }

    [Theory]
    [InlineData("'AA:BB'", "AA:BB")]
    [InlineData("%SUBST('AA:BB:CC':1:5)", "AA:BB")]
    [InlineData("'A''B:C'", "A'B:C")]
    [InlineData("'A\\B:C'", "A\\B:C")]
    public void Prototype_literals_and_nested_expressions_keep_colons_and_escaped_quotes_inside_one_argument(string expression, string expected)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); var program = new RpgProgram { Name = "LITERALS", Library = "QGPL" };
        AddPrototype(program, "SEND", "QSNDDTAQ", Char("FQ", 10), Char("FLIB", 10), Packed("FLEN"), Char("FTEXT", 8));
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "SEND('APIQUEUE':'QGPL':5:" + expression + ")" });
        new RpgInterpreter(Host()).Run(program);
        Assert.Equal(expected, Store.Receive(new("QGPL", "APIQUEUE"))!.Data.ToText());
    }

    [Theory]
    [InlineData("SEND('APIQUEUE':'QGPL':5:)")]
    [InlineData("SEND('APIQUEUE':'QGPL':5:'BAD)")]
    [InlineData("SEND('APIQUEUE':'QGPL':5:%SUBST('DATA':1:2)")]
    public void Malformed_argument_lists_fail_before_the_host_runs(string call)
    {
        var program = new RpgProgram { Name = "BADCALL", Library = "QGPL" };
        program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = call }); var called = false;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } };
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program)); Assert.False(called);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("ambiguous")]
    [InlineData("binding")]
    public void Invalid_formal_definitions_reject_before_native_receive(string failure)
    {
        Entry(); var program = Receiver();
        if (failure == "missing") program.Fields.RemoveAll(field => field.Name == "FWAIT");
        else if (failure == "duplicate") program.Prototypes[0].Parameters[4] = "FLEN";
        else if (failure == "binding") program.Prototypes.Add(new() { Name = "GET", ExternalName = "QCLRDTAQ" });
        else program.Fields.Add(Packed("FWAIT"));
        CpfException? error = null; var host = Host(onError: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Prototyped_optional_groups_prepare_keys_sender_truncated_data_and_error_receivers(int ccsid)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8, DataQueueSequence.Keyed, 2, includeSender: true);
        using (Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", new(123456, "SENDER", "QUSER")))
            Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(ccsid, "DATA"), ccsid), new(CodePage.ToBytes(ccsid, "AB"), ccsid));
        var program = Receiver(); program.Fields.AddRange([Char("KEY", 2), Char("SENDER", 44), Char("ERROR", 16)]);
        program.Fields.AddRange([Char("FORDER", 2), Packed("FKL", 3), Char("FKEY", 2), Packed("FSL", 3), Char("FSENDER", 44), Char("FRMV", 10), Packed("FSIZE"), Char("FERROR", 16), Char("FUNITS", 10)]);
        program.Prototypes[0].Parameters.AddRange(["FORDER", "FKL", "FKEY", "FSL", "FSENDER", "FRMV", "FSIZE", "FERROR", "FUNITS"]);
        program.MainStatements[0].Factor1 = "GET(Q:LIB:LEN:DATA:0:'GE':2:KEY:44:SENDER:'*NO':2:ERROR:'*MSEC')";
        var host = Host(ccsid); var context = Context(program, host); context.WriteValue("KEY", "AA");
        context.WriteValue("ERROR", new ProgramBuffer(Convert.FromHexString("00000010000000000000000000000000"), ccsid).ToText());
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.False(context.Indicators[99]); Assert.Equal(4m, context.ReadValue("LEN")); Assert.Equal("DAILTAIL", context.ReadValue("DATA")); Assert.Equal("AB", context.ReadValue("KEY"));
        Assert.Equal("0000044C0000044C", Convert.ToHexString(CodePage.ToBytes(ccsid, (string)context.ReadValue("SENDER")!).AsSpan(0, 8)));
        program.MainStatements[0].Factor1 = program.MainStatements[0].Factor1!.Replace(":2:ERROR:", ":9:ERROR:");
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", new ProgramBuffer(CodePage.ToBytes(ccsid, (string)context.ReadValue("ERROR")!).AsSpan(8, 7), ccsid).ToText());
        Assert.Equal(4m, context.ReadValue("LEN")); Assert.Equal("DAILTAIL", context.ReadValue("DATA")); Assert.Equal(0m, context.ReadValue("FLEN"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    internal static string D(string name, string type, int length = 0, int decimals = 0, string keywords = "")
    {
        var line = new string(' ', 100).ToCharArray(); line[6] = 'D'; name.CopyTo(0, line, 7, name.Length); type.CopyTo(0, line, 21, type.Length);
        if (length > 0) length.ToString().CopyTo(0, line, 38, length.ToString().Length);
        if (type == "P") line[40] = (char)('0' + decimals);
        keywords.CopyTo(0, line, 50, keywords.Length); return new string(line).TrimEnd() + "\n";
    }
    internal static string Source(string receiveOptions = "PARM")
    {
        var source = D("Q", "A", 10) + D("LIB", "A", 10) + D("LEN", "P", 5) + D("TEXT", "A", 8) + D("WAIT", "P", 5);
        source += D("SEND", "PR", keywords: "EXTPGM('QSNDDTAQ')") + D("Q", "A", 10, keywords: "PARM") + D("LIB", "A", 10, keywords: "PARM") + D("LEN", "P", 5, keywords: "PARM") + D("TEXT", "A", 8, keywords: "PARM");
        source += D("RECV", "PR", keywords: "EXTPROC('QRCVDTAQ')") + D("Q", "A", 10, keywords: "PARM") + D("LIB", "A", 10, keywords: "PARM") + D("LEN", "P", 5, keywords: "PARM") + D("TEXT", "A", 8, keywords: receiveOptions) + D("WAIT", "P", 5, keywords: "PARM");
        source += D("CLEAR", "PR", keywords: "EXTPGM('QCLRDTAQ')") + D("Q", "A", 10, keywords: "PARM") + D("LIB", "A", 10, keywords: "PARM");
        return source + "**free\neval Q = 'APIQUEUE';\neval LIB = 'QGPL';\neval LEN = 5;\neval TEXT = 'READY';\ncallp SEND(Q:LIB:LEN:TEXT);\neval TEXT = 'TAILTAIL';\ncallp RECV(Q:LIB:LEN:TEXT:WAIT);\nif TEXT = 'READYAIL';\nif LEN = 5;\nsndpgmmsg 'PROTOTYPE API ACCEPTED';\nendif;\nendif;\ncallp CLEAR(Q:LIB);";
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_source_uses_three_independent_prototypes_and_actual_receiver_fields(int ccsid)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); var source = Source(); var compiled = RpgCompiler.Compile("QGPL", "PROTOS", source);
        Assert.Equal(new[] { 4, 5, 2 }, compiled.Prototypes.Select(prototype => prototype.Parameters.Count));
        Assert.Equal(11, compiled.Prototypes.SelectMany(prototype => prototype.Parameters).Distinct().Count());
        Assert.Equal(1, compiled.Fields.Count(field => field.Name == "TEXT"));
        ClExternalCallTests.Create(_system, "PROTOS", "RPG", source);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL PROTOS"); Assert.False(result.IsError, result.Message); Assert.Contains("PROTOTYPE API ACCEPTED", result.Message);
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Source_formal_options_are_retained_and_rejected_before_a_native_receive()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); var source = Source("PARM CONST"); var compiled = RpgCompiler.Compile("QGPL", "BADOPTIONS", source);
        Assert.Equal("PARM CONST", compiled.Prototypes[1].ParameterKeywords[compiled.Prototypes[1].Parameters[3]]);
        ClExternalCallTests.Create(_system, "BADOPTIONS", "RPG", source);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        Assert.True(session.Execute("CALL BADOPTIONS").IsError); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Multiple_unnamed_prototypes_keep_independent_formal_types_and_preserve_first_legacy_parameter_names()
    {
        var program = RpgCompiler.Compile("QGPL", "NAMES", D("FIRST", "PR", keywords: "EXTPGM('QCLRDTAQ')") + D("", "A", 10, keywords: "PARM") +
            D("SECOND", "PR", keywords: "EXTPGM('QSNDDTAQ')") + D("", "P", 5, keywords: "PARM"));
        Assert.Equal("P1", program.Prototypes[0].Parameters[0]); Assert.NotEqual("P1", program.Prototypes[1].Parameters[0]);
        Assert.Equal(RpgFieldKind.Character, program.FindField(program.Prototypes[0].Parameters[0])!.Kind);
        Assert.Equal(RpgFieldKind.Packed, program.FindField(program.Prototypes[1].Parameters[0])!.Kind);
    }

    [Fact]
    public void Session_ordinary_program_target_keeps_formal_coercion_and_copied_parameter_writeback()
    {
        ClExternalCallTests.Create(_system, "PROTOCPP", "CLP", "PGM PARM(&N)\nDCL &N *DEC LEN(5 1)\nIF (&N *NE 42.1) THEN(RETURN)\nCHGVAR &N 99.9\nSNDPGMMSG MSG('LEGACY PROTOTYPE ACCEPTED')\nENDPGM");
        var source = D("N", "P", 7, 3) + D("CALC", "PR", keywords: "EXTPGM('PROTOCPP')") + D("N", "P", 5, 1, "PARM") +
            "**free\neval N = 42.125;\ncallp CALC(N);\nif N = 42.125;\nsndpgmmsg 'CALLER INPUT PRESERVED';\nendif;";
        ClExternalCallTests.Create(_system, "PROTORD", "RPG", source);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL PROTORD"); Assert.False(result.IsError, result.Message); Assert.Contains("LEGACY PROTOTYPE ACCEPTED", result.Message); Assert.Contains("CALLER INPUT PRESERVED", result.Message);
    }

    [Fact]
    public void API_program_authority_failure_precedes_borrowing_and_keeps_its_CL_monitor_message_id()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); ClExternalCallTests.Create(_system, "PROTOS", "RPG", Source());
        ClExternalCallTests.Create(_system, "MONPROTO", "CLP", "PGM\nCALL PROTOS\nMONMSG CPF9802 EXEC(SNDPGMMSG MSG('PROTOTYPE AUTHORITY MONITORED'))\nENDPGM");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "RpgProtoUserFixture27");
        _system.Security.Authority.Grant("QSYS", "QRCVDTAQ", ObjectType.Program, "QUSER", AuthorityBit.None);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        var result = session.Execute("CALL MONPROTO"); Assert.False(result.IsError, result.Message); Assert.Contains("PROTOTYPE AUTHORITY MONITORED", result.Message);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Revoked_API_signing_trust_rejects_a_prototype_receive_before_consuming()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); ClExternalCallTests.Create(_system, "PROTOS", "RPG", Source());
        var ca = _system.Certificates.CreateAuthority("PROTOROOT", "Prototype API tests");
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("PROTOSIGN", "Prototype API signer", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QRCVDTAQ", ObjectType.Program, signer.Id);
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, false);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        Assert.True(session.Execute("CALL PROTOS").IsError); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(0, "LEN", false)]
    [InlineData(5, "LEN", true)]
    [InlineData(0, "17", true)]
    [InlineData(5, "17.25", false)]
    public void QCMDEXC_prototype_checks_actual_scale_and_encodes_decimal_literals_before_dispatch(int actualScale, string argument, bool accepted)
    {
        var source = D("CMD", "A", 32) + D("LEN", "P", 15, actualScale) + D("RUN", "PR", keywords: "EXTPGM('QCMDEXC')") +
            D("CMD", "A", 32, keywords: "PARM") + D("LEN", "P", 15, 5, "PARM") +
            "**free\neval CMD = 'CHGCURLIB QUSRSYS';\neval LEN = 17;\ncallp RUN(CMD:" + argument + ");";
        ClExternalCallTests.Create(_system, "PROTOCMD", "RPG", source);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL PROTOCMD"); Assert.Equal(!accepted, result.IsError); Assert.Equal(accepted ? "QUSRSYS" : "QGPL", session.Job.CurrentLibrary);
    }
    public void Dispose() => _system.Dispose();
}
