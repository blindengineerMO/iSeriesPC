using System.Text.Json.Nodes;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgNullTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "NULLS", "**free\nctl-opt alwnull(*usrctl);\n" + source));
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Indicators_are_independent_of_scalar_and_array_data(int ccsid)
    {
        var context = Run("dcl-s N int(10) nullind inz(7);\ndcl-s A packed(5:1) dim(2) nullind inz(1);\neval %nullind(N)=*on;\neval N=8;\neval %nullind(A(2))=%nullind(N);\neval A(2)=N;\nreturn;", ccsid);
        Assert.Equal(8L, Read(context, "N")); Assert.Equal(8m, Read(context, "A(2)")); Assert.Equal(true, Read(context, "%nullind(N)")); Assert.Equal(true, Read(context, "%nullind(A(2))")); Assert.Equal(false, Read(context, "%nullind(A(1))"));
    }
    [Fact]
    public void LIKEDS_preserves_capability_and_nested_indicators_but_assignment_does_not_copy_flags()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) nullind;\nend-ds;\ndcl-ds A likeds(MODEL) inz;\ndcl-ds B qualified inz;\nITEM likeds(MODEL) dim(2);\nend-ds;\neval A.N=7;\neval %nullind(A.N)=*on;\neval B.ITEM(2)=A;\neval %nullind(B.ITEM(1).N)=*on;\nclear B.ITEM(1);\nreturn;");
        Assert.Equal(7L, Read(context, "B.ITEM(2).N")); Assert.Equal(false, Read(context, "%nullind(B.ITEM(2).N)")); Assert.Equal(true, Read(context, "%nullind(B.ITEM(1).N)"));
    }
    [Fact]
    public void Global_and_static_flags_persist_while_automatic_procedure_flags_start_off()
    {
        var context = Run("dcl-s GLOBAL int(10) nullind;\ndcl-s A ind;\ndcl-s B ind;\ndcl-s C ind;\ndcl-s D ind;\neval A=AUTO_FLAG();\neval B=AUTO_FLAG();\neval C=STATIC_FLAG();\neval D=STATIC_FLAG();\nreturn;\ndcl-proc AUTO_FLAG;\ndcl-pi *n ind end-pi;\ndcl-s LOCAL int(10) nullind;\ndcl-s WAS ind;\neval WAS=%nullind(LOCAL);\neval %nullind(LOCAL)=*on;\neval %nullind(GLOBAL)=*on;\nreturn WAS;\nend-proc;\ndcl-proc STATIC_FLAG;\ndcl-pi *n ind end-pi;\ndcl-s LOCAL int(10) nullind static;\ndcl-s WAS ind;\neval WAS=%nullind(LOCAL);\neval %nullind(LOCAL)=*on;\nreturn WAS;\nend-proc;");
        Assert.Equal(false, Read(context, "A")); Assert.Equal(false, Read(context, "B")); Assert.Equal(false, Read(context, "C")); Assert.Equal(true, Read(context, "D")); Assert.Equal(true, Read(context, "%nullind(GLOBAL)"));
    }
    [Fact]
    public void Ordinary_aggregate_parameters_borrow_data_without_borrowing_null_flags()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) nullind;\nend-ds;\ndcl-ds BUFFER likeds(MODEL) inz;\neval %nullind(BUFFER.N)=*on;\ncallp CHANGE(BUFFER);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(MODEL);\nend-pi;\nif not %nullind(P.N);\neval P.N=12;\nendif;\neval %nullind(P.N)=*on;\nend-proc;");
        Assert.Equal(12L, Read(context, "BUFFER.N")); Assert.Equal(true, Read(context, "%nullind(BUFFER.N)"));
    }
    [Fact]
    public void Fixed_control_and_field_keywords_support_native_null_indicators()
    {
        var source = "     H ALWNULL(*USRCTL)\n" + RpgFixedStructureTests.D("N", "S", length: "10", type: 'I', decimals: "0", keywords: "NULLIND INZ(7)") + "\n" + RpgFixedCalculationTests.C("EVAL", expression: "%NULLIND(N)=*ON") + "\n" + RpgFixedCalculationTests.C("RETURN");
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "NULLS", source)); Assert.Equal(true, Read(context, "%nullind(N)")); Assert.Equal(7L, Read(context, "N"));
    }
    [Fact]
    public void External_nullable_metadata_restores_without_a_live_description_and_rejects_old_versions()
    {
        var source = "**free\nctl-opt alwnull(*usrctl);\ndcl-ds BUFFER qualified extname('DESC') inz;\nend-ds;\neval %nullind(BUFFER.TEXT)=*on;\nreturn;";
        var program = RpgCompiler.Compile("QGPL", "NULLS", source, externalResolver: _ => new("QGPL/DESC", "ROW", "DISK", 4, [new("TEXT", RpgFieldKind.Character, 4, 0, 4, 1, NullCapable: true)]));
        var json = RpgExternalBindings.Serialize(program); var saved = RpgExternalBindings.Restore(source, json); Assert.Equal(2, saved.Version);
        var restored = RpgCompiler.Compile("QGPL", "NULLS", source, externalResolver: saved.Resolve); saved.Verify(restored);
        var context = new RpgInterpreter(new()).Run(restored); Assert.Equal(true, Read(context, "%nullind(BUFFER.TEXT)"));
        var node = JsonNode.Parse(json)!; node["version"] = 1; Assert.Throws<InvalidDataException>(() => RpgExternalBindings.Restore(source, node.ToJsonString()));
        node = JsonNode.Parse(json)!; node["descriptions"]![0]!["layout"]!["device"] = "WORKSTN"; Assert.Throws<InvalidDataException>(() => RpgExternalBindings.Restore(source, node.ToJsonString()));
    }
    [Theory]
    [InlineData("dcl-s N int(10);\neval %nullind(N)=*on;")]
    [InlineData("dcl-s N int(10) nullind;\ndcl-s COPY like(N);\neval %nullind(COPY)=*on;")]
    [InlineData("dcl-s N int(10) dim(2) nullind;\neval %nullind(N)=*on;")]
    [InlineData("dcl-s N int(10) nullind(OTHER);")]
    [InlineData("dcl-s N int(10) nullind;\nctl-opt alwnull(*no);")]
    public void Unsupported_or_invalid_null_contracts_reject_before_execution(string source) => Assert.Throws<RpgCompileException>(() => Run(source));
    [Theory]
    [InlineData("*no")] [InlineData("*inputonly")] [InlineData("*unknown")]
    public void Explicit_null_fields_require_user_control(string mode) => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NULLS", "**free\nctl-opt alwnull(" + mode + ");\ndcl-s N int(10) nullind;"));
}
