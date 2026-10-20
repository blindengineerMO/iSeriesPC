using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgNullParameterTests
{
    private static RpgRuntimeContext Run(string text, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "NULLPARM", "**free\nctl-opt alwnull(*usrctl);\n" + text));
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Scalar_parameters_share_live_automatic_and_named_null_indicators(int ccsid)
    {
        var context = Run("dcl-s N int(10) nullind inz(7);\ndcl-s M int(10) nullind(FLAG) inz(8);\ndcl-s FLAG ind inz(*on);\neval %nullind(N)=*on;\ncallp CHANGE(N);\ncallp CHANGE(M);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\nif %nullind(P);\neval P=P+1;\neval %nullind(P)=*off;\nendif;\nend-proc;", ccsid);
        Assert.Equal(8L, Read(context, "N")); Assert.Equal(9L, Read(context, "M")); Assert.Equal(false, Read(context, "%nullind(N)")); Assert.Equal(false, Read(context, "FLAG"));
    }
    [Fact]
    public void Arrays_and_nested_structures_forward_indicators_through_multiple_frames()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) nullind dim(2);\nTEXT char(2);\nend-ds;\ndcl-ds BOX qualified inz;\nDATA likeds(MODEL) dim(2);\nend-ds;\neval %nullind(BOX.DATA(2).N(2))=*on;\ncallp FIRST(BOX.DATA);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n;\nP likeds(MODEL) dim(2) options(*nullind);\nend-pi;\ncallp SECOND(P(2).N);\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nP int(10) dim(2) options(*nullind);\nend-pi;\nif %nullind(P(2));\neval P(2)=19;\neval %nullind(P(2))=*off;\neval %nullind(P(1))=*on;\nendif;\nend-proc;");
        Assert.Equal(19L, Read(context, "BOX.DATA(2).N(2)")); Assert.Equal(false, Read(context, "%nullind(BOX.DATA(2).N(2))")); Assert.Equal(true, Read(context, "%nullind(BOX.DATA(2).N(1))")); Assert.Equal(false, Read(context, "%nullind(BOX.DATA(1).N(1))"));
    }
    [Fact]
    public void Const_parameters_query_indicators_without_copying_the_caller_map()
    {
        var context = Run("dcl-s N int(10) nullind;\ndcl-s FOUND ind;\neval %nullind(N)=*on;\neval FOUND=ISNULL(N);\nreturn;\ndcl-proc ISNULL;\ndcl-pi *n ind;\nP int(10) const options(*nullind);\nend-pi;\nreturn %nullind(P);\nend-proc;"); Assert.Equal(true, Read(context, "FOUND"));
    }
    [Fact]
    public void Nonnullable_scalar_actuals_use_a_scoped_default_off_map()
    {
        var context = Run("dcl-s N int(10) inz(4);\ncallp CHANGE(N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\nif not %nullind(P);\neval P=9;\neval %nullind(P)=*on;\nendif;\nend-proc;"); Assert.Equal(9L, Read(context, "N"));
    }
    [Theory]
    [InlineData("dcl-s N int(10);\ncallp TARGET(1);")]
    [InlineData("dcl-s N int(5);\ncallp TARGET(N);")]
    [InlineData("dcl-s N char(4);\ncallp TARGET(N);")]
    public void Exact_variable_requirements_apply_even_to_const_parameters(string caller)
        => Assert.Throws<RpgCompileException>(() => Run(caller + "\nreturn;\ndcl-proc TARGET;\ndcl-pi *n;\nP int(10) const options(*nullind);\nend-pi;\nend-proc;"));
    [Theory]
    [InlineData("value options(*nullind)")]
    [InlineData("options(*nullind:*exact)")]
    [InlineData("options(*nullind:*varsize)")]
    [InlineData("options(*nullind:*trim)")]
    public void Invalid_null_parameter_modes_reject_before_execution(string mode)
        => Assert.Throws<RpgCompileException>(() => Run("return;\ndcl-proc TARGET;\ndcl-pi *n;\nP int(10) " + mode + ";\nend-pi;\nend-proc;"));
    [Fact]
    public void Strided_overlay_subfields_retain_bounded_data_and_null_aliases()
    {
        var context = Run("dcl-ds MODEL qualified template;\nRAW char(10) dim(2);\nN int(10) overlay(RAW) nullind;\nend-ds;\ndcl-ds DATA likeds(MODEL) inz;\neval %nullind(DATA.N(2))=*on;\ncallp FIRST(DATA);\nreturn;\ndcl-proc FIRST;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\nif %nullind(P.N(2));\ncallp SECOND(P.N(1));\nendif;\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\neval P=19;\neval %nullind(P)=*on;\nend-proc;");
        Assert.Equal(19L, Read(context, "DATA.N(1)")); Assert.Equal(true, Read(context, "%nullind(DATA.N(1))")); Assert.Equal(true, Read(context, "%nullind(DATA.N(2))"));
    }
    [Fact]
    public void Anonymous_padding_fields_do_not_hide_later_null_entries()
    {
        var context = Run("dcl-ds MODEL qualified template;\n*n char(3);\nN int(10) nullind;\nend-ds;\ndcl-ds DATA likeds(MODEL) inz;\ncallp CHANGE(DATA);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\neval %nullind(P.N)=*on;\nend-proc;"); Assert.Equal(true, Read(context, "%nullind(DATA.N)"));
    }
    [Fact]
    public void Opaque_structures_without_subfields_pass_an_empty_map()
    {
        var context = Run("dcl-ds MODEL qualified template len(4) end-ds;\ndcl-ds DATA likeds(MODEL) inz;\ncallp CHANGE(DATA);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\neval %subst(P:1:1)='X';\nend-proc;"); Assert.Equal("X   ", Assert.IsType<Ipc.Core.Work.ProgramBuffer>(Read(context, "DATA")).ToText());
    }

    [Fact]
    public void Omitted_and_unpassed_null_parameters_preserve_presence_guards()
    {
        var context = Run("dcl-s N int(10) nullind;\ndcl-s COUNT int(10);\ncallp CHECK();\ncallp CHECK(*omit);\ncallp CHECK(N);\nreturn;\ndcl-proc CHECK;\ndcl-pi *n;\nP int(10) options(*nullind:*omit:*nopass);\nend-pi;\nif %passed(P) and not %omitted(P);\neval COUNT=COUNT+1;\neval %nullind(P)=*on;\nendif;\nend-proc;"); Assert.Equal(1L, Read(context, "COUNT")); Assert.Equal(true, Read(context, "%nullind(N)"));
    }
    [Fact]
    public void Repeated_aliases_share_one_live_null_indicator()
    {
        var context = Run("dcl-s N int(10) nullind;\ncallp CHANGE(N:N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP int(10) options(*nullind);\nQ int(10) options(*nullind);\nend-pi;\neval %nullind(P)=*on;\nif %nullind(Q);\neval Q=19;\nendif;\nend-proc;"); Assert.Equal(19L, Read(context, "N")); Assert.Equal(true, Read(context, "%nullind(N)"));
    }
    [Fact]
    public void Const_does_not_allow_mutating_a_borrowed_null_map()
        => Assert.Throws<RpgCompileException>(() => Run("return;\ndcl-proc TARGET;\ndcl-pi *n;\nP int(10) const options(*nullind);\nend-pi;\neval %nullind(P)=*on;\nend-proc;"));
    [Fact]
    public void Unrelated_structure_types_reject_even_if_their_data_and_null_shapes_match()
        => Assert.Throws<RpgCompileException>(() => Run("dcl-ds A qualified template;\nN int(10) nullind;\nend-ds;\ndcl-ds B qualified template;\nN int(10) nullind;\nend-ds;\ndcl-ds DATA likeds(B) inz;\ncallp TARGET(DATA);\nreturn;\ndcl-proc TARGET;\ndcl-pi *n;\nP likeds(A) options(*nullind);\nend-pi;\nend-proc;"));

}

