using System.Globalization;
using Ipc.Core.Compilation;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgCompileTimeArrayTests
{
    private static Ipc.Rpg.Model.RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "CTARRAY", "**free\n" + source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    [Fact]
    public void Named_sections_bind_by_name_with_PERRCD_padding_defaults_and_record_comments()
    {
        var context = Run("dcl-s FIRST char(3) dim(5) ctdata perrcd(2);\ndcl-s SECOND char(2) dim(2) ctdata;\nreturn;\n" +
            "**CTDATA SECOND\nXY commentary\nZ\n**CTDATA FIRST\nAAABBBcommentary\nCCC\n");
        Assert.Equal(new object?[] { "AAA", "BBB", "CCC", "   ", "   " }, Assert.IsType<object?[]>(context.ReadValue("FIRST")));
        Assert.Equal(new object?[] { "XY", "Z " }, Assert.IsType<object?[]>(context.ReadValue("SECOND")));
    }
    [Fact]
    public void Unnamed_sections_follow_compile_time_declaration_order()
    {
        var context = Run("dcl-s A char(2) dim(2) ctdata;\ndcl-s ORDINARY int(5) dim(2);\ndcl-s B int(5) dim(2) ctdata;\nreturn;\n**\nAA\nBB\n**\n00001\n00002");
        Assert.Equal("BB", Read(context, "A(2)")); Assert.Equal(2L, Read(context, "B(2)")); Assert.Equal(0L, Read(context, "ORDINARY(1)"));
    }
    [Theory]
    [InlineData("ABC", 1)] [InlineData("ABC\n", 1)] [InlineData("ABC\n\n", 2)]
    public void DIM_CTDATA_uses_physical_records_and_is_available_to_later_layout_constants(string records, int expected)
    {
        var context = Run("dcl-s DATA char(3) dim(*ctdata);\ndcl-c COUNT %elem(DATA);\ndcl-s COPY int(5) dim(COUNT);\nreturn;\n**CTDATA DATA\n" + records);
        Assert.Equal(expected, Read(context, "%elem(DATA)")); Assert.Equal((decimal)expected, context.ReadValue("COUNT")); Assert.Equal(expected, Read(context, "%elem(COPY)"));
    }
    [Fact]
    public void Compile_time_records_preserve_comments_directives_quotes_and_phantom_declarations()
    {
        var called = false;
        var program = RpgCompiler.Compile("QGPL", "CTARRAY", new SourceDocument("ROOT", "**free\ndcl-s DATA char(14) dim(5) ctdata;\nreturn;\n**CTDATA DATA\n// literal\n/COPY MISSING\n000000*data\nDCL-F PHANTOM\n\\'//"),
            (_, _) => { called = true; throw new InvalidOperationException(); });
        var context = new RpgInterpreter(new()).Run(program);
        Assert.False(called); Assert.Empty(program.FileSpecs);
        Assert.Equal(new object?[] { "// literal".PadRight(14), "/COPY MISSING".PadRight(14), "000000*data".PadRight(14), "DCL-F PHANTOM".PadRight(14), "\\'//".PadRight(14) }, Assert.IsType<object?[]>(context.ReadValue("DATA")));
    }
    [Fact]
    public void Markers_and_directives_inside_multiline_code_literals_do_not_start_program_data()
    {
        var called = false;
        var program = RpgCompiler.Compile("QGPL", "CTARRAY", new SourceDocument("ROOT", "**free\ndcl-c TEXT 'start\n**CTDATA FAKE\n/COPY MISSING\nend';\nreturn;"),
            (_, _) => { called = true; throw new InvalidOperationException(); });
        var context = new RpgInterpreter(new()).Run(program); Assert.False(called); Assert.Contains("**CTDATA FAKE", Assert.IsType<string>(context.ReadValue("TEXT")));
    }
    [Fact]
    public void Skipped_unbalanced_literals_do_not_hide_ENDIF_or_array_data()
    {
        var context = Run("/if defined(NO)\ndcl-s BROKEN char(2) inz('unterminated;\n/endif\ndcl-s DATA char(2) dim(*ctdata);\nreturn;\n**CTDATA DATA\nOK");
        Assert.Equal("OK", Read(context, "DATA(1)"));
    }
    [Theory]
    [InlineData("packed(5:2)", "", "00123", "1.23")]
    [InlineData("packed(5:2)", "extfmt(S)", "0012C", "1.23")]
    [InlineData("packed(5:2)", "", "0012L", "-1.23")]
    [InlineData("zoned(5:2)", "", "0012L", "-1.23")]
    [InlineData("packed(5:2)", "extfmt(L)", "+00123", "1.23")]
    [InlineData("packed(5:2)", "extfmt(L)", "-00123", "-1.23")]
    [InlineData("packed(5:2)", "extfmt(R)", "00123+", "1.23")]
    [InlineData("packed(5:2)", "extfmt(R)", "00123-", "-1.23")]
    [InlineData("int(3)", "extfmt(L)", "-128", "-128")]
    public void Numeric_data_uses_implied_scale_and_the_external_sign_format(string type, string format, string data, string expected)
    {
        var context = Run("dcl-s DATA " + type + " dim(2) ctdata " + format + ";\nreturn;\n**CTDATA DATA\n" + data);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), RpgValues.ToDecimal(Read(context, "%xfoot(DATA)")));
        Assert.Equal(0m, RpgValues.ToDecimal(Read(context, "DATA(2)")));
    }
    [Theory]
    [InlineData(37, "C1C24040")]
    [InlineData(1208, "41422020")]
    public void Native_byte_vectors_use_captured_job_CCSID_and_native_packed_storage(int ccsid, string characters)
    {
        var bytes = new List<string>(); var program = Compile("dcl-s TEXT char(2) dim(2) ctdata;\ndcl-s NUM packed(5:2) dim(2) ctdata;\ncallp SNAP(TEXT:NUM);\nreturn;\n**CTDATA TEXT\nAB\n**CTDATA NUM\n0012L");
        new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            foreach (var value in borrow!()) bytes.Add(Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.IsType<ProgramArgument>(value).ToBuffer()).ToArray()));
            return new() { Success = true };
        } }).Run(program);
        Assert.Equal(new[] { characters, "00123D00000C" }, bytes);
    }
    [Theory]
    [InlineData(4)] [InlineData(8)]
    public void Float_data_uses_14_or_23_source_positions_per_element(int width)
    {
        var size = width == 4 ? 14 : 23;
        var context = Run("dcl-s DATA float(" + width + ") dim(2) ctdata perrcd(2);\nreturn;\n**CTDATA DATA\n" + "1.5".PadRight(size) + "-2.5".PadRight(size) + "comment");
        Assert.Equal(-1d, Read(context, "%xfoot(DATA)"));
    }
    [Theory]
    [InlineData("ind", "1")]
    [InlineData("date", "2026-10-17")]
    [InlineData("time", "12:34:56")]
    [InlineData("timestamp", "2026-10-17-12.34.56.123456")]
    public void Indicator_and_default_ISO_datetime_data_load_typed_native_values(string type, string data)
    {
        var value = Read(Run("dcl-s DATA " + type + " dim(1) ctdata;\nreturn;\n**CTDATA DATA\n" + data), "DATA(1)");
        if (type == "ind") Assert.Equal(true, value);
        else if (type == "time") Assert.Equal(new TimeSpan(12, 34, 56), value);
        else Assert.Equal(2026, Assert.IsType<DateTimeOffset>(value).Year);
    }
    [Fact]
    public void CLEAR_applies_type_defaults_instead_of_reloading_compile_time_values()
    {
        var context = Run("dcl-s DATA int(5) dim(2) ctdata;\nclear DATA;\nreturn;\n**CTDATA DATA\n00001\n00002");
        Assert.Equal(0m, Read(context, "%xfoot(DATA)"));
    }
    [Fact]
    public void A_fixed_compile_time_array_without_records_uses_type_defaults()
    {
        var context = Run("dcl-s DATA int(5) dim(2) ctdata;\nreturn;");
        Assert.Equal(new object?[] { 0L, 0L }, Assert.IsType<object?[]>(context.ReadValue("DATA")));
    }
    [Fact]
    public void Numeric_sequence_validation_includes_default_tail_elements()
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s DATA int(5) dim(2) ctdata ascend;\nreturn;\n**CTDATA DATA\n00001"));
    }
    [Fact]
    public void Character_sequence_validation_uses_activation_CCSID_and_preserves_data_source_location()
    {
        const string source = "dcl-s DATA char(1) dim(3) ctdata ascend;\nreturn;\n**CTDATA DATA\na\nA\n0";
        Assert.Equal("a", Read(Run(source), "DATA(1)"));
        var error = Assert.Throws<RpgRuntimeException>(() => Run(source, 1208)); Assert.Equal(1052, error.StatusCode); Assert.Equal(6, error.Location!.Line);
    }
    [Fact]
    public void Include_data_ignores_the_synthetic_EOF_line_and_keeps_error_origins()
    {
        var program = RpgCompiler.Compile("QGPL", "CTARRAY", new SourceDocument("ROOT", "**free\ndcl-s DATA char(3) dim(*ctdata);\nreturn;\n/copy TAIL"),
            (_, _) => new("TAIL", "**CTDATA DATA\nABC\n"));
        Assert.Equal(1, Read(new RpgInterpreter(new()).Run(program), "%elem(DATA)"));
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "CTARRAY", new SourceDocument("ROOT", "**free\ndcl-s DATA int(5) dim(1) ctdata;\nreturn;\n/copy TAIL"),
            (_, _) => new("TAIL", "**CTDATA DATA\nBAD\n")));
        Assert.Equal("TAIL", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Fact]
    public void Earlier_constants_supply_PERRCD_and_inferred_sections_precede_fixed_sections()
    {
        var context = Run("dcl-c PER 2;\ndcl-s FIXED char(2) dim(3) ctdata perrcd(PER);\ndcl-s INFERRED char(2) dim(*ctdata) perrcd(1);\nreturn;\n" +
            "**CTDATA INFERRED\nXY\nZ\n**CTDATA FIXED\nAABB\nCC  comments");
        Assert.Equal(2, Read(context, "%elem(INFERRED)")); Assert.Equal("CC", Read(context, "FIXED(3)"));
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s FIXED char(2) dim(1) ctdata;\ndcl-s INFERRED char(2) dim(*ctdata);\nreturn;\n**CTDATA FIXED\nAA\n**CTDATA INFERRED\nBB"));
    }
    [Fact]
    public void Initialization_encoding_failures_preserve_the_record_location_and_prevent_calculations()
    {
        var sent = false; var program = Compile("dcl-s DATA char(4) dim(1) ctdata;\nsndpgmmsg 'must not run';\nreturn;\n**CTDATA DATA\n€");
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { Ccsid = () => 37, SendMessage = _ => sent = true }).Run(program));
        Assert.Equal(452, error.StatusCode); Assert.Equal(6, error.Location!.Line); Assert.False(sent);
        Assert.Equal("€ ", Read(new RpgInterpreter(new() { Ccsid = () => 1208 }).Run(program), "DATA(1)"));
    }
    [Theory]
    [InlineData("dcl-s DATA int(5) ctdata;")]
    [InlineData("dcl-s DATA int(5) dim(2) ctdata(1);")]
    [InlineData("dcl-s DATA int(5) dim(2) ctdata inz;")]
    [InlineData("dcl-s DATA int(5) dim(2) ctdata inz(1);")]
    [InlineData("dcl-s DATA int(5) dim(2) perrcd(1);")]
    [InlineData("dcl-s DATA int(5) dim(2) extfmt(S);")]
    [InlineData("dcl-s DATA char(2) dim(*ctdata) perrcd(2);\n**CTDATA DATA\nAA")]
    [InlineData("dcl-s DATA float(8) dim(2) ctdata extfmt(S);")]
    [InlineData("dcl-s DATA int(5) dim(2) ctdata extfmt;")]
    [InlineData("dcl-s DATA char(2) dim(2) ctdata extfmt(L);")]
    [InlineData("dcl-s DATA char(2) dim(2) ctdata ccsid(*hex);")]
    [InlineData("dcl-s DATA char(101) dim(1) ctdata;")]
    [InlineData("dcl-s DATA char(40) dim(3) ctdata perrcd(3);")]
    [InlineData("dcl-s DATA char(2) dim(*ctdata);")]
    [InlineData("dcl-s DATA char(2) dim(*ctdata);\n**CTDATA DATA\n")]
    [InlineData("dcl-s DATA int(3) dim(1) ctdata extfmt(L);\n**CTDATA DATA\n+128")]
    [InlineData("dcl-s DATA int(5) dim(1) ctdata extfmt(L);\n**CTDATA DATA\n00001")]
    [InlineData("dcl-s DATA packed(5:2) dim(1) ctdata;\n**CTDATA DATA\n01.23")]
    [InlineData("dcl-s DATA float(4) dim(1) ctdata;\n**CTDATA DATA\n3.5E38")]
    [InlineData("dcl-s DATA date dim(1) ctdata;\n**CTDATA DATA\n2026-02-30")]
    [InlineData("dcl-s DATA char(2) dim(1) ctdata;\n**CTDATA MISSING\nAB")]
    [InlineData("dcl-s DATA char(2) dim(1) ctdata;\n**CTDATA DATA\nAB\n**CTDATA DATA\nCD")]
    [InlineData("dcl-s DATA char(2) dim(1) ctdata;\n**CTDATA DATA\nAB\n**\nCD")]
    [InlineData("dcl-s DATA char(2) dim(1) ctdata;\n**CTDATA DATA\nAB\nCD")]
    [InlineData("dcl-s DATA char(1) dim(3) ctdata perrcd(2);\n**CTDATA DATA\nAB\nCD")]
    [InlineData("**ALTSEQ\nBAD")]
    public void Invalid_declarations_records_and_sections_fail_with_source_locations(string source)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location);
    }
}
