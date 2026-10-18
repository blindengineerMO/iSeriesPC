using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;

namespace Ipc.Core.Tests;

public sealed class RpgFixedCalculationTests
{
    internal static string C(string operation = "", string factor1 = "", string factor2 = "", string result = "", string length = "", string decimals = "",
        string hi = "", string lo = "", string eq = "", string condition = "", string control = "", string? expression = null, string comment = "")
    {
        var chars = Enumerable.Repeat(' ', 100).ToArray(); chars[5] = 'C';
        void Put(int column, string text, int maximum) { Assert.True(text.Length <= maximum, text); text.CopyTo(0, chars, column - 1, text.Length); }
        Put(7, control, 2); Put(9, condition.PadLeft(3), 3); Put(12, factor1, 14); Put(26, operation, 10);
        if (expression is not null) Put(36, expression, 45);
        else { Put(36, factor2, 14); Put(50, result, 14); Put(64, length.PadLeft(5), 5); Put(69, decimals.PadLeft(2), 2); Put(71, hi, 2); Put(73, lo, 2); Put(75, eq, 2); }
        Put(81, comment, 20); return new string(chars).TrimEnd() + "\n";
    }
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "NATCALC", source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static string Number(string name = "N", string initial = "0") => Fixed(name, "S", "5", 'P', "0", "INZ(" + initial + ")");

