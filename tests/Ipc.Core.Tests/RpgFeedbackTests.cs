using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgFeedbackTests
{
    private const string Psds = """
        dcl-ds P qualified psds;
          PROGRAM *proc;
          STATUS *status;
          PREVIOUS zoned(5) pos(16);
          LINE char(8) pos(21);
          ROUTINE *routine;
          PARAMETERS *parms;
          TYPE char(3) pos(40);
          NUMBER char(4) pos(43);
          LIBRARY char(10) pos(81);
          MESSAGE char(80) pos(91);
          LAST_FILE char(10) pos(175);
          FILE_INFO char(35) pos(209);
          JOB char(10) pos(244);
          USER char(10) pos(254);
          JOB_NUMBER zoned(6) pos(264);
          CURRENT_USER char(10) pos(358);
        end-ds;
        """ + "\n";
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);
    private static RpgRuntimeContext Run(string body, RpgHost? host = null, IReadOnlyList<object?>? parameters = null)
        => new RpgInterpreter(host ?? new()).Run(RpgCompiler.Compile("QGPL", "FEEDBACK", "**free\n" + body), parameters);

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void PSDS_reserved_fields_and_positional_job_identity_use_native_bytes(int ccsid)
    {
        var context = Run(Psds, new() { Ccsid = () => ccsid, Job = new(123456, "MYJOB", "ORIGINAL"), CurrentUser = "ACTIVE" }, [1, 2]);
        Assert.Equal("FEEDBACK  ", Read(context, "P.PROGRAM")); Assert.Equal(0m, Read(context, "P.STATUS"));
        Assert.Equal(0m, Read(context, "P.PREVIOUS")); Assert.Equal(2m, Read(context, "P.PARAMETERS"));
        Assert.Equal(2, Read(context, "%parms")); Assert.Equal("QGPL      ", Read(context, "P.LIBRARY"));
        Assert.Equal("MYJOB     ", Read(context, "P.JOB")); Assert.Equal("ORIGINAL  ", Read(context, "P.USER"));
        Assert.Equal(123456m, Read(context, "P.JOB_NUMBER")); Assert.Equal("ACTIVE    ", Read(context, "P.CURRENT_USER"));
        var bytes = Assert.IsType<ProgramBuffer>(Read(context, "P")).ToArray();
        Assert.Equal("F0F0F0F0F0F0F0F0F0F0", Convert.ToHexString(bytes.AsSpan(10, 10)));
        Assert.Equal("F0F0F2", Convert.ToHexString(bytes.AsSpan(36, 3)));
    }
    [Fact]
    public void Program_status_records_previous_error_and_does_not_clear_on_success()
    {
        var context = Run(Psds + "dcl-s N int(10);\nmonitor;\neval N=1/0;\non-error 102;\neval N=7;\nendmon;\nmonitor;\neval P.PARAMETERS='bad';\non-error 105;\nendmon;");
        Assert.Equal(105m, Read(context, "P.STATUS")); Assert.Equal(102m, Read(context, "P.PREVIOUS"));
        Assert.Equal("*DETC   ", Read(context, "P.ROUTINE")); Assert.Equal(7L, context.ReadValue("N"));
        Assert.NotEmpty(((string)Read(context, "P.MESSAGE")!).Trim());
    }
    [Fact]
    public void EXSR_error_feedback_keeps_original_routine_and_line_when_caller_monitor_handles_it()
    {
        var body = Psds + "dcl-s N int(10);\nmonitor;\nexsr FAIL;\non-error 102;\nendmon;\nreturn;\nbegsr FAIL;\neval N=1/0;\nendsr;";
        var source = "**free\n" + body; var program = RpgCompiler.Compile("QGPL", "FEEDBACK", source);
        var context = new RpgInterpreter(new()).Run(program);
        Assert.Equal(102m, Read(context, "P.STATUS")); Assert.Equal(0m, Read(context, "P.PREVIOUS")); Assert.Equal("FAIL    ", Read(context, "P.ROUTINE"));
        Assert.Equal(program.Subroutines.Single().Statements.Single(s => s.Opcode == Ipc.Rpg.Model.RpgOpcode.Eval).LineNumber.ToString("D8"), Read(context, "P.LINE"));
    }
    [Fact]
    public void Call_failure_records_caller_line_202_and_original_CPF_identity()
    {
        var source = "**free\n" + Psds + "monitor;\ncallp MISSING();\non-error 202;\nendmon;";
        var program = RpgCompiler.Compile("QGPL", "FEEDBACK", source);
        var context = new RpgInterpreter(new() { ProgramCaller = (_, _, _) => throw new CpfException("CPF1234", "missing") }).Run(program);
        Assert.Equal(202m, Read(context, "P.STATUS")); Assert.Equal("CPF", Read(context, "P.TYPE")); Assert.Equal("1234", Read(context, "P.NUMBER"));
        Assert.Equal("MISSING ", Read(context, "P.ROUTINE"));
        Assert.Equal(program.MainStatements.Single(s => s.Opcode == Ipc.Rpg.Model.RpgOpcode.CallP).LineNumber.ToString("D8"), Read(context, "P.LINE"));
    }
    [Fact]
    public void Retained_call_updates_parameter_count_but_preserves_error_and_initial_job_identity()
    {
        var program = RpgCompiler.Compile("QGPL", "FEEDBACK", "**free\n" + Psds + "dcl-s N int(10);\nmonitor;\neval N=1/0;\non-error 102;\nendmon;\nreturn;");
        var context = new RpgInterpreter(new() { Job = new(1, "FIRST", "USER") }).Run(program, [1]);
        new RpgInterpreter(new() { Job = new(2, "SECOND", "OTHER") }).Run(program, [1, 2, 3], context);
        Assert.Equal(3m, Read(context, "P.PARAMETERS")); Assert.Equal(102m, Read(context, "P.PREVIOUS"));
        Assert.Equal("FIRST     ", Read(context, "P.JOB")); Assert.Equal(1m, Read(context, "P.JOB_NUMBER"));
    }
    [Fact]
    public void Anonymous_PSDS_exports_its_subfields()
    {
        var context = Run("dcl-ds *n psds;\nNAME *proc;\nCOUNT *parms;\nend-ds;", parameters: [1]);
        Assert.Equal("FEEDBACK  ", context.ReadValue("NAME")); Assert.Equal(1m, context.ReadValue("COUNT"));
    }
    [Fact]
    public void Partial_PSDS_character_fields_do_not_mask_an_error_when_a_UTF8_character_cannot_fit()
    {
        var context = Run("dcl-ds P psds qualified;\nS *status;\nTEXT char(1) pos(91);\nend-ds;\nmonitor;\ncallp FAIL();\non-error 202;\nendmon;",
            new() { Ccsid = () => 1208, ProgramCaller = (_, _, _) => throw new RpgRuntimeException("☃") });
        Assert.Equal(202m, Read(context, "P.S")); Assert.Equal(" ", Read(context, "P.TEXT"));
    }
    [Fact]
    public void PSDS_with_only_PROC_is_safe_when_status_and_parameters_are_outside_its_storage()
    {
        var context = Run("dcl-ds P psds;\nNAME *proc;\nend-ds;\ndcl-s N int(10);\nmonitor;\neval N=1/0;\non-error 102;\nendmon;");
        Assert.Equal("FEEDBACK  ", context.ReadValue("NAME")); Assert.Equal(10, Read(context, "%size(P)"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_procedure_parameter_count_restores_on_return_or_error_while_PSDS_retains_entry_count(bool fail)
    {
        static string Procedure(char marker)
        { var line = new string(' ', 80).ToCharArray(); line[6] = 'P'; "LOCAL".CopyTo(0, line, 7, 5); line[23] = marker; return new(line); }
        var source = "**free\n" + Psds + "dcl-s N int(10);\nmonitor;\ncallp LOCAL(1:2);\non-error 202;\nendmon;\n" +
            "sndpgmmsg %char(%parms())+':'+%char(P.PARAMETERS);\nreturn;\n" + Procedure('B') +
            "\nsndpgmmsg %char(%parms())+':'+%char(P.PARAMETERS);\n" + (fail ? "eval N=1/0;\n" : "return;\n") + Procedure('E');
        var messages = new List<string>(); var program = RpgCompiler.Compile("QGPL", "FEEDBACK", source);
        var context = new RpgInterpreter(new() { SendMessage = messages.Add }).Run(program, [1, 2, 3]);
        Assert.Equal(new[] { "2:3", "3:3" }, messages); Assert.Equal(3, Read(context, "%parms"));
        if (fail)
        {
            Assert.Equal(202m, Read(context, "P.STATUS")); Assert.Equal(102m, Read(context, "P.PREVIOUS"));
            Assert.Equal("LOCAL   ", Read(context, "P.ROUTINE"));
            Assert.Equal(program.MainStatements.Single(s => s.Opcode == Ipc.Rpg.Model.RpgOpcode.CallP).LineNumber.ToString("D8"), Read(context, "P.LINE"));
        }
    }
    [Fact]
    public void Unhandled_program_error_populates_PSDS_before_propagating_with_its_location()
    {
        var program = RpgCompiler.Compile("QGPL", "FEEDBACK", "**free\n" + Psds + "dcl-ds BAD qualified;\nN packed(5);\nend-ds;\nsndpgmmsg %char(BAD.N);");
        var context = new RpgRuntimeContext(program, new());
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(program, existingContext: context));
        Assert.Equal(907, error.StatusCode); Assert.NotNull(error.Location); Assert.Equal(907m, Read(context, "P.STATUS"));
    }
    [Theory]
    [InlineData("dcl-ds P psds dim(2) qualified;\nN *status;\nend-ds;")]
    [InlineData("dcl-ds P psds(1);\nN *status;\nend-ds;")]
    [InlineData("dcl-ds P psds;\nN *file;\nend-ds;")]
    [InlineData("dcl-ds P psds;\nN *status inz(0);\nend-ds;")]
    [InlineData("dcl-ds P psds;\nN *status pos(1);\nend-ds;")]
    [InlineData("dcl-ds P psds;\nN *status;\nend-ds;\ndcl-ds Q psds;\nM *status;\nend-ds;")]
    [InlineData("dcl-ds P;\nN *status;\nend-ds;")]
    [InlineData("dcl-ds P;\nN *proc;\nend-ds;")]
    public void Invalid_feedback_declarations_fail_with_source_locations(string body)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\n" + body)).Location); }
}
