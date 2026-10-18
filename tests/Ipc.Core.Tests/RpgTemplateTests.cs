using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgTemplateTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "TEMPLATES", source));
    private static object? Read(RpgRuntimeContext c, string expression) => RpgExpressionParser.Parse(expression).Eval(c);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Structure_templates_define_inherited_defaults_and_exact_types_without_values(int ccsid)
    {
        var c = Run("**free\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified template inz;\nN packed(5:2) inz(12.34);\nTEXT char(4) inz('AB');\nend-ds;\ndcl-s OUT packed(5:2);\neval OUT=GET(COPY);\nreturn;\ndcl-proc GET;\ndcl-pi *n packed(5:2);\nP likeds(ROOT) const options(*exact);\nend-pi;\nreturn P.N;\nend-proc;", ccsid);
        Assert.Equal(12.34m, c.ReadValue("OUT")); Assert.Equal("AB  ", Read(c, "COPY.TEXT")); Assert.True(c.Program.FindDs("ROOT")!.IsTemplate); Assert.False(c.Program.FindDs("COPY")!.IsTemplate);
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(()=>Read(c,"ROOT")).StatusCode);
    }

    [Fact]
    public void Standalone_templates_supply_types_without_inheriting_DIM_order_or_initialization()
    {
        var c = Run("**free\ndcl-s ROOT packed(7:2) dim(3) descend template;\ndcl-s N like(ROOT) inz(12.34);\ndcl-c LEN %len(ROOT);\ndcl-c SCALE %decpos(ROOT);\ndcl-s BYTES int(10);\neval BYTES=%size(ROOT:*all)+%elem(ROOT);\nreturn;"); Assert.Equal(12.34m,c.ReadValue("N")); Assert.Equal(7m,c.ReadValue("LEN")); Assert.Equal(2m,c.ReadValue("SCALE")); Assert.Equal(15L,c.ReadValue("BYTES")); Assert.False(c.Program.FindField("N")!.IsTemplate); Assert.False(c.Program.FindField("N")!.IsArray);
    }

    [Fact]
    public void Large_array_templates_do_not_consume_runtime_storage_budgets()
    {
        var c = Run("**free\ndcl-s ROOT char(32767) dim(32767) template;\ndcl-s A like(ROOT) inz('AB');\ndcl-s N int(20);\neval N=%size(ROOT:*all);\nreturn;"); Assert.Equal(1073676289L,c.ReadValue("N")); Assert.StartsWith("AB",Assert.IsType<string>(c.ReadValue("A")));
    }

    [Fact]
    public void Structure_array_templates_can_describe_more_than_one_MiB_without_allocating_it()
    {
        var c = Run("**free\ndcl-ds ROOT qualified len(1048576) dim(32767) template;\nX char(1);\nend-ds;\ndcl-s N int(20);\neval N=%size(ROOT:*all);\nreturn;"); Assert.Equal(34358689792L,c.ReadValue("N"));
    }

    [Theory]
    [InlineData("eval N=ROOT;")] [InlineData("eval ROOT=7;")]
    [InlineData("clear ROOT;")] [InlineData("callp GET(ROOT);")]
    [InlineData("eval N=%len(ROOT);")] [InlineData("eval N=%decpos(ROOT);")]
    [InlineData("eval N=%abs(ROOT);")]
    [InlineData("eval N=%size(%len(ROOT));")]
    public void Template_data_access_rejects_during_compilation(string statement)
        => Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-s ROOT int(3) template;\ndcl-s N int(3);\n"+statement));

    [Theory]
    [InlineData("eval N=ROOT.X;")] [InlineData("eval ROOT.X=7;")]
    [InlineData("clear ROOT;")] [InlineData("eval N=%len(ROOT.X);")]
    public void Qualified_template_members_have_no_data_value(string statement)
        => Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-ds ROOT qualified template;\nX int(3);\nend-ds;\ndcl-s N int(3);\n"+statement));

    [Fact]
    public void Unqualified_template_members_are_metadata_only()
    {
        Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-ds ROOT template;\nX int(3);\nend-ds;\ndcl-s N int(3);\neval N=X;"));
        var c=Run("**free\ndcl-ds ROOT template;\nX packed(5:2);\nend-ds;\ndcl-s N like(X) inz(12.34);\ndcl-c SCALE %decpos(X);\nreturn;"); Assert.Equal(12.34m,c.ReadValue("N")); Assert.Equal(2m,c.ReadValue("SCALE"));
    }

    [Fact]
    public void Allowed_layout_inquiries_compose_as_ordinary_numeric_expressions()
    {
        var c=Run("**free\ndcl-s ROOT char(4) dim(3) template;\ndcl-s N int(10);\ndcl-s TEXT char(20);\neval N=%size(ROOT(2)) + %elem(ROOT);\neval TEXT=%char(%size(ROOT:*all));\nreturn;"); Assert.Equal(7L,c.ReadValue("N")); Assert.Equal("12",Assert.IsType<string>(c.ReadValue("TEXT")).Trim());
    }

    [Fact]
    public void Template_names_in_literals_special_values_labels_and_other_member_names_are_not_data_references()
    {
        var c=Run("**free\ndcl-s ROOT int(3) template;\ndcl-s X int(3) template;\ndcl-s E3 int(3) template;\ndcl-s YES ind template;\ndcl-s DATE date template;\ndcl-s IN01 ind template;\ndcl-s N float(8);\ndcl-s BYTES char(2) ccsid(*hex);\ndcl-ds OTHER qualified inz;\nROOT int(3) inz(7);\nend-ds;\ndcl-s TEXT char(10);\neval N=1E3;\neval BYTES=X'0102';\neval TEXT=%char(*YES);\neval TEXT=%char(*DATE);\neval TEXT=%char(*IN01);\neval TEXT='ROOT';\ngoto ROOT;\ntag ROOT;\neval OTHER.ROOT=8;\neval OTHER . ROOT=9;\nreturn;"); Assert.Equal(9L,Read(c,"OTHER.ROOT")); Assert.Equal(1000d,c.ReadValue("N"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_template_declarations_support_both_native_column_origins(int shift)
    {
        var c=Run(D("ROOT","DS",keywords:"QUALIFIED TEMPLATE INZ",shift:shift)+D("X",length:"3",type:'I',decimals:"0",keywords:"INZ(7)",shift:shift)+D("COPY","DS",keywords:"LIKEDS(ROOT) INZ(*LIKEDS)",shift:shift)+RpgFixedCalculationTests.C("RETURN")); Assert.Equal(7L,Read(c,"COPY.X"));
    }

    [Theory]
    [InlineData("dcl-s ROOT char(4) template(1);")]
    [InlineData("dcl-s ROOT char(4) template template;")]
    [InlineData("dcl-s ROOT char(4) template dim(2) ctdata;")]
    [InlineData("dcl-ds ROOT qualified;\nX int(3) template;\nend-ds;")]
    public void Invalid_template_locations_and_options_reject(string source)
        => Assert.Throws<RpgCompileException>(()=>Run("**free\n"+source));
    [Fact]
    public void Procedure_local_templates_do_not_consume_invocation_memory_and_shadow_global_types()
    {
        var c=Run("**free\ndcl-s ROOT char(4) template;\ndcl-s N int(20);\neval N=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(20) end-pi;\ndcl-ds ROOT qualified len(1048576) dim(32767) template;\nX char(1);\nend-ds;\nreturn %size(ROOT:*all);\nend-proc;"); Assert.Equal(34358689792L,c.ReadValue("N"));
    }

    [Theory]
    [InlineData("char(4)")] [InlineData("float(8)")]
    public void DECPOS_rejects_character_and_floating_template_layouts(string type)
        => Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-s ROOT "+type+" template;\ndcl-c SCALE %decpos(ROOT);"));

    [Fact]
    public void Multiple_occurrence_templates_expose_layout_without_occurrence_storage()
    {
        var c=Run("**free\ndcl-ds ROOT qualified occurs(3) template;\nX int(3);\nend-ds;\ndcl-s N int(3);\neval N=%size(ROOT:*all)+%elem(ROOT);\nreturn;"); Assert.Equal(6L,c.ReadValue("N"));
    }

    [Theory]
    [InlineData("eval N=N*ROOT;")]
    [InlineData("if *off;\neval N=ROOT;\nendif;")]
    [InlineData("eval N=%size(ROOT(N));")]
    public void Template_access_cannot_hide_in_multiplication_unreachable_code_or_runtime_indices(string statement)
        => Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-s ROOT int(3) dim(2) template;\ndcl-s N int(3) inz(1);\n"+statement));

    [Fact]
    public void Metadata_template_count_is_bounded_without_a_storage_budget()
    {
        var source="**free\n"+string.Join('\n',Enumerable.Range(1,256).Select(n=>$"dcl-s T{n} char(32767) dim(32767) template;"));
        Run(source+"\nreturn;"); Assert.Throws<RpgCompileException>(()=>Run(source+"\ndcl-s EXTRA char(1) template;"));
    }

    [Fact]
    public void Direct_runtime_access_and_borrowing_cannot_create_template_storage()
    {
        var c=Run("**free\ndcl-s ROOT char(4) dim(3) template;\ndcl-s N int(3);\neval N=%size(ROOT(+2));\nreturn;");
        Assert.Equal(4L,c.ReadValue("N"));
        foreach(var access in new Action[] { ()=>c.ReadValue("ROOT"), ()=>c.WriteValue("ROOT","AB"), ()=>c.ReadArrayValue("ROOT",1), ()=>c.WriteArrayValue("ROOT",1,"AB"), ()=>c.ClearValue("ROOT"), ()=>Read(c,"%len(ROOT)"), ()=>Read(c,"%subst(ROOT(1):1:2)") })
            Assert.Equal(105,Assert.Throws<RpgRuntimeException>(access).StatusCode);
    }

    [Theory]
    [InlineData("*ENTRY")] [InlineData("ARGS")]
    public void Traditional_parameter_lists_cannot_bind_template_storage(string list)
        => Assert.Contains("TEMPLATE",Assert.Throws<RpgCompileException>(()=>Run(D("ROOT","S",length:"3",type:'I',decimals:"0",keywords:"TEMPLATE")+RpgFixedCalculationTests.C("PLIST",factor1:list)+RpgFixedCalculationTests.C("PARM",result:"ROOT"))).Message);

    [Fact]
    public void Traditional_key_lists_cannot_read_template_fields()
        => Assert.Contains("TEMPLATE",Assert.Throws<RpgCompileException>(()=>Run(D("ROOT","S",length:"3",type:'I',decimals:"0",keywords:"TEMPLATE")+RpgFixedCalculationTests.C("KLIST",factor1:"KEYS")+RpgFixedCalculationTests.C("KFLD",result:"ROOT"))).Message);

    [Fact]
    public void Indicator_bindings_require_runtime_storage()
        => Assert.Contains("INDDS",Assert.Throws<RpgCompileException>(()=>Run("**free\ndcl-f SCREEN workstn indds(ROOT);\ndcl-ds ROOT qualified template;\nX ind dim(99);\nend-ds;")).Message);

}
