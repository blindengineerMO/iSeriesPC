using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgApiArgumentTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public RpgApiArgumentTests()
    { _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RpgApiFixture26"); }
    private DataQueueStore Store => new(_system.Connections);
    private static RpgField Char(string name, int length) => new() { Name = name, Length = length, Kind = RpgFieldKind.Character };
    private static RpgField Packed(string name, int digits, int decimals = 0) => new() { Name = name, Length = digits, Decimals = decimals, Kind = RpgFieldKind.Packed };
    private static ProgramBuffer Buffer(string hex, int ccsid = 37) => new(Convert.FromHexString(hex), ccsid);
    private static RpgProgram Program(string target, string[] parameters, params RpgField[] fields)
    {
        var program = new RpgProgram { Name = "RPGAPI", Library = "QGPL" }; program.Fields.AddRange(fields);
        program.MainStatements.Add(new() { Opcode = RpgOpcode.Plist, Factor1 = "P" });
        program.MainStatements.AddRange(parameters.Select(name => new RpgStatement { Opcode = RpgOpcode.Parm, Factor1 = name }));
        program.MainStatements.Add(new() { Opcode = RpgOpcode.Call, Factor1 = "'" + target + "'", Factor2 = "P", Indicator1 = 99 });
        return program;
    }
    private RpgHost ApiHost(int ccsid = 37, Action<CpfException>? failure = null, CancellationToken token = default) => new() {
        Ccsid = () => ccsid, CancellationToken = token,
        TypedProgramCaller = (_, target, values, borrow) => {
            try { new DataQueueApi(_system.Connections, _ => ["QGPL", "QSYS"], ccsid, token).Invoke(target, borrow?.Invoke() ?? values); return new() { Success = true }; }
            catch (CpfException error) { failure?.Invoke(error); return new() { Success = false, Message = error.Message }; }
        } };
    private static RpgProgram Receiver(RpgField? length = null, int dataLength = 8)
        => Program("QRCVDTAQ", ["Q", "LIB", "LEN", "DATA", "WAIT"], Char("Q", 10), Char("LIB", 10),
            length ?? Packed("LEN", 5), Char("DATA", dataLength), Packed("WAIT", 5));
    private static RpgRuntimeContext Context(RpgProgram program, RpgHost host)
    {
        var context = new RpgRuntimeContext(program, host);
        foreach (var (name, value) in new Dictionary<string, object?> { ["Q"] = "APIQUEUE", ["LIB"] = "QGPL", ["LEN"] = 99m, ["DATA"] = "TAILTAIL" })
            if (context.HasField(name)) context.WriteValue(name, value);
        return context;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scalar_aliases_are_live_and_cells_and_factory_expire_on_return_or_exception(bool fail)
    {
        var program = Program("FAKE", ["N", "N", "TEXT"], Packed("N", 5), Char("TEXT", 4));
        ProgramArgument? retained = null; Func<IReadOnlyList<object?>>? factory = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, semantic, borrow) => {
            Assert.Equal(new object?[] { 0m, 0m, "    " }, semantic); factory = borrow;
            var arguments = borrow!(); Assert.Same(arguments, borrow()); Assert.Same(arguments[0], arguments[1]);
            retained = Assert.IsType<ProgramArgument>(arguments[0]); Assert.Equal("*DEC", retained.Type); Assert.Equal(5, retained.Length);
            retained.Value = 42m; Assert.Equal(42m, ((ProgramArgument)arguments[1]!).Value);
            var text = Assert.IsType<ProgramArgument>(arguments[2]); Assert.Equal("40404040", Convert.ToHexString(text.ToBuffer()!.ToArray()));
            text.Value = new ProgramBuffer(CodePage.ToBytes(37, "DONE"), 37);
            if (fail) throw new RpgRuntimeException("Host failure");
            return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host);
        if (fail) Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program, existingContext: context));
        else new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(42m, context.ReadValue("N")); Assert.Equal("DONE", context.ReadValue("TEXT"));
        Assert.Throws<ObjectDisposedException>(() => retained!.Value); Assert.Throws<ObjectDisposedException>(() => retained!.Value = 3m);
        Assert.Throws<ObjectDisposedException>(() => factory!());
    }

    [Fact]
    public void Unused_borrow_factory_expires_and_legacy_host_keeps_semantic_values()
    {
        var program = Program("FAKE", ["N"], Packed("N", 5)); Func<IReadOnlyList<object?>>? retained = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, values, borrow) => { retained = borrow; Assert.IsType<decimal>(values[0]); return new() { Success = true }; } };
        new RpgInterpreter(host).Run(program); Assert.Throws<ObjectDisposedException>(() => retained!());
        var legacy = new RpgHost { ProgramCaller = (_, _, values) => { Assert.Equal(0m, values[0]); return new() { Success = true, UpdatedParameters = [7m] }; } };
        Assert.Equal(7m, new RpgInterpreter(legacy).Run(program).ReadValue("N"));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Direct_RPG_receiver_updates_length_and_data_and_preserves_tail_and_timeout_storage(int ccsid)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(ccsid, "READY"), ccsid));
        var program = Receiver(); var host = ApiHost(ccsid); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(5m, context.ReadValue("LEN")); Assert.Equal("READYAIL", context.ReadValue("DATA")); Assert.False(context.Indicators[99]);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(0m, context.ReadValue("LEN")); Assert.Equal("READYAIL", context.ReadValue("DATA"));
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(7, 0)]
    [InlineData(5, 1)]
    public void Mismatched_RPG_packed_declarations_reject_before_dequeue(int digits, int decimals)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), Buffer("4142"));
        CpfException? error = null; var host = ApiHost(failure: e => error = e); var program = Receiver(Packed("LEN", digits, decimals)); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData("FF00")]
    [InlineData("C3A9")]
    public void Unsafe_UTF8_output_conversion_is_prepared_before_removal_and_preserves_every_RPG_field(string hex)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), Buffer(hex, 1208));
        CpfException? error = null; var host = ApiHost(1208, e => error = e); var program = Receiver(); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.NotNull(error); Assert.True(context.Indicators[99]); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Small_RPG_receiver_and_expression_receiver_reject_without_consuming()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), Buffer("01020304"));
        CpfException? error = null; var host = ApiHost(failure: e => error = e); var program = Receiver(dataLength: 2); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context); Assert.Equal("CPF9506", error?.MessageId); Assert.Equal("TA", context.ReadValue("DATA"));
        var immutable = Program("QRCVDTAQ", ["Q", "LIB", "LEN", "'READONLY'", "WAIT"], Char("Q", 10), Char("LIB", 10), Packed("LEN", 5), Packed("WAIT", 5));
        new RpgInterpreter(host).Run(immutable, existingContext: Context(immutable, host)); Assert.Equal("CPF3C90", error?.MessageId);
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Optional_groups_update_real_key_sender_and_error_cells_and_allow_peek_and_truncation(int ccsid)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8, DataQueueSequence.Keyed, 2, includeSender: true);
        using (Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", new(123456, "SENDER", "QUSER")))
            Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(ccsid, "DATA"), ccsid), new(CodePage.ToBytes(ccsid, "AB"), ccsid));
        var fields = new[] { Char("Q", 10), Char("LIB", 10), Packed("LEN", 5), Char("DATA", 8), Packed("WAIT", 5),
            Char("ORDER", 2), Packed("KL", 3), Char("KEY", 2), Packed("SL", 3), Char("SENDER", 44), Char("RMV", 10), Packed("SIZE", 5), Char("ERROR", 16), Char("UNITS", 10) };
        var program = Program("QRCVDTAQ", fields.Select(f => f.Name).ToArray(), fields); var host = ApiHost(ccsid); var context = Context(program, host);
        foreach (var (name, value) in new Dictionary<string, object?> { ["ORDER"] = "GE", ["KL"] = 2m, ["KEY"] = "AA", ["SL"] = 44m, ["RMV"] = "*NO", ["SIZE"] = 2m, ["UNITS"] = "*MSEC",
            ["ERROR"] = Buffer("00000010000000000000000000000000", ccsid).ToText() }) context.WriteValue(name, value);
        new RpgInterpreter(host).Run(program, existingContext: context); Assert.False(context.Indicators[99]);
        Assert.Equal(4m, context.ReadValue("LEN")); Assert.Equal("DAILTAIL", context.ReadValue("DATA")); Assert.Equal("AB", context.ReadValue("KEY"));
        var sender = CodePage.ToBytes(ccsid, (string)context.ReadValue("SENDER")!);
        Assert.Equal("0000044C0000044C", Convert.ToHexString(sender.AsSpan(0, 8))); Assert.Equal("SENDER    QUSER     123456QSECOFR   ", new ProgramBuffer(sender.AsSpan(8), ccsid).ToText());
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        context.WriteValue("SIZE", 9m); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", new ProgramBuffer(CodePage.ToBytes(ccsid, (string)context.ReadValue("ERROR")!).AsSpan(8, 7), ccsid).ToText());
        Assert.Equal("DAILTAIL", context.ReadValue("DATA")); Assert.Equal(4m, context.ReadValue("LEN"));
    }

    [Fact]
    public void Aliases_forwarded_from_CL_entry_cells_keep_one_identity_and_reject_overlapping_API_outputs()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8, DataQueueSequence.Keyed, 8);
        Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(37, "DATA"), 37), new(CodePage.ToBytes(37, "SAMEKEY "), 37));
        var fields = new[] { Char("Q", 10), Char("LIB", 10), Packed("LEN", 5), Char("DATA", 8), Packed("WAIT", 5), Char("ORDER", 2), Packed("KL", 3), Char("KEY", 8), Packed("SL", 3), Char("SENDER", 1) };
        var program = Program("QRCVDTAQ", fields.Select(f => f.Name).ToArray(), fields);
        program.MainStatements.InsertRange(0, new[] { new RpgStatement { Opcode = RpgOpcode.Plist, Factor1 = "*ENTRY" }, new() { Opcode = RpgOpcode.Parm, Factor1 = "DATA" }, new() { Opcode = RpgOpcode.Parm, Factor1 = "KEY" } });
        object? value = "SAMEKEY "; var reference = new ProgramArgument(() => value, v => value = v, v => v, "*CHAR", 8);
        CpfException? error = null; var host = ApiHost(failure: e => error = e); var context = Context(program, host);
        context.WriteValue("ORDER", "EQ"); context.WriteValue("KL", 8m);
        new RpgInterpreter(host).Run(program, [reference, reference], context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal("SAMEKEY ", value); Assert.Equal(99m, context.ReadValue("LEN"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Theory]
    [InlineData(RpgFieldKind.Zoned)]
    [InlineData(RpgFieldKind.VaryingChar)]
    [InlineData(RpgFieldKind.Date)]
    public void Unsupported_scalar_kinds_reject_before_the_API_runs(RpgFieldKind kind)
    {
        var field = new RpgField { Name = "FIELD", Length = 5, Kind = kind };
        var program = Program("FAKE", ["FIELD"], field); var invoked = false;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => { _ = borrow!(); invoked = true; return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId); Assert.False(invoked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Arrays_and_data_structure_fields_cannot_be_borrowed(bool array)
    {
        var field = new RpgField { Name = "FIELD", Length = 5, Kind = RpgFieldKind.Character, IsArray = array, Dimension = 2, IsDataStructure = !array };
        var program = Program("FAKE", ["FIELD"], field);
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => { _ = borrow!(); return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }

    [Fact]
    public void A_character_field_marked_VARYING_cannot_be_borrowed_as_fixed_storage()
    {
        var program = Program("FAKE", ["TEXT"], new RpgField { Name = "TEXT", Kind = RpgFieldKind.Character, Length = 8, Varying = true });
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => { _ = borrow!(); return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }

    [Theory]
    [InlineData(256, 4096, true)]
    [InlineData(257, 1, false)]
    [InlineData(2, 524289, false)]
    public void Borrowing_enforces_argument_count_and_aggregate_byte_bounds(int count, int size, bool accepted)
    {
        var program = Program("FAKE", Enumerable.Repeat("TEXT", count).ToArray(), Char("TEXT", size));
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            var values = borrow!(); Assert.Equal(count, values.Count); Assert.Same(values[0], values[^1]); return new() { Success = true };
        } };
        if (accepted) new RpgInterpreter(host).Run(program);
        else Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }

    [Fact]
    public async Task Cancelling_a_direct_RPG_wait_expires_borrowed_cells_and_preserves_outputs()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); var program = Receiver();
        using var cancellation = new CancellationTokenSource(); using var entered = new ManualResetEventSlim();
        ProgramArgument? retained = null; Func<IReadOnlyList<object?>>? factory = null;
        var host = new RpgHost { CancellationToken = cancellation.Token, TypedProgramCaller = (_, target, _, borrow) => {
            factory = borrow; var values = borrow!(); retained = (ProgramArgument)values[3]!; entered.Set();
            new DataQueueApi(_system.Connections, _ => ["QGPL"], 37, cancellation.Token).Invoke(target, values); return new() { Success = true };
        } };
        var context = Context(program, host); context.WriteValue("WAIT", -1m);
        var running = Task.Factory.StartNew(() => new RpgInterpreter(host).Run(program, existingContext: context), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Throws<ObjectDisposedException>(() => retained!.ToBuffer()); Assert.Throws<ObjectDisposedException>(() => factory!());
    }

    [Fact]
    public void An_outer_receiver_normalizer_can_reject_before_dequeue_or_any_output_write()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(37, "READY"), 37));
        var program = Receiver(); program.MainStatements.InsertRange(0, new[] { new RpgStatement { Opcode = RpgOpcode.Plist, Factor1 = "*ENTRY" }, new() { Opcode = RpgOpcode.Parm, Factor1 = "DATA" } });
        var writes = 0;
        var reference = new ProgramArgument(() => "TAILTAIL", _ => writes++, value => value is string text && text == "TAILTAIL" ? text : throw new CpfException("CPF3C3A", "Outer receiver rejected output."), "*CHAR", 8);
        CpfException? error = null; var host = ApiHost(failure: e => error = e); var context = Context(program, host);
        new RpgInterpreter(host).Run(program, [reference], context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(0, writes); Assert.Equal(99m, context.ReadValue("LEN")); Assert.Equal("TAILTAIL", context.ReadValue("DATA"));
        Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Every_call_reads_the_current_job_CCSID_in_retained_storage()
    {
        var program = Program("FAKE", ["TEXT"], Char("TEXT", 4));
        var current = 37; var observed = new List<string>();
        var host = new RpgHost { Ccsid = () => current, TypedProgramCaller = (_, _, _, borrow) => {
            var argument = (ProgramArgument)borrow!()[0]!; observed.Add(Convert.ToHexString(argument.ToBuffer()!.ToArray())); return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host); context.WriteValue("TEXT", "TEST");
        new RpgInterpreter(host).Run(program, existingContext: context); current = 1208;
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(new[] { "E3C5E2E3", "54455354" }, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_character_bytes_are_not_interpreted_as_RPG_assignment_keywords(bool borrowedEntry)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(37, "*BLANK"), 37));
        var program = Receiver(dataLength: 6); var host = ApiHost(); var context = Context(program, host);
        ProgramBuffer storage = new(CodePage.ToBytes(37, "*BLANK"), 37);
        ProgramBuffer Normalize(object? value) => value is ProgramBuffer buffer && buffer.Length == 6 ? buffer : new(CodePage.ToBytes(37, (string)value!), 37);
        var reference = new ProgramArgument(() => storage, value => storage = (ProgramBuffer)value!, value => Normalize(value), "*CHAR", 6, buffer: () => storage);
        if (borrowedEntry) program.MainStatements.InsertRange(0, new[] { new RpgStatement { Opcode = RpgOpcode.Plist, Factor1 = "*ENTRY" }, new() { Opcode = RpgOpcode.Parm, Factor1 = "DATA" } });
        new RpgInterpreter(host).Run(program, borrowedEntry ? [reference] : null, context);
        Assert.Equal("*BLANK", context.ReadValue("DATA")); Assert.Equal(6m, context.ReadValue("LEN"));
        if (borrowedEntry) Assert.Equal("*BLANK", storage.ToText());
    }

    [Theory]
    [InlineData(37, "BAD😀")]
    [InlineData(1208, "é")]
    public void Unrepresentable_or_wider_than_declared_character_inputs_do_not_send(int ccsid, string text)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8);
        var program = Program("QSNDDTAQ", ["Q", "LIB", "LEN", "DATA"], Char("Q", 10), Char("LIB", 10), Packed("LEN", 5), Char("DATA", 8));
        CpfException? error = null; var host = ApiHost(ccsid, e => error = e); var context = Context(program, host);
        context.WriteValue("LEN", 4m); context.WriteValue("DATA", text); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal("CPF3C3A", error?.MessageId); Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void A_named_constant_is_an_immutable_API_receiver()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), Buffer("0102"));
        var program = Program("QRCVDTAQ", ["Q", "LIB", "LEN", "DATA", "WAIT"], Char("Q", 10), Char("LIB", 10), Packed("LEN", 5),
            new() { Name = "DATA", Length = 8, Kind = RpgFieldKind.Character, Source = RpgFieldSource.Constant, InitialValue = "READONLY" }, Packed("WAIT", 5));
        CpfException? error = null; var host = ApiHost(failure: e => error = e); var context = new RpgRuntimeContext(program, host);
        context.WriteValue("Q", "APIQUEUE"); context.WriteValue("LIB", "QGPL");
        new RpgInterpreter(host).Run(program, existingContext: context); Assert.Equal("CPF3C90", error?.MessageId);
        Assert.Equal("READONLY", context.ReadValue("DATA")); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Prototype_bridge_supplies_borrowed_receivers_and_updates_actual_fields()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); Store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(37, "OK"), 37));
        var program = Receiver(); var prototype = new RpgPrototype { Name = "GET", ExternalName = "QRCVDTAQ" };
        prototype.Parameters.AddRange(new[] { "Q", "LIB", "LEN", "DATA", "WAIT" }); program.Prototypes.Add(prototype);
        program.MainStatements.Clear(); program.MainStatements.Add(new() { Opcode = RpgOpcode.CallP, Factor1 = "GET(Q:LIB:LEN:DATA:WAIT)", Indicator1 = 99 });
        CpfException? error = null; var host = ApiHost(failure: e => error = e);
        var context = Context(program, host); new RpgInterpreter(host).Run(program, existingContext: context); Assert.Null(error);
        Assert.Equal(2m, context.ReadValue("LEN")); Assert.Equal("OKILTAIL", context.ReadValue("DATA"));
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    private static string Declaration(string name, string type, int length, int decimals = 0)
    {
        var line = ClExternalCallTests.Declaration(name, type, length.ToString()).ToCharArray();
        line[40] = type == "A" ? ' ' : (char)('0' + decimals); return new(line);
    }
    private static string SessionProgram(string style, string wait = "0")
    {
        var fields = Declaration("Q", "A", 10) + Declaration("LIB", "A", 10) + Declaration("LEN", "P", 5) + Declaration("DATA", "A", 8) + Declaration("WAIT", "P", 5);
        var setup = "eval Q = 'APIQUEUE';\neval LIB = 'QGPL';\neval LEN = 5;\neval DATA = 'READY';\neval WAIT = " + wait + ";\n";
        if (style == "INLINE") return fields + "**free\n" + setup + "callp QSNDDTAQ(Q:LIB:LEN:DATA);\neval DATA = 'TAILTAIL';\ncallp QRCVDTAQ(Q:LIB:LEN:DATA:WAIT);\ndsply (DATA);\ndsply LEN;\ncallp QCLRDTAQ(Q:LIB);";
        string Plist(string name, params string[] parameters) => ClExternalCallTests.Calculation(name, "PLIST") + string.Concat(parameters.Select(p => ClExternalCallTests.Calculation(p, "PARM")));
        return fields + "/free\n" + setup + "/end-free\n" + Plist("SEND", "Q", "LIB", "LEN", "DATA") + Plist("RECV", "Q", "LIB", "LEN", "DATA", "WAIT") + Plist("CLEAR", "Q", "LIB") +
            ClExternalCallTests.Calculation("'QSNDDTAQ'", style, "SEND") + ClExternalCallTests.Calculation("'QRCVDTAQ'", style, "RECV") +
            ClExternalCallTests.Calculation("DATA", "DSPLY") + ClExternalCallTests.Calculation("LEN", "DSPLY") + ClExternalCallTests.Calculation("'QCLRDTAQ'", style, "CLEAR");
    }

    [Theory]
    [InlineData(37, "CALL")]
    [InlineData(1208, "CALL")]
    [InlineData(37, "CALLB")]
    [InlineData(1208, "CALLB")]
    [InlineData(37, "INLINE")]
    [InlineData(1208, "INLINE")]
    public void Session_RPG_calls_all_three_native_queues_without_a_CL_wrapper(int ccsid, string style)
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); ClExternalCallTests.Create(_system, "RPGDIRECT", "RPG", SessionProgram(style));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL RPGDIRECT"); Assert.False(result.IsError, result.Message); Assert.Contains("READY", result.Message); Assert.EndsWith("5", result.Message);
        Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
    }

    [Fact]
    public void Direct_RPG_call_keeps_API_and_queue_authority_checks()
    {
        Store.Create(new("QGPL", "APIQUEUE"), 8); ClExternalCallTests.Create(_system, "RPGDIRECT", "RPG", SessionProgram("INLINE"));
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "RpgApiUserFixture26");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        _system.Security.Authority.Grant("QGPL", "APIQUEUE", ObjectType.DataQueue, "QUSER", AuthorityBit.None);
        Assert.True(session.Execute("CALL RPGDIRECT").IsError); Assert.Equal(0, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        _system.Security.Authority.Grant("QGPL", "APIQUEUE", ObjectType.DataQueue, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.Read | AuthorityBit.Delete);
        Assert.False(session.Execute("CALL RPGDIRECT").IsError);
        _system.Security.Authority.Grant("QSYS", "QRCVDTAQ", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.True(session.Execute("CALL RPGDIRECT").IsError); Assert.Equal(1, Store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        ClExternalCallTests.Create(_system, "MONRPGAPI", "CLP", "PGM\nCALL RPGDIRECT\nMONMSG CPF9802 EXEC(SNDPGMMSG MSG('API AUTHORITY MONITORED'))\nENDPGM");
        var monitored = session.Execute("CALL MONRPGAPI"); Assert.False(monitored.IsError, monitored.Message); Assert.Contains("API AUTHORITY MONITORED", monitored.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, true)]
    public void Typed_QCMDEXC_checks_the_RPG_declared_scale_before_dispatch(int decimals, bool accepted)
    {
        var source = Declaration("CMD", "A", 32) + Declaration("LEN", "P", 15, decimals) + "**free\neval CMD = 'CHGCURLIB QUSRSYS';\neval LEN = 17;\ncallp QCMDEXC(CMD:LEN);";
        ClExternalCallTests.Create(_system, "RPGCOMMAND", "RPG", source);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL RPGCOMMAND"); Assert.Equal(!accepted, result.IsError); Assert.Equal(accepted ? "QUSRSYS" : "QGPL", session.Job.CurrentLibrary);
    }

    public void Dispose() => _system.Dispose();
}
