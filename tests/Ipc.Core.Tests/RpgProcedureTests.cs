using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Core.Work;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgProcedureTests
{
    private static RpgRuntimeContext Run(string source) => new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "PROC", source));

    [Fact]
    public void Local_functions_copy_VALUE_parameters_and_initialize_automatic_locals_for_each_call()
    {
        var context = Run("**free\ndcl-s N int(3) inz(7);\ndcl-s X int(10) inz(100);\ndcl-s A int(10);\ndcl-s B int(10);\neval A=DOUBLE(N);\neval B=DOUBLE(8);\nreturn;\n" +
            "dcl-proc DOUBLE;\ndcl-pi *n int(10);\nN int(3) value;\nend-pi;\ndcl-s X int(10) inz(2);\neval N=N+1;\nreturn N*X;\nend-proc;");
        Assert.Equal(16L, context.ReadValue("A")); Assert.Equal(18L, context.ReadValue("B")); Assert.Equal(7L, context.ReadValue("N")); Assert.Equal(100L, context.ReadValue("X"));
    }

    [Fact]
    public void Recursive_functions_have_distinct_VALUE_and_local_storage()
    {
        var context = Run("**free\ndcl-s N int(20);\neval N=FACT(7);\nreturn;\ndcl-proc FACT;\ndcl-pi *n int(20);\nN int(3) value;\nend-pi;\n" +
            "if N<=1;\nreturn 1;\nendif;\nreturn N*FACT(N-1);\nend-proc;"); Assert.Equal(5040L, context.ReadValue("N"));
    }

    [Fact]
    public void Reference_parameters_keep_aliases_while_VALUE_parameters_remain_private()
    {
        var context = Run("**free\ndcl-s N int(3) inz(7);\ncallp CHANGE(N:N:N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nA int(3);\nB int(3);\nCOPY int(3) value;\nend-pi;\n" +
            "eval A=A+1;\neval B=A+B;\neval COPY=100;\nreturn;\nend-proc;"); Assert.Equal(16L, context.ReadValue("N"));
    }

    [Fact]
    public void Static_local_values_persist_but_do_not_shadow_or_replace_module_globals()
    {
        var context = Run("**free\ndcl-s N int(10) inz(99);\ndcl-s A int(10);\ndcl-s B int(10);\neval A=NEXT();\neval B=NEXT();\nreturn;\n" +
            "dcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\ndcl-s N int(10) static inz(0);\neval N=N+1;\nreturn N;\nend-proc;");
        Assert.Equal(1L, context.ReadValue("A")); Assert.Equal(2L, context.ReadValue("B")); Assert.Equal(99L, context.ReadValue("N"));
    }

    [Fact]
    public void Callee_global_names_resolve_in_the_module_instead_of_in_the_callers_locals()
    {
        var context = Run("**free\ndcl-s X int(10) inz(1);\ndcl-s N int(10);\neval N=FIRST();\nreturn;\n" +
            "dcl-proc FIRST;\ndcl-pi *n int(10) end-pi;\ndcl-s X int(10) inz(20);\nreturn SECOND();\nend-proc;\n" +
            "dcl-proc SECOND;\ndcl-pi *n int(10) end-pi;\neval X=X+1;\nreturn X;\nend-proc;");
        Assert.Equal(2L, context.ReadValue("N")); Assert.Equal(2L, context.ReadValue("X"));
    }

    private static string P(string name, bool begin, int shift = 0)
    { var line = new string(' ', 80 + shift).ToCharArray(); line[5 + shift] = 'P'; name.CopyTo(0, line, 6 + shift, name.Length); line[23 + shift] = begin ? 'B' : 'E'; return new string(line) + "\n"; }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_P_specs_and_returning_PR_PI_support_both_native_column_origins(int shift)
    {
        var source = D("TRIPLE", "PR", length: "10", type: 'I', decimals: "0", shift: shift) + D("X", length: "3", type: 'I', decimals: "0", keywords: "VALUE", shift: shift) +
            D("N", "S", length: "10", type: 'I', decimals: "0", shift: shift) + RpgFixedCalculationTests.C("EVAL", expression: "N=TRIPLE(7)") + RpgFixedCalculationTests.C("RETURN") +
            P("TRIPLE", true, shift) + D("TRIPLE", "PI", length: "10", type: 'I', decimals: "0", shift: shift) + D("X", length: "3", type: 'I', decimals: "0", keywords: "VALUE", shift: shift) +
            RpgFixedCalculationTests.C("RETURN", expression: "X*3") + P("TRIPLE", false, shift);
        Assert.Equal(21L, Run(source).ReadValue("N"));
    }

    [Fact]
    public void Explicit_prototypes_bind_local_EXTPROC_aliases_and_allow_implicit_calls()
    {
        var context = Run("**free\ndcl-pr ALIAS int(10) extproc('ACTUAL');\nN int(3) value;\nend-pr;\ndcl-s RESULT int(10);\neval RESULT=ALIAS(4);\nTOUCH(RESULT);\nreturn;\n" +
            "dcl-proc ACTUAL export;\ndcl-pi ACTUAL int(10);\nN int(3) value;\nend-pi;\nreturn N*3;\nend-proc;\n" +
            "dcl-proc TOUCH;\ndcl-pi *n;\nN int(10);\nend-pi;\neval N=N+1;\nreturn;\nend-proc;"); Assert.Equal(13L, context.ReadValue("RESULT"));
    }

    [Theory]
    [InlineData("char(4)", "'AB'", "char(4)", "'AB  '")]
    [InlineData("packed(5:1)", "12.34", "packed(5:1)", "12.3")]
    [InlineData("zoned(3:1)", "-12.34", "zoned(3:1)", "-12.3")]
    [InlineData("int(3)", "12.9", "int(3)", "12")]
    [InlineData("float(4)", "1.5", "float(4)", "1.5")]
    [InlineData("ind", "*on", "ind", "*on")]
    [InlineData("date", "'2026-10-18'", "date", "'2026-10-18'")]
    [InlineData("time", "'12:34:56'", "time", "%time('12:34:56')")]
    [InlineData("timestamp", "'2026-10-18T12:34:56.123456'", "timestamp", "%timestamp('2026-10-18T12:34:56.123456')")]
    [InlineData("char(2) ccsid(*hex)", "X'FF00'", "char(2) ccsid(*hex)", "X'FF00'")]
    public void Scalar_return_values_use_the_native_codec(string returns, string value, string receiver, string expected)
    {
        var context = Run("**free\ndcl-s RESULT " + receiver + ";\ndcl-s OK ind;\neval RESULT=GET();\neval OK=RESULT=" + expected + ";\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n " + returns + " end-pi;\nreturn " + value + ";\nend-proc;"); Assert.Equal(true, context.ReadValue("OK"));
    }

    [Fact]
    public void VALUE_and_CONST_share_conversion_rules_but_only_VALUE_is_mutable_and_both_leave_the_actual_unchanged()
    {
        var context = Run("**free\ndcl-s N zoned(7:3) inz(42.125);\ndcl-s RESULT packed(5:1);\neval RESULT=CONVERT(N:N);\nreturn;\n" +
            "dcl-proc CONVERT;\ndcl-pi *n packed(5:1);\nCOPY packed(5:1) value;\nINPUT packed(5:1) const;\nend-pi;\neval COPY=100.5;\nreturn COPY+INPUT;\nend-proc;");
        Assert.Equal(142.6m, context.ReadValue("RESULT")); Assert.Equal(42.125m, context.ReadValue("N"));
    }

    [Fact]
    public void An_unpassed_caller_parameter_does_not_hide_a_converted_temporary_with_the_same_formal_name()
    {
        var source = "**free\ndcl-pi *n;\nN int(3) options(*nopass);\nend-pi;\ndcl-s RESULT int(3);\neval RESULT=GET(7);\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n int(3);\nN int(3) value;\nend-pi;\nreturn N;\nend-proc;";
        Assert.Equal(7L, Run(source).ReadValue("RESULT"));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(2, 3)]
    public void Local_interfaces_distinguish_unpassed_omitted_and_present_const_parameters(int state, int expected)
    {
        var actual = state == 0 ? "" : state == 1 ? "*omit" : "'DATA'";
        var context = Run("**free\ndcl-s RESULT int(3);\neval RESULT=GET(" + actual + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nP char(4) const options(*omit:*nopass);\nend-pi;\n" +
            "if %passed(P) and P='DATA';\nreturn 3;\nendif;\nif %omitted(P);\nreturn 2;\nendif;\nreturn 1;\nend-proc;"); Assert.Equal((long)expected, context.ReadValue("RESULT"));
    }

    [Fact]
    public void Local_parameter_counts_do_not_overwrite_the_main_PSDS_parameter_count()
    {
        var context = Run("**free\ndcl-ds PS qualified psds;\nCOUNT *parms;\nend-ds;\ndcl-s RESULT int(3);\neval RESULT=GET(7:8);\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n int(3);\nA int(3) value;\nB int(3) value;\nend-pi;\nreturn %parms()+PS.COUNT;\nend-proc;");
        Assert.Equal(2L, context.ReadValue("RESULT")); Assert.Equal(0, RpgExpressionParser.Parse("%parms()").Eval(context));
    }

    [Fact]
    public void Captured_array_actuals_keep_their_address_when_a_later_function_argument_changes_the_index()
    {
        var context = Run("**free\ndcl-s ARR int(3) dim(2) inz(0);\ndcl-s I int(3) inz(1);\ncallp SET(ARR(I):ADVANCE());\nreturn;\n" +
            "dcl-proc SET;\ndcl-pi *n;\nN int(3);\nCOPY int(3) value;\nend-pi;\neval N=9;\nreturn;\nend-proc;\n" +
            "dcl-proc ADVANCE;\ndcl-pi *n int(3) end-pi;\neval I=2;\nreturn I;\nend-proc;");
        Assert.Equal(9L, RpgExpressionParser.Parse("ARR(1)").Eval(context)); Assert.Equal(0L, RpgExpressionParser.Parse("ARR(2)").Eval(context)); Assert.Equal(2L, context.ReadValue("I"));
    }

    [Theory]
    [InlineData("const value")] [InlineData("value options(*omit)")] [InlineData("value value")] [InlineData("value(1)")]
    public void Invalid_VALUE_modes_reject_at_the_local_interface(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nN int(3) " + options + ";\nend-pi;\nreturn;\nend-proc;")); Assert.Equal(5, error.Location!.Line);
    }

    [Theory]
    [InlineData("dcl-pi *n int(3) end-pi;\nreturn;")]
    [InlineData("dcl-pi *n end-pi;\nreturn 1;")]
    public void Return_operands_match_the_interface(string body)
        => Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\n" + body + "\nend-proc;"));

    [Theory]
    [InlineData("GET()")]
    [InlineData("GET(1:2)")]
    [InlineData("GET('BAD')")]
    public void Procedure_expression_arguments_are_checked_against_the_bound_interface(string expression)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s RESULT int(3);\neval RESULT=" + expression + ";\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nN int(3);\nend-pi;\nreturn N;\nend-proc;"));

    [Fact]
    public void A_void_procedure_cannot_be_used_as_an_expression()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\nreturn;\nend-proc;"));

    [Fact]
    public void Callee_error_writes_survive_CALLP_E_and_argument_evaluation_keeps_its_original_status()
    {
        var context = Run("**free\ndcl-s N int(3) inz(7);\ndcl-s OK ind;\ncallp(e) BAD(N);\nif %error() and N=9 and %status()=202;\neval OK=*on;\nendif;\nreturn;\n" +
            "dcl-proc BAD;\ndcl-pi *n;\nN int(3);\nend-pi;\neval N=9;\neval N=1/0;\nreturn;\nend-proc;"); Assert.Equal(true, context.ReadValue("OK"));
    }

    [Fact]
    public void Procedure_recursion_hits_a_controlled_limit_instead_of_exhausting_the_host_stack()
    {
        var context = Run("**free\ncallp(e) FOREVER();\nreturn;\ndcl-proc FOREVER;\ncallp FOREVER();\nreturn;\nend-proc;");
        Assert.Equal(true, RpgExpressionParser.Parse("%error()").Eval(context)); Assert.Equal(202, RpgExpressionParser.Parse("%status()").Eval(context));
    }

    [Fact]
    public void Local_arrays_and_structures_initialize_for_each_invocation_and_can_shadow_global_structures()
    {
        var context = Run("**free\ndcl-ds STATE qualified inz;\nN int(3) inz(99);\nend-ds;\ndcl-s A int(3);\ndcl-s B int(3);\neval A=GET();\neval B=GET();\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-s ARR int(3) dim(2) inz(1);\ndcl-ds STATE qualified inz;\nN int(3) inz(2);\nend-ds;\neval STATE.N=STATE.N+ARR(1);\neval ARR(1)=10;\nreturn STATE.N;\nend-proc;");
        Assert.Equal(3L, context.ReadValue("A")); Assert.Equal(3L, context.ReadValue("B")); Assert.Equal(99L, RpgExpressionParser.Parse("STATE.N").Eval(context));
    }

    [Fact]
    public void A_native_procedure_PSSR_sees_module_feedback_and_returns_its_declared_value()
    {
        var context = Run("**free\ndcl-ds PS qualified psds;\nSTATUS *status;\nend-ds;\ndcl-s N int(10);\neval N=GET();\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n int(10) end-pi;\ndcl-s N int(3);\neval N=1/0;\nreturn 0;\nbegsr *pssr;\nreturn PS.STATUS;\nendsr;\nend-proc;");
        Assert.Equal(102L, context.ReadValue("N"));
    }

    [Fact]
    public void Procedure_actual_references_expire_after_return_and_VALUE_descriptors_are_mutable_without_aliasing()
    {
        var program = RpgCompiler.Compile("QGPL", "PROC", "**free\ndcl-s N int(3) inz(7);\ncallp GET(N:N);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nA int(3);\nB int(3) value;\nend-pi;\ncallp HOST(A:B);\nreturn;\nend-proc;");
        ProgramArgument? retained = null; var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var args = borrow!(); var a = (ProgramArgument)args[0]!; var b = (ProgramArgument)args[1]!; retained = b; Assert.False(b.IsReadOnly); Assert.False(ProgramArgument.Overlaps(a, b)); b.Value = 100L; a.Value = 8L; return new() { Success = true }; } }).Run(program);
        Assert.Equal(8L, context.ReadValue("N")); Assert.Throws<ObjectDisposedException>(() => retained!.Value);
    }

    [Fact]
    public void STATIC_storage_is_bounded_across_distinct_procedures_in_the_same_module()
    {
        var source = "**free\ndcl-s COUNT int(10) inz(0);\n" + string.Concat(Enumerable.Range(0, 17).Select(i => "callp(e) P" + i + "();\n")) + "return;\n" +
            string.Concat(Enumerable.Range(0, 17).Select(i => "dcl-proc P" + i + ";\ndcl-s STORAGE char(32767) dim(32) static;\neval COUNT=COUNT+1;\nreturn;\nend-proc;\n"));
        var context = Run(source); Assert.Equal(16L, context.ReadValue("COUNT")); Assert.Equal(true, RpgExpressionParser.Parse("%error()").Eval(context));
    }

    [Theory]
    [InlineData("dcl-f DATA disk usage(*input);")]
    [InlineData("dcl-ds PS qualified psds;\nSTATUS *status;\nend-ds;")]
    public void Unsupported_procedure_local_file_and_program_status_definitions_reject(string declaration)
        => Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\ndcl-proc GET;\n" + declaration + "\nreturn;\nend-proc;"));

    [Fact]
    public void Multiline_procedure_headers_and_endings_accept_comments_without_losing_source_lines()
    {
        var context = Run("**free\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET\nexport; // exported locally\ndcl-pi *n int(3) end-pi;\nreturn 7;\nend-proc\nGET; // end\n"); Assert.Equal(7L, context.ReadValue("N"));
    }

    [Theory]
    [InlineData("dcl-proc GET;\nreturn;\nend-proc OTHER;", 5)]
    [InlineData("dcl-proc GET;\ndcl-proc NESTED;\nend-proc;\nend-proc;", 4)]
    [InlineData("dcl-proc GET;\nreturn;", 3)]
    public void Malformed_procedure_boundaries_keep_the_original_source_location(string declaration, int line)
    {
        var error = Assert.Throws<RpgCompileException>(() => Run("**free\nreturn;\n" + declaration)); Assert.Equal(line, error.Location!.Line);
    }

    [Fact]
    public void EXTPROC_names_are_case_sensitive_and_do_not_silently_resolve_foreign_symbols()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr ALIAS int(3) extproc('get');\nend-pr;\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\nreturn 1;\nend-proc;"));

    [Fact]
    public void Native_P_spec_procedures_can_omit_an_empty_void_interface()
    {
        var source = D("N", "S", length: "3", type: 'I', decimals: "0") + RpgFixedCalculationTests.C("CALLP", expression: "GET()") + RpgFixedCalculationTests.C("RETURN") +
            P("GET", true) + RpgFixedCalculationTests.C("EVAL", expression: "N=7") + RpgFixedCalculationTests.C("RETURN") + P("GET", false);
        Assert.Equal(7L, Run(source).ReadValue("N"));
    }

    [Theory]
    [InlineData("int(3)", "int(10)", "value", "value")]
    [InlineData("int(3)", "int(3)", "value", "")]
    [InlineData("int(3)", "int(3)", "const", "value")]
    public void Explicit_local_prototypes_must_match_the_definition(string declared, string actual, string declaredMode, string actualMode)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr GET " + declared + ";\nN int(3) " + declaredMode + ";\nend-pr;\nreturn;\ndcl-proc GET;\ndcl-pi *n " + actual + ";\nN int(3) " + actualMode + ";\nend-pi;\nreturn N;\nend-proc;"));

    [Fact]
    public void Native_procedure_PSSR_does_not_accept_main_cycle_return_points()
    {
        Assert.Throws<RpgCompileException>(() => Run("**free\ncallp(e) BAD();\nreturn;\ndcl-proc BAD;\ndcl-s N int(3);\neval N=1/0;\nreturn;\nbegsr *pssr;\nendsr '*CANCL';\nend-proc;"));
    }

    [Fact]
    public void A_falling_through_local_PSSR_abnormally_ends_the_procedure_before_the_caller_E_handler()
    {
        var context = Run("**free\ndcl-s COUNT int(3);\ncallp(e) BAD();\nreturn;\ndcl-proc BAD;\ndcl-s N int(3);\neval N=1/0;\neval COUNT=99;\nreturn;\nbegsr *pssr;\neval COUNT=1;\nendsr;\nend-proc;");
        Assert.Equal(1L, context.ReadValue("COUNT")); Assert.Equal(true, RpgExpressionParser.Parse("%error()").Eval(context));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cancellation_during_a_native_procedure_is_not_consumed_by_E_and_expires_private_receivers(bool cpf)
    {
        var program = RpgCompiler.Compile("QGPL", "PROC", "**free\ncallp(e) GET(7);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nN int(3) value;\nend-pi;\ncallp(e) HOST(N);\nreturn;\nend-proc;");
        ProgramArgument? retained = null; var interpreter = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) => { retained = (ProgramArgument)borrow!()[0]!; if (cpf) throw new Ipc.Core.Messages.CpfException("CPF1241", "End job."); throw new OperationCanceledException(); } });
        if (cpf) Assert.Equal("CPF1241", Assert.Throws<Ipc.Core.Messages.CpfException>(() => interpreter.Run(program)).MessageId);
        else Assert.Throws<OperationCanceledException>(() => interpreter.Run(program));
        Assert.Throws<ObjectDisposedException>(() => retained!.Value);
    }

    [Fact]
    public void Procedure_occurrence_changes_share_the_module_global_structure_selection()
    {
        var context = Run("**free\ndcl-ds STATE qualified occurs(2) inz;\nN int(3);\nend-ds;\nCHANGE();\nreturn;\ndcl-proc CHANGE;\neval %occur(STATE)=2;\neval STATE.N=7;\nreturn;\nend-proc;");
        Assert.Equal(2, RpgExpressionParser.Parse("%occur(STATE)").Eval(context)); Assert.Equal(7L, RpgExpressionParser.Parse("STATE.N").Eval(context));
    }

    [Fact]
    public void Procedure_table_searches_preserve_global_compile_time_data_and_share_current_selection()
    {
        var context = Run("**free\ndcl-s TABKEY char(1) dim(*ctdata) ctdata ascend;\ndcl-s GOT ind;\neval GOT=FIND();\nreturn;\n" +
            "dcl-proc FIND;\ndcl-pi *n ind end-pi;\nreturn %tlookup('B':TABKEY);\nend-proc;\n**CTDATA TABKEY\nA\nB\n");
        Assert.Equal(true, context.ReadValue("GOT")); Assert.Equal("B", context.ReadValue("TABKEY"));
    }
}
