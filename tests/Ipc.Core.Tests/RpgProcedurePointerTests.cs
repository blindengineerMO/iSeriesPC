using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgProcedurePointerTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "MAIN", "**free\n" + source));
    private const string Add = "dcl-proc ADD;\ndcl-pi *n int(10);\nP int(10);\nend-pi;\neval P=P+1;\nreturn P*2;\nend-proc;";
    private const string Main = "dcl-s FN pointer(*proc);\ndcl-pr RUN int(10) extproc(FN);\nP int(10);\nend-pr;\ndcl-s N int(10) inz(5);\ndcl-s RESULT int(10);\neval FN=%paddr(ADD);\neval RESULT=RUN(N);\nreturn;\n";
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Procedure_addresses_call_local_native_functions_and_borrow_reference_parameters(int ccsid)
    {
        var context = Run(Main + Add, ccsid); Assert.Equal(12L, context.ReadValue("RESULT")); Assert.Equal(6L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("%paddr (ADD)")] [InlineData("%paddr(ADD)")] [InlineData("%paddr('ADD')")] [InlineData("%paddr(NAME)")] [InlineData("%paddr(x'C1C4C4')")]
    public void Address_expressions_accept_bound_prototypes_and_constant_entry_names(string expression)
    {
        var context = Run("dcl-c NAME 'ADD';\n" + Main.Replace("%paddr(ADD)", expression) + Add); Assert.Equal(12L, context.ReadValue("RESULT"));
    }
    [Fact]
    public void Pointer_initialization_and_null_comparisons_preserve_opaque_identity()
    {
        var context = Run("dcl-s FN pointer(*proc) inz(%paddr(ADD));\ndcl-s EMPTY pointer(*proc);\ndcl-s MATCH int(10);\nif EMPTY=*null and FN=%paddr('ADD');\neval MATCH=1;\nendif;\nclear FN;\nif FN=*null;\neval MATCH=MATCH+1;\nendif;\nreturn;\n" + Add);
        Assert.Equal(2L, context.ReadValue("MATCH")); Assert.Equal(16, context.Program.FindField("FN")!.Length);
    }
    [Fact]
    public void Pointer_arrays_can_supply_an_indexed_EXTPROC_target()
    {
        var context = Run(Main.Replace("FN pointer(*proc)", "FN pointer(*proc) dim(2)").Replace("extproc(FN)", "extproc(FN(2))").Replace("eval FN=", "eval FN(2)=") + Add);
        Assert.Equal(12L, context.ReadValue("RESULT"));
    }
    [Fact]
    public void Structure_pointer_members_have_native_alignment_and_can_be_called()
    {
        var source = Main.Replace("dcl-s FN pointer(*proc);", "dcl-ds HANDLER qualified;\nPREFIX char(1);\nFN pointer(*proc);\nTAIL char(1);\nend-ds;").Replace("extproc(FN)", "extproc(HANDLER.FN)").Replace("eval FN=", "eval HANDLER.FN=");
        var context = Run(source + Add); Assert.Equal(12L, context.ReadValue("RESULT")); var layout = context.Program.FindDs("HANDLER")!;
        Assert.Equal(16, layout.Elements.Single(e => e.LocalName == "FN").Offset); Assert.Equal(48, layout.Length);
    }
    [Fact]
    public void Null_pointer_calls_fail_before_evaluating_arguments()
    {
        var source = Main.Replace("P int(10);", "P int(10) const;").Replace("eval FN=%paddr(ADD);", "dcl-s EFFECT int(10);\nmonitor;").Replace("eval RESULT=RUN(N);", "eval RESULT=RUN(SIDE());\non-error 202;\neval RESULT=99;\nendmon;") +
            "dcl-proc SIDE;\ndcl-pi *n int(10) end-pi;\neval EFFECT=1;\nreturn 1;\nend-proc;\n" + Add;
        var context = Run(source); Assert.Equal(0L, context.ReadValue("EFFECT")); Assert.Equal(99L, context.ReadValue("RESULT"));
    }
    [Fact]
    public void An_incompatible_pointer_target_rejects_before_argument_effects()
    {
        var source = Main.Replace("P int(10);", "P int(10) const;").Replace("dcl-pr RUN int(10)", "dcl-pr RUN char(4)").Replace("eval FN=%paddr(ADD);", "dcl-s EFFECT int(10);\neval FN=%paddr(ADD);\nmonitor;").Replace("eval RESULT=RUN(N);", "callp RUN(SIDE());\non-error 202;\neval RESULT=99;\nendmon;") +
            "dcl-proc SIDE;\ndcl-pi *n int(10) end-pi;\neval EFFECT=1;\nreturn 1;\nend-proc;\n" + Add;
        var context = Run(source); Assert.Equal(0L, context.ReadValue("EFFECT")); Assert.Equal(99L, context.ReadValue("RESULT"));
    }
    [Theory]
    [InlineData("dcl-s FN pointer(*proc) dim(2);\ndcl-pr RUN extproc(FN) end-pr;")]
    [InlineData("dcl-s FN pointer(*data);")]
    [InlineData("dcl-s FN pointer(*proc) inz('ADD');")]
    [InlineData("dcl-s FN char(16);\ndcl-pr RUN extproc(FN);\nend-pr;")]
    [InlineData("dcl-s FN pointer(*proc);\ndcl-s NAME char(10) inz('ADD');\neval FN=%paddr(NAME);")]
    [InlineData("dcl-s FN pointer(*proc);\ndcl-pr TARGET extpgm('MAIN');\nend-pr;\neval FN=%paddr(TARGET);")]
    [InlineData("dcl-ds BOX qualified;\nFN pointer(*proc) pos(2);\nend-ds;")]
    public void Invalid_native_pointer_declarations_and_address_operands_reject_at_compilation(string source)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\n" + source));
    [Theory]
    [InlineData("'ADD'")] [InlineData("0")] [InlineData("1")]
    public void Character_and_numeric_values_cannot_be_assigned_to_typed_pointers(string value)
        => Assert.Throws<RpgRuntimeException>(() => Run("dcl-s FN pointer(*proc);\neval FN=" + value + ";\nreturn;"));
    [Fact]
    public void Captured_module_images_restore_address_imports_and_pointer_calls()
    {
        var main = RpgCompiler.CompileModule("QGPL", "MAIN", new SourceDocument("MAIN", "**free\n" + Main.Replace("%paddr(ADD)", "%paddr('ADD')")));
        var helper = RpgCompiler.CompileModule("QGPL", "HELPER", new SourceDocument("HELPER", "**free\nctl-opt nomain;\n" + Add.Replace("ADD;", "ADD export;")));
        var image = new RpgBoundProgram(1, "QGPL/MAIN", [RpgModuleImage.Capture(main), RpgModuleImage.Capture(helper)]); var restored = RpgBoundProgram.Deserialize(image.Serialize()).Restore();
        Assert.Equal(12L, new RpgInterpreter(new()).Run(restored).ReadValue("RESULT"));
    }
    [Fact]
    public void A_native_function_can_return_a_procedure_pointer()
    {
        var context = Run(Main.Replace("%paddr(ADD)", "MAKE()") + "dcl-proc MAKE;\ndcl-pi *n pointer(*proc) end-pi;\nreturn %paddr(ADD);\nend-proc;\n" + Add);
        Assert.Equal(12L, context.ReadValue("RESULT"));
    }
    [Theory]
    [InlineData("const")] [InlineData("value")] [InlineData("")]
    public void Procedure_pointer_parameters_can_be_called_by_the_receiving_procedure(string mode)
    {
        var context = Run(Main.Replace("eval RESULT=RUN(N);", "eval RESULT=APPLY(FN:N);") +
            "dcl-proc APPLY;\ndcl-pi *n int(10);\nADDRESS pointer(*proc) " + mode + ";\nVALUE int(10);\nend-pi;\ndcl-pr TARGET int(10) extproc(ADDRESS);\nP int(10);\nend-pr;\nreturn TARGET(VALUE);\nend-proc;\n" + Add);
        Assert.Equal(12L, context.ReadValue("RESULT")); Assert.Equal(6L, context.ReadValue("N"));
    }
    [Theory]
    [InlineData("FN")] [InlineData("FN+1")] [InlineData("%int(FN)")] [InlineData("%float(FN)")]
    [InlineData("%char(FN)")] [InlineData("%date(FN)")]
    public void Procedure_addresses_reject_scalar_conversions_and_arithmetic(string expression)
        => Assert.Throws<RpgRuntimeException>(() => Run("dcl-s FN pointer(*proc);\ndcl-s N int(10);\neval N=" + expression + ";\nreturn;"));
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Expired_and_foreign_execution_addresses_cannot_call_a_target(bool expire)
    {
        var registry = new RpgProcedurePointerRegistry(); var producerHost = new RpgHost { ProcedurePointers = registry };
        var producer = RpgCompiler.Compile("QGPL", "PRODUCER", "**free\ndcl-s FP pointer(*proc);\neval FP=%paddr(ADD);\nreturn;\n" + Add);
        var origin = new RpgInterpreter(producerHost).Run(producer); var address = origin.ReadValue("FP");
        if (expire) origin.ReleaseProcedureAddresses();
        var receiverHost = new RpgHost { ProcedurePointers = expire ? registry : new() };
        var receiver = RpgCompiler.Compile("QGPL", "RECEIVER", "**free\n" + Main.Replace("eval FN=%paddr(ADD);", ""));
        var context = new RpgRuntimeContext(receiver, receiverHost); context.WriteValue("FN", address);
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(receiverHost).Run(receiver, [], context));
    }
    [Fact]
    public void Paddr_of_a_pointer_based_prototype_returns_the_current_address()
    {
        var context = Run(Main.Replace("dcl-s FN", "dcl-s COPY pointer(*proc);\ndcl-s FN").Replace("eval RESULT=RUN(N);", "eval COPY=%paddr(RUN);\nclear FN;\neval FN=COPY;\neval RESULT=RUN(N);") + Add);
        Assert.Equal(12L, context.ReadValue("RESULT"));
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\ndcl-s FP pointer(*proc);\ndcl-pr RUN extproc(FP) end-pr;\ndcl-s COPY pointer(*proc) inz(%paddr(RUN));"));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_procedure_pointers_work_as_standalone_and_aligned_structure_fields(int shift)
    {
        string D(string name, string definition = "", char type = ' ', string keywords = "", string length = "") =>
            RpgFixedStructureTests.D(name, definition, length: length, type: type, keywords: keywords, shift: shift);
        string P(bool begin)
        { var line = new string(' ', 80 + shift).ToCharArray(); line[5 + shift] = 'P'; "ADD".CopyTo(0, line, 6 + shift, 3); line[23 + shift] = begin ? 'B' : 'E'; return new string(line) + "\n"; }
        var source = D("FP", "S", '*', "PROCPTR") + D("BOX", "DS", keywords: "QUALIFIED INZ") +
            D("PREFIX", length: "1") + D("FN", type: '*', keywords: "PROCPTR") +
            RpgFixedCalculationTests.C("EVAL", expression: "FP=%paddr('ADD')") +
            RpgFixedCalculationTests.C("EVAL", expression: "BOX.FN=FP") + RpgFixedCalculationTests.C("RETURN") +
            P(true) + D("ADD", "PI") + RpgFixedCalculationTests.C("RETURN") + P(false);
        var program = RpgCompiler.Compile("QGPL", "FIXED", source); var context = new RpgInterpreter(new()).Run(program);
        Assert.IsType<RpgProcedureAddress>(context.ReadValue("FP")); Assert.Equal(16, program.FindDs("BOX")!.Find("FN")!.Offset);
    }

    [Theory]
    [InlineData("const")] [InlineData("value")] [InlineData("")]
    public void Pointer_arrays_pass_and_return_with_the_native_aggregate_contract(string mode)
    {
        var source = Main.Replace("FN pointer(*proc)", "FN pointer(*proc) dim(2)").Replace("extproc(FN)", "extproc(FN(2))").Replace("eval FN=%paddr(ADD);", "eval FN=MAKE();") +
            "dcl-proc MAKE;\ndcl-pi *n pointer(*proc) dim(2) rtnparm end-pi;\ndcl-s ITEMS pointer(*proc) dim(2);\neval ITEMS=%paddr(ADD);\nreturn COPY(ITEMS);\nend-proc;\n" +
            "dcl-proc COPY;\ndcl-pi *n pointer(*proc) dim(2);\nITEMS pointer(*proc) dim(2) " + mode + ";\nend-pi;\nreturn ITEMS;\nend-proc;\n" + Add;
        Assert.Equal(12L, Run(source).ReadValue("RESULT"));
    }
    [Fact]
    public void Procedure_address_is_selected_before_arguments_change_the_pointer()
    {
        var source = Main.Replace("P int(10);", "P int(10) const;").Replace("eval RESULT=RUN(N);", "eval RESULT=RUN(SWAP());") +
            "dcl-proc SWAP;\ndcl-pi *n int(10) end-pi;\nclear FN;\nreturn 5;\nend-proc;\n" + Add.Replace("P int(10);", "P int(10) const;").Replace("eval P=P+1;\n", "");
        var context = Run(source); Assert.Equal(10L, context.ReadValue("RESULT")); Assert.True(Assert.IsType<RpgProcedureAddress>(context.ReadValue("FN")).IsNull);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Pointer_members_survive_a_LIKEDS_return_and_call(int ccsid)
    {
        var source = "dcl-ds BOX qualified inz;\nPREFIX char(1);\nFN pointer(*proc);\nend-ds;\n" +
            Main.Replace("dcl-s FN pointer(*proc);", "").Replace("extproc(FN)", "extproc(BOX.FN)").Replace("eval FN=%paddr(ADD);", "eval BOX=MAKE();") +
            "dcl-proc MAKE;\ndcl-pi *n likeds(BOX) end-pi;\ndcl-ds LOCAL likeds(BOX);\neval LOCAL.FN=%paddr(ADD);\nreturn LOCAL;\nend-proc;\n" + Add;
        Assert.Equal(12L, Run(source, ccsid).ReadValue("RESULT"));
    }

    [Fact]
    public void Unknown_pointer_bytes_cannot_resolve_a_procedure()
    {
        var source = "dcl-ds BOX qualified;\nFN pointer(*proc);\nRAW char(16) overlay(FN);\nend-ds;\n" +
            Main.Replace("dcl-s FN pointer(*proc);", "").Replace("extproc(FN)", "extproc(BOX.FN)").Replace("eval FN=%paddr(ADD);", "eval BOX.RAW='ABCDEFGHIJKLMNOP';\nmonitor;").Replace("eval RESULT=RUN(N);", "eval RESULT=RUN(N);\non-error 202;\neval RESULT=99;\nendmon;");
        Assert.Equal(99L, Run(source).ReadValue("RESULT"));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_pointer_PR_PI_parameters_and_return_types_match_the_native_contract(int shift)
    {
        string D(string name, string definition = "", string length = "", char type = ' ', string decimals = "", string keywords = "") =>
            RpgFixedStructureTests.D(name, definition, length: length, type: type, decimals: decimals, keywords: keywords, shift: shift);
        string P(string name, bool begin)
        { var line = new string(' ', 80 + shift).ToCharArray(); line[5 + shift] = 'P'; name.CopyTo(0, line, 6 + shift, name.Length); line[23 + shift] = begin ? 'B' : 'E'; return new string(line) + "\n"; }
        string C(string operation, string expression = "") => RpgFixedCalculationTests.C(operation, expression: expression);
        var source = D("FP", "S", type: '*', keywords: "PROCPTR") + D("N", "S", "10", 'I', "0") +
            C("EVAL", "FP=MAKE()") + C("CALLP", "APPLY(FP)") + C("RETURN") +
            P("MAKE", true) + D("MAKE", "PI", type: '*', keywords: "PROCPTR") + C("RETURN", "%paddr('TARGET')") + P("MAKE", false) +
            P("APPLY", true) + D("APPLY", "PI") + D("PARAM", type: '*', keywords: "PROCPTR CONST") +
            D("RUN", "PR", keywords: "EXTPROC(PARAM)") + C("CALLP", "RUN()") + C("RETURN") + P("APPLY", false) +
            P("TARGET", true) + D("TARGET", "PI") + C("EVAL", "N=7") + C("RETURN") + P("TARGET", false);
        var context = new RpgInterpreter(new()).Run(RpgCompiler.Compile("QGPL", "FIXED", source)); Assert.Equal(7L, context.ReadValue("N"));
    }

    [Fact]
    public void Indexed_structure_array_members_can_supply_a_pointer_target()
    {
        var source = Main.Replace("dcl-s FN pointer(*proc);", "dcl-ds BOX qualified dim(2);\nFN pointer(*proc);\nend-ds;").Replace("extproc(FN)", "extproc(BOX(2).FN)").Replace("eval FN=", "eval BOX(2).FN=");
        Assert.Equal(12L, Run(source + Add).ReadValue("RESULT"));
    }
    [Fact]
    public void A_pointer_to_an_incompatible_CCSID_fails_before_argument_effects()
    {
        var registry = new RpgProcedurePointerRegistry(); var producerHost = new RpgHost { ProcedurePointers = registry, Ccsid = () => 37 };
        var producer = RpgCompiler.Compile("QGPL", "PRODUCER", "**free\ndcl-s FP pointer(*proc);\neval FP=%paddr(ADD);\nreturn;\n" + Add.Replace("P int(10);", "P int(10) const;").Replace("eval P=P+1;\n", ""));
        var origin = new RpgInterpreter(producerHost).Run(producer);
        var receiverHost = new RpgHost { ProcedurePointers = registry, Ccsid = () => 1208 };
        var source = Main.Replace("P int(10);", "P int(10) const;").Replace("eval FN=%paddr(ADD);", "").Replace("dcl-s N", "dcl-s EFFECT int(10);\ndcl-s N").Replace("RUN(N)", "RUN(SIDE())") +
            "dcl-proc SIDE;\ndcl-pi *n int(10) end-pi;\neval EFFECT=1;\nreturn 1;\nend-proc;";
        var receiver = RpgCompiler.Compile("QGPL", "RECEIVER", "**free\n" + source); var context = new RpgRuntimeContext(receiver, receiverHost); context.WriteValue("FN", origin.ReadValue("FP"));
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(receiverHost).Run(receiver, [], context)); Assert.Equal(0L, context.ReadValue("EFFECT"));
    }

    [Fact]
    public void Literal_entry_names_are_case_sensitive()
        => Assert.Throws<RpgCompileException>(() => Run(Main.Replace("%paddr(ADD)", "%paddr('add')") + Add));

}
