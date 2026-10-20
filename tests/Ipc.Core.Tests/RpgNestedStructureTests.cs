using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgNestedStructureTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "NESTED", "**free\n" + source));
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);
    private const string Layout = "dcl-ds ITEM qualified template inz;\nN int(10) inz(7);\nTEXT char(3) inz('ABC');\nend-ds;\n";

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Referenced_nested_structures_share_parent_bytes_and_initialize_every_array_element(int ccsid)
    {
        var context = Run(Layout + "dcl-ds OUTER qualified inz;\nHEAD char(2);\nCHILD likeds(ITEM) dim(2) inz(*likeds);\nTAIL char(2);\nend-ds;\neval OUTER.HEAD='AA';\neval OUTER.TAIL='ZZ';\neval OUTER.CHILD(2).N=12;\nreturn;", ccsid);
        Assert.Equal(7L, Read(context, "OUTER.CHILD(1).N")); Assert.Equal(12L, Read(context, "OUTER.CHILD(2).N")); Assert.Equal("ABC", Read(context, "OUTER.CHILD(2).TEXT"));
        var bytes = Assert.IsType<ProgramBuffer>(Read(context, "OUTER")); Assert.Equal(18, bytes.Length);
        Assert.Equal(7, Assert.IsType<ProgramBuffer>(Read(context, "OUTER.CHILD(1)")).Length);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_aggregate_and_scalar_parameter_aliases_observe_the_same_storage(int ccsid)
    {
        var context = Run(Layout + "dcl-ds OUTER qualified inz;\nCHILD likeds(ITEM);\nend-ds;\ncallp CHANGE(OUTER.CHILD:OUTER.CHILD.N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(ITEM) options(*exact);\nN int(10);\nend-pi;\neval P.N=8;\neval N=N+P.N;\neval P.TEXT='XYZ';\nend-proc;", ccsid);
        Assert.Equal(16L, Read(context, "OUTER.CHILD.N")); Assert.Equal("XYZ", Read(context, "OUTER.CHILD.TEXT"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Inline_nested_arrays_are_qualified_and_clear_only_the_selected_storage(int ccsid)
    {
        var context = Run("dcl-ds FAMILY qualified inz;\nHEAD char(2);\ndcl-ds PEOPLE dim(2);\nN int(10);\ndcl-ds PETS dim(2);\nTEXT char(3);\nVALUE packed(5:2);\nend-ds PETS;\nend-ds PEOPLE;\nTAIL char(2);\nend-ds FAMILY;\neval FAMILY.HEAD='AA';\neval FAMILY.TAIL='ZZ';\neval FAMILY.PEOPLE(1).N=3;\neval FAMILY.PEOPLE(2).PETS(2).TEXT='DOG';\neval FAMILY.PEOPLE(2).PETS(2).VALUE=12.34;\nclear FAMILY.PEOPLE(2).PETS(1);\nreturn;", ccsid);
        Assert.Equal(3L, Read(context, "FAMILY.PEOPLE(1).N")); Assert.Equal(0m, Read(context, "FAMILY.PEOPLE(2).PETS(1).VALUE"));
        Assert.Equal(12.34m, Read(context, "FAMILY.PEOPLE(2).PETS(2).VALUE")); Assert.Equal("DOG", Read(context, "FAMILY.PEOPLE(2).PETS(2).TEXT"));
        Assert.Equal("AA", Read(context, "FAMILY.HEAD")); Assert.Equal("ZZ", Read(context, "FAMILY.TAIL"));
    }

    [Fact]
    public void Nested_metadata_resolves_forward_scalars_templates_and_exact_return_types()
    {
        var context = Run("dcl-s VALUE like(BOX.INNER.N);\ndcl-ds RESULT likeds(BOX.INNER);\ndcl-ds BOX qualified template;\ndcl-ds INNER;\nN int(10);\nTEXT char(3);\nend-ds;\nend-ds;\neval VALUE=5;\neval RESULT=GET(VALUE);\nreturn;\ndcl-proc GET;\ndcl-pi *n likeds(BOX.INNER);\nN int(10);\nend-pi;\ndcl-ds LOCAL likeds(BOX.INNER) inz;\neval LOCAL.N=N+1;\neval LOCAL.TEXT='RET';\nreturn LOCAL;\nend-proc;");
        Assert.Equal(5L, Read(context, "VALUE")); Assert.Equal(6L, Read(context, "RESULT.N")); Assert.Equal("RET", Read(context, "RESULT.TEXT"));
    }

    [Fact]
    public void Nested_layout_inquiries_and_likeds_initialization_keep_recursive_defaults()
    {
        var context = Run(Layout + "dcl-ds BOX qualified template inz;\ndcl-ds INNER;\nROW likeds(ITEM) dim(2) inz(*likeds);\nend-ds;\nend-ds;\ndcl-c WIDTH %size(BOX.INNER);\ndcl-c COUNT %elem(BOX.INNER.ROW);\ndcl-s COPYTEXT like(BOX.INNER.ROW.TEXT);\ndcl-ds COPY likeds(BOX) inz(*likeds);\nclear COPY.INNER.ROW(2);\nsndpgmmsg %char(WIDTH);\nreturn;");
        Assert.Equal(14m, context.ReadValue("WIDTH")); Assert.Equal(2m, context.ReadValue("COUNT"));
        Assert.Equal(7L, Read(context, "COPY.INNER.ROW(1).N")); Assert.Equal(0L, Read(context, "COPY.INNER.ROW(2).N")); Assert.Equal("   ", Read(context, "COPY.INNER.ROW(2).TEXT"));
    }

    [Theory]
    [InlineData("")] [InlineData("value")] [InlineData("const")]
    public void Nested_whole_structure_parameters_preserve_reference_VALUE_and_CONST_behavior(string mode)
    {
        var body = mode == "const" ? "return P.CHILD.N;" : "eval P.CHILD.N=9;\nreturn P.CHILD.N;";
        var context = Run(Layout + "dcl-ds OUTER qualified inz;\nCHILD likeds(ITEM) inz(*likeds);\nend-ds;\ndcl-s RESULT int(10);\neval RESULT=CHANGE(OUTER);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n int(10);\nP likeds(OUTER) " + mode + ";\nend-pi;\n" + body + "\nend-proc;");
        Assert.Equal(mode == "const" ? 7L : 9L, context.ReadValue("RESULT")); Assert.Equal(mode == "" ? 9L : 7L, Read(context, "OUTER.CHILD.N"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_LIKEDS_subfields_share_the_qualified_outer_structure(int shift)
    {
        var source = RpgFixedStructureTests.D("ITEM", "DS", keywords: "QUALIFIED TEMPLATE", shift: shift) + RpgFixedStructureTests.D("N", length: "10", type: 'I', decimals: "0", shift: shift) +
            RpgFixedStructureTests.D("BOX", "DS", keywords: "QUALIFIED INZ", shift: shift) + RpgFixedStructureTests.D("CHILD", keywords: "LIKEDS(ITEM)", shift: shift) +
            RpgFixedCalculationTests.C("EVAL", expression: "BOX.CHILD.N=9") + RpgFixedCalculationTests.C("RETURN");
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "NESTED", source)); Assert.Equal(9L, Read(context, "BOX.CHILD.N"));
    }

    [Fact]
    public void Nested_arrays_support_keyed_SORTA_LOOKUP_and_SUBARR_without_moving_siblings()
    {
        var context = Run("dcl-ds BOX qualified inz;\nHEAD char(2);\ndcl-ds ITEMS dim(3);\nN int(10);\nTEXT char(2);\nend-ds;\nTAIL char(2);\nend-ds;\ndcl-s FOUND int(10);\neval BOX.HEAD='AA';\neval BOX.TAIL='ZZ';\neval BOX.ITEMS(1).N=3;\neval BOX.ITEMS(1).TEXT='CC';\neval BOX.ITEMS(2).N=1;\neval BOX.ITEMS(2).TEXT='AA';\neval BOX.ITEMS(3).N=2;\neval BOX.ITEMS(3).TEXT='BB';\nsorta BOX.ITEMS %fields(N);\neval FOUND=%lookup(2:BOX.ITEMS(*).N);\nclear %subarr(BOX.ITEMS:3:1);\nreturn;");
        Assert.Equal(2L, context.ReadValue("FOUND")); Assert.Equal("AA", Read(context, "BOX.ITEMS(1).TEXT")); Assert.Equal("BB", Read(context, "BOX.ITEMS(2).TEXT"));
        Assert.Equal(0L, Read(context, "BOX.ITEMS(3).N")); Assert.Equal("AA", Read(context, "BOX.HEAD")); Assert.Equal("ZZ", Read(context, "BOX.TAIL"));
    }

    [Fact]
    public void Recursive_nested_parameters_preserve_STATIC_and_automatic_storage()
    {
        var messages = new List<string>();
        var source = "**free\ncallp WORK(2);\ncallp WORK(1);\nreturn;\ndcl-proc WORK;\ndcl-pi *n;\nN int(10) value;\nend-pi;\ndcl-ds BOX qualified static inz;\ndcl-ds INNER;\nCOUNT int(10);\nend-ds;\nend-ds;\ndcl-ds LOCAL qualified inz;\ndcl-ds INNER;\nVALUE int(10);\nend-ds;\nend-ds;\neval BOX.INNER.COUNT=BOX.INNER.COUNT+1;\neval LOCAL.INNER.VALUE=N;\nif N>1;\ncallp WORK(N-1);\nendif;\nsndpgmmsg %char(LOCAL.INNER.VALUE)+':'+%char(BOX.INNER.COUNT);\nend-proc;";
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(RpgCompiler.Compile("QGPL", "NESTED", source)); Assert.Equal(new[] { "1:2", "2:2", "1:3" }, messages);
    }

    [Theory]
    [InlineData("dcl-ds BOX;\nCHILD likeds(ITEM);\nend-ds;")]
    [InlineData("dcl-ds BOX;\ndcl-ds CHILD;\nN int(10);\nend-ds;\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\nCHILD likeds(ITEM) const;\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\nCHILD likeds(ITEM) inz(1);\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\nCHILD likeds(ITEM) pos(0);\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\nCHILD likeds(ITEM) dim(0);\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\nCHILD likeds(ITEM) dim(32768);\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\ndcl-ds CHILD static;\nN int(10);\nend-ds;\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\ndcl-ds CHILD template;\nN int(10);\nend-ds;\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\ndcl-ds CHILD occurs(2);\nN int(10);\nend-ds;\nend-ds;")]
    [InlineData("dcl-ds BOX qualified;\ndcl-ds CHILD;\nN int(10);\nend-ds WRONG;\nend-ds;")]
    public void Invalid_nested_declarations_reject_with_source_locations(string declaration)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NESTED", "**free\n" + Layout + declaration)); Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("eval P.CHILD.N=2;")] [InlineData("clear P.CHILD;")] [InlineData("eval %subst(P.CHILD:1:1)='X';")]
    public void CONST_nested_parameters_reject_direct_and_indirect_writes(string operation)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NESTED", "**free\n" + Layout + "dcl-ds BOX qualified;\nCHILD likeds(ITEM);\nend-ds;\nreturn;\ndcl-proc WORK;\ndcl-pi *n;\nP likeds(BOX) const;\nend-pi;\n" + operation + "\nend-proc;"));

    [Fact]
    public void Nested_recursive_layout_cycles_are_reported_before_execution()
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NESTED", "**free\ndcl-ds ONE qualified;\nTWO likeds(OTHER);\nend-ds;\ndcl-ds OTHER qualified;\nONE likeds(ONE);\nend-ds;"));

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_overlay_arrays_preserve_base_strides_and_padding_during_CLEAR(int ccsid)
    {
        var context = Run(Layout + "dcl-ds BOX qualified inz;\nRAW char(14) dim(2);\nCHILD likeds(ITEM) overlay(RAW);\nend-ds;\neval BOX.RAW(1)='ABCDEFGHIJKLMN';\neval BOX.RAW(2)='ABCDEFGHIJKLMN';\neval BOX.CHILD(2).N=9;\nclear BOX.CHILD(2);\nreturn;", ccsid);
        Assert.Equal(0L, Read(context, "BOX.CHILD(2).N")); Assert.Equal("   ", Read(context, "BOX.CHILD(2).TEXT")); Assert.Equal("ABCDEFGHIJKLMN", Read(context, "BOX.RAW(1)"));
        Assert.Equal("HIJKLMN", Assert.IsType<ProgramBuffer>(Read(context, "%subst(BOX.RAW(2):8:7)")).ToText());
    }

    [Theory]
    [InlineData(64, true)] [InlineData(65, false)]
    public void Nested_layout_depth_is_bounded_before_recursive_binding(int levels, bool accepted)
    {
        var source = "dcl-ds ROOT qualified inz;\n" + string.Concat(Enumerable.Range(1, levels - 1).Select(n => "dcl-ds N" + n + ";\n")) + "VALUE int(10);\n" + string.Concat(Enumerable.Repeat("end-ds;\n", levels));
        var target = "ROOT." + string.Join('.', Enumerable.Range(1, levels - 1).Select(n => "N" + n)) + ".VALUE";
        if (!accepted) Assert.Throws<RpgCompileException>(() => Run(source));
        else Assert.Equal(9L, Read(Run(source + "eval " + target + "=9;\nreturn;"), target));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Nested_receivers_accept_detached_structure_returns_and_RTNPARM(bool rtnparm)
    {
        var context = Run(Layout + "dcl-ds BOX qualified inz;\nHEAD char(2);\nCHILD likeds(ITEM);\nTAIL char(2);\nend-ds;\neval BOX.HEAD='AA';\neval BOX.TAIL='ZZ';\neval BOX.CHILD=MAKE();\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(ITEM) " + (rtnparm ? "rtnparm" : "") + ";\nend-pi;\ndcl-ds RESULT likeds(ITEM) inz(*likeds);\neval RESULT.N=12;\nreturn RESULT;\nend-proc;");
        Assert.Equal(12L, Read(context, "BOX.CHILD.N")); Assert.Equal("ABC", Read(context, "BOX.CHILD.TEXT")); Assert.Equal("AA", Read(context, "BOX.HEAD")); Assert.Equal("ZZ", Read(context, "BOX.TAIL"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_views_of_multiple_occurrence_storage_keep_their_own_width_and_argument_range(int ccsid)
    {
        var context = Run("dcl-ds BOX qualified occurs(2) inz;\ndcl-ds CHILD dim(2);\nN int(10);\nend-ds;\nend-ds;\ndcl-s WIDTH int(10);\ndcl-s CHANGED int(10);\neval %occur(BOX)=2;\neval BOX.CHILD(2).N=9;\neval WIDTH=%size(BOX.CHILD:*all);\ncallp CHANGE(BOX.CHILD(2));\neval CHANGED=BOX.CHILD(2).N;\neval %occur(BOX)=1;\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(BOX.CHILD);\nend-pi;\neval P.N=P.N+1;\nend-proc;", ccsid);
        Assert.Equal(8L, context.ReadValue("WIDTH")); Assert.Equal(0L, Read(context, "BOX.CHILD(2).N"));
        Assert.Equal(10L, context.ReadValue("CHANGED"));
        // The first occurrence remains independent after the second was passed by reference.
        Assert.Equal(0L, Read(context, "BOX.CHILD(1).N"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Explicit_nested_LIKEDS_initialization_preserves_uninitialized_parent_numeric_bytes(int ccsid)
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(10) inz(7);\nRAW int(10);\nend-ds;\ndcl-ds BOX qualified inz;\nCHILD likeds(MODEL) inz(*likeds);\nend-ds;\nreturn;", ccsid);
        Assert.Equal(7L, Read(context, "BOX.CHILD.N")); Assert.Equal("    ", Assert.IsType<ProgramBuffer>(Read(context, "%subst(BOX.CHILD:5:4)")).ToText());
    }

    [Fact]
    public void Recursive_subfield_count_is_bounded_before_runtime_storage_is_created()
    {
        var model = "dcl-ds MODEL qualified template;\n" + string.Concat(Enumerable.Range(1, 32).Select(n => "F" + n + " char(1);\n")) + "end-ds;\n";
        var owner = "dcl-ds BOX qualified template;\n" + string.Concat(Enumerable.Range(1, 32).Select(n => "C" + n + " likeds(MODEL);\n")) + "end-ds;";
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "NESTED", "**free\n" + model + owner));
    }

    [Fact]
    public void Column_limited_inline_headers_keep_continuation_lines_and_source_positions()
    {
        var source = string.Join('\n', new[] { "dcl-ds BOX qualified inz;", "dcl-ds CHILD", "dim(2);", "TEXT char(3);", "end-ds;", "end-ds;", "/free", "eval BOX.CHILD(2).TEXT='YES';", "return;", "/end-free" }.Select(line => "       " + line));
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "NESTED", source)); Assert.Equal("YES", Read(context, "BOX.CHILD(2).TEXT"));
    }

}
