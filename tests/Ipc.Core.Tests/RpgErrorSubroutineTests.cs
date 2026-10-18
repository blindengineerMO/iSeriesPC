using Ipc.Core.Messages;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgErrorSubroutineTests
{
    private const string Header = "**free\ndcl-s N int(10);\ndcl-ds P psds qualified;\nS *status;\nR *routine;\nend-ds;\n";
    private static RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "ERRORSR", Header + body);
    private static RpgRuntimeContext Run(string body, List<string> messages) => new RpgInterpreter(new() { SendMessage = messages.Add }).Run(Compile(body));
    private static string Procedure(string name, char marker)
    { var line = new string(' ', 80).ToCharArray(); line[6] = 'P'; name.CopyTo(0, line, 7, name.Length); line[23] = marker; return new(line); }

    [Fact]
    public void Program_error_dispatches_PSSR_after_feedback_and_RETURN_ends_the_program()
    {
        var messages = new List<string>(); var context = Run("eval N=1/0;\nsndpgmmsg 'BAD CONTINUATION';\nbegsr *pssr;\nsndpgmmsg %char(P.S)+':'+%trim(P.R);\nreturn;\nendsr;", messages);
        Assert.Equal(new[] { "102:*DETC" }, messages); Assert.Equal(102, RpgExpressionParser.Parse("%status").Eval(context));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blank_ENDSR_uses_default_failure_automatically_but_returns_after_explicit_EXSR(bool explicitCall)
    {
        var messages = new List<string>(); var program = Compile((explicitCall ? "exsr *pssr;" : "eval N=1/0;") +
            "\nsndpgmmsg 'AFTER';\nreturn;\nbegsr *pssr;\nsndpgmmsg 'HANDLER';\nendsr;");
        var interpreter = new RpgInterpreter(new() { SendMessage = messages.Add });
        if (explicitCall) { interpreter.Run(program); Assert.Equal(new[] { "HANDLER", "AFTER" }, messages); }
        else { Assert.Equal(102, Assert.Throws<RpgRuntimeException>(() => interpreter.Run(program)).StatusCode); Assert.Equal(new[] { "HANDLER" }, messages); }
    }
    [Theory]
    [InlineData("eval N=1/0;")]
    [InlineData("exsr FAIL;")]
    [InlineData("exsr OUTER;")]
    public void Same_procedure_MONITOR_precedes_PSSR_even_through_nested_EXSR(string operation)
    {
        var messages = new List<string>(); Run("monitor;\n" + operation + "\non-error 102;\nsndpgmmsg 'MONITOR';\nendmon;\nreturn;\n" +
            "begsr FAIL;\neval N=1/0;\nendsr;\nbegsr OUTER;\nexsr FAIL;\nendsr;\nbegsr *pssr;\nsndpgmmsg 'BAD PSSR';\nreturn;\nendsr;", messages);
        Assert.Equal(new[] { "MONITOR" }, messages);
    }
    [Fact]
    public void Nonmatching_monitor_falls_through_to_PSSR()
    {
        var messages = new List<string>(); Run("monitor;\neval N=1/0;\non-error 105;\nsndpgmmsg 'BAD MONITOR';\nendmon;\nreturn;\n" +
            "begsr *pssr;\nsndpgmmsg 'PSSR';\nreturn;\nendsr;", messages); Assert.Equal(new[] { "PSSR" }, messages);
    }
    [Fact]
    public void Default_failure_through_nested_EXSR_runs_the_PSSR_once()
    {
        var messages = new List<string>();
        Assert.Equal(102, Assert.Throws<RpgRuntimeException>(() => Run("exsr OUTER;\nreturn;\nbegsr OUTER;\nexsr FAIL;\nendsr;\nbegsr FAIL;\neval N=1/0;\nendsr;\n" +
            "begsr *pssr;\nsndpgmmsg 'ONCE';\nendsr;", messages)).StatusCode);
        Assert.Equal(new[] { "ONCE" }, messages);
    }
    [Theory]
    [InlineData("'*CANCL'")]
    [InlineData("POINT")]
    [InlineData("POINTS(2)")]
    [InlineData("INFO.POINT")]
    [InlineData("INFO.POINTS(2)")]
    [InlineData("ARRAY(2).POINT")]
    public void CANCL_reports_RNX9001_and_resets_a_variable_return_point(string point)
    {
        var declarations = "dcl-s POINT char(6) inz('*CANCL');\ndcl-ds EXPORTED;\nPOINTS char(6) dim(2) inz('*CANCL');\nend-ds;\ndcl-ds INFO qualified;\nPOINT char(6) inz('*CANCL');\nPOINTS char(6) dim(2) inz('*CANCL');\nend-ds;\ndcl-ds ARRAY qualified dim(2);\nPOINT char(6) inz('*CANCL');\nend-ds;\n";
        var program = Compile(declarations + "exsr *pssr;\nreturn;\nbegsr *pssr;\nendsr " + point + ";"); var context = new RpgRuntimeContext(program, new());
        Assert.Equal("RNX9001", Assert.Throws<CpfException>(() => new RpgInterpreter(new()).Run(program, existingContext: context)).MessageId);
        Assert.True(context.IsCancelled);
        if (point[0] != '\'') Assert.Equal("      ", RpgExpressionParser.Parse(point).Eval(context));
    }
    [Theory]
    [InlineData("'*DETC'")]
    [InlineData("'*GETIN'")]
    [InlineData("'*TOTC'")]
    [InlineData("'*TOTL'")]
    [InlineData("'*DETL'")]
    [InlineData("'*OFL'")]
    [InlineData("'*cancl'")]
    [InlineData("N")]
    [InlineData("1")]
    [InlineData("%char(N)")]
    public void Unsupported_cycle_points_and_invalid_types_fail_with_source_locations(string point)
        => Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("return;\nbegsr *pssr;\nendsr " + point + ";")).Location);
    [Fact]
    public void ENDSR_point_on_an_ordinary_subroutine_is_rejected()
        => Assert.Throws<RpgCompileException>(() => Compile("return;\nbegsr NORMAL;\nendsr '*CANCL';"));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Each_procedure_has_its_own_PSSR_and_callee_handler_precedes_caller_monitor(bool localReturn)
    {
        var body = "monitor;\ncallp LOCAL();\nsndpgmmsg 'RETURNED';\non-error 202;\nsndpgmmsg 'CALLER';\nendmon;\nreturn;\n" +
            "begsr *pssr;\nsndpgmmsg 'BAD MAIN';\nreturn;\nendsr;\n" + Procedure("LOCAL", 'B') +
            "\neval N=1/0;\nbegsr *pssr;\nsndpgmmsg 'LOCAL';\n" + (localReturn ? "return;\n" : "") + "endsr;\n" + Procedure("LOCAL", 'E');
        var messages = new List<string>(); Run(body, messages);
        Assert.Equal(localReturn ? new[] { "LOCAL", "RETURNED" } : new[] { "LOCAL", "CALLER" }, messages);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_subroutine_GOTO_recovers_in_its_own_procedure_and_preserves_control_state(bool local)
    {
        var body = "eval N=1/0;\ntag RECOVER;\nsndpgmmsg 'RECOVERED';\nreturn;\nbegsr *pssr;\ngoto RECOVER;\nendsr;\n";
        if (local) body = "callp LOCAL();\nreturn;\n" + Procedure("LOCAL", 'B') + "\n" + body + Procedure("LOCAL", 'E');
        var messages = new List<string>(); Run(body, messages); Assert.Equal(new[] { "RECOVERED" }, messages);
    }
    [Fact]
    public void Recursive_PSSR_errors_remain_cancellable_and_have_a_bounded_stack()
    {
        var messages = new List<string>(); var error = Assert.Throws<RpgRuntimeException>(() => Run("eval N=1/0;\nreturn;\nbegsr *pssr;\neval N=1/0;\nendsr;", messages));
        Assert.Equal(9999, error.StatusCode); Assert.Contains("64", error.Message);
    }
    [Fact]
    public void A_handler_can_handle_its_own_expected_error_with_MONITOR()
    {
        var messages = new List<string>(); Run("eval N=1/0;\nreturn;\nbegsr *pssr;\nmonitor;\neval N=1/0;\non-error 102;\nsndpgmmsg 'NESTED';\nendmon;\nreturn;\nendsr;", messages);
        Assert.Equal(new[] { "NESTED" }, messages);
    }
    [Fact]
    public void Job_end_is_not_caught_or_reissued_to_PSSR()
    {
        var calls = 0; var program = Compile("callp STOP();\nreturn;\nbegsr *pssr;\nsndpgmmsg 'BAD';\nreturn;\nendsr;");
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => new RpgInterpreter(new() {
            ProgramCaller = (_, _, _) => throw new CpfException("CPF1241", "Job ended"), SendMessage = _ => calls++ }).Run(program)).MessageId);
        Assert.Equal(0, calls);
    }
    [Fact]
    public void Cancellation_during_PSSR_escapes_the_handler()
    {
        using var cancellation = new CancellationTokenSource();
        var program = Compile("eval N=1/0;\nreturn;\nbegsr *pssr;\nsndpgmmsg 'CANCEL';\neval N=1/0;\nendsr;");
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(new() {
            CancellationToken = cancellation.Token, SendMessage = _ => cancellation.Cancel() }).Run(program));
    }
    [Fact]
    public void A_variable_cycle_return_point_is_reset_and_rejected_with_the_ENDSR_source_location()
    {
        var program = Compile("dcl-s POINT char(6) inz('*DETC');\nexsr *pssr;\nreturn;\nbegsr *pssr;\nendsr POINT;");
        var context = new RpgRuntimeContext(program, new());
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(program, existingContext: context));
        Assert.Equal(9999, error.StatusCode); Assert.Equal(program.Subroutines.Single().Ending!.Location, error.Location);
        Assert.Equal("      ", context.ReadValue("POINT")); Assert.True(context.IsCancelled);
    }
    [Fact]
    public void Subprocedure_ENDSR_return_points_are_rejected()
        => Assert.Throws<RpgCompileException>(() => Compile("return;\n" + Procedure("LOCAL", 'B') + "\nreturn;\nbegsr *pssr;\nendsr '*CANCL';\n" + Procedure("LOCAL", 'E')));
    [Fact]
    public void Error_subroutine_GOTO_cannot_enter_a_monitor_handler()
        => Assert.Throws<RpgCompileException>(() => Compile("monitor;\neval N=1/0;\non-error 105;\ntag INSIDE;\nendmon;\nreturn;\nbegsr *pssr;\ngoto INSIDE;\nendsr;"));
    [Fact]
    public void Handler_local_GOTO_and_label_names_are_case_insensitive()
    {
        var messages = new List<string>(); Run("eval N=1/0;\nreturn;\nbegsr *pssr;\ngoto inside;\nsndpgmmsg 'BAD';\ntag INSIDE;\nsndpgmmsg 'LOCAL LABEL';\nreturn;\nendsr;", messages);
        Assert.Equal(new[] { "LOCAL LABEL" }, messages);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_GOTO_preserves_enclosing_FOR_and_SELECT_frames(bool selected)
    {
        var body = selected ?
            "for N=1 to 2;\nselect;\nwhen N=1;\neval P.S=1/0;\ntag FIRST;\nsndpgmmsg '1';\nwhen N=2;\neval P.S=1/0;\ntag SECOND;\nsndpgmmsg '2';\nendsl;\nendfor;\nreturn;\n" +
            "begsr *pssr;\nif N=1;\ngoto FIRST;\nelse;\ngoto SECOND;\nendif;\nendsr;" :
            "for N=1 to 2;\neval P.S=1/0;\ntag RECOVER;\nsndpgmmsg %char(N);\nendfor;\nreturn;\nbegsr *pssr;\ngoto RECOVER;\nendsr;";
        var messages = new List<string>(); Run(body, messages); Assert.Equal(new[] { "1", "2" }, messages);
    }
}
