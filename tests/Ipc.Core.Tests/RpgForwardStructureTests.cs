using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgForwardStructureTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "FORWARDDS", source));
    private static object? Read(RpgRuntimeContext c, string expression) => RpgExpressionParser.Parse(expression).Eval(c);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Forward_copies_keep_declared_initializers_and_independent_runtime_storage(int ccsid)
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified inz;\nN packed(5:2) inz(12.34);\nTEXT char(4) inz('AB');\nend-ds;\ndcl-s BEFORE packed(5:2);\neval BEFORE=COPY.N;\neval ROOT.N=56.78;\neval COPY.N=90.12;\nclear COPY;\nreturn;", ccsid); Assert.Equal(12.34m, c.ReadValue("BEFORE")); Assert.Equal(0m, Read(c, "COPY.N")); Assert.Equal(56.78m, Read(c, "ROOT.N")); Assert.Equal("    ", Read(c, "COPY.TEXT"));
    }

    [Theory]
    [InlineData("", 64L)] [InlineData("inz", 0L)] [InlineData("inz(*likeds)", 7L)]
    public void Forward_LIKEDS_has_distinct_plain_default_and_inherited_initialization(string mode, long expected)
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) " + mode + ";\ndcl-ds ROOT qualified inz;\nN int(3) inz(7);\nend-ds;\nreturn;"); Assert.Equal(expected, Read(c, "COPY.N"));
    }

    [Fact]
    public void Initializer_inheritance_preserves_uninitialized_parent_blanks()
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified;\nA int(3) inz(7);\nB int(3);\nend-ds;\nreturn;"); Assert.Equal(7L, Read(c, "COPY.A")); Assert.Equal(64L, Read(c, "COPY.B"));
    }

    [Fact]
    public void Forward_template_chains_use_constants_at_the_template_definition()
    {
        var c = Run("**free\ndcl-ds A likeds(B) inz(*likeds);\ndcl-ds B likeds(ROOT) inz(*likeds);\ndcl-c INITIAL 7;\ndcl-ds ROOT qualified inz;\nN int(3) inz(INITIAL);\nend-ds;\nreturn;"); Assert.Equal(7L, Read(c, "A.N")); Assert.Equal(7L, Read(c, "B.N"));
    }

    [Fact]
    public void Forward_copies_and_a_later_template_share_exact_structure_ancestry()
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-s N int(3);\ndcl-pr GET int(3);\nP likeds(ROOT) const options(*exact);\nend-pr;\ndcl-ds ROOT qualified inz;\nX int(3) inz(7);\nend-ds;\neval N=GET(COPY);\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3);\nP likeds(ROOT) const;\nend-pi;\nreturn P.X;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Forward_LIKEDS_return_and_parameter_templates_support_RTNPARM()
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT);\ndcl-pr MAKE likeds(ROOT) rtnparm end-pr;\ndcl-ds ROOT qualified inz;\nX int(3) inz(7);\nend-ds;\neval COPY=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(ROOT) rtnparm end-pi;\nreturn ROOT;\nend-proc;"); Assert.Equal(7L, Read(c, "COPY.X"));
    }

    [Fact]
    public void Later_local_templates_shadow_global_templates_and_constants()
    {
        var c = Run("**free\ndcl-c INITIAL 4;\ndcl-ds ROOT qualified;\nX char(4);\nend-ds;\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-c INITIAL 7;\ndcl-ds ROOT qualified inz;\nX int(3) inz(INITIAL);\nend-ds;\nreturn COPY.X;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Theory]
    [InlineData("dcl-ds A likeds(B);\ndcl-ds B likeds(A);")]
    [InlineData("dcl-ds A likeds(B) inz(*likeds);\ndcl-ds B likeds(A) inz(*likeds);")]
    public void Cyclic_structure_templates_reject_with_source_diagnostics(string body)
    {
        var e = Assert.Throws<RpgCompileException>(() => Run("**free\n" + body)); Assert.Contains("Cyclic", e.Message); Assert.NotNull(e.Location);
    }
    [Fact]
    public void Copies_do_not_inherit_root_DIM_but_keep_subfield_arrays_fillers_and_overlays()
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) dim(2) inz(*likeds);\ndcl-ds ROOT qualified dim(3);\nA int(3) dim(2) inz(7);\n*N char(1) inz('X');\nB char(1) ccsid(*hex) overlay(A);\nend-ds;\nreturn;"); Assert.Equal(2, Read(c, "%elem(COPY)")); Assert.Equal(3, Read(c, "%elem(ROOT)")); Assert.Equal(7L, Read(c, "COPY(2).A(2)")); Assert.Equal(3, Read(c, "%size(COPY)")); Assert.Equal("07", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(c, "COPY(1).B(1)")).ToArray()));
    }

    [Fact]
    public void Copies_of_occurrence_templates_do_not_inherit_OCCURS()
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT occurs(3) inz;\nX int(3) inz(7);\nend-ds;\nreturn;"); Assert.False(c.Program.FindDs("COPY")!.IsMultipleOccurrence); Assert.Equal(3, Read(c, "%elem(ROOT)")); Assert.Equal(7L, Read(c, "COPY.X"));
    }

    [Fact]
    public void Forward_PI_structure_templates_are_available_without_copying_runtime_input()
    {
        var c = Run("**free\ndcl-ds ROOT qualified inz;\nX int(3) inz(7);\nend-ds;\ndcl-s N int(3);\neval N=GET(ROOT);\nreturn;\ndcl-proc GET;\ndcl-ds COPY likeds(P) inz;\ndcl-ds SECOND likeds(COPY) inz;\ndcl-pi *n int(3);\nP likeds(ROOT) const;\nend-pi;\nreturn SECOND.X+P.X;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Local_scalar_names_prevent_falling_back_to_a_global_structure_template()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds ROOT qualified;\nX char(4);\nend-ds;\ncallp GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n end-pi;\ndcl-ds COPY likeds(ROOT);\ndcl-s ROOT int(3);\nreturn;\nend-proc;"));

    [Fact]
    public void Global_copy_layouts_do_not_bind_to_later_local_shadowed_templates()
    {
        var c = Run("**free\ndcl-ds ROOT qualified;\nX char(4);\nend-ds;\ndcl-ds GCOPY likeds(ROOT);\ndcl-s N int(3);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) end-pi;\ndcl-ds LCOPY likeds(ROOT) inz;\ndcl-ds ROOT qualified;\nX char(8);\nend-ds;\ndcl-c WIDTH %size(GCOPY);\nreturn WIDTH+%size(LCOPY);\nend-proc;"); Assert.Equal(12L, c.ReadValue("N")); Assert.Equal(4, Read(c, "%size(GCOPY)"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_forward_copies_inherit_initializers_at_both_column_origins(int shift)
    {
        var c = Run(D("COPY", "DS", keywords: "LIKEDS(ROOT) INZ(*LIKEDS)", shift: shift) + D("ROOT", "DS", keywords: "QUALIFIED INZ", shift: shift) + D("X", length: "3", type: 'I', decimals: "0", keywords: "INZ(7)", shift: shift) + RpgFixedCalculationTests.C("RETURN")); Assert.Equal(7L, Read(c, "COPY.X"));
    }

    [Theory]
    [InlineData("MISSING")] [InlineData("NUMBER")]
    public void Missing_or_non_structure_forward_templates_reject(string name)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds COPY likeds(" + name + ");\ndcl-s NUMBER int(3);"));

    [Fact]
    public void Invalid_initializer_values_are_rejected_without_evaluating_runtime_fields()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-s VALUE int(3) inz(7);\ndcl-ds ROOT qualified inz;\nX int(3) inz(VALUE);\nend-ds;"));

    [Fact]
    public void Long_template_chains_are_bounded_before_native_stack_exhaustion()
    {
        var source = "**free\n"+string.Join('\n',Enumerable.Range(0,80).Select(n=>"dcl-ds A"+n+" likeds(A"+(n+1)+") inz(*likeds);"))+"\ndcl-ds A80 qualified;\nX int(3);\nend-ds;";
        Assert.Contains("64 levels", Assert.Throws<RpgCompileException>(()=>Run(source)).Message);
    }

    [Theory]
    [InlineData("COPY", "ROOT")] [InlineData("copy", "rOoT")]
    public void Forward_template_lookup_uses_normal_case_rules(string copy, string root)
    {
        var c = Run("**free\ndcl-ds "+copy+" likeds("+root+") inz(*likeds);\ndcl-ds "+root+" qualified inz;\nx int(3) inz(7);\nend-ds;\nreturn;"); Assert.Equal(7L, Read(c, "COPY.X"));
    }

    [Fact]
    public void Local_forward_structure_return_templates_are_available_before_the_definition()
    {
        var c = Run("**free\ndcl-ds OUTPUT likeds(ROOT);\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\neval OUTPUT=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(LOCAL) rtnparm end-pi;\ndcl-ds LOCAL qualified inz;\nX int(3) inz(7);\nend-ds;\nreturn LOCAL;\nend-proc;"); Assert.Equal(7L, Read(c, "OUTPUT.X"));
    }

    [Fact]
    public void Equal_layouts_from_unrelated_forward_templates_still_fail_EXACT()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds COPY likeds(OTHER);\ndcl-pr GET;\nP likeds(ROOT) const options(*exact);\nend-pr;\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ndcl-ds OTHER qualified;\nX int(3);\nend-ds;\ncallp GET(COPY);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) const;\nend-pi;\nreturn;\nend-proc;"));

    [Theory]
    [InlineData("dcl-ds COPY likeds(ROOT) dim(2);\ndcl-ds ROOT qualified len(1048576);\nX char(1);\nend-ds;")]
    [InlineData("dcl-pr GET;\nP likeds(ROOT) dim(2);\nend-pr;\ndcl-ds ROOT qualified len(1048576);\nX char(1);\nend-ds;")]
    public void Forward_layouts_retain_the_per_declaration_storage_limit(string source)
        => Assert.Throws<RpgCompileException>(() => Run("**free\n"+source));

    [Fact]
    public void Forward_status_templates_remain_invalid_for_plain_parameter_interfaces()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr GET;\nP likeds(PS);\nend-pr;\ndcl-ds PS psds qualified;\nN *parms;\nend-ds;"));

    [Fact]
    public void Forward_indicator_templates_are_not_admitted_to_plain_parameter_interfaces()
    {
        var e = Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-f SCREEN workstn indds(BANK);\ndcl-pr GET;\nP likeds(BANK);\nend-pr;\ndcl-ds BANK qualified len(99);\nFLAG ind;\nend-ds;")); Assert.Contains("plain layout",e.Message);
    }

}
