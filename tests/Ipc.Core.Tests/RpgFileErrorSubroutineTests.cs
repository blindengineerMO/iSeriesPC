using Ipc.Core.Messages;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private const string ErrorFile = "**free\ndcl-f DATA keyed usropn infds(F) infsr(FAIL);\n" + Infds;

    [Fact]
    public void INFSR_receives_file_feedback_and_RETURN_ends_without_resuming_the_failed_operation()
    {
        var messages = new List<string>(); var program = Parse(ErrorFile + "read DATA;\nsndpgmmsg 'BAD CONTINUATION';\nreturn;\n" +
            "begsr FAIL;\nsndpgmmsg %char(F.STATUS)+':'+%trim(F.OP);\nreturn;\nendsr;");
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "1211:READ F" }, messages);
    }
    [Theory]
    [InlineData("read(e) DATA;\nsndpgmmsg 'HANDLED';")]
    [InlineData("monitor;\nread DATA;\non-error 1211;\nsndpgmmsg 'HANDLED';\nendmon;")]
    [InlineData("monitor;\nexsr INNER;\non-error 1211;\nsndpgmmsg 'HANDLED';\nendmon;")]
    public void E_and_matching_MONITOR_precede_INFSR(string operation)
    {
        var messages = new List<string>(); var program = Parse(ErrorFile + operation + "\nreturn;\n" +
            "begsr INNER;\nread DATA;\nendsr;\nbegsr FAIL;\nsndpgmmsg 'BAD INFSR';\nreturn;\nendsr;");
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "HANDLED" }, messages);
    }
    [Fact]
    public void Error_indicator_precedes_INFSR_without_setting_ERROR()
    {
        var messages = new List<string>(); var program = Parse(ErrorFile + "read DATA;\nif *in20 and not %error;\nsndpgmmsg 'ER';\nendif;\nreturn;\n" +
            "begsr FAIL;\nsndpgmmsg 'BAD';\nreturn;\nendsr;");
        program.MainStatements.First().Indicator2 = 20;
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "ER" }, messages);
    }
    [Fact]
    public void File_error_without_INFSR_does_not_fall_into_PSSR()
    {
        var messages = new List<string>(); var program = Parse("**free\ndcl-f DATA usropn;\nread DATA;\nreturn;\nbegsr *pssr;\nsndpgmmsg 'BAD';\nreturn;\nendsr;");
        Assert.Equal(1211, Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program)).StatusCode);
        Assert.Empty(messages);
    }
    [Fact]
    public void INFSR_PSSR_explicitly_routes_file_errors_to_the_program_error_subroutine()
    {
        var messages = new List<string>(); var program = Parse("**free\ndcl-f DATA usropn infsr(*pssr);\nread DATA;\nreturn;\nbegsr *pssr;\nsndpgmmsg %char(%status(DATA));\nreturn;\nendsr;");
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "1211" }, messages);
    }
    [Fact]
    public void One_INFSR_can_handle_multiple_files()
    {
        Data("OTHER"); var messages = new List<string>();
        var program = Parse("**free\ndcl-f DATA usropn infsr(FAIL);\ndcl-f OTHER usropn rename(ROW:OTHERROW) prefix(O_) infsr(FAIL);\n" +
            "exsr FAIL;\nread OTHER;\nreturn;\nbegsr FAIL;\nsndpgmmsg %char(%status(OTHER));\nif %status(OTHER)=1211;\nreturn;\nendif;\nendsr;");
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "0", "1211" }, messages);
    }
    [Fact]
    public void INFSR_GOTO_recovers_in_main_after_repairing_the_file_state()
    {
        var messages = new List<string>(); var program = Parse(ErrorFile + "read DATA;\ntag RETRY;\nread DATA;\nsndpgmmsg %char(ID);\nreturn;\n" +
            "begsr FAIL;\nopen DATA;\ngoto RETRY;\nendsr;");
        var host = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", SendMessage = messages.Add };
        new RpgInterpreter(host).Run(program); Assert.Equal(new[] { "102" }, messages);
    }
    [Theory]
    [InlineData("infsr()")]
    [InlineData("infsr('FAIL')")]
    [InlineData("infsr(FAIL:OTHER)")]
    [InlineData("infsr(FAIL) infsr(FAIL)")]
    [InlineData("infsr(MISSING)")]
    public void Invalid_INFSR_keywords_and_missing_routines_fail_with_source_locations(string keyword)
        => Assert.NotNull(Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA " + keyword + ";\nreturn;\nbegsr FAIL;\nendsr;")).Location);
    [Fact]
    public void Subprocedures_cannot_access_global_files_with_INFSR_even_via_local_subroutines()
    {
        static string P(char marker) { var chars = new string(' ', 80).ToCharArray(); chars[6] = 'P'; "LOCAL".CopyTo(0, chars, 7, 5); chars[23] = marker; return new(chars); }
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Parse(ErrorFile + "return;\nbegsr FAIL;\nendsr;\n" + P('B') +
            "\nexsr INNER;\nreturn;\nbegsr INNER;\nread DATA;\nendsr;\n" + P('E'))).Location);
    }
    [Fact]
    public void CANCL_releases_retained_update_locks_and_reinitializes_the_next_call()
    {
        Compile(UpdateFile() + "dcl-ds STATE qualified inz;\nCOUNT int(10);\nend-ds;\neval STATE.COUNT=STATE.COUNT+1;\nif STATE.COUNT<>1;\nreturn;\nendif;\nchain 20 DATA;\nexsr *pssr;\nreturn;\nbegsr *pssr;\nendsr '*CANCL';", group: "ERRORS");
        Compile(UpdateFile() + "chain 20 DATA;\neval TEXT='released';\nupdate ROW;\nseton LR;", name: "OTHERAPP");
        using var first = Session(); using var second = Session();
        for (var i = 0; i < 2; i++) { var result = first.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("RNX9001", result.Message); }
        var updated = second.Execute("CALL QGPL/OTHERAPP"); Assert.False(updated.IsError, updated.Message); Assert.Equal("released", Text(101));
    }
    [Fact]
    public void Saved_INFSR_program_runs_after_source_removal()
    {
        Compile(ErrorFile + "read DATA;\nreturn;\nbegsr FAIL;\nsndpgmmsg 'SAVED INFSR';\nreturn;\nendsr;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "SAVED INFSR");
    }
    [Fact]
    public void Recursive_INFSR_can_use_a_first_time_switch_and_RETURN()
    {
        var messages = new List<string>(); var program = Parse(ErrorFile + "dcl-s COUNT int(10) inz(0);\nread DATA;\nreturn;\n" +
            "begsr FAIL;\neval COUNT=COUNT+1;\nif COUNT=2;\nsndpgmmsg 'RECURSIVE FILE ERROR';\nreturn;\nendif;\nread DATA;\nendsr;");
        new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program); Assert.Equal(new[] { "RECURSIVE FILE ERROR" }, messages);
    }
    [Fact]
    public void A_default_error_after_PSSR_ends_releases_retained_update_locks()
    {
        Compile(UpdateFile() + "chain 20 DATA;\neval ID=1/0;\nreturn;\nbegsr *pssr;\nendsr;", group: "ERRORS");
        Compile(UpdateFile() + "chain 20 DATA;\neval TEXT='released';\nupdate ROW;\nseton LR;", name: "OTHERAPP");
        using var first = Session(); using var second = Session(); Assert.True(first.Execute("CALL QGPL/APP").IsError);
        var updated = second.Execute("CALL QGPL/OTHERAPP"); Assert.False(updated.IsError, updated.Message); Assert.Equal("released", Text(101));
    }
    [Fact]
    public void Implicit_open_failure_uses_default_handling_instead_of_INFSR()
    {
        var messages = new List<string>(); var program = Parse(ErrorFile.Replace("usropn ", "") +
            "return;\nbegsr FAIL;\nsndpgmmsg 'BAD';\nreturn;\nendsr;");
        Assert.Equal("CPF4101", Assert.Throws<CpfException>(() => new RpgInterpreter(new() {
            OpenDisk = _ => throw new CpfException("CPF4101", "Open failed"), SendMessage = messages.Add }).Run(program)).MessageId);
        Assert.Empty(messages);
    }
}
