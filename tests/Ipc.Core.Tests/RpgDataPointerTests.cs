using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgDataPointerTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "MAIN", "**free\n" + source));
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Based_scalars_follow_pointer_changes_and_write_the_original_storage(int ccsid)
    {
        var context = Run("dcl-s A char(8) inz('ABCDEFGH');\ndcl-s B char(8) inz('12345678');\ndcl-s P pointer;\ndcl-s VIEW char(3) based(P);\neval P=%addr(A)+2;\neval VIEW='xyz';\neval P=%addr(B)+4;\neval VIEW='uvw';\nreturn;", ccsid);
        Assert.Equal("ABxyzFGH", context.ReadValue("A")); Assert.Equal("1234uvw8", context.ReadValue("B"));
    }
    [Fact]
    public void Based_pointers_can_be_implicit_and_initialize_from_forward_storage()
    {
        var context = Run("dcl-s P pointer inz(%addr(A));\ndcl-s VIEW int(10) based(IMPLICIT);\ndcl-s A int(10) inz(7);\neval IMPLICIT=P;\neval VIEW=9;\nreturn;");
        Assert.Equal(9L, context.ReadValue("A")); Assert.IsType<RpgDataAddress>(context.ReadValue("IMPLICIT"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Based_arrays_and_structures_alias_numeric_and_character_bytes(int ccsid)
    {
        var context = Run("dcl-ds BOX qualified inz;\nPREFIX char(4) inz('ABCD');\nNUM int(10) inz(123);\nend-ds;\ndcl-s P pointer;\ndcl-ds VIEW qualified based(P);\nTEXT char(4);\nN int(10);\nend-ds;\ndcl-s BYTES char(1) dim(8) based(P);\neval P=%addr(BOX);\neval VIEW.N=456;\neval BYTES(2)='Z';\nreturn;", ccsid);
        Assert.Equal(456L, RpgExpressionParser.Parse("BOX.NUM").Eval(context)); Assert.Equal("AZCD", RpgExpressionParser.Parse("BOX.PREFIX").Eval(context));
    }
    [Fact]
    public void Array_element_addresses_keep_the_same_space_and_byte_offsets()
    {
        var context = Run("dcl-s ITEMS int(10) dim(3) inz(1);\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\ndcl-s OFFSET int(10);\neval P=%addr(ITEMS(2));\neval VIEW=7;\neval OFFSET=%addr(ITEMS(3))-%addr(ITEMS(1));\nreturn;");
        Assert.Equal(7L, context.ReadArrayValue("ITEMS", 2)); Assert.Equal(8L, context.ReadValue("OFFSET"));
    }
    [Fact]
    public void Address_of_based_storage_is_the_current_pointer_plus_the_member_offset()
    {
        var context = Run("dcl-s P pointer;\ndcl-ds VIEW qualified based(P);\nA char(4);\nB int(10);\nend-ds;\ndcl-s SAME int(10);\nif %addr(VIEW)=*null;\neval SAME=1;\nendif;\neval P=%alloc(8);\nif %addr(VIEW.B)=P+4;\neval SAME=SAME+1;\nendif;\ndealloc(n) P;\nreturn;");
        Assert.Equal(2L, context.ReadValue("SAME")); Assert.True(Assert.IsType<RpgDataAddress>(context.ReadValue("P")).IsNull);
    }
    [Fact]
    public void Allocation_reallocation_and_deallocation_preserve_the_prefix_and_expire_old_aliases()
    {
        var context = Run("dcl-s P pointer;\ndcl-s OLD pointer;\ndcl-s VIEW char(8) based(P);\ndcl-s SMALL char(4) based(P);\ndcl-s STALE char(1) based(OLD);\ndcl-s N int(10);\neval P=%alloc(8);\neval VIEW='ABCDEFGH';\neval OLD=P;\neval P=%realloc(P:4);\nif SMALL='ABCD';\neval N=1;\nendif;\nmonitor;\neval STALE='X';\non-error 222;\neval N=N+1;\nendmon;\ndealloc(n) P;\nif P=*null;\neval N=N+1;\nendif;\nreturn;");
        Assert.Equal(3L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("16776705")] [InlineData("F")]
    public void Invalid_allocation_sizes_fail_without_changing_the_pointer(string size)
    {
        var context = Run("dcl-s P pointer;\ndcl-s N int(10);\ndcl-s F float(8) inz(4);\nmonitor;\neval P=%alloc(" + size + ");\non-error 425;\neval N=1;\nendmon;\nreturn;");
        Assert.Equal(1L, context.ReadValue("N")); Assert.True(Assert.IsType<RpgDataAddress>(context.ReadValue("P")).IsNull);
    }
    [Theory]
    [InlineData("*null")] [InlineData("%addr(A)+5")] [InlineData("%addr(A)-1")]
    public void Null_and_out_of_range_access_reject_before_mutating_storage(string value)
    {
        var context = Run("dcl-s A char(4) inz('SAFE');\ndcl-s P pointer;\ndcl-s VIEW char(4) based(P);\ndcl-s N int(10);\neval P=" + value + ";\nmonitor;\neval VIEW='FAIL';\non-error 222;\neval N=1;\nendmon;\nreturn;");
        Assert.Equal("SAFE", context.ReadValue("A")); Assert.Equal(1L, context.ReadValue("N"));
    }
    [Fact]
    public void Dealloc_error_and_null_extenders_keep_the_pointer_on_failure()
    {
        var context = Run("dcl-s A char(4) inz('SAFE');\ndcl-s P pointer;\ndcl-s N int(10);\neval P=%addr(A);\ndealloc(en) P;\nif %error() and %status()=426 and P=%addr(A);\neval N=1;\nendif;\nclear P;\ndealloc(n) P;\nreturn;");
        Assert.Equal(1L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("dcl-s P pointer(*data);")]
    [InlineData("dcl-s P char(16);\ndcl-s V char(1) based(P);")]
    [InlineData("dcl-s P pointer dim(2);\ndcl-s V char(1) based(P);")]
    [InlineData("dcl-s V char(1) based(P) inz('X');")]
    [InlineData("dcl-s P pointer based(P);")]
    [InlineData("dcl-s P pointer;\ndcl-s V char(1) based(P);\ndcl-s Q pointer inz(%addr(V));")]
    [InlineData("dcl-s P pointer;\ndcl-ds BOX qualified based(P);\nN int(10) inz(1);\nend-ds;")]
    public void Invalid_based_declarations_reject_at_compile_time(string source)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\n" + source));
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_pointer_based_storage_and_memory_operations_support_both_column_origins(int shift)
    {
        string D(string name, string definition = "", string length = "", char type = ' ', string decimals = "", string keywords = "") => RpgFixedStructureTests.D(name, definition, length: length, type: type, decimals: decimals, keywords: keywords, shift: shift);
        string C(string operation, string factor2 = "", string result = "", string? expression = null, string error = "") => RpgFixedCalculationTests.C(operation, factor2: factor2, result: result, expression: expression, lo: error);
        var source = D("P", "S", type: '*') + D("VIEW", "S", "4", keywords: "BASED(P)") + D("N", "S", "10", 'I', "0") +
            C("ALLOC", "4", "P") + C("EVAL", expression: "VIEW='ABCD'") + C("REALLOC", "8", "P") +
            C("EVAL", expression: "N=%size(P)") + C("REALLOC(E)", "0", "P") +
            C("DEALLOC(N)", result: "P") + C("RETURN");
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "FIXED", source));
        Assert.Equal(16L, context.ReadValue("N")); Assert.True(Assert.IsType<RpgDataAddress>(context.ReadValue("P")).IsNull);
    }
    [Theory]
    [InlineData("'BAD'")] [InlineData("1")] [InlineData("%char(P)")] [InlineData("P*2")]
    public void Basing_pointer_values_reject_character_numeric_assignments_and_conversions(string expression)
        => Assert.Throws<RpgRuntimeException>(() => Run("dcl-s P pointer;\neval P=" + expression + ";\nreturn;"));
    [Fact]
    public void Reallocation_failure_keeps_the_original_pointer_and_contents()
    {
        var context = Run("dcl-s P pointer;\ndcl-s SAVED pointer;\ndcl-s VIEW char(4) based(P);\ndcl-s N int(10);\neval P=%alloc(4);\neval SAVED=P;\neval VIEW='SAFE';\nmonitor;\neval P=%realloc(P:0);\non-error 425;\nif P=SAVED and VIEW='SAFE';\neval N=1;\nendif;\nendmon;\ndealloc(n) P;\nreturn;"); Assert.Equal(1L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Foreign_and_reclaimed_addresses_cannot_access_another_execution_storage(bool reclaim)
    {
        var registry = new RpgDataPointerRegistry(); var host = new RpgHost { DataPointers = registry };
        var origin = new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "SOURCE", "**free\ndcl-s N int(10);\ndcl-s P pointer;\neval P=%addr(N);\nreturn;"));
        var address = origin.ReadValue("P"); if (reclaim) origin.ReleaseProcedureAddresses();
        var otherHost = new RpgHost { DataPointers = reclaim ? registry : new() };
        var program = RpgCompiler.Compile("QGPL", "TARGET", "**free\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\nreturn;");
        var target = new RpgRuntimeContext(program, otherHost); target.WriteValue("P", address);
        Assert.Throws<RpgRuntimeException>(() => target.WriteValue("VIEW", 3));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Basing_pointers_reinterpret_independent_native_packed_bytes(int ccsid)
    {
        var context = Run("dcl-s NUMBER packed(5:2);\ndcl-s P pointer;\ndcl-s RAW char(3) ccsid(*hex) based(P);\neval P=%addr(NUMBER);\neval RAW=x'01234C';\nreturn;", ccsid);
        Assert.Equal(12.34m, context.ReadValue("NUMBER"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Based_procedure_pointer_storage_requires_native_alignment(bool misaligned)
    {
        var context = Run("dcl-s P pointer;\ndcl-s BASE pointer;\ndcl-s CALLBACK pointer(*proc) based(P);\ndcl-pr RUN int(10) extproc(CALLBACK) end-pr;\ndcl-s N int(10);\neval BASE=%alloc(32);\neval P=BASE+" + (misaligned ? "1" : "0") + ";\nmonitor;\neval CALLBACK=%paddr(TARGET);\neval N=RUN();\non-error 222;\neval N=99;\nendmon;\ndealloc BASE;\nreturn;\ndcl-proc TARGET;\ndcl-pi *n int(10) end-pi;\nreturn 7;\nend-proc;");
        Assert.Equal(misaligned ? 99L : 7L, context.ReadValue("N"));
    }
    [Fact]
    public void Addressing_noncontiguous_structure_subfields_uses_real_native_offsets()
    {
        var context = Run("dcl-ds ROWS qualified dim(3) inz;\nA int(10);\nB int(10);\nend-ds;\ndcl-s POINTERS pointer dim(3);\ndcl-s N int(10);\neval POINTERS=%addr(ROWS(*).B);\neval N=POINTERS(3)-POINTERS(1);\nreturn;");
        Assert.Equal(16L, context.ReadValue("N"));
    }

    [Fact]
    public void Heap_quota_failure_preserves_existing_allocations_and_free_releases_capacity()
    {
        var source = "dcl-s POINTERS pointer dim(4);\ndcl-s P pointer;\ndcl-s I int(10);\ndcl-s N int(10);\nfor I=1 to 4;\neval POINTERS(I)=%alloc(16776704);\nendfor;\nmonitor;\neval P=%alloc(4096);\non-error 425;\neval N=1;\nendmon;\nfor I=1 to 4;\ndealloc POINTERS(I);\nendfor;\neval P=%alloc(4096);\ndealloc(n) P;\nreturn;";
        Assert.Equal(1L, Run(source).ReadValue("N"));
    }
    [Fact]
    public void Native_address_region_limit_is_released_when_an_activation_is_reclaimed()
    {
        var program = RpgCompiler.Compile("QGPL", "SOURCE", "**free\ndcl-s N int(10);\ndcl-s P pointer;\neval P=%addr(N);\nreturn;");
        var registry = new RpgDataPointerRegistry(); var host = new RpgHost { DataPointers = registry }; var contexts = new List<RpgRuntimeContext>();
        for (var i = 0; i < 4096; i++) contexts.Add(new RpgInterpreter(host).Run(program));
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program)); contexts[0].ReleaseProcedureAddresses();
        contexts.Add(new RpgInterpreter(host).Run(program)); foreach (var context in contexts) context.ReleaseProcedureAddresses();
        Assert.IsType<RpgDataAddress>(new RpgInterpreter(host).Run(program).ReadValue("P"));
    }
    [Fact]
    public void Pointer_aliases_use_the_original_CCSID_and_reject_foreign_representations()
    {
        var registry = new RpgDataPointerRegistry(); var host = new RpgHost { DataPointers = registry, Ccsid = () => 37 };
        var origin = new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "SOURCE", "**free\ndcl-s TEXT char(4);\ndcl-s P pointer;\neval P=%addr(TEXT);\nreturn;"));
        var other = new RpgHost { DataPointers = registry, Ccsid = () => 1208 }; var program = RpgCompiler.Compile("QGPL", "OTHER", "**free\ndcl-s P pointer;\ndcl-s VIEW char(4) based(P);\nreturn;");
        var target = new RpgRuntimeContext(program, other); target.WriteValue("P", origin.ReadValue("P"));
        Assert.Throws<RpgRuntimeException>(() => target.WriteValue("VIEW", "FAIL")); Assert.Equal("    ", origin.ReadValue("TEXT"));
    }

    [Fact]
    public void Based_metadata_and_pointer_expression_sizes_do_not_dereference_null_storage_or_allocate()
    {
        var context = Run("dcl-s P pointer;\ndcl-s ITEMS char(4) dim(3) based(P);\ndcl-s N int(10);\neval N=%size(ITEMS:*all)+%elem(ITEMS)+%len(ITEMS)+%size(%alloc(0))+%size(*null);\nreturn;");
        Assert.Equal(51L, context.ReadValue("N"));
    }

    [Fact]
    public void Multiple_occurrence_based_structures_clear_all_storage_from_the_original_pointer()
    {
        var context = Run("dcl-s P pointer;\ndcl-ds VIEW qualified occurs(2) based(P);\nN int(10);\nend-ds;\ndcl-s TOTAL int(10);\neval P=%alloc(8);\nclear *all VIEW;\neval VIEW.N=3;\neval %occur(VIEW)=2;\neval VIEW.N=7;\nclear *all VIEW;\neval TOTAL=VIEW.N;\neval %occur(VIEW)=2;\neval TOTAL=TOTAL+VIEW.N;\ndealloc P;\nreturn;");
        Assert.Equal(0L, context.ReadValue("TOTAL"));
    }
    [Fact]
    public void Pointer_initialization_rejects_dynamic_member_array_indexes()
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\ndcl-s I int(10) inz(1);\ndcl-ds BOX qualified;\nITEMS int(10) dim(2);\nend-ds;\ndcl-s P pointer inz(%addr(BOX.ITEMS(I)));"));

    [Fact]
    public void Realloc_requires_an_existing_allocation_and_keeps_a_null_pointer_on_error()
    {
        var context = Run("dcl-s P pointer;\ndcl-s N int(10);\nmonitor;\neval P=%realloc(P:4);\non-error 426;\neval N=1;\nendmon;\nreturn;");
        Assert.Equal(1L, context.ReadValue("N")); Assert.True(Assert.IsType<RpgDataAddress>(context.ReadValue("P")).IsNull);
    }

}
