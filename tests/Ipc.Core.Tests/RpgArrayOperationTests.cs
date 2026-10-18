using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgArrayOperationTests
{
    private static Ipc.Rpg.Model.RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "ARRAYOPS", "**free\n" + body);
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Sequence(bool descending) => "dcl-s KEYS int(5) dim(7) " + (descending ? "descend" : "ascend") + ";\n" +
        string.Join('\n', (descending ? new[] { 7, 5, 5, 5, 3, 3, 1 } : new[] { 1, 3, 3, 3, 5, 5, 7 }).Select((v, i) => $"eval KEYS({i + 1})={v};")) + "\n";

    [Theory]
    [InlineData(false, "LOOKUP", 3, 2)] [InlineData(true, "LOOKUP", 3, 5)]
    [InlineData(false, "LOOKUPLE", 3, 2)] [InlineData(true, "LOOKUPLE", 3, 5)]
    [InlineData(false, "LOOKUPGE", 3, 2)] [InlineData(true, "LOOKUPGE", 3, 5)]
    [InlineData(false, "LOOKUPLT", 4, 4)] [InlineData(true, "LOOKUPLT", 4, 5)]
    [InlineData(false, "LOOKUPLE", 4, 4)] [InlineData(true, "LOOKUPLE", 4, 5)]
    [InlineData(false, "LOOKUPGE", 4, 5)] [InlineData(true, "LOOKUPGE", 4, 4)]
    [InlineData(false, "LOOKUPGT", 4, 5)] [InlineData(true, "LOOKUPGT", 4, 4)]
    [InlineData(false, "LOOKUPLT", 3, 1)] [InlineData(true, "LOOKUPLT", 3, 7)]
    [InlineData(false, "LOOKUPGT", 5, 7)] [InlineData(true, "LOOKUPGT", 5, 1)]
    [InlineData(false, "LOOKUP", 4, 0)] [InlineData(true, "LOOKUP", 4, 0)]
    public void Sequenced_lookup_matches_IBM_duplicate_and_nearest_value_rules(bool descending, string operation, int key, int expected)
    {
        var context = Run(Sequence(descending) + "return;");
        Assert.Equal(expected, Read(context, "%" + operation + "(" + key + ":KEYS)")); Assert.Equal(false, Read(context, "%found()"));
    }
    [Fact]
    public void Unsequenced_lookup_returns_the_first_match_within_start_and_count()
    {
        var context = Run("dcl-s KEYS int(5) dim(4);\neval KEYS(1)=5;\neval KEYS(2)=1;\neval KEYS(3)=5;\neval KEYS(4)=2;\nreturn;");
        Assert.Equal(1, Read(context, "%lookup(5:KEYS)")); Assert.Equal(3, Read(context, "%lookup(5:KEYS:2:3)"));
        Assert.Equal(0, Read(context, "%lookup(5:KEYS:2:1)"));
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => Read(context, "%lookuple(3:KEYS)")).StatusCode);
    }
    [Theory]
    [InlineData(false, 3, 3)]
    [InlineData(true, 6, 6)]
    public void Sequenced_lookup_returns_the_first_duplicate_inside_the_requested_range(bool descending, int start, int expected)
    {
        var context = Run(Sequence(descending) + "return;");
        foreach (var function in new[] { "lookup", "lookuple", "lookupge" })
            Assert.Equal(expected, Read(context, "%" + function + "(3:KEYS:" + start + ":2)"));
    }
    [Fact]
    public void Numeric_lookup_does_not_narrow_the_search_argument_to_the_element_scale()
    {
        var context = Run(Sequence(false) + "return;");
        Assert.Equal(0, Read(context, "%lookup(3.5:KEYS)")); Assert.Equal(5, Read(context, "%lookupge(3.5:KEYS)")); Assert.Equal(4, Read(context, "%lookuple(3.5:KEYS)"));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(31)] [InlineData(257)]
    public void Sorting_odd_and_power_boundary_dimensions_matches_an_independent_numeric_order(int count)
    {
        var program = Compile("dcl-s NUM int(10) dim(" + count + ");\nsorta NUM;\nreturn;"); var host = new RpgHost(); var context = new RpgRuntimeContext(program, host);
        var numbers = Enumerable.Range(0, count).Select(i => (long)((i * 29 + 7) % 53 - 26)).ToArray();
        context.WriteValue("NUM", numbers.Cast<object?>().ToArray()); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(numbers.Order().Cast<object?>().ToArray(), Assert.IsType<object?[]>(context.ReadValue("NUM")));
    }
    [Fact]
    public void Character_lookup_pads_shorter_search_values_with_native_blanks()
    {
        var context = Run("dcl-s TEXT char(3) dim(2);\neval TEXT(1)='A';\neval TEXT(2)='B';\nreturn;");
        Assert.Equal(1, Read(context, "%lookup('A':TEXT)")); Assert.Equal(1, Read(context, "%lookup('A    ':TEXT)")); Assert.Equal(0, Read(context, "%lookup('AX':TEXT)"));
    }
    [Theory]
    [InlineData(false, "sorta", 1L, 3L)]
    [InlineData(true, "sorta", 3L, 1L)]
    [InlineData(null, "sorta(d)", 3L, 1L)]
    [InlineData(null, "sorta(a)", 1L, 3L)]
    public void SORTA_uses_declaration_sequence_or_an_allowed_extender(bool? descending, string operation, long first, long last)
    {
        var context = Run("dcl-s NUM int(5) dim(3) " + (descending is null ? "" : descending.Value ? "descend" : "ascend") + ";\n" +
            "eval NUM(1)=2;\neval NUM(2)=3;\neval NUM(3)=1;\n" + operation + " NUM;\nreturn;");
        Assert.Equal(first, Read(context, "NUM(1)")); Assert.Equal(last, Read(context, "NUM(3)"));
    }
    [Theory]
    [InlineData(37, "a", "A", "0")]
    [InlineData(1208, "0", "A", "a")]
    public void Character_sort_and_lookup_use_native_CCSID_byte_order(int ccsid, string first, string second, string third)
    {
        var context = Run("dcl-s TEXT char(1) dim(3) ascend;\neval TEXT(1)='A';\neval TEXT(2)='0';\neval TEXT(3)='a';\nsorta TEXT;\nreturn;", ccsid);
        Assert.Equal(new object?[] { first, second, third }, Assert.IsType<object?[]>(context.ReadValue("TEXT")));
        Assert.Equal(2, Read(context, "%lookup('" + second + "':TEXT)"));
    }
    [Fact]
    public void Opaque_sort_and_lookup_preserve_bytes_that_are_invalid_UTF8()
    {
        var context = Run("dcl-s RAW char(1) ccsid(*hex) dim(3) ascend;\neval RAW(1)=x'FF';\neval RAW(2)=x'00';\neval RAW(3)=x'01';\nsorta RAW;\nreturn;", 1208);
        Assert.Equal(new[] { "00", "01", "FF" }, Assert.IsType<object?[]>(context.ReadValue("RAW")).Select(v => Convert.ToHexString(Assert.IsType<ProgramBuffer>(v).ToArray())));
        Assert.Equal(3, Read(context, "%lookup(x'FF':RAW)"));
    }
    [Fact]
    public void Sorting_an_overlay_moves_entire_base_records_and_subarray_sort_keeps_the_other_records()
    {
        var context = Run("dcl-ds DATA qualified;\nRECORDS char(4) dim(4);\nKEYS char(1) overlay(RECORDS) ascend;\nend-ds;\n" +
            "eval DATA.RECORDS(1)='Z001';\neval DATA.RECORDS(2)='C002';\neval DATA.RECORDS(3)='A003';\neval DATA.RECORDS(4)='B004';\n" +
            "sorta %subarr(DATA.KEYS:2:3);\nreturn;");
        Assert.Equal(new object?[] { "Z001", "A003", "B004", "C002" }, Assert.IsType<object?[]>(Read(context, "DATA.RECORDS")));
    }
    [Fact]
    public void LIKEDS_keeps_overlay_and_sequence_metadata_in_independent_storage()
    {
        var context = Run("dcl-ds DATA qualified;\nRECORDS char(4) dim(2);\nKEYS char(1) overlay(RECORDS) descend;\nend-ds;\n" +
            "dcl-ds COPY likeds(DATA);\neval COPY.RECORDS(1)='A001';\neval COPY.RECORDS(2)='Z002';\nsorta COPY.KEYS;\nreturn;");
        Assert.Equal("Z002", Read(context, "COPY.RECORDS(1)")); Assert.Equal("    ", Read(context, "DATA.RECORDS(1)"));
    }
    [Fact]
    public void Chained_overlay_sort_moves_base_records_only_in_the_selected_occurrence()
    {
        var context = Run("dcl-ds DATA qualified occurs(2);\nRECORDS char(4) dim(3);\nPART char(2) overlay(RECORDS:2);\nKEY char(1) overlay(PART) ascend;\nend-ds;\ndcl-s SORTED char(12);\n" +
            "eval DATA.RECORDS='INIT';\neval %occur(DATA)=2;\neval DATA.RECORDS(1)='XC13';\neval DATA.RECORDS(2)='YA21';\neval DATA.RECORDS(3)='ZB32';\nsorta DATA.KEY;\n" +
            "eval SORTED=DATA.RECORDS(1)+DATA.RECORDS(2)+DATA.RECORDS(3);\neval %occur(DATA)=1;\nreturn;");
        Assert.Equal("YA21ZB32XC13", context.ReadValue("SORTED"));
        Assert.Equal(new object?[] { "INIT", "INIT", "INIT" }, Assert.IsType<object?[]>(Read(context, "DATA.RECORDS")));
    }
    [Fact]
    public void Sorting_selected_structure_array_subfields_does_not_modify_other_structure_elements()
    {
        var context = Run("dcl-ds DATA qualified dim(2) inz;\nNUM int(5) dim(3) ascend;\nend-ds;\n" +
            "eval DATA(1).NUM=9;\neval DATA(2).NUM(1)=3;\neval DATA(2).NUM(2)=1;\neval DATA(2).NUM(3)=2;\nsorta DATA(2).NUM;\nreturn;");
        Assert.Equal(new object?[] { 9L, 9L, 9L }, Assert.IsType<object?[]>(Read(context, "DATA(1).NUM")));
        Assert.Equal(new object?[] { 1L, 2L, 3L }, Assert.IsType<object?[]>(Read(context, "DATA(2).NUM")));
    }
    [Fact]
    public void SUBARR_assignment_and_numeric_expressions_preserve_elements_outside_the_slice()
    {
        var context = Run("dcl-s NUM int(5) dim(5) inz(9);\ndcl-s SOURCE int(5) dim(2) inz(2);\n" +
            "eval %subarr(NUM:2:3)=SOURCE+1;\neval %subarr(NUM:4)=7;\nreturn;");
        Assert.Equal(new object?[] { 9L, 3L, 3L, 7L, 7L }, Assert.IsType<object?[]>(context.ReadValue("NUM")));
        Assert.Equal(new object?[] { 3L, 7L, 7L }, Assert.IsType<object?[]>(Read(context, "%subarr(NUM:3)")));
        Assert.Equal(3, Read(context, "%elem(%subarr(NUM:3))"));
    }
    [Fact]
    public void XFOOT_sums_numeric_arrays_subarrays_and_array_expressions()
    {
        var context = Run("dcl-s NUM packed(5:2) dim(3) inz(1.25);\nreturn;");
        Assert.Equal(3.75m, Read(context, "%xfoot(NUM)")); Assert.Equal(2.50m, Read(context, "%xfoot(%subarr(NUM:2))"));
        Assert.Equal(6.75m, Read(context, "%xfoot(NUM+1)"));
    }
    [Fact]
    public void XFOOT_sums_float_elements_from_the_first_index_up()
    {
        var context = Run("dcl-s NUM float(8) dim(3);\neval NUM(1)=1E16;\neval NUM(2)=1;\neval NUM(3)=-1E16;\nreturn;");
        Assert.Equal(0d, Read(context, "%xfoot(NUM)"));
    }
    [Fact]
    public void XFOOT_does_not_silently_round_an_unrepresentable_decimal_total()
    {
        var context = Run("return;");
        Assert.Equal(103, Assert.Throws<RpgRuntimeException>(() => context.EvalBuiltin("XFOOT", [new RpgLiteral(new object?[] { 10000000000000000000000000000m, 0.0000000000000000000000000001m })])).StatusCode);
    }
    [Theory]
    [InlineData("packed(29:0)", "70000000000000000000000000000")]
    [InlineData("float(8)", "1E308")]
    public void XFOOT_reports_overflow_without_changing_the_source_array(string type, string value)
    {
        var context = Run("dcl-s NUM " + type + " dim(2) inz(" + value + ");\nreturn;"); var before = Assert.IsType<object?[]>(context.ReadValue("NUM"));
        Assert.Equal(103, Assert.Throws<RpgRuntimeException>(() => Read(context, "%xfoot(NUM)")).StatusCode);
        Assert.Equal(before, Assert.IsType<object?[]>(context.ReadValue("NUM")));
    }
    [Fact]
    public void Invalid_numeric_data_preserves_the_array_during_failed_sort()
    {
        var context = Run("dcl-ds DATA qualified;\nNUM packed(3:0) dim(3);\nend-ds;\neval DATA.NUM(1)=3;\neval DATA.NUM(2)=1;\n" +
            "monitor;\nsorta DATA.NUM;\non-error 907;\nendmon;\nreturn;");
        Assert.Equal("003C001C4040", Convert.ToHexString(Assert.IsType<ProgramBuffer>(context.ReadValue("DATA")).ToArray()));
    }
    [Fact]
    public void SUBARR_call_values_are_copies_and_host_writeback_does_not_change_program_storage()
    {
        var program = Compile("dcl-s NUM int(5) dim(3) inz(5);\ncallp MODIFY(%subarr(NUM:2));\nreturn;");
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var values = Assert.IsType<object?[]>(Assert.Single(borrow!())); values[0] = 9m;
            return new() { Success = true, UpdatedParameters = [new object?[] { 8m, 8m }] };
        } }).Run(program);
        Assert.Equal(new object?[] { 5L, 5L, 5L }, Assert.IsType<object?[]>(context.ReadValue("NUM")));
    }
    [Fact]
    public void An_element_index_computed_from_a_subarray_still_borrows_the_element_by_reference()
    {
        var program = Compile("dcl-s NUM int(5) dim(3) inz(5);\ndcl-s INDEXES int(5) dim(1) inz(2);\n" +
            "callp MODIFY(NUM(%xfoot(%subarr(INDEXES:1))));\nreturn;");
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            Assert.IsType<ProgramArgument>(Assert.Single(borrow!())).Value = 9L; return new() { Success = true };
        } }).Run(program);
        Assert.Equal(9L, Read(context, "NUM(2)"));
    }
    [Theory]
    [InlineData("%subarr(NUM:0)", 121)]
    [InlineData("%subarr(NUM:4)", 121)]
    [InlineData("%subarr(NUM:1:0)", 121)]
    [InlineData("%subarr(NUM:2:3)", 121)]
    [InlineData("%subarr(NUM:1.0)", 105)]
    [InlineData("%subarr(NUM:1:2E0)", 105)]
    [InlineData("%lookup(1:NUM:0)", 121)]
    [InlineData("%lookup(1:NUM:1:4)", 121)]
    [InlineData("%lookup(1:NUM:1:0)", 121)]
    [InlineData("%lookup(1:%subarr(NUM:1))", 105)]
    [InlineData("%lookup('1':NUM)", 105)]
    [InlineData("%xfoot(1)", 105)]
    public void Invalid_array_function_arguments_report_numeric_status(string expression, int status)
    {
        Assert.Equal(status, Assert.Throws<RpgRuntimeException>(() => Read(Run("dcl-s NUM int(5) dim(3);\nreturn;"), expression)).StatusCode);
    }
    [Theory]
    [InlineData("dcl-s NUM int(5) ascend;")]
    [InlineData("dcl-s NUM int(5) dim(3) ascend descend;")]
    [InlineData("dcl-s NUM int(5) dim(3) ascend(1);")]
    [InlineData("dcl-ds DATA qualified;\nN int(5) descend;\nend-ds;")]
    [InlineData("dcl-s NUM int(5) dim(3);\nsorta;")]
    [InlineData("dcl-s NUM int(5) dim(3);\nsorta(e) NUM;")]
    [InlineData("dcl-s NUM int(5) dim(3) ascend;\nsorta(d) NUM;")]
    [InlineData("dcl-s NUM int(5) dim(3) descend;\nsorta(a) %subarr(NUM:1);")]
    [InlineData("dcl-ds DATA qualified;\nNUM int(5) dim(3) ascend;\nend-ds;\nsorta(d) DATA.NUM;")]
    public void Invalid_array_declarations_and_sort_syntax_keep_source_locations(string source)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location);
    }
}
