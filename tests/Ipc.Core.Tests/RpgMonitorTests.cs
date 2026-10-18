using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Fact]
    public void MONITOR_selects_the_first_matching_handler_and_resumes_after_ENDMON()
    {
        Compile(UpdateFile(extra: "usropn") + "monitor;\nread DATA;\nsndpgmmsg 'BAD BODY';\non-error 102;\nsndpgmmsg 'BAD FILTER';\n" +
            "on-error 1211;\nsndpgmmsg 'MATCHED';\non-error *all;\nsndpgmmsg 'BAD DEFAULT';\nendmon;\nsndpgmmsg 'AFTER';\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "MATCHED"); Assert.Contains("AFTER", result.Message); Assert.DoesNotContain("BAD", result.Message);
    }
    [Theory]
    [InlineData("*FILE")]
    [InlineData("*ALL")]
    [InlineData("102:1211")]
    [InlineData("01211")]
    [InlineData("")]
    public void MONITOR_supports_file_specific_lists_and_catch_all_filters(string filter)
    {
        Compile(UpdateFile(extra: "usropn") + "monitor;\nread DATA;\non-error " + filter + ";\nsndpgmmsg %char(%status);\nendmon;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "1211");
    }
    [Fact]
    public void E_takes_precedence_over_MONITOR_for_expected_file_errors()
    {
        Compile(UpdateFile(extra: "usropn") + "monitor;\nread(e) DATA;\nif %error and %status=1211;\nsndpgmmsg 'E HANDLED';\nendif;\n" +
            "on-error *file;\nsndpgmmsg 'BAD HANDLER';\nendmon;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "E HANDLED"); Assert.DoesNotContain("BAD", result.Message);
    }
    [Fact]
    public void A_program_error_in_a_file_operand_escapes_E_and_reaches_the_program_handler()
    {
        Compile(UpdateFile() + "monitor;\nchain(e) (1/0) DATA;\nsndpgmmsg 'BAD E';\non-error 102;\n" +
            "if not %error and %status=102 and %status(DATA)=0;\nsndpgmmsg 'PROGRAM ERROR';\nendif;\nendmon;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "PROGRAM ERROR"); Assert.DoesNotContain("BAD", result.Message);
    }
    [Fact]
    public void An_error_in_a_handler_bypasses_that_group_and_reaches_the_enclosing_monitor()
    {
        Compile(UpdateFile(extra: "usropn") + "monitor;\nmonitor;\nread DATA;\non-error 1211;\neval ID=1/0;\nendmon;\n" +
            "on-error *program;\nsndpgmmsg %char(%status)+':'+%char(%status(DATA));\nendmon;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102:1211");
    }
    [Fact]
    public void A_monitor_in_the_caller_handles_an_EXSR_error_with_its_original_file_status()
    {
        Compile(UpdateFile(extra: "usropn") + "monitor;\nexsr FAIL;\non-error 1211;\nsndpgmmsg 'SUBROUTINE';\nendmon;\nseton LR;\n" +
            "begsr FAIL;\nread DATA;\nendsr;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "SUBROUTINE");
    }
    [Fact]
    public void Handler_transfer_unwinds_inner_loops_and_keeps_an_outer_loop_active()
    {
        Compile(UpdateFile(extra: "usropn") + "dcl-s I int(10);\ndcl-s J int(10);\nfor I=1 to 2;\nmonitor;\nfor J=1 to 3;\nread DATA;\nendfor;\n" +
            "on-error 1211;\nsndpgmmsg %char(I);\nendmon;\nendfor;\nsndpgmmsg 'DONE';\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "DONE"); Assert.Contains("1", result.Message); Assert.Contains("2", result.Message);
    }
    [Fact]
    public void Normal_monitor_flow_skips_every_handler()
    {
        Compile(UpdateFile() + "monitor;\nread DATA;\non-error *all;\nsndpgmmsg 'BAD';\nendmon;\nsndpgmmsg 'NORMAL';\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "NORMAL"); Assert.DoesNotContain("BAD", result.Message);
    }
    [Theory]
    [InlineData("iter", "12:3")]
    [InlineData("leave", "1:1")]
    public void Monitor_handlers_can_leave_or_iterate_an_enclosing_loop_without_leaking_SELECT_state(string transfer, string expected)
    {
        Compile(UpdateFile(extra: "usropn") + "dcl-s I int(10);\ndcl-s SEEN char(2);\nselect;\nwhen *on;\nfor I=1 to 2;\nmonitor;\nread DATA;\n" +
            "on-error 1211;\neval SEEN=%trim(SEEN)+%char(I);\nselect;\nwhen *on;\n" + transfer + ";\nendsl;\nendmon;\n" +
            "sndpgmmsg 'BAD TRANSFER';\nendfor;\nwhen *on;\nsndpgmmsg 'BAD SELECT';\nendsl;\nsndpgmmsg %trim(SEEN)+':'+%char(I);\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, expected); Assert.DoesNotContain("BAD", result.Message);
    }
    [Fact]
    public void Unmatched_monitor_errors_keep_their_numeric_status_and_original_location()
    {
        var program = Parse(UpdateFile(extra: "usropn") + "monitor;\nread DATA;\non-error 102;\nendmon;");
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new RpgHost()).Run(program));
        Assert.Equal(1211, error.StatusCode); Assert.True(error.FileError); Assert.NotNull(error.Location); Assert.Contains("not open", error.Message);
    }
    [Theory]
    [InlineData("eval ID=1/0;", 102)]
    [InlineData("eval TEXT=%subst('abc':0:1);", 100)]
    [InlineData("eval TEXT=%subst('abc':1:4);", 100)]
    public void Program_monitor_filters_use_typed_arithmetic_and_string_statuses(string operation, int status)
    {
        Compile(UpdateFile() + "monitor;\n" + operation + "\non-error *program;\nsndpgmmsg %char(%status);\nendmon;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), status.ToString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Called_program_failures_report_202_to_the_callers_monitor(bool cpf)
    {
        var program = Parse("**free\nmonitor;\ncall 'FAIL';\non-error 202;\nsndpgmmsg %char(%status);\nendmon;\nseton LR;");
        var messages = new List<string>();
        new RpgInterpreter(new RpgHost { SendMessage = messages.Add, ProgramCaller = (_, _, _) =>
            throw (cpf ? new Ipc.Core.Messages.CpfException("CPF9898", "failed") : new RpgRuntimeException("failed") { StatusCode = 102 }) }).Run(program);
        Assert.Equal(new[] { "202" }, messages);
    }
    [Fact]
    public void MONITOR_does_not_swallow_job_end_or_cancellation_from_a_called_program()
    {
        var program = Parse("**free\nmonitor;\ncall 'FAIL';\non-error *all;\nendmon;");
        var ended = new Ipc.Core.Messages.CpfException("CPF1241", "Job ended");
        Assert.Same(ended, Assert.Throws<Ipc.Core.Messages.CpfException>(() => new RpgInterpreter(new RpgHost { ProgramCaller = (_, _, _) => throw ended }).Run(program)));
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(new RpgHost { ProgramCaller = (_, _, _) => throw new OperationCanceledException() }).Run(program));
    }
}
