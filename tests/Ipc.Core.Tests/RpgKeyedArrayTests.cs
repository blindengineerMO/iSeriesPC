using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgKeyedArrayTests
{
    private const string Declaration = "dcl-ds ROWS qualified dim(5) inz;\nID char(1);\nNAME char(6);\nPAY packed(7:2);\nCODES int(5) dim(2) descend;\nINDEX int(5);\nend-ds;\n";
    private static string Input => Declaration + InitialValues;
    private static string InitialValues => string.Join('\n', Enumerable.Range(0, 5).Select(i =>
        $"eval ROWS({i + 1}).ID='{(char)('A' + i)}';\neval ROWS({i + 1}).NAME='{new[] { "Maria", "Pablo", "Bill", "Alex", "Zelda" }[i]}';\n" +
        $"eval ROWS({i + 1}).PAY={new[] { 13, 12, 11, 12, 99 }[i]};\neval ROWS({i + 1}).CODES(1)={(i + 1) * 100};\n" +
        $"eval ROWS({i + 1}).CODES(2)={new[] { 7, 2, 9, 4, 0 }[i]};\neval ROWS({i + 1}).INDEX={(i == 0 ? 2 : 1)};")) + "\n";
    private static Ipc.Rpg.Model.RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "KEYARRAY", "**free\n" + body);
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Ids(RpgRuntimeContext context) => string.Concat(Enumerable.Range(1, 5).Select(i => Read(context, $"ROWS({i}).ID")));
    private static void Payloads(RpgRuntimeContext context)
    {
        for (var i = 1; i <= 5; i++)
        {
            var id = Assert.IsType<string>(Read(context, $"ROWS({i}).ID"))[0] - 'A' + 1;
            Assert.Equal((long)(id * 100), Read(context, $"ROWS({i}).CODES(1)"));
        }
    }

    [Theory]
    [InlineData("sorta ROWS(*).NAME", "DCABE")]
    [InlineData("sorta(d) ROWS(*).NAME", "EBACD")]
    [InlineData("sorta(a) ROWS(*).NAME", "DCABE")]
    [InlineData("sorta ROWS %fields(PAY:NAME)", "CDBAE")]
    [InlineData("SoRtA RoWs %FiElDs(pAy : nAmE)", "CDBAE")]
    [InlineData("sorta rows(*).name", "DCABE")]
    [InlineData("sorta(d) ROWS %fields(PAY:NAME)", "EABDC")]
    [InlineData("sorta ROWS %fields(NAME:PAY)", "DCABE")]
    [InlineData("sorta ROWS %fields(NAME)", "DCABE")]
    [InlineData("sorta %subarr(ROWS:2:3) %fields(PAY:NAME)", "ACDBE")]
    [InlineData("sorta %subarr(ROWS(*).NAME:2:3)", "ADCBE")]
    [InlineData("sorta(d) %subarr(ROWS(*).NAME:2)", "AEBCD")]
    [InlineData("sorta(d) %subarr(ROWS:2) %fields(PAY:NAME)", "AEBDC")]
    [InlineData("sorta ROWS(*).CODES(2)", "EBDAC")]
    [InlineData("sorta(d) ROWS(*).CODES(2)", "CADBE")]
    [InlineData("sorta %subarr(ROWS(*).CODES(2):2:3)", "ABDCE")]
    [InlineData("sorta ROWS(*).CODES(ROWS(1).INDEX)", "EBDAC")]
    [InlineData("sorta ROWS ( * ).NAME", "DCABE")]
    public void Keyed_sort_moves_complete_records_with_direction_key_priority_and_frozen_indexes(string operation, string expected)
    {
        var context = Run(Input + operation + ";\nreturn;"); Assert.Equal(expected, Ids(context)); Payloads(context);
    }
    [Theory]
    [InlineData("%lookup(12:ROWS(*).PAY)", 2)]
    [InlineData("%lookup(12:ROWS(*).PAY:3:2)", 4)]
    [InlineData("%lookup(12.5:ROWS(*).PAY)", 0)]
    [InlineData("%lookup('Pablo':ROWS(*).NAME)", 2)]
    [InlineData("%lookup(4:ROWS(*).CODES(2))", 4)]
    [InlineData("%lookup(4:ROWS(*).CODES(2):1:3)", 0)]
    [InlineData("%lookup(9:ROWS(*).CODES(ROWS(1).INDEX))", 3)]
    public void Keyed_lookup_is_sequential_and_respects_its_key_type_and_search_range(string expression, int expected)
    {
        var context = Run(Input + "return;"); Assert.Equal(expected, Read(context, expression)); Assert.Equal("ABCDE", Ids(context));
    }
    [Theory]
    [InlineData(37, "CAB")]
    [InlineData(1208, "BAC")]
    public void Keyed_character_sort_uses_native_CCSID_bytes(int ccsid, string expected)
    {
        var context = Run("dcl-ds ROWS qualified dim(3);\nID char(1);\nKEY char(1);\nend-ds;\n" +
            "eval ROWS(1).ID='A';\neval ROWS(2).ID='B';\neval ROWS(3).ID='C';\n" +
            "eval ROWS(1).KEY='A';\neval ROWS(2).KEY='0';\neval ROWS(3).KEY='a';\nsorta ROWS %fields(KEY);\nreturn;", ccsid);
        Assert.Equal(expected, string.Concat(Enumerable.Range(1, 3).Select(i => Read(context, $"ROWS({i}).ID"))));
    }
    [Theory]
    [InlineData("int(3)", "-5", "7", "-1", "ACB")]
    [InlineData("zoned(5:1)", "2.5", "-1.5", "0.5", "BCA")]
    [InlineData("float(8)", "1E100", "-1E100", "1E-100", "BCA")]
    [InlineData("ind", "*on", "*off", "*on", "BAC")]
    [InlineData("date", "%date('2026-03-01')", "%date('2025-01-01')", "%date('2026-01-01')", "BCA")]
    [InlineData("time", "%time('12:00:00')", "%time('00:00:00')", "%time('06:00:00')", "BCA")]
    [InlineData("timestamp", "%timestamp('2026-03-01T12:00:00')", "%timestamp('2025-01-01T12:00:00')", "%timestamp('2026-01-01T12:00:00')", "BCA")]
    public void Typed_keys_use_native_values_for_sorting_and_lookup(string type, string first, string second, string third, string expected)
    {
        var context = Run("dcl-ds ROWS qualified dim(3) inz;\nID char(1);\nKEY " + type + ";\nend-ds;\n" +
            "eval ROWS(1).ID='A';\neval ROWS(2).ID='B';\neval ROWS(3).ID='C';\n" +
            "eval ROWS(1).KEY=" + first + ";\neval ROWS(2).KEY=" + second + ";\neval ROWS(3).KEY=" + third + ";\nsorta ROWS %fields(KEY:ID);\nreturn;");
        Assert.Equal(expected, string.Concat(Enumerable.Range(1, 3).Select(i => Read(context, $"ROWS({i}).ID"))));
        Assert.Equal(expected.IndexOf('B') + 1, Read(context, "%lookup(" + second + ":ROWS(*).KEY)"));
    }
    [Theory]
    [InlineData("ROWS(*).KEY")]
    [InlineData("ROWS %fields(KEY)")]
    public void Keyed_sort_preserves_every_byte_in_record_gaps(string operand)
    {
        var context = Run("dcl-ds ROWS qualified dim(2) len(9);\nKEY char(1) pos(3);\nend-ds;\n" +
            "eval ROWS(1)=x'FF10C2203040506070';\neval ROWS(2)=x'EEF0C1998800776655';\nsorta " + operand + ";\nreturn;");
        Assert.Equal("EEF0C1998800776655", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "ROWS(1)")).ToArray()));
        Assert.Equal("FF10C2203040506070", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "ROWS(2)")).ToArray()));
    }
    [Theory]
    [InlineData(37, "C1404000FF", "C24040FF00")]
    [InlineData(1208, "41202000FF", "422020FF00")]
    public void Nonkey_numeric_blanks_and_opaque_bytes_move_without_conversion(int ccsid, string first, string second)
    {
        var context = Run("dcl-ds ROWS qualified dim(2);\nKEY char(1);\nNUM packed(3:0);\nRAW char(2) ccsid(*hex);\nend-ds;\n" +
            "eval ROWS(1).KEY='B';\neval ROWS(1).RAW=x'FF00';\neval ROWS(2).KEY='A';\neval ROWS(2).RAW=x'00FF';\nsorta ROWS %fields(KEY);\nreturn;", ccsid);
        Assert.Equal(first, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "ROWS(1)")).ToArray()));
        Assert.Equal(second, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "ROWS(2)")).ToArray()));
    }
    [Theory]
    [InlineData(1)] [InlineData(1024)] [InlineData(1025)]
    public void Sort_field_count_is_bounded_without_materializing_an_element_by_key_matrix(int count)
    {
        var source = "dcl-ds ROWS qualified dim(3);\nKEY char(1);\nend-ds;\n" +
            "eval ROWS(1).KEY='B';\neval ROWS(2).KEY='A';\neval ROWS(3).KEY='A';\nsorta ROWS %fields(" + string.Join(':', Enumerable.Repeat("KEY", count)) + ");\nreturn;";
        if (count > 1024) { Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location); return; }
        var context = Run(source); Assert.Equal("A", Read(context, "ROWS(1).KEY")); Assert.Equal("B", Read(context, "ROWS(3).KEY"));
    }
    [Fact]
    public void LIKEDS_sort_uses_independent_record_storage()
    {
        var context = Run(Declaration + "dcl-ds COPY likeds(ROWS) dim(5);\n" + InitialValues +
            "eval COPY=ROWS;\nsorta COPY %fields(PAY:NAME);\nreturn;");
        Assert.Equal("ABCDE", Ids(context)); Assert.Equal("CDBAE", string.Concat(Enumerable.Range(1, 5).Select(i => Read(context, $"COPY({i}).ID"))));
    }
    [Fact]
    public void A_quoted_FIELDS_spelling_inside_a_range_expression_does_not_split_sort_operands()
    {
        var context = Run(Declaration + "dcl-s NAMES char(16) dim(1) inz(' %FIELDS(NAME)');\n" + InitialValues +
            "sorta %subarr(ROWS:%lookup(' %FIELDS(NAME)':NAMES):5) %fields(PAY:NAME);\nreturn;");
        Assert.Equal("CDBAE", Ids(context));
    }
    [Theory]
    [InlineData("ROWS(*).KEY")]
    [InlineData("ROWS %fields(KEY:ID)")]
    public void A_numeric_key_failure_preserves_every_record(string operand)
    {
        var context = Run("dcl-ds ROWS qualified dim(3);\nID char(1);\nKEY packed(3:0);\nend-ds;\n" +
            "eval ROWS(1).ID='C';\neval ROWS(2).ID='A';\neval ROWS(3).ID='B';\neval ROWS(1).KEY=3;\neval ROWS(2).KEY=1;\n" +
            "monitor;\nsorta " + operand + ";\non-error 907;\nendmon;\nreturn;");
        Assert.Equal("C3003CC1001CC24040", string.Concat(Enumerable.Range(1, 3).Select(i => Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, $"ROWS({i})")).ToArray()))));
    }
    [Theory]
    [InlineData("0", 121)] [InlineData("3", 121)] [InlineData("1.0", 105)] [InlineData("2E0", 105)]
    public void Invalid_key_subscripts_report_status_without_moving_records(string index, int status)
    {
        var context = Run(Input + "monitor;\nsorta ROWS(*).CODES(" + index + ");\non-error " + status + ";\nendmon;\nreturn;");
        Assert.Equal("ABCDE", Ids(context)); Payloads(context); Assert.Equal(status, Read(context, "%status()"));
    }
    [Theory]
    [InlineData("%lookuplt(3:ROWS(*).CODES(2))")]
    [InlineData("%lookuple(3:ROWS(*).CODES(2))")]
    [InlineData("%lookupgt(3:ROWS(*).CODES(2))")]
    [InlineData("%lookupge(3:ROWS(*).CODES(2))")]
    [InlineData("%lookup(3:ROWS(*).CODES)")]
    [InlineData("%lookup(3:ROWS(*).MISSING)")]
    [InlineData("%lookup('3':ROWS(*).PAY)")]
    [InlineData("%xfoot(ROWS(*).PAY)")]
    [InlineData("%size(ROWS(*).PAY)")]
    public void Unsupported_or_incompatible_key_uses_report_105(string expression)
    {
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => Read(Run(Input + "return;"), expression)).StatusCode);
    }
    [Fact]
    public void A_keyed_selection_cannot_be_borrowed_as_a_writable_API_argument()
    {
        var called = false;
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } })
            .Run(Compile(Input + "callp MODIFY(ROWS(*).PAY);\nreturn;")));
        // Argument preparation fails before target dispatch, so its original
        // type/selection status survives rather than becoming a call failure.
        Assert.Equal(105, error.StatusCode); Assert.False(called); Assert.Contains("(*)", error.Message);
    }
    [Theory]
    [InlineData("ROWS")]
    [InlineData("ROWS(*)")]
    [InlineData("ROWS(*).MISSING")]
    [InlineData("ROWS(*).CODES")]
    [InlineData("ROWS(*).NAME(1)")]
    [InlineData("ROWS(*).CODES(*)")]
    [InlineData("ROWS %fields()")]
    [InlineData("ROWS %fields(PAY,NAME)")]
    [InlineData("ROWS %fields(PAY")]
    [InlineData("ROWS %fields(PAY:NAME")]
    [InlineData("ROWS %fields(PAY:)")]
    [InlineData("ROWS %fields(MISSING)")]
    [InlineData("ROWS %fields(CODES)")]
    [InlineData("ROWS %fields(CODES(1))")]
    [InlineData("ROWS %fields(ROWS.PAY)")]
    [InlineData("ROWS %fields(PAY+1)")]
    [InlineData("ROWS %fields('PAY')")]
    [InlineData("ROWS %fields(PAY) trailing")]
    [InlineData("ROWS(*).NAME %fields(PAY)")]
    [InlineData("ROWS(1) %fields(PAY)")]
    public void Invalid_sort_keys_fail_compilation_with_source_locations(string operand)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(Declaration + "sorta " + operand + ";")).Location);
    }
    [Fact]
    public void A_scalar_array_cannot_supply_structure_sort_fields()
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s NUM int(5) dim(3);\nsorta NUM %fields(NUM);"));
    }
}
