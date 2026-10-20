using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgDataBindingTests
{
    private static RpgProgram Module(string name, string source) => RpgCompiler.CompileModule("QGPL", name, new SourceDocument("SRC/" + name, "**free\n" + source));
    private static RpgRuntimeContext Run(string entry, string provider, int ccsid = 37)
    {
        var main = Module("MAIN", entry); var helper = Module("HELPER", "ctl-opt nomain;\n" + provider);
        RpgCompiler.BindModules([main, helper], main); return new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(main);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Static_data_imports_and_procedures_share_exporter_storage(int ccsid)
    {
        var context = Run("dcl-s LOCAL int(10) import('COUNTER');\ndcl-pr GET int(10);\nend-pr;\ndcl-s RESULT int(10);\neval LOCAL=LOCAL+2;\neval RESULT=GET();\nreturn;",
            "dcl-s COUNTER int(10) inz(40) export;\ndcl-proc GET export;\ndcl-pi *n int(10) end-pi;\nreturn COUNTER;\nend-proc;", ccsid);
        Assert.Equal(42L, context.ReadValue("LOCAL")); Assert.Equal(42L, context.ReadValue("RESULT"));
    }
    [Theory]
    [InlineData("char(4)", "'TEXT'", "'LIVE'", "LIVE")]
    [InlineData("packed(9:2)", "12.25", "21.75", "21.75")]
    [InlineData("int(3)", "1", "12", "12")]
    public void Scalar_arrays_are_live_byte_aliases(string type, string initial, string value, string expected)
    {
        var context = Run($"dcl-s ITEMS {type} dim(3) import('VALUES');\neval ITEMS(2)={value};\nreturn;", $"dcl-s VALUES {type} dim(3) inz({initial}) export;");
        Assert.Equal(expected, Convert.ToString(context.ReadArrayValue("ITEMS", 2), global::System.Globalization.CultureInfo.InvariantCulture)?.Trim());
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_structure_array_imports_preserve_layout_and_allow_different_local_names(int ccsid)
    {
        const string members = "dcl-ds CHILD dim(2);\nN int(10);\nTEXT char(4);\nend-ds;\n";
        var context = Run("dcl-ds LOCAL qualified dim(2) import('SHARED');\n" + members + "end-ds;\neval LOCAL(2).CHILD(1).N=77;\neval LOCAL(2).CHILD(2).TEXT='DATA';\nreturn;",
            "dcl-ds SHARED qualified dim(2) export;\n" + members + "end-ds;", ccsid);
        Assert.Equal(77L, RpgExpressionParser.Parse("LOCAL(2).CHILD(1).N").Eval(context));
        Assert.Equal("DATA", RpgExpressionParser.Parse("LOCAL(2).CHILD(2).TEXT").Eval(context));
    }
    [Fact]
    public void Multiple_occurrence_imports_share_all_bytes_and_keep_local_occurrence_numbers()
    {
        var context = Run("dcl-ds LOCAL qualified occurs(3) import('SHARED');\nN int(10);\nend-ds;\ndcl-pr CHECK int(10);\nend-pr;\ndcl-s RESULT int(10);\neval %occur(LOCAL)=2;\neval LOCAL.N=27;\neval RESULT=CHECK();\nreturn;",
            "dcl-ds SHARED qualified occurs(3) export;\nN int(10) inz(7);\nend-ds;\ndcl-proc CHECK export;\ndcl-pi *n int(10) end-pi;\nif %occur(SHARED)=1;\neval %occur(SHARED)=2;\nreturn SHARED.N;\nendif;\nreturn -1;\nend-proc;");
        Assert.Equal(27L, context.ReadValue("RESULT")); Assert.Equal(2L, Convert.ToInt64(RpgExpressionParser.Parse("%occur(LOCAL)").Eval(context)));
    }
    [Fact]
    public void Nullable_imports_share_live_automatic_null_indicators()
    {
        var context = Run("ctl-opt alwnull(*usrctl);\ndcl-s LOCAL char(4) nullind import('SHARED');\ndcl-pr CHECK int(10);\nend-pr;\ndcl-s RESULT int(10);\neval LOCAL='DATA';\neval %nullind(LOCAL)=*on;\neval RESULT=CHECK();\nreturn;",
            "ctl-opt alwnull(*usrctl);\ndcl-s SHARED char(4) nullind export;\ndcl-proc CHECK export;\ndcl-pi *n int(10) end-pi;\nif %nullind(SHARED) and SHARED='DATA';\nreturn 1;\nendif;\nreturn 0;\nend-proc;");
        Assert.Equal(1L, context.ReadValue("RESULT"));
    }
    [Fact]
    public void Exported_entry_module_data_is_initialized_once_across_LR_restarts()
    {
        var main = Module("MAIN", "dcl-s COUNT int(10) inz(10) export;\ndcl-s ORDINARY int(10) inz(1);\neval COUNT=COUNT+1;\neval ORDINARY=ORDINARY+1;\nseton LR;");
        RpgCompiler.BindModules([main], main); var interpreter = new RpgInterpreter(new()); var first = interpreter.Run(main); var restarted = first.RestartCycle();
        var second = interpreter.Run(main, existingContext: restarted); Assert.Equal(12L, second.ReadValue("COUNT")); Assert.Equal(2L, second.ReadValue("ORDINARY"));
    }
    [Theory]
    [InlineData("export(*DCLCASE)", "import('MiXeD')")]
    [InlineData("export('MiXeD')", "import('MiXeD')")]
    [InlineData("export(SYMBOL)", "import('MiXeD')")]
    public void External_data_names_preserve_literal_constant_and_declared_case(string export, string import)
    {
        var context = Run($"dcl-s LOCAL int(10) {import};\nreturn;", $"dcl-c SYMBOL 'MiXeD';\ndcl-s MiXeD int(10) inz(9) {export};");
        Assert.Equal(9L, context.ReadValue("LOCAL"));
    }
    [Theory]
    [InlineData("dcl-s X int(10) import inz(1);")]
    [InlineData("dcl-s X int(10) import export;")]
    [InlineData("dcl-s X int(10) export template;")]
    [InlineData("dcl-s X int(10) import dim(2) ctdata;")]
    [InlineData("dcl-s X int(10) export(7);")]
    [InlineData("dcl-s X int(10) export('BAD-NAME');")]
    [InlineData("dcl-ds *n export;\nX int(10);\nend-ds;")]
    [InlineData("dcl-ds X qualified import;\nN int(10) inz(1);\nend-ds;")]
    [InlineData("dcl-ds X qualified import;\ndcl-ds CHILD inz;\nN int(10);\nend-ds;\nend-ds;")]
    [InlineData("dcl-s X int(10) export('SAME');\ndcl-s Y int(10) import('SAME');")]
    public void Invalid_data_binding_declarations_reject_with_original_locations(string source)
    {
        var error = Assert.Throws<RpgCompileException>(() => Module("BAD", source)); Assert.Equal("SRC/BAD", error.Location!.Source);
    }
    [Fact]
    public void Procedure_local_imports_and_exports_are_rejected()
    {
        foreach (var keyword in new[] { "import", "export" })
            Assert.Throws<RpgCompileException>(() => Module("BAD", "dcl-proc LOCAL;\ndcl-s X int(10) " + keyword + ";\nend-proc;"));
    }
    [Theory]
    [InlineData("missing")] [InlineData("type")] [InlineData("dimension")] [InlineData("case")] [InlineData("duplicate")] [InlineData("procedure")]
    public void Missing_duplicate_or_incompatible_data_exports_fail_before_links_publish(string defect)
    {
        var main = Module("MAIN", "dcl-s LOCAL int(10) import('SHARED');\nreturn;");
        var source = defect switch { "missing" => "dcl-s SHARED int(10);", "type" => "dcl-s SHARED char(4) export;", "dimension" => "dcl-s SHARED int(10) dim(2) export;", "case" => "dcl-s SHARED int(10) export('shared');", "procedure" => "dcl-proc SHARED export;\nend-proc;", _ => "dcl-s SHARED int(10) export;" };
        var helper = Module("HELPER", "ctl-opt nomain;\n" + source); IReadOnlyList<RpgProgram> modules = defect == "duplicate" ? [main, helper, Module("OTHER", "ctl-opt nomain;\n" + source)] : [main, helper];
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules(modules, main)); Assert.False(main.IsBoundProgram);
    }
    [Fact]
    public void Captured_static_images_restore_data_links_without_original_modules()
    {
        var main = Module("MAIN", "dcl-s LOCAL int(10) import('SHARED');\neval LOCAL=LOCAL+1;\nreturn;"); var helper = Module("HELPER", "ctl-opt nomain;\ndcl-s SHARED int(10) inz(8) export;");
        var image = new RpgBoundProgram(1, "QGPL/MAIN", [RpgModuleImage.Capture(main), RpgModuleImage.Capture(helper)]);
        var restored = RpgBoundProgram.Deserialize(image.Serialize()).Restore(); Assert.Equal(9L, new RpgInterpreter(new()).Run(restored).ReadValue("LOCAL"));
    }
    [Fact]
    public void Ordinary_compilation_rejects_imports_without_a_binding_phase()
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\ndcl-s X int(10) import;\nreturn;"));
    [Fact]
    public void Borrowed_service_data_memory_and_null_maps_expire_when_the_service_is_disposed()
    {
        var module = Module("HELPER", "ctl-opt nomain;\ndcl-s SHARED int(10) inz(9) export;");
        var exports = RpgCompiler.ModuleDataExports([module]);
        var definition = new RpgServiceDefinition([module], [RpgCompiler.GeneratedExports(["SHARED"])], new Dictionary<string, RpgSubprocedure>()) { Data = exports };
        var runtime = new RpgServiceRuntime(definition); var data = runtime.DataStorage(exports["SHARED"], new()); runtime.Dispose();
        Assert.Throws<RpgRuntimeException>(() => data.Memory.Read(0, 4)); Assert.Throws<RpgRuntimeException>(() => data.Nulls.Read(0));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_standalone_definitions_bind_data_in_both_supported_column_origins(int shift)
    {
        var mainSource = RpgFixedStructureTests.D("LOCAL", "S", length: "10", type: 'I', decimals: "0", keywords: "IMPORT('SHARED')", shift: shift) + "/free\neval LOCAL=LOCAL+1;\nreturn;\n/end-free";
        var providerSource = "     H NOMAIN\n" + RpgFixedStructureTests.D("SHARED", "S", length: "10", type: 'I', decimals: "0", keywords: "EXPORT INZ(8)", shift: shift);
        var main = RpgCompiler.CompileModule("QGPL", "MAIN", new SourceDocument("MAIN", mainSource));
        var provider = RpgCompiler.CompileModule("QGPL", "HELPER", new SourceDocument("HELPER", providerSource));
        RpgCompiler.BindModules([main, provider], main); Assert.Equal(9L, new RpgInterpreter(new()).Run(main).ReadValue("LOCAL"));
    }
    [Fact]
    public void Exported_module_storage_is_initialized_before_main_can_change_the_job_CCSID()
    {
        var main = Module("MAIN", "dcl-s LOCAL char(2) import('SHARED');\ndcl-s RESULT char(2);\nsndpgmmsg 'CHANGE';\neval RESULT=LOCAL;\nreturn;");
        var provider = Module("HELPER", "ctl-opt nomain;\ndcl-s SHARED char(2) inz('é') export;"); RpgCompiler.BindModules([main, provider], main);
        var ccsid = 37; var host = new RpgHost { Ccsid = () => ccsid, SendMessage = _ => ccsid = 1208 };
        var result = new RpgInterpreter(host).Run(main); Assert.Equal("é ", result.ReadValue("RESULT"));
    }
}
