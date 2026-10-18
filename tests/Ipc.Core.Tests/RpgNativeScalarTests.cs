using Ipc.Api;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Messages;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;

namespace Ipc.Core.Tests;

public sealed class RpgNativeScalarTests
{
    private static RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "SCALARS", "**free\n" + body);
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static RpgProgram Entry(string declarations, string body, params string[] names)
    {
        var program = Compile(declarations + body);
        program.MainStatements.InsertRange(0, new[] { new RpgStatement { Opcode = RpgOpcode.Plist, Factor1 = "*ENTRY" } }
            .Concat(names.Select(name => new RpgStatement { Opcode = RpgOpcode.Parm, Factor1 = name })));
        return program;
    }

    [Theory]
    [InlineData(37, "C1C24040", "F1F2D3", "F1")]
    [InlineData(1208, "41422020", "F1F2D3", "31")]
    public void Scalar_declarations_use_native_API_bytes_without_becoming_single_element_arrays(int ccsid, string text, string zoned, string flag)
    {
        var images = new List<string>(); var types = new List<string?>();
        var program = Compile("dcl-s TEXT char(4) inz('AB');\ndcl-s PACK packed(3:1) inz(-12.3);\ndcl-s ZONE zoned(3:1) inz(-12.3);\n" +
            "dcl-s BYTE int(3) inz(-7);\ndcl-s HALF int(5) inz(-7);\ndcl-s WORD int(10) inz(-7);\ndcl-s LONG int(20) inz(-7);\n" +
            "dcl-s SINGLE float(4) inz(1.5);\ndcl-s DOUBLE float(8) inz(1.5);\ndcl-s FLAG ind inz(*on);\ndcl-s RAW char(2) ccsid(*hex) inz(X'00FF');\n" +
            "callp SNAP(TEXT:PACK:ZONE:BYTE:HALF:WORD:LONG:SINGLE:DOUBLE:FLAG:RAW);\nreturn;");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            foreach (var arg in borrow!().Select(Assert.IsType<ProgramArgument>))
            { images.Add(Convert.ToHexString(arg.ToBuffer()!.ToArray())); types.Add(arg.Type); }
            return new() { Success = true };
        } }).Run(program);
        Assert.Equal(new[] { text, "123D", zoned, "F9", "FFF9", "FFFFFFF9", "FFFFFFFFFFFFFFF9", "3FC00000", "3FF8000000000000", flag, "00FF" }, images);
        Assert.Equal(new[] { "*CHAR", "*DEC", "*ZONED", "*INT", "*INT", "*INT", "*INT", "*FLT", "*FLT", "*LGL", "*CHAR" }, types);
        Assert.All(program.Fields, field => { Assert.True(field.NativeScalar); Assert.False(field.IsArray); Assert.True(context.HasVariable(field.Name)); });
        Assert.Equal(2, Read(context, "%size(PACK)")); Assert.Equal(3, Read(context, "%len(PACK)"));
        Assert.Throws<RpgRuntimeException>(() => Read(context, "%elem(PACK)"));
    }
    [Theory]
    [InlineData("int(3)", "127", "128")]
    [InlineData("int(3)", "-128", "-129")]
    [InlineData("int(5)", "32767", "32768")]
    [InlineData("int(5)", "-32768", "-32769")]
    [InlineData("int(10)", "2147483647", "2147483648")]
    [InlineData("int(10)", "-2147483648", "-2147483649")]
    [InlineData("int(20)", "9223372036854775807", "9223372036854775808")]
    [InlineData("int(20)", "-9223372036854775808", "-9223372036854775809")]
    [InlineData("packed(3:1)", "99.9", "100")]
    [InlineData("zoned(3:1)", "-99.9", "-100")]
    [InlineData("float(4)", "1.5", "1E300")]
    public void Overflow_reports_103_and_preserves_the_previous_scalar_image(string type, string initial, string invalid)
    {
        var context = Run("dcl-s VALUE " + type + " inz(" + initial + ");\ndcl-s STATUS int(5);\nmonitor;\neval VALUE=" + invalid + ";\non-error 103;\neval STATUS=%status();\nendmon;\nreturn;");
        Assert.Equal(103L, context.ReadValue("STATUS"));
        var before = Run("dcl-s VALUE " + type + " inz(" + initial + ");\nreturn;"); Assert.Equal(before.ReadValue("VALUE"), context.ReadValue("VALUE"));
    }
    [Theory]
    [InlineData("packed(5:2)", "12.349", "12.34")]
    [InlineData("packed(5:2)", "-12.349", "-12.34")]
    [InlineData("zoned(5:2)", "12.349", "12.34")]
    [InlineData("zoned(5:2)", "-12.349", "-12.34")]
    [InlineData("packed(3:0)", "12.9", "12")]
    [InlineData("int(3)", "-12.9", "-12")]
    public void Ordinary_numeric_assignment_truncates_fractional_digits_to_the_native_target(string type, string value, string expected)
    {
        var context = Run("dcl-s VALUE " + type + ";\neval VALUE=" + value + ";\nreturn;");
        Assert.Equal(expected, RpgValues.ToText(context.ReadValue("VALUE")));
    }
    [Fact]
    public void Single_precision_assignment_rounds_to_four_bytes()
    {
        var context = Run("dcl-s VALUE float(4);\neval VALUE=1.234567890123;\nreturn;");
        Assert.Equal((float)1.234567890123, Assert.IsType<float>(context.ReadValue("VALUE")));
    }
    [Fact]
    public void Character_storage_and_substrings_count_UTF8_bytes()
    {
        var context = Run("dcl-s TEXT char(3) inz('AB');\neval TEXT='éZ';\neval %subst(TEXT:3:1)='Q';\nreturn;", 1208);
        Assert.Equal("éQ", context.ReadValue("TEXT")); Assert.Equal(3, Read(context, "%len(TEXT)"));
        Assert.Equal("C3A9", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "%subst(TEXT:1:2)")).ToArray()));
    }
    [Fact]
    public void Byte_truncation_preserves_raw_partial_UTF8_until_a_character_read()
    {
        var context = Run("dcl-s TEXT char(1);\neval TEXT='é';\nreturn;", 1208);
        Assert.Equal("C3", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "%subst(TEXT:1:1)")).ToArray()));
        Assert.Equal(452, Assert.Throws<RpgRuntimeException>(() => context.ReadValue("TEXT")).StatusCode);
    }
    [Fact]
    public void Unrepresentable_character_assignment_is_atomic()
    {
        var context = Run("dcl-s TEXT char(4) inz('KEEP');\ndcl-s STATUS int(5);\nmonitor;\neval TEXT='😀';\non-error 452;\neval STATUS=%status();\nendmon;\nreturn;");
        Assert.Equal("KEEP", context.ReadValue("TEXT")); Assert.Equal(452L, context.ReadValue("STATUS"));
    }
    [Fact]
    public void Literal_blank_keywords_remain_text_and_CLEAR_uses_type_defaults()
    {
        var context = Run("dcl-s TEXT char(7);\ndcl-s NUMBER packed(3:1) inz(1.2);\neval TEXT='*BLANKS';\nclear NUMBER;\nreturn;");
        Assert.Equal("*BLANKS", context.ReadValue("TEXT")); Assert.Equal(0m, context.ReadValue("NUMBER"));
    }
    [Theory]
    [InlineData("date", "'2026-10-17'")]
    [InlineData("time", "'12:34:56'")]
    [InlineData("timestamp", "'2026-10-17T12:34:56.123456'")]
    public void Date_and_time_scalars_have_native_defaults_and_CLEAR_images(string type, string value)
    {
        var context = Run("dcl-s VALUE " + type + " inz(" + value + ");\nclear VALUE;\nreturn;");
        Assert.Equal(type == "time" ? (object)TimeSpan.Zero : new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), context.ReadValue("VALUE"));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_scalar_definitions_share_native_conversion_and_monitor_status(int shift)
    {
        var source = Fixed("VALUE", "S", "3", 'I', "0", "INZ(127)", shift) + Fixed("STATUS", "S", "5", 'I', "0", shift: shift) +
            "/free\nmonitor;\neval VALUE=128;\non-error 103;\neval STATUS=%status();\nendmon;\n/end-free";
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "SCALARS", source));
        Assert.Equal(127L, context.ReadValue("VALUE")); Assert.Equal(103L, context.ReadValue("STATUS"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Borrowed_entry_aliases_stay_live_and_forwarded_API_cells_share_identity(int ccsid)
    {
        object? external = "ABCD"; var original = new ProgramArgument(() => external, value => external = value, value => value, "*CHAR", 4);
        var program = Entry("dcl-s A char(4);\ndcl-s B char(4);\n", "eval A='DONE';\ncallp CHECK(A:B);\nreturn;", "A", "B");
        ProgramArgument? retained = null;
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!(); Assert.Same(args[0], args[1]); retained = Assert.IsType<ProgramArgument>(args[0]);
            Assert.Equal("DONE", retained.Value); Assert.NotSame(original, retained.Storage!.Owner);
            retained.Value = new ProgramBuffer(CodePage.ToBytes(ccsid, "LIVE"), ccsid); Assert.Equal("LIVE", external); return new() { Success = true };
        } }).Run(program, [original, original]);
        Assert.Equal("LIVE", context.ReadValue("A")); Assert.Equal("LIVE", context.ReadValue("B"));
        Assert.Throws<ObjectDisposedException>(() => retained!.Value);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Borrowed_integer_conversion_and_retained_state_follow_native_widths(int ccsid)
    {
        object? external = 127L; var original = new ProgramArgument(() => external, value => external = value, value => value, "*INT", 1);
        var source = Fixed("VALUE", "S", "3", 'I', "0") + ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("VALUE", "PARM") +
            "/free\nmonitor;\neval VALUE=128;\non-error 103;\neval VALUE=VALUE-1;\nendmon;\nreturn;\n/end-free";
        var program = RpgCompiler.Compile("QGPL", "SCALARS", source);
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(program, [original]);
        Assert.Equal(126L, external); Assert.Equal(126L, context.ReadValue("VALUE"));
    }
    [Fact]
    public void API_normalization_validates_the_outer_entry_receiver_before_exposing_an_image()
    {
        object? external = "KEEP"; var writes = 0; var reached = false;
        var original = new ProgramArgument(() => external, value => { writes++; external = value; },
            value => value is "KEEP" ? value : throw new CpfException("CPF3C3A", "Rejected by outer receiver."), "*CHAR", 4);
        var program = Entry("dcl-s VALUE char(4);\n", "callp CHECK(VALUE);\nreturn;", "VALUE");
        var context = new RpgRuntimeContext(program, new());
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var arg = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Throws<CpfException>(() => arg.Normalize("FAIL"));
            reached = true; return new() { Success = true };
        } };
        new RpgInterpreter(host).Run(program, [original], context);
        Assert.True(reached); Assert.Equal(0, writes); Assert.Equal("KEEP", external); Assert.Equal("KEEP", context.ReadValue("VALUE"));
    }
    [Theory]
    [InlineData("*INT", 2)] [InlineData("*DEC", 3)] [InlineData("*CHAR", 1)]
    public void Incompatible_entry_layouts_fail_before_calculations(string type, int length)
    {
        var ran = false; var original = new ProgramArgument(() => 1L, _ => { }, value => value, type, length);
        var program = Entry("dcl-s VALUE int(3);\n", "sndpgmmsg 'RAN';\nreturn;", "VALUE");
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { SendMessage = _ => ran = true }).Run(program, [original])); Assert.False(ran);
    }
    [Fact]
    public void Native_storage_limits_count_scalar_declarations_with_structures_and_arrays()
    {
        Assert.Throws<RpgCompileException>(() => Compile(string.Concat(Enumerable.Range(0, 257).Select(i => "dcl-s V" + i + " char(1);\n"))));
    }
    private sealed class CancellingText(CancellationTokenSource stop)
    {
        public override string ToString() { stop.Cancel(); return "NEXT"; }
    }
    [Fact]
    public void Cancellation_during_scalar_conversion_prevents_publication()
    {
        using var stop = new CancellationTokenSource(); var host = new RpgHost { CancellationToken = stop.Token };
        var context = new RpgInterpreter(host).Run(Compile("dcl-s TEXT char(4) inz('KEEP');\nreturn;"));
        Assert.Throws<OperationCanceledException>(() => context.WriteValue("TEXT", new CancellingText(stop)));
        Assert.Equal("KEEP", context.ReadValue("TEXT"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Outer_entry_rejection_keeps_the_real_queue_entry_and_every_receiver_unchanged(int ccsid)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var store = new DataQueueStore(system.Connections);
        store.Create(new("QGPL", "SCALARR"), 8); store.Send(new("QGPL", "SCALARR"), new(CodePage.ToBytes(ccsid, "DATA"), ccsid));
        object? external = "TAILTAIL"; var writes = 0; CpfException? rejected = null;
        var original = new ProgramArgument(() => external, value => { writes++; external = value; },
            value => value is "TAILTAIL" ? value : throw new CpfException("CPF3C3A", "Outer receiver rejected the prefix."), "*CHAR", 8);
        var program = Entry("dcl-s DATA char(8);\ndcl-s LEN packed(5:0) inz(99);\n", "callp QRCVDTAQ('SCALARR':'QGPL':LEN:DATA:0);\nreturn;", "DATA");
        program.MainStatements.Single(s => s.Opcode == RpgOpcode.CallP).Indicator1 = 99;
        var host = new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, target, _, borrow) =>
        {
            try { new DataQueueApi(system.Connections, _ => ["QGPL", "QSYS"], ccsid).Invoke(target, borrow!()); return new() { Success = true }; }
            catch (CpfException error) { rejected = error; return new() { Success = false, Message = error.Message }; }
        } };
        var context = new RpgInterpreter(host).Run(program, [original]);
        Assert.Equal("CPF3C3A", rejected?.MessageId); Assert.Equal(0, writes); Assert.Equal("TAILTAIL", external);
        Assert.Equal("TAILTAIL", context.ReadValue("DATA")); Assert.Equal(99m, context.ReadValue("LEN"));
        Assert.Equal(1, store.Inspect(new("QGPL", "SCALARR")).Entries);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Distinct_entry_cells_with_overlapping_storage_keep_live_byte_ranges(int ccsid)
    {
        var bytes = CodePage.ToBytes(ccsid, "ABCDEF"); var owner = new object();
        ProgramArgument Cell(int offset) => new(
            () => new ProgramBuffer(bytes.AsSpan(offset, 4), ccsid),
            value => Assert.IsType<ProgramBuffer>(value).ToArray().CopyTo(bytes, offset),
            value => value is ProgramBuffer buffer && buffer.Length == 4 && buffer.Ccsid == ccsid ? buffer : throw new CpfException("CPF3C3A", "Invalid slice."),
            "*CHAR", 4, 0, () => new ProgramBuffer(bytes.AsSpan(offset, 4), ccsid), null, new(owner, offset, 4));
        var program = Entry("dcl-s A char(4);\ndcl-s B char(4);\n", "callp CHECK(A:B);\nreturn;", "A", "B");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!(); Assert.NotSame(args[0], args[1]); Assert.True(ProgramArgument.Overlaps(args[0], args[1]));
            Assert.IsType<ProgramArgument>(args[0]).Value = new ProgramBuffer(CodePage.ToBytes(ccsid, "WXYZ"), ccsid);
            Assert.Equal("YZEF", Assert.IsType<ProgramArgument>(args[1]).Value); return new() { Success = true };
        } }).Run(program, [Cell(0), Cell(2)]);
        Assert.Equal("WXYZ", context.ReadValue("A")); Assert.Equal("YZEF", context.ReadValue("B"));
    }
    [Fact]
    public void Forwarded_readonly_entry_metadata_rejects_an_API_receiver_before_queue_processing()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var store = new DataQueueStore(system.Connections);
        store.Create(new("QGPL", "SCALARRO"), 4); var calls = 0; CpfException? rejected = null;
        var buffer = new ProgramBuffer(CodePage.ToBytes(37, "KEEP"), 37); var literal = new ProgramConstant("*CHAR", 4, 0, buffer, "KEEP");
        var original = new ProgramArgument(() => "KEEP", _ => throw new InvalidOperationException("Readonly write."),
            _ => { calls++; throw new CpfException("CPF3C90", "Readonly receiver."); }, "*CHAR", 4, 0, () => buffer, literal);
        var program = Entry("dcl-s DATA char(4);\ndcl-s LEN packed(5:0) inz(99);\n", "callp QRCVDTAQ('SCALARRO':'QGPL':LEN:DATA:0);\nreturn;", "DATA");
        program.MainStatements.Single(s => s.Opcode == RpgOpcode.CallP).Indicator1 = 99;
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, target, _, borrow) =>
        {
            try { new DataQueueApi(system.Connections, _ => ["QGPL", "QSYS"], 37).Invoke(target, borrow!()); return new() { Success = true }; }
            catch (CpfException error) { rejected = error; return new() { Success = false, Message = error.Message }; }
        } }).Run(program, [original]);
        Assert.Equal("CPF3C90", rejected?.MessageId); Assert.Equal(0, calls); Assert.Equal(99m, context.ReadValue("LEN"));
    }
    [Fact]
    public void Cancellation_detaches_the_latest_semantic_entry_value_without_reapplying_write_checks()
    {
        using var stop = new CancellationTokenSource(); object? external = 5L;
        var original = new ProgramArgument(() => external, value => external = value, value => value, "*INT", 1);
        var program = Entry("dcl-s VALUE int(3);\n", "callp CHECK(VALUE);\nreturn;", "VALUE");
        var host = new RpgHost { CancellationToken = stop.Token, TypedProgramCaller = (_, _, _, _) =>
        { external = 6L; stop.Cancel(); return new() { Success = true }; } };
        var context = new RpgRuntimeContext(program, host);
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program, [original], context));
        Assert.Equal(6L, context.ReadValue("VALUE")); external = 7L; Assert.Equal(6L, context.ReadValue("VALUE"));
    }
}