public sealed class RpgNamedNullParameterTests
{
    private static RpgRuntimeContext Run(string source) => new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "NAMEDPARM", "**free\nctl-opt alwnull(*usrctl);\n" + source));
    private static object? Read(RpgRuntimeContext context, string source) => RpgExpressionParser.Parse(source).Eval(context);
    [Fact]
    public void Separate_indicator_parameters_alias_the_supplied_flags_with_different_PR_PI_names()
    {
        var context = Run("dcl-pr CHANGE;\nVALUE int(10) nullind(ISNULL);\nISNULL ind;\nend-pr;\ndcl-s N int(10) inz(7);\ndcl-s FLAG ind inz(*on);\ncallp CHANGE(N:FLAG);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP int(10) nullind(F);\nF ind;\nend-pi;\nif %nullind(P);\neval P=P+1;\neval %nullind(P)=*off;\nendif;\nend-proc;");
        Assert.Equal(8L, Read(context, "N")); Assert.Equal(false, Read(context, "FLAG"));
    }
    [Fact]
    public void Named_parameter_array_relationships_share_each_indicator_element()
    {
        var context = Run("dcl-s N int(10) dim(2);\ndcl-s FLAGS ind dim(2);\neval FLAGS(2)=*on;\ncallp CHANGE(N:FLAGS);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP int(10) dim(2) nullind(F);\nF ind dim(2);\nend-pi;\nif %nullind(P(2));\neval P(2)=19;\neval %nullind(P(2))=*off;\nendif;\nend-proc;"); Assert.Equal(19L, Read(context, "N(2)")); Assert.Equal(false, Read(context, "FLAGS(2)"));
    }
    [Theory]
    [InlineData("P int(10) nullind(F);\nF char(1);")]
    [InlineData("P int(10) nullind(F);\nF ind options(*nullind);")]
    [InlineData("P int(10) nullind(F);\nQ int(10) nullind(F);\nF ind;")]
    [InlineData("P int(10) nullind(F);")]
    [InlineData("P int(10) nullind(F) options(*nullind);\nF ind;")]
    public void Named_formal_contracts_require_one_matching_indicator_parameter(string parameters)
        => Assert.Throws<RpgCompileException>(() => Run("return;\ndcl-proc TARGET;\ndcl-pi *n;\n" + parameters + "\nend-pi;\nend-proc;"));
    [Theory]
    [InlineData("P int(10) nullind(F);\nF ind;\nOTHER ind;", "P int(10) nullind(OTHER);\nF ind;\nOTHER ind;")]
    [InlineData("P int(10) options(*nullind);", "P int(10);")]
    public void Prototype_and_interface_null_modes_and_association_positions_must_match(string prototype, string pi)
        => Assert.Throws<RpgCompileException>(() => Run("dcl-pr TARGET;\n" + prototype + "\nend-pr;\nreturn;\ndcl-proc TARGET;\ndcl-pi *n;\n" + pi + "\nend-pi;\nend-proc;"));
    [Theory]
    [InlineData("*no")] [InlineData("*inputonly")]
    public void Null_map_parameters_require_user_control_during_binding(string mode)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NULLMODE", "**free\nctl-opt alwnull(" + mode + ");\nreturn;\ndcl-proc TARGET;\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\nend-proc;"));
}
