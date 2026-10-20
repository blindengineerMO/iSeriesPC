using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgModuleBindingTests
{
    private static RpgProgram Module(string name, string source) => RpgCompiler.CompileModule("QGPL", name, new SourceDocument("SRC/" + name, "**free\n" + source));
    private const string Main = "dcl-pr BUMP;\nP int(10);\nend-pr;\ndcl-s COUNT int(10) inz(100);\ndcl-s N int(10) inz(3);\ncallp BUMP(N);\ncallp BUMP(N);\nreturn;";
    private const string Helper = "ctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc BUMP export;\ndcl-pi *n;\nP int(10);\nend-pi;\neval COUNT=COUNT+1;\neval P=P+COUNT;\nend-proc;";
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Bound_procedures_share_the_activation_but_keep_separate_module_globals(int ccsid)
    {
        var main = Module("MAIN", Main); var helper = Module("HELPER", Helper); RpgCompiler.BindModules([main, helper], main);
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(main);
        Assert.Equal(6L, context.ReadValue("N")); Assert.Equal(100L, context.ReadValue("COUNT"));
    }
    [Fact]
    public void Modules_cannot_execute_until_bound_and_normal_programs_still_require_local_resolution()
    {
        var module = Module("MAIN", Main); Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(module));
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "MAIN", "**free\n" + Main));
    }
    [Theory]
    [InlineData("ctl-opt nomain;\nsndpgmmsg 'BAD';")]
    [InlineData("ctl-opt nomain;\ndcl-pi *n end-pi;")]
    [InlineData("ctl-opt nomain(1);")]
    public void Invalid_nomain_modules_fail_with_the_original_source_location(string source)
    {
        var error = Assert.Throws<RpgCompileException>(() => Module("HELPER", source)); Assert.Equal("SRC/HELPER", error.Location!.Source);
    }
    [Fact]
    public void A_nomain_module_cannot_be_the_program_entry()
    {
        var helper = Module("HELPER", Helper); Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules([helper], helper));
    }
    [Theory]
    [InlineData("missing")] [InlineData("private")] [InlineData("duplicate")] [InlineData("mode")] [InlineData("return")]
    public void Missing_private_duplicate_or_incompatible_exports_reject_binding(string defect)
    {
        var main = Module("MAIN", Main); var source = Helper;
        if (defect == "private") source = source.Replace("BUMP export", "BUMP");
        if (defect == "mode") source = source.Replace("P int(10);", "P int(10) const;").Replace("eval P=P+COUNT;", "");
        if (defect == "return") source = source.Replace("dcl-pi *n;", "dcl-pi *n int(10);").Replace("eval P=P+COUNT;", "return P;");
        var helper = Module("HELPER", source);
        IReadOnlyList<RpgProgram> modules = defect == "missing" ? [main] : defect == "duplicate" ? [main, helper, Module("OTHER", Helper)] : [main, helper];
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules(modules, main));
    }
    [Fact]
    public void Export_matching_is_case_sensitive_and_private_helpers_remain_local()
    {
        var main = Module("MAIN", Main.Replace("dcl-pr BUMP;", "dcl-pr BUMP extproc('bump');")); var helper = Module("HELPER", Helper);
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules([main, helper], main));
    }
    [Fact]
    public void Native_function_results_and_cross_module_callbacks_use_the_owning_global_context()
    {
        var main = Module("MAIN", "dcl-pr GET int(10);\nend-pr;\ndcl-s RESULT int(10);\neval RESULT=GET();\nreturn;\ndcl-proc CALLBACK export;\ndcl-pi *n int(10) end-pi;\nreturn 7;\nend-proc;");
        var helper = Module("HELPER", "ctl-opt nomain;\ndcl-pr CALLBACK int(10);\nend-pr;\ndcl-proc GET export;\ndcl-pi *n int(10) end-pi;\nreturn CALLBACK()+1;\nend-proc;");
        RpgCompiler.BindModules([main, helper], main); var context = new RpgInterpreter(new()).Run(main); Assert.Equal(8L, context.ReadValue("RESULT"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Null_maps_borrow_live_flags_across_module_procedure_calls(bool splitControls)
    {
        var main = Module("MAIN", "ctl-opt alwnull(*usrctl);\ndcl-pr BUMP;\nP char(4) options(*nullind);\nend-pr;\ndcl-s TEXT char(4) nullind;\ncallp BUMP(TEXT);\nreturn;");
        var helper = Module("HELPER", (splitControls ? "ctl-opt nomain;\nctl-opt alwnull(*usrctl);" : "ctl-opt nomain alwnull(*usrctl);") + "\ndcl-proc BUMP export;\ndcl-pi *n;\nP char(4) options(*nullind);\nend-pi;\nevalr P='X';\neval %nullind(P)=*on;\nend-proc;");
        RpgCompiler.BindModules([main, helper], main); var context = new RpgInterpreter(new()).Run(main); Assert.Equal("   X", context.ReadValue("TEXT")); Assert.Equal(true, RpgExpressionParser.Parse("%nullind(TEXT)").Eval(context));
    }
    [Fact]
    public void A_failed_rebind_keeps_the_previous_successful_link_intact()
    {
        var main = Module("MAIN", Main); var helper = Module("HELPER", Helper); RpgCompiler.BindModules([main, helper], main);
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules([main], main));
        Assert.Equal(6L, new RpgInterpreter(new()).Run(main).ReadValue("N"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Bound_source_limits_count_UTF8_bytes_before_serialization(bool multibyte)
    {
        var source = new string(multibyte ? 'é' : 'X', multibyte ? 2100000 : 4194305);
        var image = new RpgModuleImage("QGPL", "MAIN", source, "", "", "", "");
        Assert.Throws<InvalidDataException>(() => new RpgBoundProgram(1, "QGPL/MAIN", [image]).Serialize());
    }
    [Fact]
    public void Prototyped_CALLB_uses_the_native_bound_procedure_path()
    {
        var main = Module("MAIN", Main.Replace("callp", "callb")); var helper = Module("HELPER", Helper);
        RpgCompiler.BindModules([main, helper], main); Assert.Equal(6L, new RpgInterpreter(new()).Run(main).ReadValue("N"));
    }
    [Fact]
    public void Unprototyped_module_CALLB_rejects_before_it_can_use_a_legacy_dispatch_path()
    {
        var error = Assert.Throws<RpgCompileException>(() => Module("MAIN", "callb UNKNOWN();")); Assert.NotNull(error.Location); Assert.Contains("prototype", error.Message);
    }
    [Theory]
    [InlineData(0)] [InlineData(17)]
    public void Bound_manifest_module_counts_are_bounded(int count)
    {
        var images = Enumerable.Range(0, count).Select(i => new RpgModuleImage("QGPL", "M" + i, "", "", "", "", "")).ToArray();
        Assert.Throws<InvalidDataException>(() => new RpgBoundProgram(1, "QGPL/M0", images).Serialize());
    }
    [Fact]
    public void Cancelled_binding_does_not_publish_any_links()
    {
        var main = Module("MAIN", Main); var helper = Module("HELPER", Helper); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RpgCompiler.BindModules([main, helper], main, cancelled.Token)); Assert.False(main.IsBoundProgram);
    }
    [Theory]
    [InlineData(0)] [InlineData(17)]
    public void Binder_module_counts_are_bounded(int count)
    {
        var main = Module("MAIN", Main); var modules = Enumerable.Range(0, count).Select(i => Module("M" + i, Helper.Replace("BUMP", "F" + i))).ToArray();
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindModules(modules, main));
    }
}
