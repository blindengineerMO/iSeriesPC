using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgForwardDeclarationTests
{
    private static RpgRuntimeContext Run(string source) => new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "FORWARD", source));
    private static object? Read(RpgRuntimeContext c, string expression) => RpgExpressionParser.Parse(expression).Eval(c);

    [Fact]
    public void Forward_LIKE_chains_keep_types_defaults_and_exact_ancestry()
    {
        var c = Run("**free\ndcl-s A like(B) inz(12.34);\ndcl-s B like(ROOT:+2);\ndcl-s ROOT packed(5:2) inz(56.78);\ndcl-s N packed(7:2);\neval N=GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(7:2);\nP like(ROOT:+2) const options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(12.34m, c.ReadValue("N")); Assert.Equal(0m, c.ReadValue("B")); Assert.Equal(56.78m, c.ReadValue("ROOT"));
    }

    [Fact]
    public void Forward_subfields_keep_source_layout_and_overlay_positions()
    {
        var c = Run("**free\ndcl-ds DATA qualified inz;\nA like(B) inz(12.34);\nB packed(5:2) inz(56.78);\nC like(DATA.B) overlay(A);\nend-ds;\neval DATA.A=90.12;\nreturn;"); Assert.Equal(6, Read(c, "%size(DATA)")); Assert.Equal(90.12m, Read(c, "DATA.C")); Assert.Equal(56.78m, Read(c, "DATA.B"));
    }

    [Fact]
    public void Forward_whole_structures_and_members_define_scalar_bytes_and_numbers()
    {
        var c = Run("**free\ndcl-s IMAGE like(DATA);\ndcl-s N like(DATA.N) inz(12.34);\ndcl-ds DATA qualified;\nTEXT char(4);\nN packed(5:2);\nend-ds;\nreturn;"); Assert.Equal(7, Read(c, "%size(IMAGE)")); Assert.IsType<ProgramBuffer>(c.ReadValue("IMAGE")); Assert.Equal(12.34m, c.ReadValue("N"));
    }

    [Fact]
    public void Later_local_types_shadow_globals_even_before_the_local_definition()
    {
        var c = Run("**free\ndcl-s ROOT char(4);\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-s A like(ROOT) inz(7);\ndcl-s ROOT int(3);\nreturn A;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Forward_explicit_and_implicit_procedure_returns_supply_types(bool explicitPrototype)
    {
        var c = Run("**free\ndcl-s A like(MAKE);\n" + (explicitPrototype ? "dcl-pr MAKE int(3) end-pr;\n" : "") + "eval A=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n int(3) end-pi;\nreturn 7;\nend-proc;"); Assert.Equal(7L, c.ReadValue("A"));
    }

    [Fact]
    public void Implicit_return_types_can_depend_on_later_local_types_and_constants()
    {
        var c = Run("**free\ndcl-s A like(MAKE);\neval A=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) end-pi;\ndcl-c WIDTH 4;\ndcl-s ROOT char(WIDTH);\nreturn 'AB';\nend-proc;"); Assert.Equal("AB  ", c.ReadValue("A"));
    }

    [Theory]
    [InlineData("dcl-s A like(B);\ndcl-s B like(A);")]
    [InlineData("dcl-s A like(A);")]
    [InlineData("dcl-ds DATA qualified;\nA like(B);\nB like(A);\nend-ds;")]
    [InlineData("dcl-s A like(MAKE);\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(A) end-pi;\nreturn A;\nend-proc;")]
    public void Dependency_cycles_reject_with_source_diagnostics(string body)
    {
        var e = Assert.Throws<RpgCompileException>(() => Run("**free\n" + body)); Assert.Contains("Cyclic", e.Message); Assert.True(e.LineNumber > 0 || e.Location is not null);
    }
    [Fact]
    public void Local_constant_widths_shadow_global_constants_in_type_previews()
    {
        var c = Run("**free\ndcl-c WIDTH 4;\ndcl-s A like(MAKE);\neval A=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) end-pi;\ndcl-c WIDTH 6;\ndcl-s ROOT char(WIDTH);\nreturn 'ABCDEF';\nend-proc;"); Assert.Equal("ABCDEF", c.ReadValue("A"));
    }

    [Fact]
    public void Metadata_preview_does_not_inherit_or_evaluate_target_initializers()
    {
        var c = Run("**free\ndcl-s A like(ROOT);\ndcl-c VALUE 7;\ndcl-s ROOT int(3) inz(VALUE);\nreturn;"); Assert.Equal(0L, c.ReadValue("A")); Assert.Equal(7L, c.ReadValue("ROOT"));
    }

    [Fact]
    public void Future_type_widths_can_use_constants_and_layouts_available_at_the_definition()
    {
        var c = Run("**free\ndcl-s A like(ROOT);\ndcl-c WIDTH 4;\ndcl-s OTHER char(WIDTH);\ndcl-s ROOT char(%size(OTHER));\nreturn;"); Assert.Equal(4, Read(c, "%size(A)"));
    }

    [Fact]
    public void Future_PI_parameters_define_related_scalar_types()
    {
        var c = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "FORWARD", "**free\ndcl-s A like(P);\ndcl-pi *n;\nP packed(5:2);\nend-pi;\neval A=P;\nreturn;"), new object?[] { 12.34m }); Assert.Equal(12.34m, c.ReadValue("A"));
    }

    [Fact]
    public void Forward_subfields_of_LIKEDS_templates_and_parameters_keep_identity()
    {
        var c = Run("**free\ndcl-s A like(COPY.X) inz(7);\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ndcl-ds COPY likeds(ROOT);\ndcl-s N int(3);\neval N=GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nP like(ROOT.X) const options(*exact);\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Forward_local_structures_shadow_global_layouts()
    {
        var c = Run("**free\ndcl-ds ROOT qualified;\nX char(4);\nend-ds;\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-s A like(ROOT.X) inz(7);\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\nreturn A;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Feedback_subfields_are_available_before_their_PSDS_definition()
    {
        var c = Run("**free\ndcl-s N like(PS.STATUS);\ndcl-ds PS psds qualified;\nSTATUS *status;\nend-ds;\nreturn;"); Assert.Equal(5, Read(c, "%len(N)")); Assert.Equal(0m, c.ReadValue("N"));
    }

    [Fact]
    public void Metadata_scan_skips_declarations_embedded_in_multiline_literals()
    {
        var c = Run("**free\ndcl-s A like(ROOT);\ndcl-c TEXT 'first\ndcl-s ROOT packed(5:2);\nlast';\ndcl-s ROOT char(4);\nreturn;"); Assert.Equal(4, Read(c, "%size(A)")); Assert.Equal("    ", c.ReadValue("A"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_forward_types_and_subfields_use_both_column_origins(int shift)
    {
        var c = Run(D("A", "S", length: "+2", keywords: "LIKE(ROOT) INZ('ABCDEF')", shift: shift) +
            D("DATA", "DS", keywords: "QUALIFIED INZ", shift: shift) + D("X", keywords: "LIKE(Y)", shift: shift) +
            D("Y", length: "5", type: 'P', decimals: "2", shift: shift) + D("ROOT", "S", length: "4", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "DATA.X=12.34") + RpgFixedCalculationTests.C("RETURN"));
        Assert.Equal("ABCDEF", c.ReadValue("A")); Assert.Equal(12.34m, Read(c, "DATA.X")); Assert.Equal(6, Read(c, "%size(DATA)"));
    }

    [Theory]
    [InlineData("dcl-s A like(ROOT);\ndcl-s ROOT char(4) ccsid(*hex);")]
    [InlineData("dcl-s A like(ROOT);\ndcl-s ROOT char(4) dim(3) descend;")]
    public void Forward_types_keep_opaque_modes_without_inheriting_dimensions(string source)
    {
        var c = Run("**free\n"+source+"\nreturn;"); Assert.Equal(4, Read(c, "%size(A)")); Assert.False(c.Program.FindField("A")!.IsArray); Assert.Null(c.Program.FindField("A")!.ArrayDescending);
        if (source.Contains("*hex")) Assert.IsType<ProgramBuffer>(c.ReadValue("A")); else Assert.IsType<string>(c.ReadValue("A"));
    }

    [Fact]
    public void Long_dependency_chains_reject_before_exhausting_the_process_stack()
    {
        var source = "**free\n" + string.Join('\n', Enumerable.Range(0, 80).Select(n => "dcl-s A"+n+" like(A"+(n+1)+");")) + "\ndcl-s A80 char(4);";
        Assert.Contains("64 levels", Assert.Throws<RpgCompileException>(() => Run(source)).Message);
    }

    [Theory]
    [InlineData("dcl-s A like(MISSING);")]
    [InlineData("dcl-s A like(GET);\nreturn;\ndcl-proc GET;\ndcl-pi *n end-pi;\nreturn;\nend-proc;")]
    public void Missing_and_void_return_types_reject_without_inventing_storage(string source)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n"+source));

    [Fact]
    public void Return_widths_can_use_prior_PI_parameter_numbers()
    {
        var c = Run("**free\ndcl-s A like(MAKE);\neval A=MAKE(7);\nreturn;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) rtnparm;\nP int(3) value;\nend-pi;\ndcl-c WIDTH %parmnum(P);\ndcl-s ROOT char(WIDTH);\nreturn 'AB';\nend-proc;"); Assert.Equal("AB", c.ReadValue("A"));
    }

    [Fact]
    public void Whole_forward_LIKEDS_types_resolve_before_the_template_is_bound()
    {
        var c = Run("**free\ndcl-s IMAGE like(COPY);\ndcl-ds ROOT qualified;\nX char(4);\nend-ds;\ndcl-ds COPY likeds(ROOT);\nreturn;"); Assert.Equal(4, Read(c, "%size(IMAGE)")); Assert.IsType<ProgramBuffer>(c.ReadValue("IMAGE"));
    }

    [Fact]
    public void Later_PI_parameters_can_define_earlier_parameter_types()
    {
        var c = Run("**free\ndcl-s N int(3);\neval N=GET(7:9);\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nP like(Q) const;\nQ int(3) const;\nend-pi;\nreturn P+Q;\nend-proc;"); Assert.Equal(16L, c.ReadValue("N"));
    }

    [Fact]
    public void Prototype_return_and_parameter_types_can_refer_to_later_module_fields()
    {
        var c = Run("**free\ndcl-pr GET like(ROOT);\nP like(ROOT) value;\nend-pr;\ndcl-s ROOT int(3);\ndcl-s N like(ROOT);\neval N=GET(7);\nreturn;\ndcl-proc GET;\ndcl-pi *n like(ROOT);\nP like(ROOT) value;\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Anonymous_structures_can_use_later_member_types()
    {
        var c = Run("**free\ndcl-ds *n inz;\nA like(B) inz(7);\nB int(3);\nend-ds;\nreturn;"); Assert.Equal(7L, c.ReadValue("A")); Assert.Equal(0L, c.ReadValue("B"));
    }

    [Fact]
    public void Constant_type_cycles_reject_with_a_dependency_diagnostic()
    {
        var e = Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s A like(ROOT);\ndcl-c WIDTH %size(A);\ndcl-s ROOT char(WIDTH);")); Assert.Contains("Cyclic", e.Message);
    }

    [Fact]
    public void Ordinary_constant_initializers_still_require_prior_declarations()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s A int(3) inz(WIDTH);\ndcl-c WIDTH 4;"));

    [Fact]
    public void Missing_structure_dependencies_keep_the_real_source_location()
    {
        var e = Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-s A like(COPY);\ndcl-ds COPY likeds(MISSING);")); Assert.NotNull(e.Location); Assert.Contains("Missing structure", e.Message);
    }

    [Fact]
    public void Forward_implicit_return_metadata_keeps_exact_type_ancestry()
    {
        var c = Run("**free\ndcl-s N like(MAKE);\ndcl-pr CHECK int(3);\nP like(MAKE) const options(*exact);\nend-pr;\neval N=CHECK(MAKE());\nreturn;\ndcl-proc MAKE;\ndcl-pi *n int(3) end-pi;\nreturn 7;\nend-proc;\ndcl-proc CHECK;\ndcl-pi *n int(3);\nP like(MAKE) const;\nend-pi;\nreturn P;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Theory]
    [InlineData("root", "width")] [InlineData("rOoT", "wIdTh")]
    public void Metadata_names_use_the_same_case_rules_as_final_declarations(string root, string width)
    {
        var c = Run("**free\ndcl-s result like("+root+");\ndcl-c "+width+" 4;\ndcl-s "+root+" char("+width+");\neval result='AB';\nreturn;"); Assert.Equal("AB  ", c.ReadValue("RESULT"));
    }

    [Fact]
    public void Lowercase_implicit_return_definitions_keep_their_local_type_names()
    {
        var c = Run("**free\ndcl-s result like(make);\neval result=make();\nreturn;\ndcl-proc make;\ndcl-pi *n like(text) end-pi;\ndcl-s text char(4);\nreturn 'AB';\nend-proc;"); Assert.Equal("AB  ", c.ReadValue("RESULT"));
    }

}
