using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgRightAssignmentTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "RIGHTALIGN", "**free\n" + source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(37, "AB", "   AB")] [InlineData(1208, "AB", "   AB")]
    [InlineData(37, "ABCDEFG", "CDEFG")] [InlineData(1208, "ABCDEFG", "CDEFG")]
    [InlineData(37, "AB  ", " AB  ")] [InlineData(1208, "AB  ", " AB  ")]
    [InlineData(37, "", "     ")] [InlineData(1208, "", "     ")]
    public void Scalar_assignments_pad_left_and_truncate_left_without_trimming_the_source(int ccsid, string input, string expected)
    {
        var context = Run("dcl-s TEXT char(5) inz('KEEP');\nevalr TEXT='" + input + "';", ccsid); Assert.Equal(expected, Read(context, "TEXT"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Whole_structure_and_hex_assignments_preserve_raw_bytes(int ccsid)
    {
        var context = Run("dcl-ds DATA qualified len(5) end-ds;\ndcl-s RAW char(4) ccsid(*hex);\nevalr DATA='XY';\nevalr RAW=X'00FF';", ccsid);
        Assert.Equal("   XY", Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToText());
        var blank = Ipc.Core.Text.CodePage.FromCcsid(ccsid).GetBytes(" ")[0]; Assert.Equal(new byte[] { blank, blank, 0, 255 }, Assert.IsType<ProgramBuffer>(Read(context, "RAW")).ToArray());
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_fixed_extended_factor_two_executes_right_adjusted_assignment(int ccsid)
    {
        var source = "       dcl-s TEXT char(5);\n" + RpgFixedCalculationTests.C("EVALR", expression: "TEXT='AB'");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "FIXRIGHT", source)); Assert.Equal("   AB", Read(context, "TEXT"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Array_assignments_use_common_prefixes_and_keep_extra_target_elements(bool explicitStar)
    {
        var context = Run("dcl-s A char(2) dim(2);\ndcl-s B char(4) dim(3) inz('KEEP');\neval A(1)='AB';\neval A(2)='XY';\nevalr B" + (explicitStar ? "(*)" : "") + "=A" + (explicitStar ? "(*)" : "") + ";");
        Assert.Equal("  AB", Read(context, "B(1)")); Assert.Equal("  XY", Read(context, "B(2)")); Assert.Equal("KEEP", Read(context, "B(3)"));
    }
    [Fact]
    public void Scalar_function_results_broadcast_after_one_evaluation()
    {
        var context = Run("dcl-s COUNT int(10);\ndcl-s A char(4) dim(3);\nevalr A=GETTEXT();\nreturn;\ndcl-proc GETTEXT;\ndcl-pi *n char(2) end-pi;\neval COUNT=COUNT+1;\nreturn 'AB';\nend-proc;"); Assert.Equal(1L, Read(context, "COUNT")); Assert.Equal(new object?[] { "  AB", "  AB", "  AB" }, Assert.IsType<object?[]>(Read(context, "A")));
    }
    [Fact]
    public void Elementwise_character_expressions_align_each_result()
    {
        var context = Run("dcl-s A char(2) dim(2) inz('AB');\ndcl-s B char(1) dim(3) inz('X');\ndcl-s OUT char(5) dim(3) inz('KEEP');\nevalr OUT=A(*)+B(*);"); Assert.Equal("  ABX", Read(context, "OUT(2)")); Assert.Equal("KEEP ", Read(context, "OUT(3)"));
    }
    [Fact]
    public void Overlapping_array_slices_snapshot_the_source_before_publishing_updates()
    {
        var context = Run("dcl-s A char(3) dim(3);\neval A(1)='ONE';\neval A(2)='TWO';\neval A(3)='THR';\nevalr %subarr(A:2:2)=%subarr(A:1:2);"); Assert.Equal("ONE", Read(context, "A(2)")); Assert.Equal("TWO", Read(context, "A(3)"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Nested_structure_arrays_accept_scalar_or_array_sources(bool arraySource)
    {
        var context = Run("dcl-ds BOX qualified inz;\ndcl-ds CHILD dim(2) len(4) end-ds;\nend-ds;\ndcl-s INPUT char(2) dim(2) inz('AB');\nevalr BOX.CHILD=" + (arraySource ? "INPUT" : "'XY'") + ";"); Assert.Equal(arraySource ? "  AB" : "  XY", Assert.IsType<ProgramBuffer>(Read(context, "BOX.CHILD(2)")).ToText());
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Substring_targets_use_the_selected_width_and_preserve_neighbors(int ccsid)
    {
        var context = Run("dcl-s TEXT char(8) inz('ABCDEFGH');\nevalr %subst(TEXT:3:4)='XY';", ccsid); Assert.Equal("AB  XYGH", Read(context, "TEXT"));
    }
    [Fact]
    public void Substring_target_indices_and_source_functions_are_captured_once()
    {
        var context = Run("dcl-s COUNT int(10);\ndcl-s TEXT char(8) inz('ABCDEFGH');\nevalr %subst(TEXT:NEXT():3)=SOURCE();\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;\ndcl-proc SOURCE;\ndcl-pi *n char(1) end-pi;\neval COUNT=COUNT+1;\nreturn 'X';\nend-proc;"); Assert.Equal(2L, Read(context, "COUNT")); Assert.Equal("A  XEFGH", Read(context, "TEXT"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Borrowed_varsize_substrings_obey_actual_storage_bounds(bool invalid)
    {
        var context = Run("dcl-s TEXT char(3) inz('ABC');\ndcl-s CAUGHT ind;\ncallp CHANGE(TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP char(10) options(*varsize);\nend-pi;\nmonitor;\nevalr %subst(P:" + (invalid ? "3:2" : "2:2") + ")='X';\non-error 105;\neval CAUGHT=*on;\nendmon;\nend-proc;"); Assert.Equal(invalid ? "ABC" : "A X", Read(context, "TEXT")); Assert.Equal(invalid, Read(context, "CAUGHT"));
    }
    [Fact]
    public void Empty_substring_assignments_are_bounded_no_ops()
    {
        var context = Run("dcl-s TEXT char(3) inz('ABC');\nevalr %subst(TEXT:4:0)='X';"); Assert.Equal("ABC", Read(context, "TEXT"));
    }
    [Fact]
    public void Right_adjustment_does_not_change_null_indicators()
    {
        var context = Run("ctl-opt alwnull(*usrctl);\ndcl-s TEXT char(4) nullind;\neval %nullind(TEXT)=*on;\ncallp CHANGE(TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP char(4) options(*nullind);\nend-pi;\nevalr P='X';\nend-proc;"); Assert.Equal("   X", Read(context, "TEXT")); Assert.Equal(true, Read(context, "%nullind(TEXT)"));
    }
    [Fact]
    public void Unrepresentable_source_text_preserves_every_array_element()
    {
        var context = Run("dcl-s A char(4) dim(2) inz('KEEP');\ndcl-s CAUGHT ind;\nmonitor;\nevalr A='😀';\non-error 452;\neval CAUGHT=*on;\nendmon;"); Assert.Equal("KEEP", Read(context, "A(1)")); Assert.Equal("KEEP", Read(context, "A(2)")); Assert.Equal(true, Read(context, "CAUGHT"));
    }
    [Fact]
    public void Host_validation_rejects_the_complete_prepared_array_before_any_writeback()
    {
        var bytes = Ipc.Core.Text.CodePage.ToBytes(37, "KEEPKEEP"); var writes = 0; var validated = false;
        var storage = new ProgramArgumentStorage(new object(), 0, bytes.Length);
        var shape = new ProgramArgumentShape("Character", 4, 0, 2);
        var memory = new ProgramArgumentMemory(storage, 37, shape,
            (offset, count) => new(bytes.AsSpan(offset, count).ToArray(), 37),
            (_, value) => { Assert.Equal("  AB  AB", value.ToText()); validated = true; throw new Ipc.Core.Messages.CpfException("CPF3C90", "Rejected host image."); },
            (_, _) => writes++);
        var argument = new ProgramArgument(() => new ProgramBuffer(bytes, 37), _ => writes++, value => value, "*CHAR", 8) { Memory = memory, Shape = shape };
        var program = RpgCompiler.Compile("QGPL", "HOSTRIGHT", "**free\ndcl-pi *n;\nP char(4) dim(2);\nend-pi;\nevalr P='AB';");
        Assert.ThrowsAny<Exception>(() => new RpgInterpreter(new()).Run(program, [argument]));
        Assert.True(validated); Assert.Equal(0, writes); Assert.Equal("KEEPKEEP", Ipc.Core.Text.CodePage.FromBytes(37, bytes));
    }
    [Fact]
    public void Whole_structure_assignment_uses_only_the_current_occurrence()
    {
        var context = Run("dcl-ds DATA qualified occurs(2) inz;\nTEXT char(4);\nend-ds;\ndcl-s FIRST char(4);\neval %occur(DATA)=2;\nevalr DATA='AB';\neval %occur(DATA)=1;\neval FIRST=DATA.TEXT;\neval %occur(DATA)=2;"); Assert.Equal("    ", Read(context, "FIRST")); Assert.Equal("  AB", Read(context, "DATA.TEXT"));
    }
    [Theory]
    [InlineData("2.1")] [InlineData("2.0")] [InlineData("'2'")] [InlineData("1E0")]
    public void Substring_positions_do_not_coerce_fractional_or_character_values(string position)
    {
        var context = Run("dcl-s TEXT char(3) inz('ABC');\ndcl-s CAUGHT ind;\nmonitor;\nevalr %subst(TEXT:" + position + ":1)='X';\non-error 105;\neval CAUGHT=*on;\nendmon;"); Assert.Equal("ABC", Read(context, "TEXT")); Assert.Equal(true, Read(context, "CAUGHT"));
    }
    [Fact]
    public void An_indexed_substring_position_does_not_repeat_its_subscript_procedure()
    {
        var context = Run("dcl-s COUNT int(10);\ndcl-s POSITIONS int(10) dim(2) inz(2);\ndcl-s TEXT char(8) inz('ABCDEFGH');\nevalr %subst(TEXT:POSITIONS(NEXT()):2)='X';\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;"); Assert.Equal(1L, Read(context, "COUNT")); Assert.Equal("A XDEFGH", Read(context, "TEXT"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Indexed_source_and_target_positions_do_not_repeat_nested_subscripts(bool source)
    {
        var context = Run("dcl-s COUNT int(10);\ndcl-s POSITIONS int(10) dim(2) inz(2);\ndcl-s INPUT char(2) dim(2) inz('AB');\ndcl-s OUT char(4) dim(2);\nevalr " + (source ? "OUT(1)=INPUT(POSITIONS(NEXT()))" : "OUT(POSITIONS(NEXT()))='X'") + ";\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;"); Assert.Equal(1L, Read(context, "COUNT")); Assert.Equal(source ? "  AB" : "   X", Read(context, source ? "OUT(1)" : "OUT(2)"));
    }
    [Theory]
    [InlineData("evalr 1='X';")] [InlineData("evalr N='X';")] [InlineData("evalr TEXT=N;")]
    [InlineData("evalr TEXT=1;")] [InlineData("evalr TEXT=ARR;")] [InlineData("evalr TEXT;")]
    [InlineData("evalr TEXT(*)='X';")] [InlineData("evalr TEXT=TEXT(*); ")]
    [InlineData("evalr %subst(N:1:1)='X';")] [InlineData("evalr(M) TEXT='X';")]
    public void Invalid_types_ranks_targets_and_extenders_fail_compilation(string statement)
    {
        var error = Assert.Throws<RpgCompileException>(() => Run("dcl-s TEXT char(4);\ndcl-s N int(10);\ndcl-s ARR char(2) dim(2);\n" + statement)); Assert.Equal(5, error.Location!.Line);
    }
    [Fact]
    public void Const_substring_targets_reject_before_execution()
    {
        Assert.Throws<RpgCompileException>(() => Run("dcl-s TEXT char(4);\ncallp CHANGE(TEXT);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP char(4) const;\nend-pi;\nevalr %subst(P:1:2)='X';\nend-proc;"));
    }
}
