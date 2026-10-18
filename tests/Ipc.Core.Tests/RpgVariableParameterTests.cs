using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgVariableParameterTests
{
    private static RpgRuntimeContext Run(string source, RpgHost? host = null) => new RpgInterpreter(host ?? new()).Run(RpgCompiler.Compile("QGPL", "VARSIZE", source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Short_character_actuals_support_partial_reads_writes_and_live_aliases(int ccsid)
    {
        var context = Run("**free\ndcl-s TEXT char(4) inz('ABCD');\ncallp CHANGE(TEXT:TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP char(100) options(*varsize);\nQ char(100) const options(*varsize);\nend-pi;\neval %subst(P:2:2)='XY';\nif %subst(Q:1:4)='AXYD';\neval %subst(P:4:1)='Z';\nendif;\nreturn;\nend-proc;", new() { Ccsid = () => ccsid }); Assert.Equal("AXYZ", context.ReadValue("TEXT"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void The_IBM_variable_character_array_search_uses_actual_element_stride(int ccsid)
    {
        var context = Run("**free\ndcl-s A char(1) dim(7) inz('A');\ndcl-s B char(10) dim(3) inz('Blue');\ndcl-s ONE int(3);\ndcl-s TWO int(3);\neval A(5)='*';\neval B(2)='Pink';\neval ONE=SEARCH(A:7:1:'*':1);\neval TWO=SEARCH(B:3:10:'Pink':4);\nreturn;\ndcl-proc SEARCH;\ndcl-pi *n int(3);\nP char(50) dim(100) const options(*varsize);\nCOUNT int(3) value;\nWIDTH int(3) value;\nFIND char(50) const options(*varsize);\nLEN int(3) value;\nend-pi;\ndcl-s I int(3);\nfor I=1 to COUNT;\nif %subst(P(I):1:WIDTH)=%subst(FIND:1:LEN);\nreturn I;\nendif;\nendfor;\nreturn 0;\nend-proc;", new() { Ccsid = () => ccsid }); Assert.Equal(5L, context.ReadValue("ONE")); Assert.Equal(2L, context.ReadValue("TWO"));
    }

    [Fact]
    public void Numeric_arrays_keep_declared_dimension_but_bound_writes_to_actual_elements()
    {
        var context = Run("**free\ndcl-s A int(3) dim(3) inz(7);\ndcl-s N int(10);\neval N=CHANGE(A:3);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n int(10);\nP int(3) dim(100) options(*varsize);\nCOUNT int(3) value;\nend-pi;\neval P(COUNT)=9;\nreturn %elem(P)*100+%size(P:*all)+P(1);\nend-proc;"); Assert.Equal(10107L, context.ReadValue("N")); Assert.Equal(new object?[] { 7L, 7L, 9L }, Read(context, "A"));
    }

    [Theory]
    [InlineData("eval %subst(P:4:1)='X';")] [InlineData("eval P='X';")]
    [InlineData("eval N=%len(%subst(P:1));")]
    public void Access_beyond_actual_character_storage_reports_105_before_writing(string operation)
    {
        var context = Run("**free\ndcl-s TEXT char(3) inz('ABC');\ndcl-s RESULT int(10);\neval RESULT=CHECK(TEXT);\nreturn;\ndcl-proc CHECK;\ndcl-pi *n int(10);\nP char(100) options(*varsize);\nend-pi;\ndcl-s N int(10);\nmonitor;\n" + operation + "\non-error;\nreturn %status();\nendmon;\nreturn 0;\nend-proc;"); Assert.Equal(105L, context.ReadValue("RESULT")); Assert.Equal("ABC", context.ReadValue("TEXT"));
    }

    [Theory]
    [InlineData("eval P(4)=9;", 105)] [InlineData("eval P(101)=9;", 121)]
    public void Actual_storage_bounds_are_distinct_from_declared_array_bounds(string operation, int expected)
    {
        var context = Run("**free\ndcl-s A int(3) dim(3) inz(7);\ndcl-s RESULT int(10);\neval RESULT=CHECK(A);\nreturn;\ndcl-proc CHECK;\ndcl-pi *n int(10);\nP int(3) dim(100) options(*varsize);\nend-pi;\nmonitor;\n" + operation + "\non-error;\nreturn %status();\nendmon;\nreturn 0;\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("RESULT")); Assert.Equal(new object?[] { 7L, 7L, 7L }, Read(context, "A"));
    }

    [Fact]
    public void Fixed_prefixes_retain_actual_capacity_when_forwarded_to_a_larger_variable_interface()
    {
        var context = Run("**free\ndcl-s TEXT char(6) inz('ABCDEF');\ndcl-s A int(3) dim(4) inz(7);\ncallp FIRST(TEXT:A);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n;\nP char(2);\nQ int(3) dim(2);\nend-pi;\ncallp SECOND(P:Q);\nreturn;\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nP char(100) options(*varsize);\nQ int(3) dim(100) options(*varsize);\nend-pi;\neval %subst(P:5:2)='XY';\neval Q(4)=9;\nreturn;\nend-proc;"); Assert.Equal("ABCDXY", context.ReadValue("TEXT")); Assert.Equal(9L, Read(context, "A(4)"));
    }

    [Fact]
    public void Variable_structure_members_observe_actual_storage_and_can_update_a_short_character_buffer()
    {
        var context = Run("**free\ndcl-ds TEMPLATE qualified;\nA char(2);\nB char(2);\nTAIL char(10);\nend-ds;\ndcl-s TEXT char(4) inz('ABCD');\ncallp CHANGE(TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(TEMPLATE) options(*varsize);\nend-pi;\neval P.B='XY';\nreturn;\nend-proc;"); Assert.Equal("ABXY", context.ReadValue("TEXT"));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 12)] [InlineData(2, 22)]
    public void Variable_optional_omitted_parameters_keep_RTNParm_positions(int state, int expected)
    {
        var context = Run("**free\ndcl-s TEXT char(2) inz('AB');\ndcl-s N int(3);\neval N=GET(" + (state == 0 ? "" : state == 1 ? "*omit" : "TEXT") + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) rtnparm;\nP char(100) const options(*varsize:*nopass:*omit);\nend-pi;\nif %passed(P);\nreturn %parms()+20;\nendif;\nif %omitted(P);\nreturn %parms()+10;\nendif;\nreturn %parms();\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("N"));
    }

    [Fact]
    public void Variable_argument_descriptors_expose_actual_storage_and_expire_including_memory_views()
    {
        ProgramArgument? retained = null;
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(100) options(*varsize);\nend-pr;\ndcl-s TEXT char(4) inz('ABCD');\ncallp RUN(TEXT);", new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            retained = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal(4, retained.Length); Assert.Equal(4, retained.Storage!.Length);
            Assert.Equal(4, retained.Memory!.Capacity); retained.Memory.Write(1, new ProgramBuffer(new byte[] { 0xE7 }, 37)); Assert.Equal("AXCD", retained.ToBuffer()!.ToText());
            Assert.Throws<CpfException>(() => retained.Memory.Write(4, new ProgramBuffer(new byte[] { 1 }, 37))); return new() { Success = true };
        } });
        Assert.Throws<ObjectDisposedException>(() => retained!.Memory!.Read(0, 1)); Assert.Throws<ObjectDisposedException>(() => retained!.ToBuffer());
    }

    [Theory]
    [InlineData("int(3) options(*varsize)")] [InlineData("char(4) value options(*varsize)")]
    [InlineData("char(4) options(*varsize:*varsize)")]
    public void Invalid_variable_passing_modes_reject_during_compilation(string formal)
        => Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP " + formal + ";\nend-pi;\nreturn;\nend-proc;"));

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Native_fixed_signatures_accept_variable_character_arrays_with_both_column_origins(int shift)
    {
        var source = RpgFixedStructureTests.D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) +
            RpgFixedStructureTests.D("P", length: "50", keywords: "DIM(100) OPTIONS(*VARSIZE)", shift: shift) +
            RpgFixedStructureTests.D("A", "S", length: "2", keywords: "DIM(3) INZ('AB')", shift: shift) + RpgFixedCalculationTests.C("CALLP", expression: "RUN(A)");
        Run(source, new() { TypedProgramCaller = (_, _, _, borrow) => { var arg = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal(6, arg.Length); Assert.Equal(2, arg.Shape!.ElementLength); Assert.Equal(3, arg.Shape.Dimension); return new() { Success = true }; } });
    }

    [Theory]
    [InlineData("int(3) dim(3)", "int(5) dim(100) options(*varsize)")]
    [InlineData("char(2) dim(3)", "char(50) options(*varsize)")]
    [InlineData("char(2)", "char(50) dim(100) options(*varsize)")]
    public void Variable_size_does_not_remove_array_rank_or_numeric_type_checks(string actual, string formal)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nP " + formal + ";\nend-pr;\ndcl-s A " + actual + ";\ncallp RUN(A);"));

    [Fact]
    public void Named_PR_PI_must_agree_on_variable_size_mode()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr GET;\nP char(4) options(*varsize);\nend-pr;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void Numeric_CONST_conversions_and_expressions_use_private_formal_images()
    {
        var context = Run("**free\ndcl-s A zoned(5:2) dim(2) inz(12.34);\ndcl-s N packed(5:1);\neval N=GET(A)+GET(A+1);\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(5:1);\nP packed(5:1) dim(3) const options(*varsize);\nend-pi;\nreturn %xfoot(P);\nend-proc;"); Assert.Equal(51.2m, context.ReadValue("N")); Assert.Equal(12.34m, Read(context, "A(1)"));
    }

    [Fact]
    public void Mutable_SUBARR_assignment_uses_the_valid_actual_numeric_prefix()
    {
        var context = Run("**free\ndcl-s A int(3) dim(4) inz(7);\ncallp SET(A);\nreturn;\ndcl-proc SET;\ndcl-pi *n;\nP int(3) dim(100) options(*varsize);\nend-pi;\neval %subarr(P:2:2)=9;\nreturn;\nend-proc;"); Assert.Equal(new object?[] { 7L, 9L, 9L, 7L }, Read(context, "A"));
    }

    [Fact]
    public void A_scalar_array_element_does_not_expose_the_rest_of_the_actual_array()
    {
        var context = Run("**free\ndcl-s A char(1) dim(3) inz('A');\ndcl-s N int(10);\neval N=FIRST(A);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n int(10);\nP char(50) dim(100) options(*varsize);\nend-pi;\nreturn SECOND(P(2));\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n int(10);\nP char(100) options(*varsize);\nend-pi;\nmonitor;\neval %subst(P:1:2)='XY';\non-error;\nreturn %status();\nendmon;\nreturn 0;\nend-proc;"); Assert.Equal(105L, context.ReadValue("N")); Assert.Equal("A", Read(context, "A(3)"));
    }

    [Fact]
    public void Plain_host_values_use_actual_capacity_and_detach_without_retaining_a_borrow()
    {
        var program = RpgCompiler.Compile("QGPL", "HOST", "**free\ndcl-pi *n;\nP char(100) options(*varsize);\nA int(3) dim(100) options(*varsize);\nend-pi;\neval %subst(P:1:1)='X';\neval A(2)=9;\nreturn;");
        var values = new object?[] { 7L, 8L };
        var context = new RpgInterpreter(new()).Run(program, new object?[] { "AB", values });
        Assert.Equal("XB", Assert.IsType<ProgramBuffer>(Read(context, "%subst(P:1:2)")).ToText()); Assert.Equal(9L, Read(context, "A(2)")); Assert.Equal(8L, values[1]);
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => Read(context, "A(3)")).StatusCode);
    }

    [Fact]
    public void Entry_admission_counts_actual_storage_instead_of_the_larger_variable_declarations()
    {
        var program = RpgCompiler.Compile("QGPL", "HOST", "**free\ndcl-pi *n;\nP char(32767) dim(32) const options(*varsize);\nQ char(32767) dim(32) const options(*varsize);\nend-pi;\nreturn;");
        new RpgInterpreter(new()).Run(program, new object?[] { new object?[] { "A" }, new object?[] { "B" } });
    }

    [Fact]
    public void Undersized_forwarding_to_a_fixed_interface_fails_before_entering_the_target()
    {
        var context = Run("**free\ndcl-s TEXT char(3) inz('ABC');\ndcl-s COUNT int(3) inz(0);\ndcl-s RESULT int(10);\neval RESULT=FIRST(TEXT);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n int(10);\nP char(100) options(*varsize);\nend-pi;\nmonitor;\ncallp SECOND(P);\non-error 105;\nreturn %status();\nendmon;\nreturn 0;\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nP char(50);\nend-pi;\neval COUNT=1;\nreturn;\nend-proc;"); Assert.Equal(105L, context.ReadValue("RESULT")); Assert.Equal(0L, context.ReadValue("COUNT"));
    }

    [Fact]
    public void Actual_backing_storage_is_bounded_before_host_dispatch_even_through_small_fixed_prefixes()
    {
        var called = false;
        var error = Assert.Throws<CpfException>(() => Run("**free\ndcl-ds BIG qualified len(1048576);\nX char(1);\nend-ds;\ndcl-ds SMALL qualified;\nX char(1);\nend-ds;\ndcl-pr RUN extpgm('TARGET');\nP likeds(SMALL);\nQ likeds(SMALL);\nend-pr;\ncallp RUN(BIG:BIG);", new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }));
        Assert.Equal("CPF3C3A", error.MessageId);
        Assert.False(called);
    }

    [Fact]
    public void Variable_CONST_descriptors_allow_unchanged_echoes_and_reject_memory_writes()
    {
        ProgramArgument? saved = null;
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(100) const options(*varsize);\nend-pr;\ndcl-s TEXT char(4) inz('ABCD');\ncallp RUN(TEXT);", new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            saved = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.True(saved.Memory!.IsReadOnly); Assert.NotNull(saved.NormalizeReturnedValue(saved.ToBuffer()));
            Assert.Throws<CpfException>(() => saved.Memory.Write(0, new(new byte[] { 0xE7 }, 37))); return new() { Success = true };
        } }); Assert.Throws<ObjectDisposedException>(() => saved!.Memory!.Validate(0, new(new byte[] { 0xE7 }, 37)));
    }

    [Fact]
    public void Short_array_RHS_assignment_updates_only_its_actual_prefix()
    {
        var context = Run("**free\ndcl-s A int(3) dim(4) inz(7);\ncallp SET(A);\nreturn;\ndcl-proc SET;\ndcl-pi *n;\nP int(3) dim(100) options(*varsize);\nend-pi;\ndcl-s SOURCE int(3) dim(2) inz(9);\neval P=SOURCE;\nreturn;\nend-proc;"); Assert.Equal(new object?[] { 9L, 9L, 7L, 7L }, Read(context, "A"));
    }

    [Fact]
    public void Substring_address_expressions_are_evaluated_once()
    {
        var context = Run("**free\ndcl-s A char(1) dim(3) inz('A');\ndcl-s COUNT int(3) inz(0);\ndcl-s RESULT char(1);\neval A(2)='B';\neval A(3)='C';\neval RESULT=%subst(A(NEXT()):1:1);\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(3) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;"); Assert.Equal("A", context.ReadValue("RESULT")); Assert.Equal(1L, context.ReadValue("COUNT"));
    }
}
