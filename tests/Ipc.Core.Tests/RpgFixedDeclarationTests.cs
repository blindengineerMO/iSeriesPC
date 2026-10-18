using System.Globalization;
using Ipc.Core.Compilation;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgFixedDeclarationTests
{
    internal static string Fixed(string name = "", string definition = "", string length = "", char type = ' ', string decimals = "", string keywords = "", int shift = 0, string comment = "")
    {
        var chars = Enumerable.Repeat(' ', 100 + shift).ToArray(); chars[5 + shift] = 'D';
        void Put(int column, string text) => text.CopyTo(0, chars, column - 1 + shift, text.Length);
        Put(7, name); Put(24, definition); Put(33, length.PadLeft(7)); chars[39 + shift] = type; Put(41, decimals.PadLeft(2));
        Assert.True(keywords.Length <= 37); Put(44, keywords); Put(81, comment); return new string(chars).TrimEnd() + "\n";
    }
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "FIXEDARR", source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_runtime_arrays_use_native_storage_in_both_supported_column_origins(int shift)
    {
        var program = Compile(Fixed("COUNT", "C", keywords: "CONST(3)", shift: shift) + Fixed("NUM", "S", "5", 'I', "0", "DIM(COUNT) INZ(-7)", shift) +
            Fixed("WORDS", "S", "2", 'A', keywords: "DIM(COUNT) INZ('AB')", shift: shift) +
            "/free\neval NUM(2)=9;\nsorta NUM;\n/end-free");
        var context = new RpgInterpreter(new()).Run(program);
        Assert.True(program.FindField("NUM")!.NativeArray); Assert.Equal(new object?[] { -7L, -7L, 9L }, Assert.IsType<object?[]>(context.ReadValue("NUM")));
        Assert.Equal("AB", Read(context, "WORDS(3)")); Assert.Equal(6, Read(context, "%size(NUM:*all)"));
    }
    [Theory]
    [InlineData('A', "2", "", "'AB'", "C1C2")]
    [InlineData(' ', "2", "", "'AB'", "C1C2")]
    [InlineData('P', "3", "1", "12.3", "123C")]
    [InlineData(' ', "3", "1", "12.3", "123C")]
    [InlineData('S', "3", "1", "-12.3", "F1F2D3")]
    [InlineData('I', "3", "0", "-7", "F9")]
    [InlineData('I', "5", "0", "-7", "FFF9")]
    [InlineData('I', "10", "0", "-7", "FFFFFFF9")]
    [InlineData('I', "20", "0", "-7", "FFFFFFFFFFFFFFF9")]
    [InlineData('F', "4", "", "1.5", "3FC00000")]
    [InlineData('F', "8", "", "1.5", "3FF8000000000000")]
    [InlineData('N', "", "", "*on", "F1")]
    public void Traditional_types_produce_native_array_byte_vectors(char type, string length, string decimals, string initial, string expected)
    {
        string? bytes = null;
        var program = Compile(Fixed("VALUE", "S", length, type, decimals, "DIM(1) INZ(" + initial + ")") + "/free\ncallp SNAP(VALUE);\n/end-free");
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { bytes = Convert.ToHexString(Assert.IsType<ProgramArgument>(borrow!().Single()).ToBuffer()!.ToArray()); return new() { Success = true }; } }).Run(program);
        Assert.Equal(expected, bytes); Assert.True(program.FindField("VALUE")!.IsArray);
    }
    [Theory]
    [InlineData('D', "2026-10-17")]
    [InlineData('T', "12:34:56")]
    [InlineData('Z', "2026-10-17-12.34.56.123456")]
    public void Implied_datetime_lengths_load_compile_time_data(char type, string value)
    {
        var context = Run(Fixed("VALUE", "S", type: type, keywords: "DIM(1) CTDATA") + "/free\nreturn;\n/end-free\n**CTDATA VALUE\n" + value);
        Assert.NotNull(Read(context, "VALUE(1)")); Assert.Equal(type == 'D' ? 10 : type == 'T' ? 8 : 26, Read(context, "%size(VALUE)"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Alternating_compile_time_arrays_accept_continuations_and_forward_definitions(bool forward)
    {
        var main = Fixed("MAIN", "S", "2", keywords: "DIM(3) PERRCD(2)") + Fixed(keywords: "CTDATA");
        var alternate = Fixed("OTHER", "S", "3", 'P', "1", "DIM(3) ALT(MAIN)") + Fixed(keywords: "EXTFMT(L)");
        var context = Run((forward ? alternate + main : main + alternate) + "/free\nsorta MAIN;\n/end-free\n**CTDATA MAIN\nBB+010AA-020\nCC+030      comments");
        Assert.Equal(new object?[] { "AA", "BB", "CC" }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
        Assert.Equal(new object?[] { 1m, -2m, 3m }, Assert.IsType<object?[]>(context.ReadValue("OTHER")));
    }
    [Fact]
    public void Inferred_dimensions_and_fixed_constants_fold_in_physical_declaration_order()
    {
        var context = Run(Fixed("OTHER", "S", "3", 'I', "0", "DIM(*CTDATA) ALT(MAIN)") + Fixed("MAIN", "S", "1", keywords: "DIM(*CTDATA)") +
            Fixed("COUNT", "C", keywords: "%ELEM(OTHER)") + Fixed("COPY", "S", "5", 'I', "0", "DIM(COUNT) INZ(7)") +
            "/free\nreturn;\n/end-free\n**CTDATA MAIN\nA001\nB002\n");
        Assert.Equal(2m, context.ReadValue("COUNT")); Assert.Equal(7L, Read(context, "COPY(2)")); Assert.Equal(2L, Read(context, "OTHER(2)"));
    }
    [Fact]
    public void Fixed_and_free_declarations_share_constants_layouts_and_declaration_order_data()
    {
        var context = Run(Fixed("FIRST", "S", "1", keywords: "DIM(1) CTDATA") + "**free\ndcl-c WIDTH %size(FIRST:*all);\ndcl-s SECOND char(WIDTH) dim(1) ctdata;\nreturn;\n**\nA\n**\nB");
        Assert.Equal("A", Read(context, "FIRST(1)")); Assert.Equal("B", Read(context, "SECOND(1)")); Assert.Equal(1m, context.ReadValue("WIDTH"));
    }
    [Fact]
    public void Fixed_scalar_constants_and_initializers_preserve_literal_text_and_are_immutable()
    {
        var context = Run(Fixed("TEXT", "C", keywords: "CONST('can''t //')") + Fixed("COPY", "S", "10", 'A', keywords: "INZ(TEXT)") +
            "/free\nreturn;\n/end-free");
        Assert.Equal("can't //  ", context.ReadValue("COPY")); Assert.Equal("can't //", context.ReadValue("TEXT"));
        Assert.Contains("constant", Assert.Throws<RpgRuntimeException>(() => context.WriteValue("TEXT", "changed")).Message);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_comments_and_commentary_columns_cannot_change_quote_or_data_state(int shift)
    {
        var comment = new string(' ', 5 + shift) + "D* ignored ' /COPY MISSING\n";
        var context = Run(Fixed("NUM", "S", "3", 'I', "0", "DIM(2)", shift, "ignored ' //") + comment +
            Fixed(keywords: "CTDATA PERRCD(2)", shift: shift) + "/free\nreturn;\n/end-free\n**CTDATA NUM\n001002");
        Assert.Equal(2L, Read(context, "NUM(2)"));
    }
    [Fact]
    public void Includes_preserve_fixed_continuation_and_bad_data_locations()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "FIXEDARR", new SourceDocument("ROOT", Fixed("NUM", "S", "3", 'I', "0", "DIM(1)") + "/copy DEFS\n/free\nreturn;\n/end-free\n/copy DATA"),
            (_, reference) => reference == "DEFS" ? new("DEFS", Fixed(keywords: "CTDATA")) : new("DATA", "**CTDATA NUM\nBAD")));
        Assert.Equal("DATA", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Theory]
    [InlineData('B', "5", "0")]
    [InlineData('U', "5", "0")]
    [InlineData('G', "5", "")]
    [InlineData('C', "5", "")]
    [InlineData('*', "", "")]
    [InlineData('I', "5", "")]
    [InlineData('I', "5", "1")]
    [InlineData('P', "5", "")]
    [InlineData('A', "5", "0")]
    [InlineData('F', "4", "2")]
    [InlineData('N', "2", "")]
    [InlineData('D', "8", "")]
    [InlineData('Z', "20", "")]
    [InlineData('A', "", "")]
    public void Unsupported_or_inconsistent_fixed_type_columns_reject_with_source_locations(char type, string length, string decimals)
    {
        Assert.Equal(1, Assert.Throws<RpgCompileException>(() => Compile(Fixed("BAD", "S", length, type, decimals, "DIM(1)"))).Location!.Line);
    }
    [Theory]
    [InlineData("DIM(0)")]
    [InlineData("DIM(32768)")]
    [InlineData("DIM(1) DIM(2)")]
    [InlineData("DIM(2) BOGUS")]
    [InlineData("DIM(2) FROMFILE(DATA)")]
    [InlineData("DIM(2) VARYING")]
    [InlineData("DIM(1) CTDATA INZ(0)")]
    [InlineData("DIM(1) ALT(MISSING)")]
    public void Fixed_keywords_use_the_same_strict_native_declaration_validation(string keywords)
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fixed("BAD", "S", "3", 'I', "0", keywords)));
    }
    [Theory]
    [InlineData("DS")]
    [InlineData("PR")]
    [InlineData("PI")]
    [InlineData("")]
    public void Native_definition_forms_without_supported_attributes_reject(string definition)
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fixed(definition.Length == 0 ? "" : "BAD", definition, keywords: "DIM(1)")));
    }
    [Fact]
    public void Invalid_reserved_columns_and_non_right_justified_lengths_reject()
    {
        foreach (var column in new[] { 22, 23, 26, 43, 33 })
        {
            var chars = Fixed("BAD", "S", "3", 'I', "0", "DIM(1)").ToCharArray(); chars[column - 1] = column == 33 ? '1' : 'X';
            Assert.Throws<RpgCompileException>(() => Compile(new string(chars)));
        }
    }
    [Fact]
    public void Split_literal_continuations_reject_instead_of_changing_the_value()
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fixed("BAD", "S", "10", keywords: "DIM(1) INZ('open") + Fixed(keywords: "close')")));
    }
    [Fact]
    public void Fixed_definitions_after_calculations_or_inside_free_regions_reject()
    {
        foreach (var source in new[] { "/free\neval X=1;\n/end-free\n" + Fixed("X", "S", "3", 'I', "0"), "/free\n" + Fixed("X", "S", "3", 'I', "0") + "/end-free" })
            Assert.Throws<RpgCompileException>(() => Compile(source));
    }
    [Fact]
    public void Duplicate_fixed_and_free_declarations_reject_before_execution()
    {
        Assert.Throws<RpgCompileException>(() => Compile(Fixed("VALUE", "S", "3", 'I', "0", "DIM(2)") + "**free\ndcl-s VALUE int(3);\nreturn;"));
    }
    [Fact]
    public void Continued_definitions_retain_the_native_keyword_bound()
    {
        var source = Fixed("VALUE", "S", "3", 'I', "0", "DIM(1)") + string.Concat(Enumerable.Repeat(Fixed(keywords: "INZ(0)"), 1024));
        Assert.Contains("Too many declaration keywords", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }
    [Theory]
    [InlineData("3", "-128")] [InlineData("3", "127")]
    [InlineData("5", "-32768")] [InlineData("5", "32767")]
    [InlineData("10", "-2147483648")] [InlineData("10", "2147483647")]
    [InlineData("20", "-9223372036854775808")] [InlineData("20", "9223372036854775807")]
    public void Fixed_scalar_and_array_integer_initializers_accept_the_native_signed_limits(string digits, string initial)
    {
        foreach (var array in new[] { false, true })
        {
            var context = Run(Fixed("VALUE", "S", digits, 'I', "0", (array ? "DIM(1) " : "") + "INZ(" + initial + ")") + "/free\nreturn;\n/end-free");
            Assert.Equal(long.Parse(initial, CultureInfo.InvariantCulture), Read(context, array ? "VALUE(1)" : "VALUE"));
        }
    }
    [Theory]
    [InlineData("3", "-129")] [InlineData("3", "128")]
    [InlineData("5", "-32769")] [InlineData("5", "32768")]
    [InlineData("10", "-2147483649")] [InlineData("10", "2147483648")]
    [InlineData("20", "-9223372036854775809")] [InlineData("20", "9223372036854775808")]
    public void Fixed_scalar_and_array_initializers_reject_values_just_outside_the_native_signed_limits(string digits, string initial)
    {
        foreach (var array in new[] { false, true })
            Assert.Throws<RpgCompileException>(() => Compile(Fixed("VALUE", "S", digits, 'I', "0", (array ? "DIM(1) " : "") + "INZ(" + initial + ")")));
    }
    [Theory]
    [InlineData("3", "128")] [InlineData("5", "32768")]
    public void Shared_integer_initializer_validation_also_rejects_oversized_free_scalars(string digits, string initial)
    {
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-s VALUE int(" + digits + ") inz(" + initial + ");\nreturn;"));
    }
}
