using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgAggregateParameterTests
{
    private static RpgRuntimeContext Run(string source, RpgHost? host = null) => new RpgInterpreter(host ?? new()).Run(RpgCompiler.Compile("QGPL", "AGGREGATE", source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private const string Template = "dcl-ds ITEM qualified;\nN int(3);\nTEXT char(3);\nend-ds;\n";

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Arrays_borrow_a_formal_prefix_and_element_aliases_observe_updates(int ccsid)
    {
        var context = Run("**free\ndcl-s ARR int(3) dim(4) inz(7);\ncallp CHANGE(ARR:ARR(2));\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nA int(3) dim(3);\nB int(3);\nend-pi;\neval A(2)=10;\neval B=B+A(2);\neval A(1)=B;\nreturn;\nend-proc;", new() { Ccsid = () => ccsid });
        Assert.Equal(new object?[] { 20L, 20L, 7L, 7L }, Read(context, "ARR"));
    }

    [Theory]
    [InlineData("const", 9)] [InlineData("value", 7)]
    public void CONST_array_aliases_are_live_and_VALUE_arrays_are_private(string mode, int expected)
    {
        var context = Run("**free\ndcl-s ARR int(3) dim(3) inz(7);\ndcl-s RESULT int(3);\neval RESULT=CHANGE(ARR:ARR);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n int(3);\nINPUT int(3) dim(2) " + mode + ";\nOUTPUT int(3) dim(2);\nend-pi;\neval OUTPUT(1)=9;\nreturn INPUT(1);\nend-proc;");
        Assert.Equal((long)expected, context.ReadValue("RESULT")); Assert.Equal(9L, Read(context, "ARR(1)")); Assert.Equal(7L, Read(context, "ARR(3)"));
    }

    [Fact]
    public void Numeric_array_copies_convert_each_element_using_the_formal_codec()
    {
        var context = Run("**free\ndcl-s ARR zoned(5:2) dim(3) inz(12.34);\ndcl-s RESULT packed(5:1);\neval RESULT=GET(ARR);\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(5:1);\nINPUT packed(5:1) dim(2) const;\nend-pi;\nreturn %xfoot(INPUT);\nend-proc;");
        Assert.Equal(24.6m, context.ReadValue("RESULT")); Assert.Equal(12.34m, Read(context, "ARR(1)"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEDS_parameters_update_qualified_subfields_and_overlapping_scalar_aliases(int ccsid)
    {
        var context = Run("**free\n" + Template + "eval ITEM.N=7;\neval ITEM.TEXT='ABC';\ncallp CHANGE(ITEM:ITEM.N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(ITEM);\nN int(3);\nend-pi;\neval P.N=P.N+1;\neval N=N+P.N;\neval P.TEXT='XYZ';\nreturn;\nend-proc;", new() { Ccsid = () => ccsid });
        Assert.Equal(16L, Read(context, "ITEM.N")); Assert.Equal("XYZ", Read(context, "ITEM.TEXT"));
    }

    [Theory]
    [InlineData("", "XYZ")] [InlineData("value", "ABC")]
    public void Structure_array_parameters_support_nested_indexing_and_private_VALUE_images(string mode, string expected)
    {
        var context = Run("**free\ndcl-ds ITEMS qualified dim(3);\nTEXT char(3);\nNUM int(3) dim(2);\nend-ds;\neval ITEMS(2).TEXT='ABC';\neval ITEMS(3).TEXT='END';\ncallp CHANGE(ITEMS);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(ITEMS) dim(2) " + mode + ";\nend-pi;\neval P(2).TEXT='XYZ';\neval P(2).NUM(2)=9;\nreturn;\nend-proc;");
        Assert.Equal(expected, Read(context, "ITEMS(2).TEXT")); Assert.Equal("END", Read(context, "ITEMS(3).TEXT"));
        if (mode == "") Assert.Equal(9L, Read(context, "ITEMS(2).NUM(2)"));
    }

    [Fact]
    public void LIKEDS_can_use_a_character_actual_and_preserves_its_tail()
    {
        var context = Run("**free\ndcl-ds TEMPLATE qualified;\nA char(2);\nB char(2);\nend-ds;\ndcl-s TEXT char(6) inz('ABCDEF');\ncallp CHANGE(TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(TEMPLATE);\nend-pi;\neval P.B='XY';\nreturn;\nend-proc;"); Assert.Equal("ABXYEF", context.ReadValue("TEXT"));
    }

    [Fact]
    public void Nested_calls_and_subarray_assignment_keep_the_original_array_binding()
    {
        var context = Run("**free\ndcl-s ARR int(3) dim(5) inz(1);\ncallp FIRST(ARR);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n;\nA int(3) dim(4);\nend-pi;\neval %subarr(A:2:2)=7;\ncallp SECOND(A);\nreturn;\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nB int(3) dim(3);\nend-pi;\neval B(3)=B(2)+1;\nreturn;\nend-proc;"); Assert.Equal(new object?[] { 1L, 7L, 8L, 1L, 1L }, Read(context, "ARR"));
    }

    [Theory]
    [InlineData("const")] [InlineData("value")]
    public void SUBARR_inputs_use_private_images_with_the_selected_range(string mode)
    {
        var context = Run("**free\ndcl-s ARR int(3) dim(4) inz(1);\ndcl-s RESULT int(3);\neval ARR(2)=7;\neval RESULT=GET(%subarr(ARR:2:2));\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nA int(3) dim(2) " + mode + ";\nend-pi;\nreturn A(1)+A(2);\nend-proc;"); Assert.Equal(8L, context.ReadValue("RESULT"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void External_aggregate_descriptors_are_bounded_live_and_expire_at_return(bool structure)
    {
        var declarations = structure ? Template : "dcl-s ITEM int(3) dim(3) inz(1);\n";
        var formal = structure ? "likeds(ITEM)" : "int(3) dim(2)";
        ProgramArgument? retained = null;
        Run("**free\n" + declarations + "dcl-pr RUN extpgm('TARGET');\nA " + formal + " const;\nB " + formal + ";\nend-pr;\ncallp RUN(ITEM:ITEM);", new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!(); var input = Assert.IsType<ProgramArgument>(args[0]); var output = Assert.IsType<ProgramArgument>(args[1]); retained = input;
            Assert.Equal(structure ? 4 : 2, input.Length); Assert.True(input.IsReadOnly); Assert.NotNull(input.Shape); Assert.True(ProgramArgument.Overlaps(input, output));
            var bytes = output.ToBuffer()!.ToArray(); bytes[0] = 9; output.Value = new ProgramBuffer(bytes, 37);
            Assert.Equal(9, input.ToBuffer()!.ToArray()[0]); Assert.NotNull(input.NormalizeReturnedValue(input.ToBuffer()));
            Assert.Throws<CpfException>(() => input.Value = input.ToBuffer()); return new() { Success = true };
        } });
        Assert.Throws<ObjectDisposedException>(() => retained!.ToBuffer());
    }

    [Theory]
    [InlineData("int(5) dim(2)")] [InlineData("int(3)")] [InlineData("char(1) dim(2)")]
    public void Incompatible_mutable_array_actuals_reject_during_compilation(string declaration)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nA int(3) dim(2);\nend-pr;\ndcl-s ARR " + declaration + ";\ncallp RUN(ARR);"));

    [Theory]
    [InlineData("A(1)=7")] [InlineData("%subarr(A:1:1)=7")]
    public void CONST_array_writes_are_source_diagnostics(string assignment)
        => Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nA int(3) dim(2) const;\nend-pi;\neval " + assignment + ";\nreturn;\nend-proc;"));

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_array_signatures_bind_with_both_column_origins(int shift)
    {
        var source = D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) + D("P", length: "3", type: 'I', decimals: "0", keywords: "DIM(2)", shift: shift) +
            D("ARR", "S", length: "3", type: 'I', decimals: "0", keywords: "DIM(3) INZ(7)", shift: shift) + RpgFixedCalculationTests.C("CALLP", expression: "RUN(ARR)");
        Run(source, new() { TypedProgramCaller = (_, _, _, borrow) => { var arg = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal(new byte[] { 7, 7 }, arg.ToBuffer()!.ToArray()); return new() { Success = true }; } });
    }

    [Theory]
    [InlineData("int(3)", "7", 2)] [InlineData("packed(4:1)", "12.3", 6)]
    [InlineData("zoned(4:1)", "12.3", 8)] [InlineData("float(8)", "1.5", 16)]
    [InlineData("char(3)", "'ABC'", 6)] [InlineData("ind", "*on", 2)]
    [InlineData("date", "'2026-10-18'", 20)] [InlineData("time", "'12:34:56'", 16)]
    [InlineData("timestamp", "'2026-10-18T12:34:56.123456'", 52)]
    public void Aggregate_descriptors_use_native_element_byte_widths(string type, string initial, int width)
    {
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP " + type + " dim(2);\nend-pr;\ndcl-s ARR " + type + " dim(3) inz(" + initial + ");\ncallp RUN(ARR);", new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var arg = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal(width, arg.Length); Assert.Equal(width, arg.Storage!.Length); Assert.Equal(width, arg.ToBuffer()!.Length); return new() { Success = true }; } });
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 3)]
    public void Optional_structure_arrays_distinguish_unpassed_omitted_and_present_storage(int state, int expected)
    {
        var context = Run("**free\n" + Template + "dcl-ds ROWS qualified dim(2);\nN int(3);\nTEXT char(3);\nend-ds;\ndcl-s RESULT int(3);\neval RESULT=GET(" + (state == 0 ? "" : state == 1 ? "*omit" : "ROWS") + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nP likeds(ITEM) dim(2) const options(*omit:*nopass);\nend-pi;\nif %passed(P);\nreturn 3;\nendif;\nif %omitted(P);\nreturn 2;\nendif;\nreturn 1;\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("RESULT"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Short_arrays_reject_before_target_dispatch(bool readOnly)
    {
        var called = false;
        var error = Assert.Throws<RpgRuntimeException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nP int(3) dim(2) " + (readOnly ? "const" : "") + ";\nend-pr;\ndcl-s ARR int(3) dim(1);\ncallp RUN(ARR);", new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }));
        Assert.Equal(105, error.StatusCode);
        Assert.False(called);
    }

    [Theory]
    [InlineData("P.N=1")] [InlineData("P.TEXT='BAD'")]
    public void CONST_structure_subfields_cannot_be_assigned(string assignment)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n" + Template + "return;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ITEM) const;\nend-pi;\neval " + assignment + ";\nreturn;\nend-proc;"));

    [Theory]
    [InlineData("int(3) dim(2)", "int(3) dim(3)")]
    [InlineData("int(3) dim(2)", "int(3)")]
    [InlineData("int(3) dim(2) const", "int(3) dim(2)")]
    public void Named_PR_PI_metadata_checks_dimensions_and_passing_modes(string prototype, string entry)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nP " + prototype + ";\nend-pr;\ndcl-pi RUN;\nP " + entry + ";\nend-pi;\nreturn;"));

    [Fact]
    public void Bound_structure_arrays_support_keyed_sort_and_lookup_without_replacing_their_tail()
    {
        var context = Run("**free\ndcl-ds ROWS qualified dim(4);\nKEY int(3);\nTEXT char(1);\nend-ds;\ndcl-s RESULT int(3);\neval ROWS(1).KEY=3;\neval ROWS(2).KEY=1;\neval ROWS(3).KEY=2;\neval ROWS(4).KEY=9;\neval RESULT=SORT(ROWS);\nreturn;\ndcl-proc SORT;\ndcl-pi *n int(3);\nP likeds(ROWS) dim(3);\nend-pi;\nsorta P(*).KEY;\nreturn %lookup(2:P(*).KEY);\nend-proc;");
        Assert.Equal(2L, context.ReadValue("RESULT")); Assert.Equal(1L, Read(context, "ROWS(1).KEY")); Assert.Equal(3L, Read(context, "ROWS(3).KEY")); Assert.Equal(9L, Read(context, "ROWS(4).KEY"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_LIKEDS_accepts_passing_keywords_before_its_type(int shift)
    {
        var source = D("ITEM", "DS", keywords: "QUALIFIED", shift: shift) + D("TEXT", length: "4", shift: shift) +
            D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) + D("P", keywords: "CONST LIKEDS(ITEM)", shift: shift) + RpgFixedCalculationTests.C("CALLP", expression: "RUN(ITEM)");
        Run(source, new() { TypedProgramCaller = (_, _, _, borrow) => { Assert.True(Assert.IsType<ProgramArgument>(borrow!().Single()).IsReadOnly); return new() { Success = true }; } });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Absent_aggregate_entry_storage_cannot_be_dereferenced(bool structure, bool omitted)
    {
        var source = "**free\n" + Template + "dcl-pi *n;\nP " + (structure ? "likeds(ITEM)" : "int(3) dim(2)") + " options(*nopass:*omit);\nend-pi;\ndcl-s N int(3);\neval N=" + (structure ? "P.N" : "P(1)") + ";";
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "ABSENT", source), omitted ? new object?[] { null } : null));
        Assert.Equal(105, error.StatusCode);
    }

    [Theory]
    [InlineData("char(32767) dim(33)")] [InlineData("int(3) dim(32768)")]
    [InlineData("int(3) dim(0)")] [InlineData("int(3) dim(2) dim(3)")]
    [InlineData("likeds(MISSING)")] [InlineData("likeds(ITEM) dim(*var)")]
    public void Invalid_aggregate_dimensions_and_templates_reject_before_runtime_allocation(string formal)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n" + Template + "dcl-pr RUN extpgm('TARGET');\nP " + formal + ";\nend-pr;\nreturn;"));

    [Theory]
    [InlineData("'ABCD'")] [InlineData("X'C1C2C3C4'")]
    public void LIKEDS_CONST_literals_use_a_private_character_image(string actual)
    {
        var context = Run("**free\ndcl-ds TEMPLATE qualified;\nA char(2);\nB char(2);\nend-ds;\ndcl-s RESULT char(2);\neval RESULT=GET(" + actual + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n char(2);\nP likeds(TEMPLATE) const;\nend-pi;\nreturn P.B;\nend-proc;"); Assert.Equal("CD", context.ReadValue("RESULT"));
    }

    [Fact]
    public void Plain_host_array_values_initialize_a_private_native_entry_image()
    {
        var program = RpgCompiler.Compile("QGPL", "HOSTARRAY", "**free\ndcl-pi *n;\nA int(3) dim(2);\nend-pi;\neval A(2)=A(1)+A(2);\nreturn;");
        var input = new object?[] { 3L, 4L };
        var context = new RpgInterpreter(new()).Run(program, new object?[] { input });
        Assert.Equal(7L, Read(context, "A(2)")); Assert.Equal(4L, input[1]);
    }

    [Fact]
    public void Whole_array_reads_do_not_refetch_the_entire_host_image_for_every_element()
    {
        var reads = 0; var image = new ProgramBuffer(Enumerable.Repeat((byte)1, 32767).ToArray(), 37);
        ProgramBuffer Buffer() { Assert.InRange(++reads, 1, 5); return image; }
        var argument = new ProgramArgument(() => Buffer(), _ => throw new InvalidOperationException(), v => v, "*CHAR", image.Length, 0, Buffer);
        var program = RpgCompiler.Compile("QGPL", "BULK", "**free\ndcl-pi *n;\nA int(3) dim(32767) const;\nend-pi;\ndcl-s TOTAL int(10);\neval TOTAL=%xfoot(A);\nreturn;");
        var context = new RpgInterpreter(new()).Run(program, new object?[] { argument }); Assert.Equal(32767L, context.ReadValue("TOTAL"));
    }

    [Fact]
    public void Whole_array_assignment_validates_and_publishes_one_image_with_bounded_host_reads()
    {
        var reads = 0; var writes = 0; var image = new ProgramBuffer(new byte[32767], 37);
        ProgramBuffer Buffer() { Assert.InRange(++reads, 1, 5); return image; }
        var argument = new ProgramArgument(() => Buffer(), value => { writes++; image = Assert.IsType<ProgramBuffer>(value); },
            value => Assert.IsType<ProgramBuffer>(value), "*CHAR", image.Length, 0, Buffer);
        var program = RpgCompiler.Compile("QGPL", "BULK", "**free\ndcl-pi *n;\nA int(3) dim(32767);\nend-pi;\neval A=2;\nreturn;");
        new RpgInterpreter(new()).Run(program, new object?[] { argument }); Assert.Equal(1, writes); Assert.All(image.ToArray(), b => Assert.Equal(2, b));
    }

    [Fact]
    public void Aggregate_call_layouts_do_not_enable_scalar_fixed_LOOKUP_on_whole_structure_arrays()
    {
        var source = D("ITEM", "DS", keywords: "QUALIFIED DIM(2)") + D("TEXT", length: "4") + RpgFixedCalculationTests.C("LOOKUP", "'ABCD'", "ITEM", eq: "11");
        Assert.Throws<RpgCompileException>(() => Run(source));
    }
}