    [Theory]
    [InlineData("ADD", "5", "2", "7")]
    [InlineData("SUB", "5", "2", "3")]
    [InlineData("MULT", "5", "2", "10")]
    [InlineData("DIV", "5", "2", "2")]
    [InlineData("Z-ADD", "", "-7", "-7")]
    [InlineData("Z-SUB", "", "7", "-7")]
    public void Native_arithmetic_uses_full_factors_and_the_physical_result_field(string operation, string left, string right, string expected)
    {
        var context = Run(Number("LONGRESULTNAME") + C(operation, left, right, "LONGRESULTNAME") + C("RETURN"));
        Assert.Equal(expected, RpgValues.ToText(context.ReadValue("LONGRESULTNAME")));
    }
    [Theory]
    [InlineData("ADD", "12")]
    [InlineData("SUB", "8")]
    [InlineData("MULT", "20")]
    [InlineData("DIV", "5")]
    public void Blank_factor1_uses_the_existing_numeric_target(string operation, string expected)
    {
        var context = Run(Number(initial: "10") + C(operation, factor2: "2", result: "N")); Assert.Equal(expected, RpgValues.ToText(context.ReadValue("N")));
    }
    [Theory]
    [InlineData("2", true, false, false)]
    [InlineData("-2", false, true, false)]
    [InlineData("0", false, false, true)]
    [InlineData("0.1", false, false, true)]
    public void Arithmetic_flags_reflect_the_converted_result_and_clear_stale_states(string value, bool hi, bool lo, bool eq)
    {
        var context = Run(Number() + C("SETON", hi: "11", lo: "12", eq: "13") + C("Z-ADD", factor2: value, result: "N", hi: "11", lo: "12", eq: "13"));
        Assert.Equal(hi, context.Indicators[11]); Assert.Equal(lo, context.Indicators[12]); Assert.Equal(eq, context.Indicators[13]);
    }
    [Theory]
    [InlineData("1", "2", false, true, false)]
    [InlineData("2", "1", true, false, false)]
    [InlineData("2", "2", false, false, true)]
    public void COMP_uses_native_HI_LO_EQ_positions(string left, string right, bool hi, bool lo, bool eq)
    {
        var context = Run(C("SETON", hi: "11", lo: "12", eq: "13") + C("COMP", left, right, hi: "11", lo: "12", eq: "13"));
        Assert.Equal(hi, context.Indicators[11]); Assert.Equal(lo, context.Indicators[12]); Assert.Equal(eq, context.Indicators[13]);
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, true)]
    public void COMP_character_order_uses_job_bytes_and_blank_padding(int ccsid, bool higher)
    {
        var context = Run(Fixed("TEXT", "S", "2", 'A', keywords: "INZ('a')") + C("COMP", "TEXT", "'A'", hi: "11", lo: "12", eq: "13") +
            C("COMP", "TEXT", "'a'", eq: "14"), ccsid);
        Assert.Equal(higher, context.Indicators[11]); Assert.Equal(!higher, context.Indicators[12]); Assert.True(context.Indicators[14]);
    }
    [Fact]
    public void SETON_SETOFF_and_conditioning_use_the_result_columns_and_negation()
    {
        var context = Run(Number() + C("SETON", hi: "01", lo: "02", eq: "03") + C("SETOFF", lo: "02") +
            C("ADD", factor2: "1", result: "N", condition: "01") + C("ADD", factor2: "10", result: "N", condition: "02") +
            C("ADD", factor2: "100", result: "N", condition: "N02"));
        Assert.Equal(101m, context.ReadValue("N")); Assert.True(context.Indicators[1]); Assert.False(context.Indicators[2]); Assert.True(context.Indicators[3]);
    }
    [Fact]
    public void Extended_expressions_and_continuations_share_control_flow_and_locations()
    {
        var source = Number() + Number("I") + C("EVAL", expression: "N = 1 +") + C(expression: "2") + C("IF", expression: "N=3") +
            C("FOR", expression: "I=1 to 3") + C("EVAL", expression: "N=N+I") + C("ENDFOR") + C("ELSE") + C("EVAL", expression: "N=99") + C("ENDIF");
        Assert.Equal(9m, Run(source).ReadValue("N"));
    }
    [Fact]
    public void Long_extended_continuations_are_expression_text_in_length_and_indicator_columns()
    {
        const string value = "1234567890123456789012345678901234567890";
        var context = Run(Fixed("TEXT", "S", "40", 'A') + C("EVAL", expression: "TEXT =") + C(expression: "'" + value + "'"));
        Assert.Equal(value, context.ReadValue("TEXT"));
    }
    [Fact]
    public void Monitor_uses_the_ten_column_ON_ERROR_spelling_and_native_overflow()
    {
        var source = Fixed("BYTE", "S", "3", 'I', "0", "INZ(127)") + Number() + C("MONITOR") + C("ADD", factor2: "1", result: "BYTE") +
            C("ON-ERROR", expression: "103") + C("EVAL", expression: "N=%status()") + C("ENDMON");
        var context = Run(source); Assert.Equal(127L, context.ReadValue("BYTE")); Assert.Equal(103m, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("1", "3", "1", "3", "4")]
    [InlineData("4", "3", "1", "0", "4")]
    [InlineData("", "", "", "1", "2")]
    [InlineData("1", "5", "2", "3", "7")]
    public void Counted_DO_supports_defaults_pretest_and_ENDDO_increment(string start, string limit, string step, string iterations, string final)
    {
        var context = Run(Number() + Number("I") + C("DO", start, limit, "I") + C("ADD", factor2: "1", result: "N") + C("ENDDO", factor2: step));
        Assert.Equal(iterations, RpgValues.ToText(context.ReadValue("N"))); Assert.Equal(final, RpgValues.ToText(context.ReadValue("I")));
    }
    [Fact]
    public void DO_without_a_result_uses_an_unexported_counter()
    {
        var context = Run(Number() + C("DO", factor2: "3") + C("ADD", factor2: "1", result: "N") + C("ENDDO")); Assert.Equal(3m, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("3", "0", 1)] [InlineData("1", "3", 3)]
    public void DO_rechecks_a_limit_modified_in_the_body(string initial, string changed, int iterations)
    {
        var context = Run(Number() + Number("I") + Number("LIMIT", initial) + C("DO", "1", "LIMIT", "I") +
            C("ADD", factor2: "1", result: "N") + C("Z-ADD", factor2: changed, result: "LIMIT") + C("ENDDO"));
        Assert.Equal((decimal)iterations, context.ReadValue("N"));
    }
    [Fact]
    public void DO_initializes_the_counter_before_reading_an_aliased_limit()
    {
        var context = Run(Number() + Number("I") + C("DO", "1", "I", "I") + C("ADD", factor2: "1", result: "N") + C("LEAVE") + C("ENDDO"));
        Assert.Equal(1m, context.ReadValue("N")); Assert.Equal(1m, context.ReadValue("I"));
    }
    [Fact]
    public void ENDDO_rechecks_an_increment_modified_in_the_body()
    {
        var context = Run(Number() + Number("I") + Number("STEP", "1") + C("DO", "1", "4", "I") +
            C("ADD", factor2: "1", result: "N") + C("ADD", factor2: "1", result: "STEP") + C("ENDDO", factor2: "STEP"));
        Assert.Equal(2m, context.ReadValue("N")); Assert.Equal(6m, context.ReadValue("I"));
    }
    [Fact]
    public void Native_DO_keeps_its_limit_rules_when_the_body_and_ending_are_free_form()
    {
        var context = Run(Number() + Number("I") + Number("LIMIT", "3") + C("DO", "1", "LIMIT", "I") +
            "/free\neval N=N+1;\neval LIMIT=0;\nenddo;\n/end-free\n" + C("RETURN"));
        Assert.Equal(1m, context.ReadValue("N")); Assert.Equal(2m, context.ReadValue("I"));
    }
    [Theory]
    [InlineData("DO", "", "3", "I")]
    [InlineData("IF", "", "", "")]
    public void Conditioned_openings_skip_the_complete_group_when_the_indicator_is_off(string operation, string left, string right, string target)
    {
        var source = Number() + Number("I") + (operation == "IF" ? C("IF", condition: "01", expression: "1=1") : C(operation, left, right, target, condition: "01")) +
            C("ADD", factor2: "1", result: "N") + C(operation == "IF" ? "ENDIF" : "ENDDO");
        Assert.Equal(0m, Run(source).ReadValue("N"));
    }
    [Fact]
    public void Native_array_arithmetic_SORTA_and_XFOOT_reuse_typed_storage()
    {
        var context = Run(Fixed("ARR", "S", "3", 'P', "0", "DIM(3) CTDATA") + Number("TOTAL") +
            C("ADD", factor2: "1", result: "ARR") + C("SORTA(D)", expression: "ARR") + C("XFOOT", factor2: "ARR", result: "TOTAL", hi: "11", lo: "12", eq: "13") +
            "**CTDATA ARR\n001\n003\n002");
        Assert.Equal(new object?[] { 4m, 3m, 2m }, Assert.IsType<object?[]>(context.ReadValue("ARR"))); Assert.Equal(9m, context.ReadValue("TOTAL")); Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void Inline_numeric_and_character_layouts_are_available_before_execution()
    {
        var context = Run(C("Z-ADD", factor2: "1.25", result: "VALUE", length: "5", decimals: "2") + C("CLEAR", result: "TEXT", length: "8"));
        Assert.Equal(1.25m, context.ReadValue("VALUE")); Assert.Equal("        ", context.ReadValue("TEXT")); Assert.True(context.Program.FindField("VALUE")!.NativeScalar);
    }
    [Fact]
    public void Subroutines_SR_markers_TAG_and_GOTO_use_their_native_factor_slots()
    {
        var context = Run(Number() + C("EXSR", factor2: "WORK") + C("RETURN") + C("BEGSR", factor1: "WORK") +
            C("GOTO", factor2: "DONE", control: "SR") + C("Z-ADD", factor2: "99", result: "N", control: "SR") +
            C("TAG", factor1: "DONE", control: "SR") + C("ADD", factor2: "1", result: "N", control: "SR") + C("ENDSR", control: "SR"));
        Assert.Equal(1m, context.ReadValue("N"));
    }
    [Fact]
    public void CALL_uses_the_evaluated_factor2_and_a_named_PLIST_result()
    {
        var source = Fixed("TARGET", "S", "10", 'A', keywords: "INZ('CHANGE')") + Number() + C("CALL", factor2: "TARGET", result: "P") + C("RETURN") +
            C("PLIST", factor1: "P") + C("PARM", result: "N");
        var context = new RpgInterpreter(new() { ProgramCaller = (_, name, args) =>
        { Assert.Equal("CHANGE", name); Assert.Equal(0m, args.Single()); return new() { Success = true, UpdatedParameters = [7m] }; } }).Run(Compile(source));
        Assert.Equal(7m, context.ReadValue("N"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_CALLP_and_CALL_E_report_failure_without_entering_MONITOR(bool callp)
    {
        var op = callp ? C("CALLP(E)", expression: "MISSING()") : C("CALL(E)", factor2: "'MISSING'");
        var context = new RpgInterpreter(new() { ProgramCaller = (_, _, _) => new() { Success = false, Message = "missing" } }).Run(Compile(Number() + op + C("EVAL", expression: "N=%status()")));
        Assert.Equal(202m, context.ReadValue("N")); Assert.True(Assert.IsType<bool>(context.EvalBuiltin("ERROR", [])));
    }
    [Fact]
    public void Inline_CALL_parameters_preserve_order_and_copy_back_host_updates()
    {
        var source = Number("A", "2") + Number("B", "3") + C("CALL", factor2: "'CHANGE'") + C("PARM", result: "B") + C("PARM", result: "A");
        var context = new RpgInterpreter(new() { ProgramCaller = (_, _, args) =>
        { Assert.Equal(new object?[] { 3m, 2m }, args); return new() { Success = true, UpdatedParameters = [7m, 8m] }; } }).Run(Compile(source));
        Assert.Equal(8m, context.ReadValue("A")); Assert.Equal(7m, context.ReadValue("B"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_CALL_borrows_ordered_aliases_from_inline_or_subroutine_parameter_lists(bool named)
    {
        var parameters = C("PARM", result: "N") + C("PARM", result: "N");
        var source = Number(initial: "2") + C("CALL", factor2: "'CHANGE'", result: named ? "P" : "") + (named ? "" : parameters) + C("RETURN") +
            (named ? C("BEGSR", factor1: "DECLARATIONS") + C("PLIST", factor1: "P") + parameters + C("ENDSR") : "");
        ProgramArgument? saved = null;
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!().Select(Assert.IsType<ProgramArgument>).ToArray(); Assert.Equal(2, args.Length);
            Assert.Equal("00002C", Convert.ToHexString(args[0].ToBuffer()!.ToArray()));
            args[0].Value = 7m; Assert.Equal(7m, args[1].Value);
            args[1].Value = 8m; saved = args[0]; return new() { Success = true };
        } }).Run(Compile(source));
        Assert.Equal(8m, context.ReadValue("N")); Assert.Throws<ObjectDisposedException>(() => saved!.Value);
    }
    [Fact]
    public void Entry_parameter_declarations_inside_unexecuted_subroutines_bind_before_main()
    {
        var program = Compile(Number() + C("ADD", factor2: "1", result: "N") + C("RETURN") + C("BEGSR", factor1: "DECLARATIONS") +
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "N") + C("ENDSR"));
        Assert.Equal(new[] { "N" }, RpgInterpreter.EntryParameterNames(program));
        object? value = 3m; var arg = new ProgramArgument(() => value, v => value = v, v => v, "*DEC", 5);
        var context = new RpgInterpreter(new()).Run(program, [arg]); Assert.Equal(4m, value); Assert.Equal(4m, context.ReadValue("N"));
    }
    [Fact]
    public void CALL_LO_error_indicator_is_reset_on_a_later_success()
    {
        var source = C("CALL", factor2: "'FAIL'", lo: "11") + C("CALL", factor2: "'GOOD'", lo: "11");
        var context = new RpgInterpreter(new() { ProgramCaller = (_, name, _) => new() { Success = name == "GOOD" } }).Run(Compile(source)); Assert.False(context.Indicators[11]);
    }
    [Theory]
    [InlineData("CALL(E)")] [InlineData("CALLP(E)")] [InlineData("CALL")]
    public void Native_call_error_handling_precedes_MONITOR_for_CPF_service_exceptions(string operation)
    {
        var call = operation.StartsWith("CALLP", StringComparison.Ordinal) ? C(operation, expression: "MISSING()") : C(operation, factor2: "'MISSING'", lo: operation == "CALL" ? "11" : "");
        var source = Number() + Number("STATUS") + C("MONITOR") + call + C("ON-ERROR", expression: "202") + C("Z-ADD", factor2: "99", result: "N") +
            C("ENDMON") + C("EVAL", expression: "STATUS=%status()");
        var context = new RpgInterpreter(new() { ProgramCaller = (_, _, _) => throw new CpfException("CPF9801", "Missing program.") }).Run(Compile(source));
        Assert.Equal(0m, context.ReadValue("N")); Assert.Equal(202m, context.ReadValue("STATUS"));
        if (operation == "CALL") Assert.True(context.Indicators[11]); else Assert.True(Assert.IsType<bool>(context.EvalBuiltin("ERROR", [])));
    }
    [Fact]
    public void Native_ENTRY_PARM_fields_borrow_caller_cells_and_release_on_RETURN()
    {
        var source = Number() + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "N") + C("ADD", factor2: "1", result: "N") + C("RETURN");
        object? value = 3m; var arg = new ProgramArgument(() => value, v => value = v, v => v, "*DEC", 5);
        var context = new RpgInterpreter(new()).Run(Compile(source), [arg]); Assert.Equal(4m, value); value = 9m; Assert.Equal(4m, context.ReadValue("N"));
    }
    [Fact]
    public void Commentary_and_fixed_comments_cannot_hide_data_markers_or_change_literals()
    {
        var context = Run(Fixed("ARR", "S", "1", 'A', keywords: "DIM(*CTDATA)") + Number() + C("Z-ADD", factor2: "1", result: "N", comment: "ignored ' //") +
            "     C* ignored ' /COPY BAD\n" + C("RETURN") + "**CTDATA ARR\nX\n");
        Assert.Equal(1m, context.ReadValue("N")); Assert.Equal(1, context.EvalBuiltin("ELEM", [new RpgFieldRef("ARR")]));
    }
    [Fact]
    public void Include_diagnostics_retain_the_bad_native_record_origin()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NATCALC", new SourceDocument("ROOT", "/copy BAD"), (_, _) => new("BAD", C("BOGUS"))));
        Assert.Equal("BAD", error.Location!.Source); Assert.Equal(1, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Theory]
    [InlineData("MOVE")] [InlineData("MVR")] [InlineData("BITON")] [InlineData("TEST")] [InlineData("CALLB")]
    [InlineData("ADD(H)")] [InlineData("EVAL(H)")] [InlineData("BOGUS")]
    public void Unsupported_native_operations_and_extenders_reject_before_execution(string op)
    {
        Assert.Throws<RpgCompileException>(() => Compile(C(op)));
    }
    [Theory]
    [InlineData("L0")] [InlineData("L1")] [InlineData("LR")] [InlineData("AN")] [InlineData("OR")] [InlineData("SR")]
    public void Unsupported_cycle_levels_indicator_groups_and_unscoped_SR_reject(string control)
    {
        Assert.Throws<RpgCompileException>(() => Compile(C("RETURN", control: control)));
    }
    [Theory]
    [InlineData("00")] [InlineData("N00")] [InlineData("X01")] [InlineData("LR")] [InlineData("KA")]
    public void Unsupported_condition_codes_reject(string condition)
    {
        Assert.Throws<RpgCompileException>(() => Compile(C("RETURN", condition: condition)));
    }
    [Theory]
    [InlineData("PLIST")] [InlineData("PARM")]
    public void Malformed_parameter_declarations_reject(string op)
    {
        Assert.Throws<RpgCompileException>(() => Compile(C(op)));
    }
    [Fact]
    public void Entry_lists_reject_duplicate_fields_arrays_and_missing_named_call_lists()
    {
        foreach (var suffix in new[] { C("PLIST", factor1: "*ENTRY") + C("PARM", result: "N") + C("PARM", result: "N"),
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "ARR"), C("CALL", factor2: "'NAME'", result: "MISSING") })
            Assert.Throws<RpgCompileException>(() => Compile(Number() + Fixed("ARR", "S", "1", 'A', keywords: "DIM(1)") + suffix));
    }
    [Fact]
    public void Result_indicators_on_whole_arrays_and_non_numeric_math_operands_reject()
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fixed("ARR", "S", "3", 'I', "0", "DIM(2)") + C("ADD", factor2: "1", result: "ARR", hi: "11")));
        Assert.Throws<RpgCompileException>(() => Compile(Fixed("TEXT", "S", "2", 'A') + C("ADD", factor2: "1", result: "TEXT")));
    }
    [Fact]
    public void Fixed_calculations_cannot_be_hidden_inside_free_regions_or_precede_later_declarations()
    {
        foreach (var source in new[] { "**free\n" + C("RETURN"), "/free\n" + C("RETURN") + "/end-free", C("RETURN") + Number() })
            Assert.Throws<RpgCompileException>(() => Compile(source));
    }
    [Fact]
    public void Arithmetic_captures_an_indexed_result_once_before_the_write_changes_its_index()
    {
        var program = Compile(Fixed("ARR", "S", "3", 'P', "0", "DIM(2) INZ(1)") + Fixed("I", "S", "3", 'I', "0") +
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "I") + C("Z-SUB", factor2: "1", result: "ARR(I)", hi: "11", lo: "12", eq: "13"));
        var context = new RpgRuntimeContext(program, new());
        var index = new ProgramArgument(() => context.ReadArrayValue("ARR", 1), value => context.WriteArrayValue("ARR", 1, value), value => value, "*INT", 1);
        new RpgInterpreter(new()).Run(program, [index], context);
        Assert.Equal(-1m, context.ReadArrayValue("ARR", 1)); Assert.Equal(1m, context.ReadArrayValue("ARR", 2)); Assert.True(context.Indicators[12]);
    }
    [Fact]
    public void Native_parameter_lists_enforce_the_argument_bound_even_for_repeated_fields()
    {
        var declarations = Number() + C("PLIST", factor1: "P");
        Compile(declarations + string.Concat(Enumerable.Repeat(C("PARM", result: "N"), 255)));
        Assert.Throws<RpgCompileException>(() => Compile(declarations + string.Concat(Enumerable.Repeat(C("PARM", result: "N"), 256))));
        Assert.Throws<RpgCompileException>(() => Compile(Number() + C("CALL", factor2: "'TARGET'") + string.Concat(Enumerable.Repeat(C("PARM", result: "N"), 256))));
    }
    [Fact]
    public void Native_reserved_columns_and_non_left_justified_operations_reject()
    {
        foreach (var column in new[] { 77, 78, 79, 80 })
        {
            var chars = C("ADD", factor2: "1", result: "N").TrimEnd('\n').PadRight(80).ToCharArray(); chars[column - 1] = 'X';
            Assert.Throws<RpgCompileException>(() => Compile(Number() + new string(chars) + "\n"));
        }
        Assert.Throws<RpgCompileException>(() => Compile(C(" RETURN")));
    }
    [Fact]
    public void Native_loop_endings_and_unavailable_operand_forms_reject()
    {
        foreach (var source in new[] { Number("I") + C("FOR", expression: "I=1 to 3") + C("ENDDO"), C("DO") + C("ENDFOR"),
            C("DOW", expression: "1=0") + C("ENDDO", factor2: "2"), C("RETURN", expression: "1"), Number() + C("Z-ADD", "1", "2", "N"),
            Number() + C("PARM", "N", result: "N"), Number() + C("Z-ADD", factor2: "1", result: "N", length: "6", decimals: "0") })
            Assert.Throws<RpgCompileException>(() => Compile(source));
    }
    [Fact]
    public void Empty_native_expressions_orphan_handlers_and_unused_parameter_fields_reject()
    {
        foreach (var source in new[] { C("EVAL"), C("CALLP"), C("ON-ERROR"), C("COMP", "1", "2"), Number() + C("PARM", result: "N"),
            Number() + C("CALL", factor2: "'TARGET'", result: "P") + C("PARM", result: "N") + C("PLIST", factor1: "P") + C("PARM", result: "N") })
            Assert.Throws<RpgCompileException>(() => Compile(source));
    }
}
