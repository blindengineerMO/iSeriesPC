using Ipc.Core.Messages;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed class RpgCallErrorTests
{
    private static string Declarations(bool native) => (native ? "       " : "**free\n") + "dcl-s ARR int(3) dim(2) inz(7);\n" +
        (native ? "       " : "") + "dcl-s HIT ind;\n" + (native ? "       " : "") + "dcl-s FIRST ind;\n";
    private static string Call(bool native, string name, string argument = "", bool error = true) => native ? C(error ? "CALLP(E)" : "CALLP", expression: name + "(" + argument + ")") :
        "callp" + (error ? "(e)" : "") + " " + name + "(" + argument + ");\n";
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(false, "ARR(99)", 121)] [InlineData(true, "ARR(99)", 121)]
    [InlineData(false, "1/0", 102)] [InlineData(true, "1/0", 102)]
    [InlineData(false, "ARR(*)", 105)] [InlineData(true, "ARR(*)", 105)]
    public void CALLP_E_does_not_mask_parameter_processing_errors_and_MONITOR_keeps_the_original_status(bool native, string argument, int status)
    {
        var source = Declarations(native) + (native ? C("MONITOR") : "monitor;\n") + Call(native, "TARGET", argument) +
            (native ? C("ON-ERROR", expression: status.ToString()) + C("EVAL", expression: "HIT=*ON") + C("ENDMON") : "on-error " + status + ";\neval HIT=*on;\nendmon;\n");
        var entered = false; var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => { entered = true; return new() { Success = true }; } };
        var context = new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "ERRCALL", source));
        Assert.False(entered); Assert.Equal(true, context.ReadValue("HIT")); Assert.Equal(status, Read(context, "%status()")); Assert.Equal(false, Read(context, "%error()"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CALLP_E_handles_final_target_failure_before_MONITOR_and_a_later_success_resets_ERROR(bool native)
    {
        var source = Declarations(native) + (native ? C("MONITOR") : "monitor;\n") + Call(native, "BAD") +
            (native ? C("ON-ERROR", expression: "202") + C("EVAL", expression: "HIT=*ON") + C("ENDMON") + C("EVAL", expression: "FIRST=%ERROR") : "on-error 202;\neval HIT=*on;\nendmon;\neval FIRST=%error();\n") + Call(native, "GOOD");
        var host = new RpgHost { TypedProgramCaller = (_, name, _, _) => new() { Success = name == "GOOD", Message = "Expected failure." } };
        var context = new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "ERRCALL", source));
        Assert.Equal(false, context.ReadValue("HIT")); Assert.Equal(true, context.ReadValue("FIRST")); Assert.Equal(false, Read(context, "%error()")); Assert.Equal(0, Read(context, "%status()"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Unresolved_targets_and_CPF_failures_report_202_for_the_final_call(bool serviceError)
    {
        var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => serviceError ? throw new CpfException("CPF9898", "Expected failure.") : null };
        var context = new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "ERRCALL", "**free\ncallp(e) TARGET();"));
        Assert.Equal(202, Read(context, "%status()")); Assert.Equal(true, Read(context, "%error()"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Unextended_CALLP_raises_an_unresolved_target_instead_of_succeeding(bool prototype)
    {
        var source = "**free\n" + (prototype ? "dcl-pr TARGET extpgm end-pr;\n" : "") + "callp TARGET();";
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => null }).Run(RpgCompiler.Compile("QGPL", "ERRCALL", source)));
        Assert.Equal(202, error.StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cancellation_and_ENDJOB_are_never_consumed_by_CALLP_E(bool cpf)
    {
        var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => cpf ? throw new CpfException("CPF1241", "Job cancelled.") : throw new OperationCanceledException() };
        var program = RpgCompiler.Compile("QGPL", "ERRCALL", "**free\ncallp(e) TARGET();");
        if (cpf) Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
        else Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program));
    }

    [Theory]
    [InlineData("CALLP(N)")] [InlineData("CALLP(EE)")] [InlineData("CALLP(M)")] [InlineData("CALLP(R)")] [InlineData("CALLP(EN)")] [InlineData("CALLP()")]
    public void Unsupported_or_repeated_free_call_extenders_reject_at_compilation(string opcode)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "ERRCALL", "**free\n" + opcode + " TARGET();")).Location); }

    [Theory]
    [InlineData("callp;")] [InlineData("callp(e);")]
    public void Empty_CALLP_targets_reject_at_compilation(string statement)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "ERRCALL", "**free\n" + statement)).Location); }
}
