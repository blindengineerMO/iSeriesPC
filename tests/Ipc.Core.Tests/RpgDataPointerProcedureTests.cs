using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgDataPointerProcedureTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "MAIN", "**free\n" + source));
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void A_parameter_address_returned_by_a_procedure_still_aliases_the_callers_storage(int ccsid)
    {
        var context = Run("dcl-s N int(10) inz(1);\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\neval P=GET(N);\neval VIEW=9;\nreturn;\ndcl-proc GET;\ndcl-pi *n pointer;\nARG int(10);\nend-pi;\nreturn %addr(ARG);\nend-proc;", ccsid);
        Assert.Equal(9L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("const")] [InlineData("value")] [InlineData("")]
    public void Pointer_parameters_can_mutate_the_addressed_storage(string mode)
    {
        var context = Run("dcl-s N int(10) inz(1);\ndcl-s P pointer;\neval P=%addr(N);\ncallp CHANGE(P);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nPTR pointer " + mode + ";\nend-pi;\ndcl-s VIEW int(10) based(PTR);\neval VIEW=7;\nend-proc;");
        Assert.Equal(7L, context.ReadValue("N"));
    }
    [Fact]
    public void Based_variables_pass_native_references_and_preserve_alias_identity()
    {
        var context = Run("dcl-s P pointer;\ndcl-s VIEW int(10) based(P);\ndcl-s N int(10) inz(1);\ndcl-s RESULT int(10);\neval P=%addr(N);\neval RESULT=CHANGE(VIEW:N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n int(10);\nA int(10);\nB int(10);\nend-pi;\neval A=8;\nif %addr(A)=%addr(B);\nreturn B;\nendif;\nreturn 0;\nend-proc;");
        Assert.Equal(8L, context.ReadValue("N")); Assert.Equal(8L, context.ReadValue("RESULT"));
    }
    [Theory]
    [InlineData("")] [InlineData("value")]
    public void Returned_addresses_of_automatic_and_value_parameter_storage_expire(string mode)
    {
        var source = "dcl-s P pointer;\ndcl-s VIEW int(10) based(P);\ndcl-s N int(10) inz(1);\neval P=GET(" + (mode.Length == 0 ? "" : "N") + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n pointer" + (mode.Length == 0 ? " end-pi;\ndcl-s ARG int(10);\n" : ";\nARG int(10) value;\nend-pi;\n") + "return %addr(ARG);\nend-proc;";
        var context = Run(source); Assert.Throws<RpgRuntimeException>(() => context.WriteValue("VIEW", 7));
    }
    [Fact]
    public void Procedure_STATIC_storage_addresses_survive_return()
    {
        var context = Run("dcl-s P pointer;\ndcl-s VIEW int(10) based(P);\ndcl-s N int(10);\neval P=GET();\neval VIEW=12;\neval P=GET();\neval N=VIEW;\nreturn;\ndcl-proc GET;\ndcl-pi *n pointer end-pi;\ndcl-s ARG int(10) static inz(1);\nreturn %addr(ARG);\nend-proc;");
        Assert.Equal(12L, context.ReadValue("N"));
    }
    [Fact]
    public void Parameter_address_initialization_waits_until_the_actual_reference_is_bound()
    {
        var context = Run("dcl-s N int(10) inz(1);\ncallp CHANGE(N);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nARG int(10);\nend-pi;\ndcl-s P pointer inz(%addr(ARG));\ndcl-s VIEW int(10) based(P);\neval VIEW=9;\nend-proc;");
        Assert.Equal(9L, context.ReadValue("N"));
    }
    [Fact]
    public void Const_parameter_addresses_can_be_compared_but_cannot_be_saved_for_mutation()
    {
        var context = Run("dcl-s N int(10) inz(1);\ndcl-s SAME int(10);\neval SAME=CHECK(N:N);\nreturn;\ndcl-proc CHECK;\ndcl-pi *n int(10);\nA int(10) const;\nB int(10) const;\nend-pi;\nif %addr(A)=%addr(B);\nreturn 1;\nendif;\nreturn 0;\nend-proc;"); Assert.Equal(1L, context.ReadValue("SAME"));
        Assert.Throws<RpgRuntimeException>(() => Run("dcl-s N int(10);\ndcl-s P pointer;\neval P=GET(N);\nreturn;\ndcl-proc GET;\ndcl-pi *n pointer;\nA int(10) const;\nend-pi;\nreturn %addr(A);\nend-proc;"));
    }
    [Fact]
    public void Address_wildcards_assign_each_element_address_to_a_pointer_array()
    {
        var context = Run("dcl-s ITEMS int(10) dim(3) inz(1);\ndcl-s POINTERS pointer dim(3);\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\neval POINTERS=%addr(ITEMS(*));\neval P=POINTERS(2);\neval VIEW=9;\nreturn;");
        Assert.Equal(9L, context.ReadArrayValue("ITEMS", 2));
    }
    [Theory]
    [InlineData("dcl-s A pointer;\ndcl-s B pointer(*proc);\neval A=B;")]
    [InlineData("dcl-s A pointer;\ndcl-s B pointer(*proc);\neval B=A;")]
    public void Null_values_of_different_pointer_types_cannot_be_assigned(string source)
        => Assert.Throws<RpgRuntimeException>(() => Run(source));
    [Fact]
    public void Deallocation_invalidates_a_based_reference_captured_before_a_later_argument_runs()
    {
        var source = "dcl-s P pointer;\ndcl-s VIEW int(10) based(P);\ndcl-s EFFECT int(10);\neval P=%alloc(4);\neval VIEW=1;\nmonitor;\ncallp CHANGE(VIEW:FREE());\non-error *all;\nendmon;\nreturn;\n" +
            "dcl-proc FREE;\ndcl-pi *n int(10) end-pi;\ndealloc P;\nreturn 0;\nend-proc;\n" +
            "dcl-proc CHANGE;\ndcl-pi *n;\nA int(10);\nB int(10) const;\nend-pi;\neval EFFECT=1;\neval A=9;\nend-proc;";
        Assert.Equal(0L, Run(source).ReadValue("EFFECT"));
    }
    [Fact]
    public void A_const_pointer_value_can_free_its_allocation_without_changing_the_pointer_cell()
    {
        var context = Run("dcl-s P pointer;\ndcl-s VIEW int(10) based(P);\neval P=%alloc(4);\ncallp FREE(P);\nreturn;\ndcl-proc FREE;\ndcl-pi *n;\nPTR pointer const;\nend-pi;\ndealloc PTR;\nend-proc;");
        Assert.False(Assert.IsType<RpgDataAddress>(context.ReadValue("P")).IsNull); Assert.Throws<RpgRuntimeException>(() => context.ReadValue("VIEW"));
    }

    [Theory]
    [InlineData("*nopass", "")] [InlineData("*omit", "*omit")]
    public void Address_of_an_unpassed_or_omitted_parameter_does_not_expose_placeholder_storage(string option, string actual)
    {
        Assert.Throws<RpgRuntimeException>(() => Run("dcl-s P pointer;\neval P=GET(" + actual + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n pointer;\nARG int(10) options(" + option + ");\nend-pi;\nreturn %addr(ARG);\nend-proc;"));
    }

}
