using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgNullAssociationTests
{
    private static RpgProgram Compile(string text, Func<RpgExternalRequest, RpgExternalLayout>? resolver = null) => RpgCompiler.Compile("QGPL", "NULLMAPS", "**free\nctl-opt alwnull(*usrctl);\n" + text, externalResolver: resolver);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Named_scalar_and_array_flags_share_live_indicator_storage(int ccsid)
    {
        var context = Run("dcl-s N int(10) nullind(FLAG) inz(7);\ndcl-s A packed(5:1) dim(2) nullind(FLAGS) inz(2);\ndcl-s FLAG ind;\ndcl-s FLAGS ind dim(2);\neval FLAG=*on;\neval %nullind(A(2))=*on;\neval N=9;\neval A(2)=N;\nreturn;", ccsid);
        Assert.Equal(true, Read(context, "%nullind(N)")); Assert.Equal(true, Read(context, "FLAGS(2)")); Assert.Equal(false, Read(context, "%nullind(A(1))")); Assert.Equal(9L, Read(context, "N")); Assert.Equal(9m, Read(context, "A(2)"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEDS_and_nested_arrays_inherit_sibling_relationships(int ccsid)
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) dim(2) nullind(FLAGS);\nFLAGS ind dim(2);\nend-ds;\ndcl-ds BOX qualified inz;\nITEM likeds(MODEL) dim(2);\nend-ds;\neval %nullind(BOX.ITEM(2).N(2))=*on;\neval BOX.ITEM(2).FLAGS(1)=*on;\nreturn;", ccsid);
        Assert.Equal(true, Read(context, "BOX.ITEM(2).FLAGS(2)")); Assert.Equal(true, Read(context, "%nullind(BOX.ITEM(2).N(1))")); Assert.Equal(false, Read(context, "%nullind(BOX.ITEM(1).N(2))"));
    }
    [Theory]
    [InlineData("dcl-s N int(10) nullind(MISSING);")]
    [InlineData("dcl-s N int(10) nullind(FLAG);\ndcl-s FLAG char(1);")]
    [InlineData("dcl-s N int(10) nullind(FLAG);\ndcl-s FLAG ind nullind;")]
    [InlineData("dcl-s N int(10) nullind(FLAG) dim(2);\ndcl-s FLAG ind;")]
    [InlineData("dcl-s N int(10) nullind(FLAG);\ndcl-s M int(10) nullind(FLAG);\ndcl-s FLAG ind;")]
    [InlineData("dcl-ds BOX qualified;\nN int(10) nullind(FLAG);\nend-ds;\ndcl-s FLAG ind;")]
    [InlineData("dcl-ds BOX qualified nullind(FLAGS);\nN int(10);\nend-ds;\ndcl-s FLAGS ind;")]
    public void Invalid_named_contracts_reject_before_runtime(string declarations) => Assert.Throws<RpgCompileException>(() => Compile(declarations));
    [Fact]
    public void Named_flags_preserve_static_storage_and_global_scope()
    {
        var context = Run("dcl-s GLOBAL int(10) nullind(GFLAG);\ndcl-s GFLAG ind;\ndcl-s FIRST ind;\ndcl-s SECOND ind;\neval FIRST=SEEN();\neval SECOND=SEEN();\nreturn;\ndcl-proc SEEN;\ndcl-pi *n ind end-pi;\ndcl-s LOCAL int(10) nullind(FLAG) static;\ndcl-s FLAG ind static;\ndcl-s WAS ind;\neval WAS=%nullind(LOCAL);\neval %nullind(LOCAL)=*on;\neval %nullind(GLOBAL)=*on;\nreturn WAS;\nend-proc;");
        Assert.Equal(false, Read(context, "FIRST")); Assert.Equal(true, Read(context, "SECOND")); Assert.Equal(true, Read(context, "GFLAG"));
    }
    [Theory]
    [InlineData(":*null")] [InlineData(":*input:*null")] [InlineData(":*null:*input")]
    public void External_null_extracts_have_one_indicator_per_original_field(string extract)
    {
        var program = Compile("dcl-ds FLAGS qualified extname('DESC'" + extract + ") inz;\nend-ds;\nreturn;", _ => new("QGPL/DESC", "ROW", "DISK", 12,
            [new("N", RpgFieldKind.Integer, 10, 0, 4, 1), new("TEXT", RpgFieldKind.Character, 8, 0, 8, 5, NullCapable: true)]));
        var flags = program.DataStructures.Single(); Assert.Equal(2, flags.Length); Assert.True(flags.RecordOrigin!.NullMap); Assert.All(flags.Elements, e => { Assert.Equal(RpgFieldKind.Indicator, e.Kind); Assert.Equal(1, e.StorageLength); Assert.False(e.NullCapable); });
        var context = new RpgInterpreter(new()).Run(program); Assert.Equal(false, Read(context, "FLAGS.TEXT"));
    }
    [Fact]
    public void Named_subfield_flags_are_ordinary_data_when_an_aggregate_is_borrowed_or_copied()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) nullind(FLAG);\nFLAG ind;\nend-ds;\ndcl-ds DATA likeds(MODEL) inz;\ndcl-ds COPY likeds(MODEL) inz;\ncallp CHANGE(DATA);\neval COPY=DATA;\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(MODEL);\nend-pi;\neval %nullind(P.N)=*on;\nend-proc;");
        Assert.Equal(true, Read(context, "DATA.FLAG")); Assert.Equal(true, Read(context, "%nullind(COPY.N)"));
    }
    [Fact]
    public void Named_array_indices_are_evaluated_once()
    {
        var context = Run("dcl-s N int(10) dim(2) nullind(FLAGS);\ndcl-s FLAGS ind dim(2);\ndcl-s COUNT int(10);\neval %nullind(N(NEXT()))=*on;\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn 2;\nend-proc;");
        Assert.Equal(1L, Read(context, "COUNT")); Assert.Equal(true, Read(context, "FLAGS(2)"));
    }
    [Fact]
    public void A_standalone_field_can_use_an_unqualified_indicator_subfield()
    {
        var context = Run("dcl-s N int(10) nullind(FLAG);\ndcl-ds FLAGS inz;\nFLAG ind;\nend-ds;\neval %nullind(N)=*on;\nreturn;"); Assert.Equal(true, Read(context, "FLAG"));
    }
    [Theory]
    [InlineData("dcl-s FLAG ind;\nreturn;\ndcl-proc RUN;\ndcl-pi *n end-pi;\ndcl-s N int(10) nullind(FLAG);\nend-proc;")]
    [InlineData("return;\ndcl-proc RUN;\ndcl-pi *n end-pi;\ndcl-s N int(10) nullind(FLAG) static;\ndcl-s FLAG ind;\nend-proc;")]
    [InlineData("dcl-s N int(10) template nullind(MISSING);")]
    [InlineData("dcl-s N int(10) nullind(FLAG);\ndcl-ds FLAGS inz;\nN2 int(10) nullind(FLAG);\nFLAG ind;\nend-ds;")]
    public void Named_associations_reject_cross_scope_storage_and_duplicate_subfield_ownership(string source) => Assert.Throws<RpgCompileException>(() => Compile(source));
    [Theory]
    [InlineData("eval P.FLAG=*on;")] [InlineData("eval %nullind(P.N)=*on;")]
    public void Const_borrowed_fields_protect_named_indicator_storage(string statement) => Assert.Throws<RpgCompileException>(() => Compile("dcl-ds MODEL qualified template;\nN int(10) nullind(FLAG);\nFLAG ind;\nend-ds;\nreturn;\ndcl-proc RUN;\ndcl-pi *n;\nP likeds(MODEL) const;\nend-pi;\n" + statement + "\nend-proc;"));
    [Fact]
    public void Null_extract_buffers_round_trip_as_native_aggregate_parameters_and_returns()
    {
        var program = Compile("dcl-ds MODEL qualified extname('DESC':*null) template end-ds;\ndcl-ds SOURCE likeds(MODEL) inz;\ndcl-ds RESULT likeds(MODEL) inz;\neval SOURCE.TEXT=*on;\neval RESULT=COPY(SOURCE);\nreturn;\ndcl-proc COPY;\ndcl-pi *n likeds(MODEL);\nP likeds(MODEL) const;\nend-pi;\nreturn P;\nend-proc;", _ => new("QGPL/DESC", "ROW", "DISK", 12, [new("N", RpgFieldKind.Integer, 10, 0, 4, 1), new("TEXT", RpgFieldKind.Character, 8, 0, 8, 5)]));
        var context = new RpgInterpreter(new()).Run(program); Assert.Equal(true, Read(context, "RESULT.TEXT")); Assert.Equal(2, Assert.IsType<ProgramBuffer>(Read(context, "RESULT")).Length);
    }
    [Fact]
    public void Saved_descriptions_reconstruct_data_and_null_map_roles_without_live_resolution()
    {
        var source = "dcl-ds DATA qualified extname('DESC':*all) nullind(FLAGS) inz end-ds;\ndcl-ds FLAGS qualified extname('DESC':*all:*null) inz end-ds;\neval FLAGS.TEXT=*on;\nreturn;";
        var calls = 0; var program = Compile(source, _ => { calls++; return new("QGPL/DESC", "ROW", "DISK", 8, [new("TEXT", RpgFieldKind.Character, 8, 0, 8, 1, NullCapable: true)]); }); Assert.Equal(1, calls);
        var saved = RpgExternalBindings.Restore(program.Source!.Text, RpgExternalBindings.Serialize(program));
        var restored = RpgCompiler.Compile("QGPL", "NULLMAPS", program.Source.Text, externalResolver: saved.Resolve); saved.Verify(restored);
        var context = new RpgInterpreter(new()).Run(restored); Assert.Equal(true, Read(context, "%nullind(DATA.TEXT)")); Assert.False(restored.FindDs("DATA")!.RecordOrigin!.NullMap); Assert.True(restored.FindDs("FLAGS")!.RecordOrigin!.NullMap);
    }
    [Fact]
    public void Exact_parameters_distinguish_null_maps_even_when_the_data_layout_is_all_indicators()
        => Assert.Throws<RpgCompileException>(() => Compile("dcl-ds DATA qualified extname('DESC') template end-ds;\ndcl-ds FLAGS qualified extname('DESC':*null) inz end-ds;\ncallp TAKE(FLAGS);\nreturn;\ndcl-proc TAKE;\ndcl-pi *n;\nP likeds(DATA) options(*exact);\nend-pi;\nend-proc;", _ => new("QGPL/DESC", "ROW", "DISK", 1, [new("FLAG", RpgFieldKind.Indicator, 1, 0, 1, 1)])));
    [Fact]
    public void Traditional_scalar_NULLIND_binds_a_forward_indicator_name()
    {
        var source = "     H ALWNULL(*USRCTL)\n" + RpgFixedStructureTests.D("N", "S", length: "10", type: 'I', decimals: "0", keywords: "NULLIND(FLAG) INZ(7)") + "       dcl-s FLAG ind;\n" + RpgFixedCalculationTests.C("EVAL", expression: "%NULLIND(N)=*ON") + RpgFixedCalculationTests.C("RETURN");
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "NULLMAPS", source)); Assert.Equal(true, Read(context, "FLAG")); Assert.Equal(7L, Read(context, "N"));
    }
}
