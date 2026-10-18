using System.Globalization;
using Ipc.Core.Compilation;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgAlternatingArrayTests
{
    private static Ipc.Rpg.Model.RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "ALTARRAY", "**free\n" + body);
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private const string Main = "dcl-s MAIN char(2) dim(4) ctdata perrcd(2);\n";
    private const string Alternate = "dcl-s OTHER packed(3:1) dim(4) alt(MAIN) extfmt(L);\n";

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Alternating_records_load_pairs_in_either_declaration_order_and_data_mode(bool forward, bool named)
    {
        var context = Run((forward ? Alternate + Main : Main + Alternate) + "return;\n" + (named ? "**CTDATA MAIN\n" : "**\n") +
            "AA+010BB-020comment\nCC+030      comment");
        Assert.Equal(new object?[] { "AA", "BB", "CC", "  " }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
        Assert.Equal(new object?[] { 1m, -2m, 3m, 0m }, Assert.IsType<object?[]>(context.ReadValue("OTHER")));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Both_inferred_arrays_use_the_main_section_count_for_later_constants(bool forward)
    {
        const string main = "dcl-s MAIN char(2) dim(*ctdata);\n";
        const string alternate = "dcl-s OTHER int(3) dim(*ctdata) alt(MAIN);\n";
        var context = Run((forward ? alternate + "dcl-c COUNT %elem(OTHER);\n" + main : main + alternate + "dcl-c COUNT %elem(OTHER);\n") +
            "return;\n**CTDATA MAIN\nAA001\nBB002\n");
        Assert.Equal(2m, context.ReadValue("COUNT")); Assert.Equal(2, Read(context, "%elem(MAIN)")); Assert.Equal(2, Read(context, "%elem(OTHER)"));
        Assert.Equal(2L, Read(context, "OTHER(2)"));
    }
    [Fact]
    public void Unnamed_sections_skip_alternate_declarations_and_follow_main_declaration_order()
    {
        var context = Run("dcl-s OTHER int(3) dim(2) alt(MAIN);\ndcl-s FIRST char(1) dim(1) ctdata;\ndcl-s MAIN char(1) dim(2) ctdata;\nreturn;\n**\nX\n**\nA001\nB002");
        Assert.Equal("X", Read(context, "FIRST(1)")); Assert.Equal("B", Read(context, "MAIN(2)")); Assert.Equal(2L, Read(context, "OTHER(2)"));
    }
    [Theory]
    [InlineData("sorta MAIN", "ABC", "312")]
    [InlineData("sorta OTHER", "BAC", "123")]
    public void SORTA_reorders_only_the_selected_array(string operation, string main, string alternate)
    {
        var context = Run("dcl-s MAIN char(1) dim(3) ctdata;\ndcl-s OTHER int(3) dim(3) alt(MAIN);\n" + operation + ";\nreturn;\n**CTDATA MAIN\nB003\nA001\nC002");
        Assert.Equal(main, string.Concat(Assert.IsType<object?[]>(context.ReadValue("MAIN"))));
        Assert.Equal(alternate, string.Concat(Assert.IsType<object?[]>(context.ReadValue("OTHER"))));
    }
    [Fact]
    public void The_two_arrays_keep_their_own_external_numeric_formats()
    {
        var context = Run("dcl-s MAIN packed(3:1) dim(2) ctdata perrcd(2) extfmt(L);\ndcl-s OTHER zoned(4:2) dim(2) alt(MAIN) extfmt(R);\nreturn;\n" +
            "**CTDATA MAIN\n+0120034-+0340056+");
        Assert.Equal(new object?[] { 1.2m, 3.4m }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
        Assert.Equal(new object?[] { -0.34m, 0.56m }, Assert.IsType<object?[]>(context.ReadValue("OTHER")));
    }
    [Theory]
    [InlineData("  123", "1.23")]
    [InlineData("  12L", "-1.23")]
    public void Zoned_source_data_accepts_space_padded_numeric_values(string data, string expected)
    {
        var context = Run("dcl-s MAIN char(6) dim(1) ctdata;\ndcl-s OTHER packed(5:2) dim(1) alt(MAIN);\nreturn;\n**CTDATA MAIN\n345126" + data);
        Assert.Equal("345126", Read(context, "MAIN(1)")); Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Read(context, "OTHER(1)"));
    }
    [Theory]
    [InlineData(37, "C1C2")]
    [InlineData(1208, "4142")]
    public void Alternating_source_produces_independent_native_API_buffers(int ccsid, string expected)
    {
        var buffers = new List<string>(); var program = Compile("dcl-s MAIN char(1) dim(2) ctdata perrcd(2);\ndcl-s OTHER int(3) dim(2) alt(MAIN);\ncallp CHANGE(MAIN:OTHER);\nreturn;\n**CTDATA MAIN\nA001B002");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!().Select(Assert.IsType<ProgramArgument>).ToArray();
            foreach (var arg in args) buffers.Add(Convert.ToHexString(Assert.IsType<ProgramBuffer>(arg.ToBuffer()).ToArray()));
            args[1].Value = new ProgramBuffer(new byte[] { 9, 2 }, ccsid); return new() { Success = true };
        } }).Run(program);
        Assert.Equal(new[] { expected, "0102" }, buffers); Assert.Equal("A", Read(context, "MAIN(1)")); Assert.Equal(9L, Read(context, "OTHER(1)"));
    }
    [Fact]
    public void Pair_initialization_checks_each_members_own_sequence()
    {
        var context = Run("dcl-s MAIN int(3) dim(2) ctdata ascend;\ndcl-s OTHER int(3) dim(2) alt(MAIN) descend;\nreturn;\n**CTDATA MAIN\n001002\n002001");
        Assert.Equal(2, Read(context, "%lookup(1:OTHER)"));
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s MAIN int(3) dim(2) ctdata ascend;\ndcl-s OTHER int(3) dim(2) alt(MAIN) descend;\nreturn;\n**CTDATA MAIN\n001001\n002002"));
    }
    [Fact]
    public void Alternate_character_sequence_validation_uses_the_activation_CCSID()
    {
        const string source = "dcl-s MAIN int(3) dim(3) ctdata ascend;\ndcl-s OTHER char(1) dim(3) alt(MAIN) ascend;\nreturn;\n**CTDATA MAIN\n001a\n002A\n0030";
        Assert.Equal("a", Read(Run(source), "OTHER(1)"));
        var error = Assert.Throws<RpgRuntimeException>(() => Run(source, 1208)); Assert.Equal(1052, error.StatusCode); Assert.NotNull(error.Location);
    }
    [Fact]
    public void Default_tails_and_CLEAR_remain_independent_for_the_two_arrays()
    {
        var context = Run("dcl-s MAIN char(1) dim(3) ctdata;\ndcl-s OTHER int(3) dim(3) alt(MAIN);\nclear MAIN;\nreturn;\n**CTDATA MAIN\nA001");
        Assert.Equal(new object?[] { " ", " ", " " }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
        Assert.Equal(new object?[] { 1L, 0L, 0L }, Assert.IsType<object?[]>(context.ReadValue("OTHER")));
    }
    [Fact]
    public void Multiple_pairs_use_their_own_sections_and_skip_independent_arrays()
    {
        var context = Run("dcl-s A char(1) dim(1) ctdata;\ndcl-s B int(3) dim(1) alt(A);\ndcl-s C char(1) dim(1) ctdata;\ndcl-s D int(3) dim(1) alt(C);\n" +
            "dcl-s E char(2) dim(1) ctdata;\nreturn;\n**CTDATA C\nX009\n**CTDATA E\nOK\n**CTDATA A\nY008");
        Assert.Equal(8L, Read(context, "B(1)")); Assert.Equal(9L, Read(context, "D(1)")); Assert.Equal("OK", Read(context, "E(1)"));
    }
    [Fact]
    public void A_bad_alternate_value_retains_the_include_record_location()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "ALTARRAY", new SourceDocument("ROOT", "**free\ndcl-s MAIN char(1) dim(1) ctdata;\ndcl-s OTHER int(3) dim(1) alt(MAIN);\nreturn;\n/copy DATA"),
            (_, _) => new("DATA", "**CTDATA MAIN\nXBAD\n")));
        Assert.Equal("DATA", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Theory]
    [InlineData("dim(2)", "dim(3)")]
    [InlineData("dim(*ctdata)", "dim(1)")]
    [InlineData("dim(1)", "dim(*ctdata)")]
    public void The_supported_pair_contract_requires_matching_fixed_or_inferred_dimensions(string main, string alternate)
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s MAIN char(1) " + main + " ctdata;\ndcl-s OTHER int(3) " + alternate + " alt(MAIN);\nreturn;\n**CTDATA MAIN\nA001"));
    }
    [Fact]
    public void Exactly_one_hundred_source_positions_can_hold_two_pairs()
    {
        var context = Run("dcl-s MAIN char(47) dim(2) ctdata perrcd(2);\ndcl-s OTHER int(3) dim(2) alt(MAIN);\nreturn;\n**CTDATA MAIN\n" +
            new string('A', 47) + "001" + new string('B', 47) + "002ignored");
        Assert.Equal(new string('B', 47), Read(context, "MAIN(2)")); Assert.Equal(2L, Read(context, "OTHER(2)"));
    }
    [Theory]
    [InlineData("X   ")]
    [InlineData(" 001")]
    public void An_unused_final_pair_requires_both_members_to_be_blank(string unused)
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-s MAIN char(1) dim(1) ctdata perrcd(2);\ndcl-s OTHER int(3) dim(1) alt(MAIN);\nreturn;\n**CTDATA MAIN\nA001" + unused));
    }
    [Fact]
    public void A_pair_without_a_data_section_keeps_both_type_defaults()
    {
        var context = Run("dcl-s MAIN char(1) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) alt(MAIN);\nreturn;");
        Assert.Equal(new object?[] { " ", " " }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
        Assert.Equal(new object?[] { 0L, 0L }, Assert.IsType<object?[]>(context.ReadValue("OTHER")));
    }
    [Fact]
    public void Space_padded_numeric_source_also_loads_without_an_alternate()
    {
        var context = Run("dcl-s MAIN int(5) dim(2) ctdata;\nreturn;\n**CTDATA MAIN\n  123\n  12L");
        Assert.Equal(new object?[] { 123L, -123L }, Assert.IsType<object?[]>(context.ReadValue("MAIN")));
    }
    [Theory]
    [InlineData("alt")]
    [InlineData("alt()")]
    [InlineData("alt('MAIN')")]
    [InlineData("alt(MAIN:OTHER)")]
    [InlineData("alt(OTHER)")]
    [InlineData("alt(MAIN) ctdata")]
    [InlineData("alt(MAIN) perrcd(1)")]
    [InlineData("alt(MAIN) inz(1)")]
    public void Invalid_alternate_options_keep_source_locations(string options)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("dcl-s MAIN int(3) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) " + options + ";\nreturn;")).Location);
    }
    [Theory]
    [InlineData("dcl-s OTHER int(3) dim(2) alt(MISSING);")]
    [InlineData("dcl-s MAIN int(3);\ndcl-s OTHER int(3) dim(2) alt(MAIN);")]
    [InlineData("dcl-s MAIN int(3) dim(2);\ndcl-s OTHER int(3) dim(2) alt(MAIN);")]
    [InlineData("dcl-s MAIN int(3) dim(2) ctdata;\ndcl-s OTHER int(3) alt(MAIN);")]
    [InlineData("dcl-s MAIN int(3) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) alt(MAIN);\ndcl-s THIRD int(3) dim(2) alt(MAIN);")]
    [InlineData("dcl-s MAIN int(3) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) alt(MAIN);\ndcl-s THIRD int(3) dim(2) alt(OTHER);")]
    [InlineData("dcl-s MAIN int(3) dim(2) alt(OTHER);\ndcl-s OTHER int(3) dim(2) alt(MAIN);")]
    [InlineData("dcl-s MAIN char(51) dim(1) ctdata;\ndcl-s OTHER char(50) dim(1) alt(MAIN);")]
    [InlineData("dcl-s MAIN char(10) dim(5) ctdata perrcd(5);\ndcl-s OTHER char(11) dim(5) alt(MAIN);")]
    [InlineData("dcl-s MAIN char(1) dim(1) ctdata;\ndcl-s OTHER int(3) dim(1) alt(MAIN);\n**CTDATA OTHER\nA001")]
    public void Unsupported_relationships_and_layouts_reject_before_execution(string source)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location);
    }
}
