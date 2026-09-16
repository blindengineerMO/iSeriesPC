using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Xunit;

namespace Ipc.Core.Tests;

public sealed class ClMonitorTests
{
    private static ClProgram Compile(string name, string source) => new ClCompiler().Compile(name, "QGPL", source);
    [Fact]
    public void Command_monitor_precedes_program_monitor_and_block_handler_resumes_after_command()
    {
        var program = Compile("MAIN", """
            PGM
            DCL &N *INT
            MONMSG MSGID(CPF0000) EXEC(GOTO GLOBAL)
            FAIL
            MONMSG MSGID(CPF9801) EXEC(DOFOR &N FROM(1) TO(2))
              SNDPGMMSG MSG(&N)
            ENDDO
            SNDPGMMSG MSG('RESUME')
            FAIL2
            SNDPGMMSG MSG('WRONG')
            GLOBAL: SNDPGMMSG MSG('GLOBAL')
            ENDPGM
            """);
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => null, call => CommandResult.Error(call.Name == "FAIL" ? "CPF9801: missing" : "CPF9802: denied"), messages.Add).Run(program);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { "1", "2", "RESUME", "GLOBAL" }, messages);
    }
    [Fact]
    public void No_action_suppresses_error_and_program_monitor_treats_failed_if_as_false()
    {
        var program = Compile("IGNORE", """
            PGM
            DCL &N *INT
            MONMSG MSGID(MCH0000)
            CHGVAR &N (1 / 0)
            MONMSG MSGID(MCH1211)
            IF (1 / 0 *EQ 5) THEN(SNDPGMMSG MSG('WRONG'))
            ELSE CMD(SNDPGMMSG MSG('FALSE'))
            SNDPGMMSG MSG(&N)
            """);
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add).Run(program);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { "FALSE", "0" }, messages);
    }
    [Fact]
    public void Recovery_action_error_uses_its_own_monitor_then_program_monitor()
    {
        var program = Compile("RECOVER", """
            PGM
            MONMSG MSGID(CPF9802) EXEC(GOTO GLOBAL)
            FAIL
            MONMSG MSGID(CPF9801) EXEC(DO)
              FAIL
              MONMSG MSGID(CPF9801) EXEC(SNDPGMMSG MSG('INNER'))
              DENY
              SNDPGMMSG MSG('WRONG')
            ENDDO
            SNDPGMMSG MSG('WRONG')
            GLOBAL: SNDPGMMSG MSG('GLOBAL')
            """);
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => null, c => CommandResult.Error(c.Name == "FAIL" ? "CPF9801: failure" : "CPF9802: denied"), messages.Add).Run(program);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { "INNER", "GLOBAL" }, messages);
    }
    [Theory]
    [InlineData("FILE", "MATCH")]
    [InlineData("OTHER", "FALLBACK")]
    public void Explicit_escape_bypasses_sender_monitors_and_retains_id_data_across_calls(string comparison, string expected)
    {
        var child = Compile("CHILD", """
            PGM
            MONMSG MSGID(CPF0000) EXEC(GOTO WRONG)
            SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('FILE detail') MSGTYPE(*ESCAPE)
            WRONG: SNDPGMMSG MSG('WRONG')
            """);
        var middle = Compile("MIDDLE", "CALL CHILD\nSNDPGMMSG MSG('WRONG')");
        var parent = Compile("PARENT", $"""
            CALL MIDDLE
            MONMSG MSGID(CPF9800) CMPDTA('{comparison}') EXEC(SNDPGMMSG MSG('MATCH'))
            MONMSG MSGID(CPF0000) EXEC(SNDPGMMSG MSG('FALLBACK'))
            SNDPGMMSG MSG('DONE')
            """);
        var messages = new List<string>();
        var interpreter = new ClInterpreter((_, n) => n == "MIDDLE" ? middle : n == "CHILD" ? child : null, _ => CommandResult.Ok(), messages.Add);
        var result = interpreter.Run(parent);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { expected, "DONE" }, messages);
        messages.Clear(); result = interpreter.Run(middle);
        Assert.True(result.IsError); Assert.Equal("CPF9898", result.MessageId); Assert.Equal("FILE detail", result.MessageData);
        Assert.Contains("QGPL/CHILD:3:", result.Message); Assert.Contains("QGPL/MIDDLE:1:", result.Message);
        Assert.Empty(messages);
    }
    [Fact]
    public void Unmatched_error_propagates_and_informational_messages_do_not_trigger_monitors()
    {
        var messages = new List<string>();
        var program = Compile("INFO", "SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('INFO') MSGTYPE(*COMP)\nMONMSG CPF0000 EXEC(SNDPGMMSG MSG('WRONG'))\nFAIL\nMONMSG MCH0000");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Error("CPF9801: absent"), messages.Add).Run(program);
        Assert.True(result.IsError); Assert.Equal("CPF9801", result.MessageId); Assert.Equal(new[] { "INFO" }, messages);
    }
    [Fact]
    public void Recovery_return_exits_current_invocation_and_leaves_caller_running()
    {
        var child = Compile("CHILD", "FAIL\nMONMSG CPF0000 EXEC(RETURN)\nSNDPGMMSG MSG('WRONG')");
        var parent = Compile("PARENT", "CALL CHILD\nSNDPGMMSG MSG('PARENT')");
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => child, _ => CommandResult.Error("CPF9801: absent"), messages.Add).Run(parent);
        Assert.False(result.IsError, result.Message); Assert.Equal(new[] { "PARENT" }, messages);
    }
    [Theory]
    [InlineData("PGM\nMONMSG CPF0000 EXEC(SNDPGMMSG MSG('bad'))")]
    [InlineData("DO\nENDDO\nMONMSG CPF0000")]
    [InlineData("IF ('1') THEN(RETURN)\nMONMSG CPF0000")]
    [InlineData("GOTO LABEL\nMONMSG CPF0000")]
    [InlineData("FAIL\nMONMSG MSGID(&ID)")]
    [InlineData("FAIL\nMONMSG MSGID(MCH12AF)")]
    [InlineData("FAIL\nMONMSG MSGID(CPF9801) CMPDTA(&DATA)")]
    [InlineData("FAIL\nMONMSG MSGID(CPF9801) CMPDTA('12345678901234567890123456789')")]
    [InlineData("SNDPGMMSG MSG('bad') MSGTYPE(*ESCAPE)")]
    [InlineData("SNDPGMMSG MSGID(CPF98ZZ) MSGF(OTHER) MSGDTA('bad')")]
    [InlineData("SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSG('bad')")]
    [InlineData("SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGTYPE(*NOTIFY)")]
    public void Unsupported_monitor_placement_or_message_contract_is_rejected(string source) => Assert.Throws<ClCompileException>(() => Compile("BAD", source));
    [Fact]
    public void Monitor_scope_limits_are_bounded_and_recovery_loop_is_cancellable()
    {
        Assert.Throws<ClCompileException>(() => Compile("BIG", "FAIL\n" + string.Concat(Enumerable.Repeat("MONMSG CPF0000\n", 101))));
        using var cancellation = new CancellationTokenSource(); var count = 0;
        var loop = Compile("LOOP", "PGM\nMONMSG CPF0000 EXEC(GOTO AGAIN)\nAGAIN: FAIL");
        var interpreter = new ClInterpreter((_, _) => null, _ => { if (++count == 5) cancellation.Cancel(); return CommandResult.Error("CPF9801: failure"); }, cancellationToken: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => interpreter.Run(loop)); Assert.Equal(5, count);
    }

    [Fact]
    public void Comparison_byte_limit_is_checked_before_side_effects_in_utf8_job()
    {
        var program = Compile("BYTES", "SIDEFFECT\nMONMSG CPF0000 CMPDTA('" + new string('漢', 10) + "')");
        var executed = false;
        var result = new ClInterpreter((_, _) => null, _ => { executed = true; return CommandResult.Ok(); }, ccsid: () => 1208).Run(program);
        Assert.True(result.IsError); Assert.Contains("28 bytes", result.Message); Assert.False(executed);
    }
}
