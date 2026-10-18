using Ipc.Core.Compilation;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgDeclarationConstantTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "CONSTANTS", "**free\n" + source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ELEM_returns_the_total_occurrence_count_independently_of_selection(int count)
    {
        var context = Run("dcl-c COUNT " + count + ";\ndcl-ds BANK occurs(COUNT);\nTEXT char(1);\nend-ds;\n" +
            "dcl-c ALLOCATED %elem(BANK);\neval %occur(BANK)=COUNT;\nreturn;");
        Assert.Equal((decimal)count, context.ReadValue("ALLOCATED")); Assert.Equal(count, Read(context, "%elem(BANK)"));
    }

    [Theory]
    [InlineData("constant")]
    [InlineData("scalar")]
    [InlineData("subfield")]
    public void File_discovery_does_not_read_declarations_inside_multiline_literal_text(string kind)
    {
        const string literal = "'first\ndcl-f FAKE workstn infds(BANK);\nlast'";
        var declaration = kind switch {
            "constant" => "dcl-c TEXT " + literal + ";\n",
            "scalar" => "dcl-s TEXT char(100) inz(" + literal + ");\n",
            _ => "dcl-ds OUTER qualified;\nTEXT char(100) inz(" + literal + ");\nend-ds;\n"
        };
        var context = Run(declaration + "dcl-ds BANK;\nVALUE char(1);\nend-ds;\ndcl-c WIDTH %size(BANK);\nreturn;");
        Assert.Empty(context.Program.Workstations); Assert.Equal(1m, context.ReadValue("WIDTH"));
    }

    [Theory]
    [InlineData("", "12.50")]
    [InlineData("CONST(", "12.50)")]
    public void DCL_C_infers_numeric_precision_scale_and_preserves_the_value(string prefix, string literal)
    {
        var context = Run("dcl-c AMOUNT " + prefix + literal + ";\ndcl-s RESULT packed(5:2) inz(AMOUNT);\nreturn;");
        Assert.Equal(12.50m, context.ReadValue("AMOUNT")); Assert.Equal(12.50m, context.ReadValue("RESULT"));
        var field = context.Program.FindField("AMOUNT")!; Assert.Equal(RpgFieldSource.Constant, field.Source);
        Assert.Equal(RpgFieldKind.Packed, field.Kind); Assert.Equal(4, field.Length); Assert.Equal(2, field.Decimals);
        Assert.Equal(3, Read(context, "%size(AMOUNT)"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Character_hex_indicator_and_float_constants_keep_their_types_and_literal_text(int ccsid)
    {
        var context = Run("dcl-c TEXT CONST('*BLANKS');\ndcl-c QUOTE 'O''Brien: X';\ndcl-c RAW X'0080FF';\n" +
            "dcl-c ENABLED *on;\ndcl-c FLOATING 1.25E2;\ndcl-c EMPTY '';\n" +
            "dcl-s COPY char(10) inz(QUOTE);\ndcl-s IMAGE char(3) ccsid(*hex) inz(RAW);\nreturn;", ccsid);
        Assert.Equal("*BLANKS", context.ReadValue("TEXT")); Assert.Equal("O'Brien: X", context.ReadValue("COPY"));
        Assert.Equal(true, context.ReadValue("ENABLED")); Assert.Equal(125d, context.ReadValue("FLOATING")); Assert.Equal("", context.ReadValue("EMPTY"));
        var image = Assert.IsType<ProgramBuffer>(context.ReadValue("IMAGE")); Assert.Equal(ccsid, image.Ccsid); Assert.Equal(new byte[] { 0, 128, 255 }, image.ToArray());
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Named_constants_drive_native_sizes_occurrences_arrays_positions_overlays_and_initialization(int ccsid)
    {
        var context = Run("dcl-c COUNT 2;\ndcl-c WIDTH 4;\ndcl-c POSITION 3;\ndcl-c SCALE 1;\ndcl-c DEFAULT_VALUE CONST(7.5);\n" +
            "dcl-ds BANK qualified occurs(COUNT) inz len(16);\n" +
            "TEXT char(WIDTH) inz('ABCD');\nTAIL char(COUNT) overlay(TEXT:POSITION) inz('CD');\n" +
            "NUM packed(%abs(-WIDTH):SCALE) inz(DEFAULT_VALUE);\nFLAGS ind dim(COUNT);\nend-ds;\n" +
            "dcl-c TOTAL %size(BANK:*all);\ndcl-c NUM_BYTES %size(BANK.NUM);\ndcl-s IMAGE char(TOTAL) ccsid(*hex);\n" +
            "eval %occur(BANK)=COUNT;\nreturn;", ccsid);
        Assert.Equal(2, Read(context, "%elem(BANK)")); Assert.Equal(2, Read(context, "%occur(BANK)"));
        Assert.Equal(32m, context.ReadValue("TOTAL")); Assert.Equal(3m, context.ReadValue("NUM_BYTES"));
        Assert.Equal("CD", Read(context, "BANK.TAIL")); Assert.Equal(7.5m, Read(context, "BANK.NUM"));
        Assert.Equal(32, Assert.IsType<ProgramBuffer>(context.ReadValue("IMAGE")).Length);
    }
    [Fact]
    public void Layout_builtins_use_declarations_instead_of_initial_values_or_current_occurrences()
    {
        var context = Run("dcl-s TEXT char(12) inz('ABC');\ndcl-c WIDTH %len(TEXT);\ndcl-c COUNT %abs(-2);\n" +
            "dcl-ds ROWS qualified dim(COUNT) inz;\nITEM char(WIDTH) dim(COUNT);\nN int(3);\nend-ds;\n" +
            "dcl-c ALL_ROWS %size(ROWS:*all);\ndcl-c ALL_ITEMS %size(ROWS(2).ITEM:*all);\n" +
            "dcl-c ITEMS %elem(ROWS(1).ITEM);\ndcl-c DIGITS %len(ROWS(1).N);\n" +
            "dcl-ds COPY qualified;\nRAW char(%size(ROWS(1).ITEM:*all));\nend-ds;\nreturn;");
        Assert.Equal(12m, context.ReadValue("WIDTH")); Assert.Equal(50m, context.ReadValue("ALL_ROWS"));
        Assert.Equal(24m, context.ReadValue("ALL_ITEMS")); Assert.Equal(2m, context.ReadValue("ITEMS")); Assert.Equal(3m, context.ReadValue("DIGITS"));
        Assert.Equal(24, Read(context, "%size(COPY)"));
    }
    [Fact]
    public void Earlier_subfield_layouts_can_supply_later_subfield_dimensions()
    {
        var context = Run("dcl-ds BANK qualified inz;\nTEXT char(6);\nCOPY char(%size(BANK.TEXT));\nend-ds;\nreturn;");
        Assert.Equal(12, Read(context, "%size(BANK)")); Assert.Equal(6, Read(context, "%len(BANK.COPY)"));
    }
    [Theory]
    [InlineData("eval N=2;")]
    [InlineData("clear N;")]
    public void Compiled_named_constants_are_immutable_calculation_targets(string statement)
    {
        var error = Assert.Throws<RpgRuntimeException>(() => Run("dcl-c N 1;\n" + statement));
        Assert.Contains("constant", error.Message, StringComparison.OrdinalIgnoreCase); Assert.NotNull(error.Location);
    }
    [Fact]
    public void Character_constant_substrings_and_host_rebinding_cannot_change_a_compiled_value()
    {
        var context = Run("dcl-c TEXT 'ABC';\nreturn;");
        Assert.Throws<RpgRuntimeException>(() => context.BindConstants(new Dictionary<string, string> { ["TEXT"] = "XYZ" }));
        Assert.Equal("ABC", context.ReadValue("TEXT"));
        Assert.Throws<RpgRuntimeException>(() => Run("dcl-c TEXT 'ABC';\neval %subst(TEXT:1:1)='Z';"));
    }
    [Fact]
    public void Constant_values_and_declaration_errors_retain_include_locations()
    {
        var source = new SourceDocument("ROOT", "**free\n/copy DEFINES\nreturn;");
        var program = RpgCompiler.Compile("QGPL", "CONSTANTS", source, (_, _) => new("DEFINES", "dcl-c N 2;\ndcl-s TEXT char(N) inz('OK');"));
        Assert.Equal("OK", new RpgInterpreter(new()).Run(program).ReadValue("TEXT"));
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "CONSTANTS", source,
            (_, _) => new("DEFINES", "dcl-c N 2;\ndcl-s TEXT char(LATER);")));
        Assert.Equal("DEFINES", error.Location!.Source); Assert.Equal(2, error.Location.Line);
    }
    [Theory]
    [InlineData("dcl-c N;")]
    [InlineData("dcl-c N CONST;")]
    [InlineData("dcl-c N CONST();")]
    [InlineData("dcl-c N CONST(1) INZ(2);")]
    [InlineData("dcl-c N 1+2;")]
    [InlineData("dcl-c N %abs(1+2);")]
    [InlineData("dcl-c N LATER;\ndcl-c LATER 2;")]
    [InlineData("dcl-c N N;")]
    [InlineData("dcl-s V int(10) inz(2);\ndcl-c N V;")]
    [InlineData("dcl-s V int(10) inz(2);\ndcl-c N %abs(V);")]
    [InlineData("dcl-c N %status();")]
    [InlineData("dcl-c N %date();")]
    [InlineData("dcl-c N *hival;")]
    [InlineData("dcl-c N 1;\ndcl-c N 2;")]
    [InlineData("dcl-c N 1;\ndcl-s N int(10);")]
    [InlineData("dcl-ds N;\nVALUE char(1);\nend-ds;\ndcl-c N 2;")]
    [InlineData("dcl-s V char(1);\ndcl-c N %elem(V);")]
    [InlineData("dcl-s V char(1);\ndcl-c N %size(V:*all);")]
    [InlineData("return;\ndcl-c N 2;")]
    [InlineData("dcl-c N 1; dcl-c M 2;")]
    public void Unsupported_or_invalid_constants_fail_before_execution_with_a_source_location(string source)
    {
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location);
    }
    [Theory]
    [InlineData("'2'")]
    [InlineData("*on")]
    [InlineData("1.5")]
    [InlineData("2.0")]
    [InlineData("2E0")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("32768")]
    public void Occurrence_counts_still_enforce_numeric_integer_and_storage_bounds(string value)
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-c COUNT " + value + ";\ndcl-ds BANK occurs(COUNT);\nV char(1);\nend-ds;"));
    }
    [Theory]
    [InlineData("dcl-c N 'ABC';\ndcl-s V char(2) inz(N);")]
    [InlineData("dcl-c N '2';\ndcl-s V int(10) inz(N);")]
    [InlineData("dcl-c N 1.25;\ndcl-s V packed(3:1) inz(N);")]
    [InlineData("dcl-c N X'0001';\ndcl-s V char(1) ccsid(*hex) inz(N);")]
    [InlineData("dcl-c N 128E0;\ndcl-ds V;\nBYTE int(3) inz(N);\nend-ds;")]
    [InlineData("dcl-c N 1E300;\ndcl-ds V;\nNUMBER float(4) inz(N);\nend-ds;")]
    public void Constant_initializers_keep_declared_type_precision_and_byte_bounds(string source)
    {
        Assert.Throws<RpgCompileException>(() => Compile(source));
    }
    [Fact]
    public void Constant_count_is_bounded_before_runtime_storage_allocation()
    {
        var source = string.Join('\n', Enumerable.Range(0, 1025).Select(i => $"dcl-c C{i} 1;"));
        Assert.Contains("1024", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }
    [Fact]
    public void Repeated_constant_aliases_cannot_expand_beyond_the_literal_storage_budget()
    {
        var source = "dcl-c LARGE '" + new string('A', 8000) + "';\n" +
            string.Join('\n', Enumerable.Range(0, 132).Select(i => $"dcl-c C{i} LARGE;"));
        Assert.Contains("1 MiB", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }
    [Fact]
    public void Constant_evaluation_nesting_is_bounded()
    {
        var expression = string.Concat(Enumerable.Repeat("%abs(", 65)) + "1" + new string(')', 65);
        Assert.Throws<RpgCompileException>(() => Compile("dcl-c N " + expression + ";"));
    }
}
