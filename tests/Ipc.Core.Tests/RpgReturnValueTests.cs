using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgReturnValueTests
{
    private static RpgRuntimeContext Run(string source, RpgHost? host = null) => new RpgInterpreter(host ?? new()).Run(RpgCompiler.Compile("QGPL", "RETURNS", source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private const string Template = "dcl-ds ITEM qualified;\nN int(3);\nTEXT char(3);\nend-ds;\n";

    [Theory]
    [InlineData("int(3)", "7", "7")]
    [InlineData("packed(5:1)", "12.34", "12.3")]
    [InlineData("zoned(5:1)", "12.34", "12.3")]
    [InlineData("float(8)", "1.5", "1.5")]
    [InlineData("char(3)", "'AB'", "'AB '")]
    [InlineData("char(2) ccsid(*hex)", "X'FF00'", "X'FF00'")]
    [InlineData("ind", "*on", "*on")]
    [InlineData("date", "'2026-10-18'", "'2026-10-18'")]
    [InlineData("time", "'12:34:56'", "%time('12:34:56')")]
    [InlineData("timestamp", "'2026-10-18T12:34:56.123456'", "%timestamp('2026-10-18T12:34:56.123456')")]
    public void Scalar_RETURN_broadcasts_through_the_declared_array_codec(string type, string value, string expected)
    {
        var context = Run("**free\ndcl-s A " + type + " dim(3);\ndcl-s OK ind;\neval A=GET();\neval OK=A(1)=" + expected + " and A(3)=" + expected + ";\nreturn;\ndcl-proc GET;\ndcl-pi *n " + type + " dim(3) end-pi;\nreturn " + value + ";\nend-proc;"); Assert.Equal(true, context.ReadValue("OK"));
    }

    [Theory]
    [InlineData(1, 0, 0)] [InlineData(2, 7, 0)] [InlineData(4, 7, 7)]
    public void Returned_arrays_pad_missing_elements_and_ignore_surplus_elements(int sourceSize, int second, int third)
    {
        var context = Run("**free\ndcl-s A int(3) dim(4) inz(9);\neval A=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(3) end-pi;\ndcl-s LOCAL int(3) dim(" + sourceSize + ") inz(7);\nreturn LOCAL;\nend-proc;");
        Assert.Equal(new object?[] { 7L, (long)second, (long)third, 9L }, Read(context, "A"));
    }

    [Fact]
    public void Array_returns_convert_expressions_and_compose_through_other_returning_procedures()
    {
        var context = Run("**free\ndcl-s A packed(5:1) dim(3);\neval A=OUTER();\nreturn;\ndcl-proc INNER;\ndcl-pi *n packed(5:1) dim(3) end-pi;\ndcl-s N zoned(5:2) dim(2) inz(12.34);\nreturn N+1;\nend-proc;\ndcl-proc OUTER;\ndcl-pi *n packed(5:1) dim(3) end-pi;\nreturn INNER()+1;\nend-proc;"); Assert.Equal(new object?[] { 14.3m, 14.3m, 1m }, Read(context, "A"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Returned_structure_images_detach_from_the_module_and_other_results(int ccsid)
    {
        var context = Run("**free\n" + Template + "dcl-ds FIRST likeds(ITEM);\ndcl-ds SECOND likeds(ITEM);\neval ITEM.N=7;\neval ITEM.TEXT='ABC';\neval FIRST=GET();\neval ITEM.N=9;\neval SECOND=GET();\neval SECOND.TEXT='XYZ';\nreturn;\ndcl-proc GET;\ndcl-pi *n likeds(ITEM) end-pi;\nreturn ITEM;\nend-proc;", new() { Ccsid = () => ccsid });
        Assert.Equal(7L, Read(context, "FIRST.N")); Assert.Equal("ABC", Read(context, "FIRST.TEXT")); Assert.Equal(9L, Read(context, "SECOND.N")); Assert.Equal("ABC", Read(context, "ITEM.TEXT"));
    }

    [Theory]
    [InlineData("'AB'", "AB  ")] [InlineData("'ABCDEF'", "ABCD")]
    public void Structure_returns_use_character_assignment_padding_and_truncation(string operand, string expected)
    {
        var context = Run("**free\ndcl-ds ITEM qualified;\nTEXT char(4);\nend-ds;\neval ITEM=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n likeds(ITEM) end-pi;\nreturn " + operand + ";\nend-proc;"); Assert.Equal(expected, Read(context, "ITEM.TEXT"));
    }

    [Theory]
    [InlineData(1, "   ")] [InlineData(4, "ABC")]
    public void Structure_array_returns_default_missing_images_and_truncate_extra_images(int dimension, string expected)
    {
        var context = Run("**free\ndcl-ds ROWS qualified dim(4);\nTEXT char(3);\nend-ds;\neval ROWS(4).TEXT='END';\neval ROWS=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n likeds(ROWS) dim(3) end-pi;\ndcl-ds LOCAL likeds(ROWS) dim(" + dimension + ");\neval LOCAL='ABC';\nreturn LOCAL;\nend-proc;");
        Assert.Equal("ABC", Read(context, "ROWS(1).TEXT")); Assert.Equal(expected, Read(context, "ROWS(3).TEXT")); Assert.Equal("END", Read(context, "ROWS(4).TEXT"));
    }

    [Theory]
    [InlineData(false, 111)] [InlineData(true, 222)]
    public void RTNPARM_counts_the_hidden_first_parameter_in_runtime_and_declaration_constants(bool hidden, int expected)
    {
        var context = Run("**free\ndcl-s N int(3) inz(7);\ndcl-s RESULT int(10);\neval RESULT=GET(N);\nreturn;\ndcl-proc GET;\ndcl-pi *n int(10) " + (hidden ? "rtnparm" : "") + ";\nN int(3);\nend-pi;\ndcl-c POSITION %parmnum(N);\neval N=N+1;\nreturn %parms()*100+%parmnum(N)*10+POSITION;\nend-proc;");
        Assert.Equal((long)expected, context.ReadValue("RESULT")); Assert.Equal(8L, context.ReadValue("N"));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 12)] [InlineData(2, 22)]
    public void RTNPARM_preserves_optional_and_omitted_explicit_parameter_positions(int state, int expected)
    {
        var context = Run("**free\ndcl-s RESULT int(3);\neval RESULT=GET(" + (state == 0 ? "" : state == 1 ? "*omit" : "7") + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) rtnparm;\nP int(3) const options(*nopass:*omit);\nend-pi;\nif %passed(P);\nreturn 20+%parms();\nendif;\nif %omitted(P);\nreturn 10+%parms();\nendif;\nreturn %parms();\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("RESULT"));
    }

    [Fact]
    public void RTNPARM_procedure_counts_do_not_change_the_main_PSDS()
    {
        var context = Run("**free\ndcl-ds PS qualified psds;\nN *parms;\nend-ds;\ndcl-s RESULT int(3);\neval RESULT=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) rtnparm end-pi;\nreturn %parms()+PS.N;\nend-proc;"); Assert.Equal(1L, context.ReadValue("RESULT")); Assert.Equal(0, Read(context, "%parms()"));
    }

    [Theory]
    [InlineData("int(3) dim(2)", "int(3) dim(3)")]
    [InlineData("int(3) rtnparm", "int(3)")]
    [InlineData("likeds(ITEM)", "char(4)")]
    public void Explicit_prototypes_require_matching_return_shape_and_hidden_parameter_mode(string prototype, string pi)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n" + Template + "dcl-pr GET " + prototype + " end-pr;\nreturn;\ndcl-proc GET;\ndcl-pi *n " + pi + " end-pi;\nreturn 1;\nend-proc;"));

    [Theory]
    [InlineData("rtnparm")] [InlineData("int(3) rtnparm rtnparm")] [InlineData("int(3) rtnparm(1)")]
    [InlineData("int(3) dim(*var)")] [InlineData("char(32767) dim(33)")]
    [InlineData("likeds(MISSING)")] [InlineData("int(3) const")]
    public void Invalid_return_declarations_reject_at_the_interface(string type)
        => Assert.NotNull(Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\ndcl-pi *n " + type + " end-pi;\nreturn 1;\nend-proc;")).Location);

    [Theory]
    [InlineData(254, true)] [InlineData(255, false)]
    public void RTNPARM_reserves_one_position_in_the_native_parameter_bound(int count, bool valid)
    {
        var source = "**free\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) rtnparm;\n" + string.Concat(Enumerable.Range(0, count).Select(i => "P" + i + " int(3) options(*nopass);\n")) + "end-pi;\nreturn %parms();\nend-proc;";
        if (valid) Assert.Equal(1L, Run(source).ReadValue("N")); else Assert.Throws<RpgCompileException>(() => Run(source));
    }

    [Fact]
    public void Return_images_participate_in_the_declared_invocation_storage_budget()
    {
        var context = Run("**free\ndcl-ds IMAGE qualified len(1048576);\nTEXT char(1);\nend-ds;\ndcl-s COUNT int(10) inz(0);\ncallp(e) GET(65);\nreturn;\ndcl-proc GET;\ndcl-pi *n likeds(IMAGE) rtnparm;\nN int(3) value;\nend-pi;\neval COUNT=COUNT+1;\nif N>0;\ncallp GET(N-1);\nendif;\nreturn IMAGE;\nend-proc;"); Assert.Equal(63L, context.ReadValue("COUNT")); Assert.Equal(true, Read(context, "%error()"));
    }

    [Fact]
    public void An_invalid_return_element_leaves_the_callers_array_receiver_unchanged()
    {
        var context = Run("**free\ndcl-s A int(3) dim(3) inz(7);\ndcl-s STATUS int(10);\nmonitor;\neval A=GET();\non-error;\neval STATUS=%status();\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(3) end-pi;\ndcl-s LOCAL int(10) dim(3) inz(1);\neval LOCAL(3)=128;\nreturn LOCAL;\nend-proc;"); Assert.Equal(new object?[] { 7L, 7L, 7L }, Read(context, "A")); Assert.Equal(202L, context.ReadValue("STATUS"));
    }

    [Theory]
    [InlineData("int(3) dim(2)")] [InlineData("likeds(ITEM)")]
    public void Aggregate_return_fallthrough_signals_202(string type)
    {
        var context = Run("**free\n" + Template + "callp(e) GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n " + type + " end-pi;\nend-proc;"); Assert.Equal(true, Read(context, "%error()")); Assert.Equal(202, Read(context, "%status()"));
    }

    [Fact]
    public void An_array_cannot_be_returned_or_assigned_to_scalar_storage()
    {
        var source = "**free\ndcl-s N int(3) inz(7);\ndcl-s STATUS int(10);\nmonitor;\neval N=GET();\non-error;\neval STATUS=%status();\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(2) end-pi;\nreturn 1;\nend-proc;";
        var context = Run(source); Assert.Equal(7L, context.ReadValue("N")); Assert.Equal(105L, context.ReadValue("STATUS"));
        Assert.Equal(202, Assert.Throws<RpgRuntimeException>(() => Run("**free\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-s A int(3) dim(2) inz(1);\nreturn A;\nend-proc;")).StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CLEAR_of_a_borrowed_structure_or_structure_array_updates_the_caller(bool array)
    {
        var context = Run("**free\ndcl-ds ITEM qualified " + (array ? "dim(3)" : "") + ";\nN int(3);\nTEXT char(3);\nend-ds;\neval " + (array ? "ITEM(1)" : "ITEM") + ".N=7;\neval " + (array ? "ITEM(1)" : "ITEM") + ".TEXT='ABC';\n" + (array ? "eval ITEM(3).TEXT='END';\n" : "") + "callp ZERO(ITEM);\nreturn;\ndcl-proc ZERO;\ndcl-pi *n;\nP likeds(ITEM) " + (array ? "dim(2)" : "") + ";\nend-pi;\nclear P;\nreturn;\nend-proc;");
        Assert.Equal(0L, Read(context, array ? "ITEM(1).N" : "ITEM.N")); Assert.Equal("   ", Read(context, array ? "ITEM(1).TEXT" : "ITEM.TEXT"));
        if (array) Assert.Equal("END", Read(context, "ITEM(3).TEXT"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_returning_array_PR_PI_preserve_native_column_origins(int shift)
    {
        string P(bool begin) { var line = new string(' ', 80 + shift).ToCharArray(); line[5 + shift] = 'P'; "GET".CopyTo(0, line, 6 + shift, 3); line[23 + shift] = begin ? 'B' : 'E'; return new string(line) + "\n"; }
        var source = D("GET", "PR", length: "3", type: 'I', decimals: "0", keywords: "DIM(3) RTNPARM", shift: shift) + D("A", "S", length: "3", type: 'I', decimals: "0", keywords: "DIM(3)", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "A=GET()") + RpgFixedCalculationTests.C("RETURN") + P(true) + D("GET", "PI", length: "3", type: 'I', decimals: "0", keywords: "DIM(3) RTNPARM", shift: shift) + RpgFixedCalculationTests.C("RETURN", expression: "7") + P(false);
        Assert.Equal(new object?[] { 7L, 7L, 7L }, Read(Run(source), "A"));
    }

    [Theory]
    [InlineData("int(3)", "7", "0")]
    [InlineData("packed(5:1)", "12.3", "0")]
    [InlineData("float(8)", "1.5", "0")]
    [InlineData("char(2)", "'AB'", "'  '")]
    [InlineData("char(2) ccsid(*hex)", "X'FF00'", "X'4040'")]
    [InlineData("ind", "*on", "*off")]
    [InlineData("date", "'2026-10-18'", "'0001-01-01'")]
    [InlineData("time", "'12:34:56'", "%time('00:00:00')")]
    [InlineData("timestamp", "'2026-10-18T12:34:56.123456'", "%timestamp('0001-01-01T00:00:00.000000')")]
    public void Missing_returned_elements_receive_native_type_defaults(string type, string value, string expected)
    {
        var context = Run("**free\ndcl-s A " + type + " dim(3);\ndcl-s OK ind;\neval A=GET();\neval OK=A(3)=" + expected + ";\nreturn;\ndcl-proc GET;\ndcl-pi *n " + type + " dim(3) end-pi;\ndcl-s LOCAL " + type + " dim(1) inz(" + value + ");\nreturn LOCAL;\nend-proc;"); Assert.Equal(true, context.ReadValue("OK"));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 3)]
    public void Returned_arrays_work_as_array_expressions_and_RTNParm_reserves_its_own_slot(bool hidden, int expected)
    {
        var context = Run("**free\ndcl-s N int(3);\neval N=%xfoot(GET());\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(3) " + (hidden ? "rtnparm" : "") + " end-pi;\nreturn %parms();\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("N"));
    }

    [Fact]
    public void CLEAR_validates_the_outer_structure_receiver_before_publishing_any_bytes()
    {
        var program = RpgCompiler.Compile("QGPL", "CLEAR", "**free\n" + Template + "dcl-pi *n;\nP likeds(ITEM);\nend-pi;\nclear P;");
        var writes = 0; var image = new ProgramBuffer(new byte[] { 7, 0xC1, 0xC2, 0xC3 }, 37);
        var argument = new ProgramArgument(() => image, _ => writes++, _ => throw new CpfException("CPF3C3A", "Rejected image"), "*CHAR", 4, 0, () => image);
        Assert.Throws<CpfException>(() => new RpgInterpreter(new()).Run(program, new object?[] { argument }));
        Assert.Equal(0, writes); Assert.Equal(new byte[] { 7, 0xC1, 0xC2, 0xC3 }, image.ToArray());
    }

    [Fact]
    public void Cancellation_while_returning_leaves_the_caller_receiver_unchanged()
    {
        using var cancelled = new CancellationTokenSource(); RpgRuntimeContext? owner = null;
        var program = RpgCompiler.Compile("QGPL", "CANCEL", "**free\ndcl-pr SIGNAL extpgm('TARGET') end-pr;\ndcl-s A int(3) dim(3) inz(7);\neval A=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(3) end-pi;\ncallp SIGNAL();\nreturn 9;\nend-proc;");
        var host = new RpgHost { CancellationToken = cancelled.Token, TypedProgramCaller = (_, _, _, _) => { cancelled.Cancel(); return new() { Success = true }; } };
        owner = new RpgRuntimeContext(program, host);
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program, existingContext: owner));
        Assert.Equal(new object?[] { 7L, 7L, 7L }, Read(owner, "A"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_LIKEDS_return_headers_allow_keywords_before_the_type(int shift)
    {
        string P(bool begin) { var line = new string(' ', 80 + shift).ToCharArray(); line[5 + shift] = 'P'; "GET".CopyTo(0, line, 6 + shift, 3); line[23 + shift] = begin ? 'B' : 'E'; return new string(line) + "\n"; }
        var source = D("ITEM", "DS", keywords: "QUALIFIED", shift: shift) + D("TEXT", length: "4", shift: shift) + D("GET", "PR", keywords: "RTNPARM LIKEDS(ITEM)", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "ITEM=GET()") + RpgFixedCalculationTests.C("RETURN") + P(true) + D("GET", "PI", keywords: "RTNPARM LIKEDS(ITEM)", shift: shift) +
            RpgFixedCalculationTests.C("RETURN", expression: "'ABCD'") + P(false);
        Assert.Equal("ABCD", Read(Run(source), "ITEM.TEXT"));
    }

    [Fact]
    public void Explicit_free_EXTPROC_aliases_link_matching_LIKEDS_array_and_RTNParm_returns()
    {
        var context = Run("**free\n" + Template + "dcl-pr GET likeds(ITEM) dim(2) rtnparm extproc('BUILD') end-pr;\ndcl-ds RESULT likeds(ITEM) dim(2);\neval ITEM.N=7;\neval ITEM.TEXT='ABC';\neval RESULT=GET();\nreturn;\ndcl-proc BUILD;\ndcl-pi *n likeds(ITEM) dim(2) rtnparm end-pi;\nreturn ITEM;\nend-proc;"); Assert.Equal(7L, Read(context, "RESULT(2).N")); Assert.Equal("ABC", Read(context, "RESULT(1).TEXT"));
    }
}
