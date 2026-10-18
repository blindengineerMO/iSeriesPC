using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgLikeDeclarationTests
{
    private static string Procedure(string name, bool begin, int shift)
    {
        var chars = new string(' ', 80 + shift).ToCharArray(); chars[5 + shift] = 'P';
        name.PadRight(15).CopyTo(0, chars, 6 + shift, 15); chars[23 + shift] = begin ? 'B' : 'E';
        return new string(chars) + "\n";
    }
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "LIKETEST", source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKE_inherits_format_and_CCSID_but_not_value_dimension_or_order(int ccsid)
    {
        var c = Run("**free\ndcl-s ROOT char(4) dim(3) descend inz('AB');\ndcl-s A like(ROOT);\ndcl-s B like(A:+2) inz('HELLO');\ndcl-s C like(B:-3) dim(%elem(ROOT));\neval C='XYZ';\nreturn;", ccsid);
        Assert.Equal("    ", c.ReadValue("A")); Assert.Equal("HELLO ", c.ReadValue("B")); Assert.Equal(new object?[] { "XYZ", "XYZ", "XYZ" }, Read(c, "C")); Assert.Equal(4, Read(c, "%len(A)"));
        Assert.Null(c.Program.FindField("A")!.ArrayDescending); Assert.False(c.Program.FindField("A")!.IsArray);
    }

    [Theory]
    [InlineData("packed(7:2)", "+2", 9, 5)]
    [InlineData("zoned(7:2)", "-2", 5, 5)]
    [InlineData("int(3)", "+2", 5, 2)]
    [InlineData("int(5)", "+5", 10, 4)]
    public void Numeric_adjustments_change_digits_and_native_storage(string type, string adjustment, int digits, int bytes)
    {
        var c = Run("**free\ndcl-s ROOT " + type + ";\ndcl-s N like(ROOT:" + adjustment + ") inz(12);\nreturn;"); Assert.Equal(digits, Read(c, "%len(N)")); Assert.Equal(bytes, Read(c, "%size(N)")); Assert.Equal(12m, Convert.ToDecimal(c.ReadValue("N")));
    }

    [Theory]
    [InlineData("float(4)", 4)] [InlineData("ind", 1)] [InlineData("date", 10)] [InlineData("time", 8)] [InlineData("timestamp", 26)]
    public void LIKE_keeps_nonadjustable_native_formats(string type, int bytes)
    {
        var c = Run("**free\ndcl-s ROOT " + type + ";\ndcl-s N like(ROOT);\nreturn;"); Assert.Equal(bytes, Read(c, "%size(N)")); Assert.Equal(c.Program.FindField("ROOT")!.Kind, c.Program.FindField("N")!.Kind);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Opaque_bytes_are_inherited_and_DFT_can_restore_job_text(int ccsid)
    {
        var c = Run("**free\ndcl-s ROOT char(3) ccsid(*hex) inz(x'00FF00');\ndcl-s N like(ROOT) inz(x'FF00FE');\ndcl-s TEXT like(ROOT) ccsid(*dft) inz('ABC');\nreturn;", ccsid); Assert.Equal("FF00FE", Convert.ToHexString(Assert.IsType<ProgramBuffer>(c.ReadValue("N")).ToArray())); Assert.Equal("ABC", c.ReadValue("TEXT"));
    }

    [Fact]
    public void Subfields_use_prior_local_and_qualified_types_and_preserve_overlay_layouts()
    {
        var c = Run("**free\ndcl-ds DATA qualified inz;\nA packed(5:2) inz(12.34);\nB like(A:+2) inz(56.78);\nC like(DATA.A) overlay(A);\nend-ds;\ndcl-s COPY like(DATA.B) inz(90.12);\neval DATA.A=12.34;\nreturn;"); Assert.Equal(56.78m, Read(c, "DATA.B")); Assert.Equal(12.34m, Read(c, "DATA.C")); Assert.Equal(90.12m, c.ReadValue("COPY")); Assert.Equal(7, Read(c, "%size(DATA)"));
    }

    [Fact]
    public void LIKE_of_a_structure_defines_character_bytes_without_cloning_subfields()
    {
        var c = Run("**free\ndcl-ds DATA qualified;\nA char(4);\nend-ds;\ndcl-s IMAGE like(DATA) inz(x'C1C2C3C4');\nreturn;"); Assert.Equal("C1C2C3C4", Convert.ToHexString(Assert.IsType<ProgramBuffer>(c.ReadValue("IMAGE")).ToArray())); Assert.Null(c.Program.FindDs("IMAGE")); Assert.Equal(4, Read(c, "%size(IMAGE)"));
    }

    [Fact]
    public void Returning_prototypes_define_types_and_return_metadata_keeps_exact_ancestry()
    {
        var c = Run("**free\ndcl-s ROOT packed(7:2);\ndcl-pr MAKE like(ROOT) rtnparm end-pr;\ndcl-s RESULT like(MAKE);\neval RESULT=GET(MAKE());\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) rtnparm end-pi;\nreturn 12.34;\nend-proc;\ndcl-proc GET;\ndcl-pi *n like(ROOT);\nP like(ROOT) const options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(12.34m, c.ReadValue("RESULT"));
    }

    [Theory]
    [InlineData("")] [InlineData("const")] [InlineData("value")]
    public void EXACT_accepts_the_root_and_its_LIKE_chain(string mode)
    {
        var c = Run("**free\ndcl-s ROOT packed(5:2) inz(12.34);\ndcl-s A like(ROOT) inz(56.78);\ndcl-s B like(A) inz(90.12);\ndcl-s N packed(7:2);\neval N=GET(ROOT)+GET(B);\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(7:2);\nP like(ROOT) " + mode + " options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(102.46m, c.ReadValue("N"));
    }

    [Theory]
    [InlineData("")] [InlineData("const")] [InlineData("value")]
    public void EXACT_rejects_equal_formats_from_unrelated_roots(string mode)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s ROOT packed(5:2);\ndcl-s OTHER packed(5:2);\ncallp GET(OTHER);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT) " + mode + " options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void EXACT_rejects_untyped_literals_and_expressions_before_dispatch()
    {
        var c = Run("**free\ndcl-s ROOT int(3);\ndcl-s N int(3);\nmonitor;\ncallp GET(1+2);\non-error 105;\neval N=1;\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT) const options(*exact);\nend-pi;\neval N=2;\nreturn;\nend-proc;"); Assert.Equal(1L, c.ReadValue("N"));
    }

    [Fact]
    public void LIKE_character_options_resolve_the_type_before_transforming()
    {
        var c = Run("**free\ndcl-s ROOT char(4);\ndcl-s A like(ROOT:+4) inz('AB');\ndcl-s N like(ROOT);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT) const options(*trim:*rightadj:*exact);\nend-pi;\neval N=P;\nreturn;\nend-proc;"); Assert.Equal("  AB", c.ReadValue("N"));
    }

    [Theory]
    [InlineData("float(4)", "+1")] [InlineData("date", "+0")]
    [InlineData("char(4)", "2")] [InlineData("char(4)", "-4")]
    [InlineData("packed(5:2)", "-4")] [InlineData("int(3)", "+1")]
    [InlineData("char(32767)", "+1")]
    public void Invalid_adjustments_reject_before_storage_allocation(string type, string adjustment)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s ROOT " + type + ";\ndcl-s A like(ROOT:" + adjustment + ");"));

    [Theory]
    [InlineData("MISSING")] [InlineData("ROOT.X")] [InlineData("ROOT(1)")]
    [InlineData("*EXT:'X':'FILE'")] [InlineData("ROOT:+1:+2")]
    public void Missing_or_unsupported_LIKE_targets_reject(string target)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s ROOT char(4);\ndcl-s A like(" + target + ");"));

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_standalones_subfields_prototypes_and_interfaces_use_signed_length_columns(int shift)
    {
        var source = D("ROOT", "S", length: "4", keywords: "INZ('AB')", shift: shift) +
            D("LONG", "S", length: "+2", keywords: "LIKE(ROOT) INZ('HELLO')", shift: shift) +
            D("DATA", "DS", keywords: "QUALIFIED INZ", shift: shift) + D("X", length: "+1", keywords: "LIKE(ROOT) INZ('ABCDE')", shift: shift) +
            D("GET", "PR", length: "+2", keywords: "LIKE(ROOT)", shift: shift) + D("P", keywords: "LIKE(ROOT) CONST", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "LONG=GET(ROOT)") + RpgFixedCalculationTests.C("RETURN") +
            Procedure("GET", true, shift) + D("GET", "PI", length: "+2", keywords: "LIKE(ROOT)", shift: shift) + D("P", keywords: "LIKE(ROOT) CONST", shift: shift) +
            RpgFixedCalculationTests.C("RETURN", expression: "P") + Procedure("GET", false, shift);
        var c = Run(source); Assert.Equal("AB    ", c.ReadValue("LONG")); Assert.Equal("ABCDE", Read(c, "DATA.X"));
    }
    [Fact]
    public void Cloned_subfields_and_array_elements_preserve_type_ancestry()
    {
        var c = Run("**free\ndcl-ds ROOT qualified;\nX packed(5:2);\nend-ds;\ndcl-ds COPY likeds(ROOT);\ndcl-s A like(ROOT.X) dim(2) inz(12.34);\ndcl-s N packed(7:2);\neval COPY.X=56.78;\neval N=GET(COPY.X)+GET(A(2));\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(7:2);\nP like(ROOT.X) const options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(69.12m, c.ReadValue("N"));
    }

    [Fact]
    public void Local_shadowed_fields_have_distinct_exact_roots()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s ROOT int(3);\ncallp OUTER();\nreturn;\ndcl-proc OUTER;\ndcl-pi *n end-pi;\ndcl-s ROOT int(3);\ncallp GET(ROOT);\nreturn;\nend-proc;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT) const options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void LIKE_does_not_inherit_CONST_or_an_input_value()
    {
        var c = Run("**free\ndcl-s N int(3);\ncallp GET(7);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP int(3) const;\nend-pi;\ndcl-s COPY like(P);\neval N=COPY;\neval COPY=9;\neval N=N+COPY;\nreturn;\nend-proc;"); Assert.Equal(9L, c.ReadValue("N"));
    }

    [Fact]
    public void LIKE_length_adjustments_accept_signed_named_constants()
    {
        var c = Run("**free\ndcl-c EXTRA 2;\ndcl-s ROOT char(4);\ndcl-s A like(ROOT:+EXTRA);\ndcl-s B like(A:-EXTRA);\nreturn;"); Assert.Equal(6, Read(c, "%size(A)")); Assert.Equal(4, Read(c, "%size(B)"));
    }

    [Fact]
    public void LIKE_variable_character_interfaces_keep_actual_capacity()
    {
        var c = Run("**free\ndcl-s ROOT char(4) inz('ABCD');\ncallp GET(ROOT);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT:+96) options(*varsize);\nend-pi;\neval %subst(P:2:2)='XY';\nreturn;\nend-proc;"); Assert.Equal("AXYD", c.ReadValue("ROOT"));
    }

    [Fact]
    public void LIKE_return_array_dimensions_are_explicit_and_exact_origins_survive_return()
    {
        var c = Run("**free\ndcl-s ROOT int(3);\ndcl-pr MAKE like(ROOT) dim(2) end-pr;\ndcl-s ONE like(MAKE);\ndcl-s A like(MAKE) dim(2);\neval A=GET(MAKE());\neval ONE=A(2);\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) dim(2) end-pi;\nreturn 7;\nend-proc;\ndcl-proc GET;\ndcl-pi *n like(ROOT) dim(2);\nP like(ROOT) dim(2) const options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(7L, c.ReadValue("ONE")); Assert.Equal(new object?[] { 7L, 7L }, Read(c, "A")); Assert.False(c.Program.FindField("ONE")!.IsArray);
    }

    [Theory]
    [InlineData("like(ROOT) value options(*trim)")]
    [InlineData("like(ROOT) options(*varsize)")]
    public void Resolved_numeric_types_reject_character_only_modes(string type)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s ROOT int(3);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP " + type + ";\nend-pi;\nreturn;\nend-proc;"));

    [Theory]
    [InlineData("dcl-c ROOT 4;")]
    [InlineData("dcl-pr ROOT extpgm('TARGET') end-pr;")]
    public void Constants_and_void_prototypes_cannot_supply_a_LIKE_type(string declaration)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n" + declaration + "\ndcl-s A like(ROOT);"));

    [Theory]
    [InlineData("2", ' ', "")]
    [InlineData("+2", 'A', "")]
    [InlineData("+2", ' ', "0")]
    public void Fixed_LIKE_rejects_unsigned_adjustments_and_explicit_type_or_scale(string length, char type, string decimals)
        => Assert.Throws<RpgCompileException>(() => Run(D("ROOT", "S", length: "4") + D("A", "S", length: length, type: type, decimals: decimals, keywords: "LIKE(ROOT)")));

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_LIKE_structure_overlays_keep_the_shared_native_position(int shift)
    {
        var c = Run(D("ROOT", "S", length: "4", shift: shift) + D("DATA", "DS", length: "8", keywords: "QUALIFIED INZ", shift: shift) +
            D("X", keywords: "LIKE(ROOT) OVERLAY(DATA)", shift: shift) + D("Y", keywords: "LIKE(ROOT) OVERLAY(DATA:*NEXT)", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "DATA.X='ABCD'") + RpgFixedCalculationTests.C("EVAL", expression: "DATA.Y='WXYZ'") + RpgFixedCalculationTests.C("RETURN"));
        Assert.Equal("ABCDWXYZ", Assert.IsType<ProgramBuffer>(Read(c, "DATA")).ToText());
    }

}
