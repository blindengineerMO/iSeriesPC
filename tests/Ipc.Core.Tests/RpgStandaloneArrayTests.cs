using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed class RpgStandaloneArrayTests
{
    private static RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "ARRAYS", "**free\n" + body);
    private static RpgRuntimeContext Run(string body) => new RpgInterpreter(new()).Run(Compile(body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(37, "C1C2C1C2C1C2", "F1F1F1")]
    [InlineData(1208, "414241424142", "313131")]
    public void Standalone_arrays_share_native_layouts_initializers_and_scoped_API_buffers(int ccsid, string text, string flags)
    {
        var buffers = new List<string>();
        var program = Compile("dcl-c COUNT 3;\ndcl-s NUM int(5) dim(COUNT) inz(-7);\ndcl-s TEXT char(2) dim(COUNT) inz('AB');\n" +
            "dcl-s FLAGS ind dim(COUNT) inz(*on);\ndcl-s MONEY packed(3:1) dim(COUNT) inz(12.3);\ncallp SNAP(NUM:TEXT:FLAGS:MONEY);\nreturn;");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            foreach (var value in borrow!())
            {
                var argument = Assert.IsType<ProgramArgument>(value); Assert.Equal(0, argument.Decimals);
                buffers.Add(Convert.ToHexString(argument.ToBuffer()!.ToArray()));
            }
            return new() { Success = true };
        } }).Run(program);
        Assert.Equal(new[] { "FFF9FFF9FFF9", text, flags, "123C123C123C" }, buffers);
        Assert.Equal(3, Read(context, "%elem(NUM)")); Assert.Equal(2, Read(context, "%size(NUM)")); Assert.Equal(6, Read(context, "%size(NUM:*all)"));
        Assert.Equal(-7L, Read(context, "NUM(3)")); Assert.Equal(12.3m, Read(context, "MONEY(2)"));
    }
    [Fact]
    public void Defaults_and_CLEAR_initialize_every_typed_element_without_restoring_INZ()
    {
        var context = Run("dcl-s NUM int(3) dim(2);\ndcl-s FLAGS ind dim(2);\ndcl-s TEXT char(2) dim(2) inz('AB');\n" +
            "dcl-s MONEY packed(5:2) dim(2);\ndcl-s WHEN date dim(2);\nclear TEXT;\nreturn;");
        Assert.Equal(0L, Read(context, "NUM(2)")); Assert.Equal(false, Read(context, "FLAGS(1)")); Assert.Equal("  ", Read(context, "TEXT(2)"));
        Assert.Equal(0m, Read(context, "MONEY(2)")); Assert.Equal(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), Read(context, "WHEN(1)"));
    }
    [Fact]
    public void Whole_assignment_and_array_arithmetic_process_common_elements_and_preserve_the_tail()
    {
        var context = Run("dcl-s LEFT int(5) dim(3) inz(9);\ndcl-s RIGHT int(5) dim(2) inz(2);\n" +
            "eval LEFT=RIGHT+1;\neval RIGHT=LEFT*2;\nreturn;");
        Assert.Equal(new object?[] { 3L, 3L, 9L }, Assert.IsType<object?[]>(context.ReadValue("LEFT")));
        Assert.Equal(new object?[] { 6L, 6L }, Assert.IsType<object?[]>(context.ReadValue("RIGHT")));
    }
    [Fact]
    public void Whole_assignment_broadcasts_scalars_and_returns_detached_semantic_arrays()
    {
        var context = Run("dcl-s NUM int(10) dim(2);\neval NUM=7;\nreturn;");
        var copy = Assert.IsType<object?[]>(context.ReadValue("NUM")); copy[0] = 100L;
        Assert.Equal(7L, Read(context, "NUM(1)")); Assert.Equal(7L, Read(context, "NUM(2)"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Constant_layout_queries_preserve_single_element_arrays(int count)
    {
        var context = Run("dcl-c COUNT " + count + ";\ndcl-s NUM int(5) dim(COUNT);\ndcl-c WIDTH %size(NUM:*all);\n" +
            "dcl-c ELEMENTS %elem(NUM);\ndcl-s COPY char(WIDTH);\neval NUM(COUNT)=11;\nreturn;");
        Assert.Equal(count, Read(context, "%elem(NUM)")); Assert.Equal((decimal)count, context.ReadValue("ELEMENTS"));
        Assert.Equal(2m * count, context.ReadValue("WIDTH")); Assert.Equal(11L, Read(context, "NUM(" + count + ")"));
    }
    [Theory]
    [InlineData("'1'")]
    [InlineData("*on")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("FLOATING")]
    [InlineData("%abs(SMALL(1))")]
    public void Invalid_index_types_report_105_for_reads_and_writes_without_changing_storage(string index)
    {
        var context = Run("dcl-s NUM int(10) dim(2) inz(7);\ndcl-s FLOATING float(8) inz(1);\ndcl-s SMALL float(4) dim(1) inz(1);\n" +
            "dcl-s HANDLED int(10);\nmonitor;\neval NUM(" + index + ")=9;\non-error 105;\neval HANDLED=HANDLED+1;\nendmon;\n" +
            "monitor;\neval HANDLED=NUM(" + index + ");\non-error 105;\neval HANDLED=HANDLED+1;\nendmon;\nreturn;");
        Assert.Equal(2L, context.ReadValue("HANDLED")); Assert.Equal(7L, Read(context, "NUM(1)"));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("9999999999999999999999999999")]
    public void Invalid_index_ranges_report_121_without_narrowing_overflow(string index)
    {
        var context = Run("dcl-s NUM int(3) dim(2) inz(7);\nmonitor;\neval NUM(" + index + ")=9;\non-error 121;\n" +
            "eval NUM(2)=11;\nendmon;\nreturn;");
        Assert.Equal(7L, Read(context, "NUM(1)")); Assert.Equal(11L, Read(context, "NUM(2)"));
    }
    [Fact]
    public void INT_truncation_selects_the_same_element_for_native_DS_and_standalone_arrays()
    {
        var context = Run("dcl-s NUM int(3) dim(2);\ndcl-ds ROWS qualified dim(2) inz;\nVALUE int(3);\nend-ds;\n" +
            "eval NUM(%int(1.9))=7;\neval ROWS(%int(1.9)).VALUE=9;\nreturn;");
        Assert.Equal(7L, Read(context, "NUM(1)")); Assert.Equal(0L, Read(context, "NUM(2)")); Assert.Equal(9L, Read(context, "ROWS(1).VALUE"));
    }
    [Theory]
    [InlineData("2.9", 2L)]
    [InlineData("-2.9", -2L)]
    [InlineData("2.9E0", 2L)]
    [InlineData("-9.223372036854776E18", long.MinValue)]
    [InlineData("' 2 . 9 '", 2L)]
    [InlineData("'2,9-'", -2L)]
    public void INT_discards_fractional_digits_for_numeric_and_character_values(string expression, long expected)
    {
        Assert.Equal(expected, Read(Run("return;"), "%int(" + expression + ")"));
    }
    [Theory]
    [InlineData("'BAD'", 105)]
    [InlineData("'1E2'", 105)]
    [InlineData("''", 105)]
    [InlineData("*on", 105)]
    [InlineData("9223372036854775808", 103)]
    public void INT_rejects_invalid_data_and_overflow_with_numeric_status(string expression, int status)
    {
        Assert.Equal(status, Assert.Throws<RpgRuntimeException>(() => Read(Run("return;"), "%int(" + expression + ")")).StatusCode);
    }
    [Fact]
    public void Failed_array_conversion_keeps_all_original_elements()
    {
        var context = Run("dcl-s DEST int(3) dim(2) inz(99);\ndcl-s ADDEND int(3) dim(2) inz(1);\n" +
            "eval ADDEND(2)=100;\nmonitor;\neval DEST=DEST+ADDEND;\non-error 103;\nendmon;\nreturn;");
        Assert.Equal(new object?[] { 99L, 99L }, Assert.IsType<object?[]>(context.ReadValue("DEST")));
    }
    [Fact]
    public void Cancelled_whole_assignment_and_CLEAR_preserve_all_elements()
    {
        using var stop = new CancellationTokenSource(); var host = new RpgHost { CancellationToken = stop.Token };
        var context = new RpgInterpreter(host).Run(Compile("dcl-s NUM int(3) dim(3) inz(7);\nreturn;")); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.WriteValue("NUM", 9m)); Assert.Throws<OperationCanceledException>(() => context.ClearValue("NUM"));
        Assert.Equal(new object?[] { 7L, 7L, 7L }, Assert.IsType<object?[]>(context.ReadValue("NUM")));
    }
    private sealed class CancellingText(CancellationTokenSource stop)
    {
        public override string ToString() { stop.Cancel(); return "C"; }
    }
    [Fact]
    public void Cancellation_during_the_last_element_conversion_prevents_array_publication()
    {
        using var stop = new CancellationTokenSource(); var host = new RpgHost { CancellationToken = stop.Token };
        var context = new RpgInterpreter(host).Run(Compile("dcl-s TEXT char(1) dim(2) inz('A');\nreturn;"));
        Assert.Throws<OperationCanceledException>(() => context.WriteValue("TEXT", new object?[] { "B", new CancellingText(stop) }));
        Assert.Equal(new object?[] { "A", "A" }, Assert.IsType<object?[]>(context.ReadValue("TEXT")));
    }
    [Fact]
    public void Borrowed_array_and_element_views_share_bytes_and_capture_indexes_before_lazy_borrowing()
    {
        RpgRuntimeContext? context = null; ProgramArgument? captured = null;
        var program = Compile("dcl-s NUM int(10) dim(3) inz(5);\ndcl-s I int(10) inz(1);\ncallp MODIFY(NUM:NUM(I):NUM(I));\nreturn;");
        var host = new RpgHost { TypedProgramCaller = (_, _, values, borrow) =>
        {
            var detached = Assert.IsType<object?[]>(values[0]); detached[1] = 999L; context!.WriteValue("I", 3L);
            var arguments = borrow!(); captured = Assert.IsType<ProgramArgument>(arguments[0]); var first = Assert.IsType<ProgramArgument>(arguments[1]);
            Assert.Same(first, arguments[2]); Assert.True(ProgramArgument.Overlaps(captured, first)); Assert.Equal(12, captured.Length);
            Assert.Equal(5L, context.ReadArrayValue("NUM", 2)); first.Value = 42L;
            Assert.Equal("0000002A0000000500000005", Convert.ToHexString(captured.ToBuffer()!.ToArray())); return new() { Success = true };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(42L, Read(context, "NUM(1)")); Assert.Equal(5L, Read(context, "NUM(3)"));
        Assert.Throws<ObjectDisposedException>(() => captured!.ToBuffer());
    }
    [Fact]
    public void Copied_host_writeback_preserves_the_tail_and_original_element_address()
    {
        RpgRuntimeContext? context = null;
        var program = Compile("dcl-s NUM int(10) dim(3) inz(5);\ndcl-s I int(10) inz(1);\ncallp MODIFY(NUM:NUM(I));\nreturn;");
        var host = new RpgHost { ProgramCaller = (_, _, values) =>
        {
            Assert.Equal(new object?[] { 5L, 5L, 5L }, Assert.IsType<object?[]>(values[0])); context!.WriteValue("I", 3L);
            return new() { Success = true, UpdatedParameters = [new object?[] { 9m, 8m }, 7m] };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(new object?[] { 7L, 8L, 5L }, Assert.IsType<object?[]>(context.ReadValue("NUM")));
    }
    [Fact]
    public void Scaled_numeric_array_borrows_as_raw_character_storage_through_a_scalar_prototype()
    {
        var program = Compile("dcl-s MONEY packed(3:1) dim(3) inz(12.3);\ncallp MODIFY(MONEY);\nreturn;");
        program.Fields.Add(new() { Name = "FORMAL", Kind = RpgFieldKind.Character, Length = 6, OpaqueBytes = true });
        var prototype = new RpgPrototype { Name = "MODIFY", ExternalName = "MODIFY_NATIVE" }; prototype.Parameters.Add("FORMAL"); program.Prototypes.Add(prototype);
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, name, _, borrow) =>
        {
            Assert.Equal("MODIFY_NATIVE", name); var argument = Assert.IsType<ProgramArgument>(Assert.Single(borrow!()));
            Assert.Equal("*CHAR", argument.Type); Assert.Equal(0, argument.Decimals); Assert.Equal(6, argument.Length);
            argument.Value = new ProgramBuffer(Convert.FromHexString("075C123C123C"), 37); return new() { Success = true };
        } }).Run(program);
        Assert.Equal(7.5m, Read(context, "MONEY(1)")); Assert.Equal(12.3m, Read(context, "MONEY(3)"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_queue_receivers_validate_character_cells_before_dequeue_and_preserve_opaque_bytes(bool opaque)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var store = new DataQueueStore(system.Connections); store.Create(new("QGPL", "APIQUEUE"), 8);
        store.Send(new("QGPL", "APIQUEUE"), new ProgramBuffer([0xFF, 0, 0xFE], 1208));
        var program = Compile("dcl-s LENGTH packed(5:0) inz(8);\ndcl-s DATA char(4) dim(2) " +
            (opaque ? "ccsid(*hex) inz(X'5441494C')" : "inz('TAIL')") + ";\ncallp QRCVDTAQ('APIQUEUE':'QGPL':LENGTH:DATA:0);\nreturn;");
        var host = new RpgHost { Ccsid = () => 1208, TypedProgramCaller = (_, target, _, borrow) =>
        {
            new DataQueueApi(system.Connections, _ => ["QGPL"], 1208).Invoke(target, borrow!()); return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host);
        if (opaque)
        {
            new RpgInterpreter(host).Run(program, existingContext: context);
            var bytes = Assert.IsType<object?[]>(context.ReadValue("DATA")).Cast<ProgramBuffer>().SelectMany(b => b.ToArray()).ToArray();
            Assert.Equal("FF00FE4C5441494C", Convert.ToHexString(bytes)); Assert.Equal(3m, context.ReadValue("LENGTH"));
            Assert.Equal(0, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
        else
        {
            Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program, existingContext: context));
            Assert.Equal(new object?[] { "TAIL", "TAIL" }, Assert.IsType<object?[]>(context.ReadValue("DATA")));
            Assert.Equal(8m, context.ReadValue("LENGTH")); Assert.Equal(1, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
    }
    [Theory]
    [InlineData("dim(0)")]
    [InlineData("dim(-1)")]
    [InlineData("dim(1.0)")]
    [InlineData("dim(2E0)")]
    [InlineData("dim('2')")]
    [InlineData("dim(32768)")]
    [InlineData("dim")]
    [InlineData("dim(2) dim(3)")]
    [InlineData("dim(*auto:10)")]
    public void Invalid_or_unimplemented_dimensions_fail_with_original_locations(string options)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("dcl-s NUM int(10) " + options + ";\nreturn;")).Location);
    }
    [Fact]
    public void Native_array_storage_and_integer_initializers_keep_existing_bounds()
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s TEXT char(32767) dim(32767);"));
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s NUM int(3) dim(2) inz(128);"));
        var source = string.Join('\n', Enumerable.Range(0, 17).Select(i => $"dcl-s ARR{i} char(32767) dim(32);"));
        Assert.Contains("16 MiB", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }
}
