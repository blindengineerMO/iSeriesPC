using System.Globalization;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed class RpgFixedLookupTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "LOOKTEST", source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Fields(string sequence = "", string index = "1") =>
        Fixed("ARR", "S", "3", 'P', "0", "DIM(7) " + sequence) + Fixed("I", "S", "5", 'P', "0", "INZ(" + index + ")");
    private static string Fill(bool descending = false)
    {
        var values = new[] { 1, 3, 3, 3, 5, 5, 7 }; if (descending) Array.Reverse(values);
        return string.Concat(values.Select((value, index) => C("Z-ADD", factor2: value.ToString(CultureInfo.InvariantCulture), result: "ARR(" + (index + 1) + ")")));
    }
    [Theory]
    [InlineData("", "3", "1", 2, true)]
    [InlineData("", "3", "3", 3, true)]
    [InlineData("", "9", "4", 1, false)]
    [InlineData("ASCEND", "3", "1", 2, true)]
    [InlineData("DESCEND", "3", "1", 4, true)]
    public void Exact_lookup_updates_the_starting_index_and_resets_flags(string sequence, string key, string start, int expected, bool found)
    {
        var context = Run(Fields(sequence, start) + Fill(sequence == "DESCEND") + C("SETON", eq: "11") +
            C("LOOKUP", key, "ARR(I)", eq: "11"));
        Assert.Equal((decimal)expected, context.ReadValue("I")); Assert.Equal(found, context.Indicators[11]);
        Assert.Equal(found, Read(context, "%found")); Assert.Equal(found, Read(context, "%equal()"));
    }
    [Theory]
    [InlineData(false, "GT", "3", 5)] [InlineData(false, "LT", "5", 4)]
    [InlineData(false, "GE", "3", 2)] [InlineData(false, "LE", "3", 2)]
    [InlineData(false, "GE", "4", 5)] [InlineData(false, "LE", "4", 4)]
    [InlineData(true, "GT", "3", 3)] [InlineData(true, "LT", "5", 4)]
    [InlineData(true, "GE", "3", 4)] [InlineData(true, "LE", "3", 4)]
    [InlineData(true, "GE", "4", 3)] [InlineData(true, "LE", "4", 4)]
    [InlineData(false, "GT", "9", 1)] [InlineData(true, "LT", "0", 1)]
    public void Sequenced_lookup_selects_nearest_values_and_duplicate_boundaries(bool descending, string operation, string key, int expected)
    {
        var context = Run(Fields(descending ? "DESCEND" : "ASCEND") + Fill(descending) + C("SETON", hi: "11", lo: "12", eq: "13") +
            C("LOOKUP", key, "ARR(I)", hi: operation.StartsWith('G') ? "11" : "", lo: operation.StartsWith('L') ? "12" : "", eq: operation.EndsWith('E') ? "13" : ""));
        Assert.Equal((decimal)expected, context.ReadValue("I"));
        var found = key is not ("9" or "0"); var equal = operation.EndsWith('E') && key == "3";
        Assert.Equal(found, Read(context, "%found()")); Assert.Equal(equal, Read(context, "%equal"));
        if (operation.StartsWith('G')) Assert.Equal(found && !equal, context.Indicators[11]);
        if (operation.StartsWith('L')) Assert.Equal(found && !equal, context.Indicators[12]);
        if (operation.EndsWith('E')) Assert.Equal(equal, context.Indicators[13]);
    }
    [Theory]
    [InlineData(false, "6", "4", "GT", 6)]
    [InlineData(true, "5", "4", "GT", 1)]
    [InlineData(true, "5", "4", "LT", 5)]
    public void Relative_lookup_excludes_elements_before_the_starting_index(bool descending, string start, string key, string operation, int expected)
    {
        var context = Run(Fields(descending ? "DESCEND" : "ASCEND", start) + Fill(descending) +
            C("LOOKUP", key, "ARR(I)", hi: operation == "GT" ? "11" : "", lo: operation == "LT" ? "12" : ""));
        Assert.Equal((decimal)expected, context.ReadValue("I"));
    }
    [Theory]
    [InlineData("3")] [InlineData("4")] [InlineData("9")]
    public void Shared_HI_EQ_indicators_combine_the_two_search_outcomes(string key)
    {
        var context = Run(Fields("ASCEND") + Fill() + C("LOOKUP", key, "ARR(I)", hi: "11", eq: "11"));
        Assert.Equal(key != "9", context.Indicators[11]); Assert.Equal(key != "9", Read(context, "%found")); Assert.Equal(key == "3", Read(context, "%equal"));
    }
    [Theory]
    [InlineData("ARR")] [InlineData("ARR(2)")] [InlineData("ARR(START)")]
    public void Unindexed_and_constant_index_searches_do_not_modify_a_counter(string target)
    {
        var context = Run(Fields() + Fixed("START", "C", keywords: "2") + Fill() + C("LOOKUP", "3", target, eq: "11"));
        Assert.True(context.Indicators[11]); Assert.Equal(1m, context.ReadValue("I")); Assert.Equal(2m, context.ReadValue("START"));
    }
    [Theory]
    [InlineData("0")] [InlineData("8")] [InlineData("-1")]
    public void Invalid_starting_indexes_report_121_and_preserve_flags_and_index(string start)
    {
        var program = Compile(Fields(index: start) + Fill() + C("SETON", eq: "11") + C("LOOKUP", "3", "ARR(I)", eq: "11"));
        var context = new RpgRuntimeContext(program, new());
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(program, existingContext: context));
        Assert.Equal(121, error.StatusCode); Assert.True(context.Indicators[11]); Assert.Equal(decimal.Parse(start, CultureInfo.InvariantCulture), context.ReadValue("I"));
    }
    [Theory]
    [InlineData('P', 'P')] [InlineData('P', 'S')] [InlineData('S', 'P')]
    public void Non_float_numeric_lookup_ignores_declared_decimal_positions(char arrayType, char searchType)
    {
        var context = Run(Fixed("ARR", "S", "3", arrayType, "2", "DIM(2)") + Fixed("KEY", "S", "3", searchType, "1", "INZ(12.3)") +
            Fixed("I", "S", "3", 'P', "0", "INZ(1)") + C("Z-ADD", factor2: "1.23", result: "ARR(2)") + C("LOOKUP", "KEY", "ARR(I)", eq: "11"));
        Assert.Equal(2m, context.ReadValue("I")); Assert.True(context.Indicators[11]);
        Assert.Equal(0, Read(context, "%lookup(KEY:ARR)"));
    }
    [Theory]
    [InlineData("1.23")] [InlineData("123")] [InlineData("KEY")]
    public void Numeric_literals_and_constants_keep_their_coefficient(string key)
    {
        var context = Run(Fixed("ARR", "S", "3", 'P', "1", "DIM(1) INZ(12.3)") + Fixed("KEY", "C", keywords: "1.23") + C("LOOKUP", key, "ARR", eq: "11"));
        Assert.True(context.Indicators[11]);
    }
    [Theory]
    [InlineData("")] [InlineData("-")]
    public void Decimal_coefficient_comparison_preserves_all_29_digits_without_multiplication_overflow(string sign)
    {
        var context = Run("       dcl-s ARR packed(29:28) dim(1)\n         inz(" + sign + "7.9228162514264337593543950335);\n" +
            "       dcl-s KEY packed(29:0)\n         inz(" + sign + "79228162514264337593543950335);\n" + C("LOOKUP", "KEY", "ARR", eq: "11"));
        Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void Integer_and_packed_search_fields_share_numeric_digit_comparison()
    {
        var context = Run(Fixed("ARR", "S", "3", 'P', "1", "DIM(1) INZ(1.2)") + Fixed("KEY", "S", "3", 'I', "0", "INZ(12)") + C("LOOKUP", "KEY", "ARR", eq: "11"));
        Assert.True(context.Indicators[11]);
    }
    [Theory]
    [InlineData(4)] [InlineData(8)]
    public void Float_lookup_aligns_decimal_values(int width)
    {
        var context = Run(Fixed("ARR", "S", width.ToString(CultureInfo.InvariantCulture), 'F', keywords: "DIM(1) INZ(1.25)") +
            Fixed("KEY", "S", width.ToString(CultureInfo.InvariantCulture), 'F', keywords: "INZ(1.25)") + C("LOOKUP", "KEY", "ARR", eq: "11"));
        Assert.True(context.Indicators[11]);
    }
    [Theory]
    [InlineData(37, "'a'", 1)] [InlineData(1208, "'a'", 2)]
    public void Character_relative_lookup_uses_unsigned_job_bytes(int ccsid, string key, int expected)
    {
        var context = Run(Fixed("ARR", "S", "1", 'A', keywords: "DIM(2) ASCEND") + Fixed("I", "S", "3", 'P', "0", "INZ(1)") +
            C("EVAL", expression: "ARR(1)='A'") + C("EVAL", expression: "ARR(2)='z'") + C("SORTA", expression: "ARR") + C("LOOKUP", key, "ARR(I)", hi: "11"), ccsid);
        Assert.Equal((decimal)expected, context.ReadValue("I")); Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void Character_lookup_compares_opaque_bytes_without_decoding_and_pads_literals()
    {
        var context = Run(Fixed("ARR", "S", "2", 'A', keywords: "DIM(2)") + C("EVAL", expression: "ARR(1)=X'FF00'") +
            C("EVAL", expression: "ARR(2)='A'") + C("LOOKUP", "X'FF00'", "ARR", eq: "11") + C("LOOKUP", "'A'", "ARR", eq: "12"), 1208);
        Assert.True(context.Indicators[11]); Assert.True(context.Indicators[12]);
    }
    [Theory]
    [InlineData("*HIVAL", "X'FFFF'")] [InlineData("*LOVAL", "X'0000'")] [InlineData("*BLANKS", "'  '")] [InlineData("*ZEROS", "'00'")]
    public void Character_figurative_constants_fill_the_element_width(string key, string value)
    {
        Assert.True(Run(Fixed("ARR", "S", "2", 'A', keywords: "DIM(1)") + C("EVAL", expression: "ARR(1)=" + value) + C("LOOKUP", key, "ARR", eq: "11")).Indicators[11]);
    }
    [Theory]
    [InlineData("*HIVAL", "999")] [InlineData("*LOVAL", "-999")] [InlineData("*ZEROS", "0")]
    public void Numeric_figurative_constants_use_the_array_precision(string key, string value)
    {
        Assert.True(Run(Fixed("ARR", "S", "3", 'P', "0", "DIM(1) INZ(" + value + ")") + C("LOOKUP", key, "ARR", eq: "11")).Indicators[11]);
    }
    [Theory]
    [InlineData("*HIVAL", "127")] [InlineData("*LOVAL", "-128")]
    public void Integer_figurative_constants_use_storage_bounds(string key, string value)
    {
        Assert.True(Run(Fixed("ARR", "S", "3", 'I', "0", "DIM(1) INZ(" + value + ")") + C("LOOKUP", key, "ARR", eq: "11")).Indicators[11]);
    }
    [Fact]
    public void Qualified_DS_array_and_index_subfields_use_captured_native_views()
    {
        var source = "       dcl-ds DS qualified;\n       ARR packed(3:0) dim(3) inz(3);\n       I int(5) inz(2);\n       end-ds;\n" +
            C("LOOKUP", "3", "DS.ARR(DS.I)", eq: "11");
        var context = Run(source); Assert.Equal(2L, Read(context, "DS.I")); Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void A_false_condition_leaves_index_indicators_and_lookup_state_unchanged()
    {
        var context = Run(Fields() + Fill() + C("LOOKUP", "3", "ARR(I)", eq: "11") + C("LOOKUP", "9", "ARR(I)", eq: "11", condition: "12"));
        Assert.Equal(2m, context.ReadValue("I")); Assert.True(context.Indicators[11]); Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Fact]
    public void Relative_only_lookup_clears_global_EQUAL_without_changing_an_unspecified_indicator()
    {
        var context = Run(Fields("ASCEND") + Fill() + C("LOOKUP", "3", "ARR", eq: "11") + C("LOOKUP", "3", "ARR", hi: "12"));
        Assert.True(context.Indicators[11]); Assert.True(context.Indicators[12]); Assert.Equal(true, Read(context, "%found")); Assert.Equal(false, Read(context, "%equal"));
    }
    [Theory]
    [InlineData("")] [InlineData("ASCEND")] [InlineData("DESCEND")]
    public void LOOKUPNE_is_sequential_and_respects_start_count_without_changing_status(string sequence)
    {
        var context = Run(Fields(sequence) + Fill(sequence == "DESCEND") + C("LOOKUP", "3", "ARR", eq: "11"));
        Assert.Equal(sequence == "DESCEND" ? 2 : 5, Read(context, "%lookupne(3:ARR:2:5)"));
        Assert.Equal(0, Read(context, sequence == "DESCEND" ? "%lookupne(3:ARR:4:3)" : "%lookupne(3:ARR:2:3)"));
        Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Fact]
    public void LOOKUPNE_searches_keyed_DS_arrays_with_scalar_and_indexed_array_keys()
    {
        var context = Run("**free\ndcl-ds DS qualified dim(3);\nK char(1) inz('A');\nV int(5) dim(2) inz(1);\nend-ds;\n" +
            "eval DS(3).K='B';\neval DS(2).V(2)=2;\n");
        Assert.Equal(3, Read(context, "%lookupne('A':DS(*).K)")); Assert.Equal(2, Read(context, "%lookupne(1:DS(*).V(2))"));
        Assert.Equal(0, Read(context, "%lookupne('A':DS(*).K:1:2)"));
    }
    [Theory]
    [InlineData("%lookupne(3)", 105)] [InlineData("%lookupne(3:ARR:1:2:3)", 105)]
    [InlineData("%lookupne(3:ARR:0)", 121)] [InlineData("%lookupne(3:ARR:1:0)", 121)]
    [InlineData("%lookupne(3:ARR:7:2)", 121)] [InlineData("%lookupne(3:ARR:1.0)", 105)]
    public void LOOKUPNE_enforces_argument_and_range_rules(string expression, int status)
    {
        var context = Run(Fields() + Fill()); var error = Assert.Throws<RpgRuntimeException>(() => Read(context, expression)); Assert.Equal(status, error.StatusCode);
    }
    [Fact]
    public void Lookup_operand_diagnostics_retain_include_origins()
    {
        var source = new SourceDocument("ROOT", Fields() + "/copy BAD\n");
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "LOOKTEST", source,
            (_, _) => new SourceDocument("BAD", C("LOOKUP", "'A'", "ARR", eq: "11"))));
        Assert.Equal("BAD", error.Location!.Source); Assert.Equal(1, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Fact]
    public void Lookup_index_conversion_failure_preserves_index_flags_and_global_search_state()
    {
        var program = Compile(Fixed("ARR", "S", "3", 'P', "0", "DIM(130) INZ(0)") + Fixed("I", "S", "3", 'I', "0", "INZ(1)") +
            C("Z-ADD", factor2: "7", result: "ARR(130)") + C("SETON", eq: "11") + C("LOOKUP", "7", "ARR(I)", eq: "11"));
        var context = new RpgRuntimeContext(program, new());
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(program, existingContext: context));
        Assert.Equal(103, error.StatusCode); Assert.Equal(1L, context.ReadValue("I")); Assert.True(context.Indicators[11]);
        Assert.Equal(false, Read(context, "%found")); Assert.Equal(false, Read(context, "%equal"));
    }
    [Fact]
    public void An_indexed_index_receiver_is_resolved_once_before_the_search()
    {
        var program = Compile(Fixed("ARR", "S", "3", 'P', "0", "DIM(3) INZ(0)") + Fixed("IDX", "S", "5", 'I', "0", "DIM(2) INZ(1)") +
            Fixed("J", "S", "5", 'I', "0") + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "J") +
            C("Z-ADD", factor2: "3", result: "ARR(3)") + C("CALLP", expression: "ARM()") + C("LOOKUP", "3", "ARR(IDX(J))", eq: "11") +
            C("CALLP", expression: "DISARM()") + C("RETURN"));
        var armed = false; var reads = 0;
        var argument = new ProgramArgument(() => armed ? ++reads == 1 ? 1L : 2L : 1L, _ => { }, value => value, "*INT", 2);
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, name, _, _) =>
        { armed = name == "ARM"; return new() { Success = true }; } }).Run(program, [argument]);
        Assert.Equal(1, reads); Assert.Equal(3L, context.ReadArrayValue("IDX", 1)); Assert.Equal(1L, context.ReadArrayValue("IDX", 2)); Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void Caller_index_rejection_preserves_the_receiver_and_search_flags()
    {
        object? value = 1m; var writes = 0;
        var argument = new ProgramArgument(() => value, v => { writes++; value = v; }, v => Equals(v, 1m) ? v :
            throw new CpfException("CPF3C3A", "Index receiver rejected."), "*DEC", 5);
        var program = Compile(Fields() + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "I") + Fill() + C("SETON", eq: "11") + C("LOOKUP", "3", "ARR(I)", eq: "11"));
        var context = new RpgRuntimeContext(program, new());
        Assert.Throws<CpfException>(() => new RpgInterpreter(new()).Run(program, [argument], context));
        Assert.Equal(0, writes); Assert.Equal(1m, value); Assert.Equal(1m, context.ReadValue("I")); Assert.True(context.Indicators[11]);
        Assert.Equal(false, Read(context, "%found")); Assert.Equal(false, Read(context, "%equal"));
    }
    [Fact]
    public void Cancellation_before_a_search_comparison_preserves_index_and_flags()
    {
        using var stop = new CancellationTokenSource(); var armed = false; var writes = 0;
        var argument = new ProgramArgument(() => { if (armed) stop.Cancel(); return 1m; }, _ => writes++, value => value, "*DEC", 5);
        var program = Compile(Fields() + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "I") + Fill() + C("SETON", eq: "11") +
            C("CALLP", expression: "ARM()") + C("LOOKUP", "3", "ARR(I)", eq: "11"));
        var context = new RpgRuntimeContext(program, new());
        var host = new RpgHost { CancellationToken = stop.Token, TypedProgramCaller = (_, _, _, _) => { armed = true; return new() { Success = true }; } };
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program, [argument], context));
        Assert.Equal(0, writes); Assert.Equal(1m, context.ReadValue("I")); Assert.True(context.Indicators[11]); Assert.Equal(false, Read(context, "%found"));
    }
    [Theory]
    [InlineData('D', "10")] [InlineData('T', "8")] [InlineData('Z', "26")] [InlineData('N', "1")]
    public void Date_time_timestamp_and_indicator_arrays_accept_matching_search_fields(char type, string length)
    {
        var context = Run(Fixed("ARR", "S", length, type, keywords: "DIM(1)") + Fixed("KEY", "S", length, type) + C("LOOKUP", "KEY", "ARR", eq: "11"));
        Assert.True(context.Indicators[11]);
    }
    [Fact]
    public void Indicator_array_search_accepts_a_global_indicator_argument()
    {
        var context = Run(Fixed("ARR", "S", "1", 'N', keywords: "DIM(1) INZ(*ON)") + C("SETON", eq: "11") + C("LOOKUP", "*IN11", "ARR", eq: "12"));
        Assert.True(context.Indicators[11]); Assert.True(context.Indicators[12]);
    }
    [Fact]
    public void Column_limited_global_declarations_share_constants_layouts_and_comment_handling()
    {
        var context = Run("00001  dcl-c COUNT 2; // comment after the declaration\n00002  // sequence-area comment\n       dcl-s ARR packed(3:0) dim(COUNT)\n         inz(3);\n" +
            "       dcl-s I int(5) inz(2);".PadRight(81) + "// ignored ' /copy BAD\n" + C("LOOKUP", "3", "ARR(I)", eq: "11"));
        Assert.Equal(2L, context.ReadValue("I")); Assert.True(context.Indicators[11]);
    }
    [Theory]
    [InlineData("dcl-s X int(5);\n")]
    [InlineData("      dcl-s X int(5);\n")]
    [InlineData("       dcl-s X int(5);" + "                                                             X\n")]
    [InlineData("       dcl-ds DS;\nX int(5);\n       end-ds;\n")]
    [InlineData("/free\n       dcl-s X int(5);\n/end-free\n")]
    public void Column_limited_declarations_reject_bad_columns_and_unsupported_regions(string source)
    {
        Assert.Throws<RpgCompileException>(() => Compile(source + C("RETURN")));
    }
    [Fact]
    public void Declarations_after_calculations_and_split_literal_continuations_reject()
    {
        Assert.Throws<RpgCompileException>(() => Compile(C("RETURN") + "       dcl-s X int(5);\n"));
        Assert.Throws<RpgCompileException>(() => Compile("       dcl-s X char(4) inz('A\n       B');\n"));
    }
    [Theory]
    [InlineData("3", "ARR", "11", "12", "")] [InlineData("3", "ARR", "", "", "")]
    [InlineData("3", "MISSING", "", "", "11")] [InlineData("'A'", "ARR", "", "", "11")]
    [InlineData("3", "ARR(I+1)", "", "", "11")]
    public void Unsupported_native_lookup_forms_reject_before_execution(string key, string array, string hi, string lo, string eq)
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fields() + C("LOOKUP", key, array, hi: hi, lo: lo, eq: eq)));
    }
    [Fact]
    public void Lookup_rejects_mismatched_field_lengths_decimal_or_float_indexes_and_unsequenced_relative_searches()
    {
        foreach (var extra in new[] { Fixed("KEY", "S", "4", 'P', "0") + C("LOOKUP", "KEY", "ARR", eq: "11"),
            Fixed("STEP", "S", "3", 'P', "1") + C("LOOKUP", "3", "ARR(STEP)", eq: "11"),
            Fixed("STEP", "S", "4", 'F') + C("LOOKUP", "3", "ARR(STEP)", eq: "11"), C("LOOKUP", "3", "ARR", hi: "11"),
            C("LOOKUP", "3", "ARR", result: "I", eq: "11"), C("LOOKUP(E)", "3", "ARR", eq: "11") })
            Assert.Throws<RpgCompileException>(() => Compile(Fields() + extra));
    }
}
