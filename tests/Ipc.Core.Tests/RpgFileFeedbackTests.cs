using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private const string Infds = """
        dcl-ds F qualified;
          FILE_NAME *file;
          OPENED ind pos(9);
          EOF ind pos(10);
          STATUS *status;
          OP *opcode;
          ROUTINE *routine;
          LINE char(8) pos(30);
          RECORD_NAME *record;
          MSGID char(7) pos(46);
        end-ds;
        """ + "\n";
    private static object? Feedback(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private RpgHost FeedbackHost(bool retain = false, int ccsid = 37) => new()
    { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", RetainFiles = retain, Ccsid = () => ccsid };

    [Fact]
    public void LIKEDS_chains_inherit_the_final_INFDS_minimum_length_without_becoming_feedback_storage()
    {
        var program = Parse("**free\ndcl-f DATA infds(F);\ndcl-ds F qualified;\nS *status;\nend-ds;\ndcl-ds COPY likeds(F);\ndcl-ds AGAIN likeds(COPY) inz(*likeds);\nreturn;");
        Assert.All(program.DataStructures, ds => Assert.Equal(80, ds.Length));
        var context = new RpgInterpreter(FeedbackHost()).Run(program);
        Assert.Equal(0m, Feedback(context, "F.S")); Assert.Equal(80, Feedback(context, "%size(AGAIN)"));
        Assert.Equal(907, Assert.Throws<RpgRuntimeException>(() => Feedback(context, "COPY.S")).StatusCode);
    }
    [Fact]
    public void Expanded_INFDS_LIKEDS_arrays_still_enforce_the_native_storage_bound()
    {
        Assert.Contains("1 MiB", Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA infds(F);\ndcl-ds F qualified;\nS *status;\nend-ds;\ndcl-ds COPY likeds(F) dim(20000);\nreturn;")).Message);
    }
    [Fact]
    public void An_INFDS_defined_with_LIKEDS_keeps_its_minimum_without_extending_the_ordinary_parent()
    {
        var program = Parse("**free\ndcl-f DATA infds(F);\ndcl-ds TEMPLATE_DS qualified;\nN char(1);\nend-ds;\ndcl-ds F likeds(TEMPLATE_DS);\ndcl-ds COPY likeds(F);\nreturn;");
        Assert.Equal(1, program.FindDs("TEMPLATE_DS")!.Length); Assert.Equal(80, program.FindDs("F")!.Length);
        Assert.Equal(80, program.FindDs("COPY")!.Length);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void INFDS_minimum_layout_is_extended_and_implicit_open_then_record_input_refresh_the_bytes(int ccsid)
    {
        var program = Parse("**free\ndcl-f DATA keyed infds(F);\n" + Infds + "chain 20 ROW;\nreturn;");
        Assert.Equal(80, program.FindDs("F")!.Length);
        var context = new RpgInterpreter(FeedbackHost(true, ccsid)).Run(program);
        try
        {
            Assert.Equal("DATA    ", Feedback(context, "F.FILE_NAME")); Assert.Equal(true, Feedback(context, "F.OPENED"));
            Assert.Equal(false, Feedback(context, "F.EOF")); Assert.Equal(0m, Feedback(context, "F.STATUS"));
            Assert.Equal("CHAINR", Feedback(context, "F.OP")); Assert.Equal("ROW     ", Feedback(context, "F.RECORD_NAME"));
            Assert.Equal("*DETC   ", Feedback(context, "F.ROUTINE"));
            var bytes = Assert.IsType<ProgramBuffer>(Feedback(context, "F")).ToArray();
            Assert.Equal("F0F0F0F0F0", Convert.ToHexString(bytes.AsSpan(10, 5)));
        }
        finally { context.CloseDiskFiles(); }
        Assert.Equal(false, Feedback(context, "F.OPENED"));
    }
    [Fact]
    public void INFDS_implicit_open_reports_OPEN_I_and_INIT_before_calculations()
    {
        var context = new RpgInterpreter(FeedbackHost(true)).Run(Parse("**free\ndcl-f DATA infds(F);\n" + Infds));
        try
        {
            Assert.Equal("OPEN I", Feedback(context, "F.OP")); Assert.Equal("*INIT   ", Feedback(context, "F.ROUTINE"));
            Assert.Equal("00000000", Feedback(context, "F.LINE")); Assert.Equal(true, Feedback(context, "F.OPENED"));
        }
        finally { context.CloseDiskFiles(); }
    }
    [Theory]
    [InlineData("chain(e) 999 DATA;", 12, false, "CHAINF")]
    [InlineData("setgt 999 DATA;\nread(e) DATA;", 11, true, "READ F")]
    [InlineData("setll 1 DATA;\nreadp(e) DATA;", 11, true, "READPF")]
    [InlineData("chain(e) 20 DATA;", 0, false, "CHAINF")]
    public void Normal_file_statuses_refresh_INFDS_status_eof_and_opcode(string operation, int status, bool eof, string opcode)
    {
        var context = new RpgInterpreter(FeedbackHost()).Run(Parse("**free\ndcl-f DATA keyed infds(F);\n" + Infds + operation));
        Assert.Equal((decimal)status, Feedback(context, "F.STATUS")); Assert.Equal(eof, Feedback(context, "F.EOF"));
        Assert.Equal(opcode, Feedback(context, "F.OP")); Assert.Equal("ROW     ", Feedback(context, "F.RECORD_NAME"));
    }
    [Theory]
    [InlineData("read(e) DATA;")]
    [InlineData("monitor;\nread DATA;\non-error 1211;\nendmon;")]
    [InlineData("monitor;\nexsr FAIL;\non-error 1211;\nendmon;\nreturn;\nbegsr FAIL;\nread DATA;\nendsr;")]
    public void INFDS_and_last_file_PSDS_are_populated_before_E_or_MONITOR_handlers(string operation)
    {
        var body = "**free\ndcl-f DATA usropn infds(F);\n" + Infds + "dcl-ds P psds qualified;\nS *status;\nLAST char(10) pos(175);\nINFO char(35) pos(209);\nend-ds;\n" + operation;
        var context = new RpgInterpreter(FeedbackHost()).Run(Parse(body));
        Assert.Equal(1211m, Feedback(context, "F.STATUS")); Assert.Equal(0m, Feedback(context, "P.S"));
        Assert.Equal("DATA      ", Feedback(context, "P.LAST"));
        Assert.Equal(Assert.IsType<ProgramBuffer>(Feedback(context, "%subst(F:11:35)")).ToArray(),
            Assert.IsType<ProgramBuffer>(Feedback(context, "%subst(P:209:35)")).ToArray());
        Assert.Equal(operation.Contains("exsr") ? "FAIL    " : "*DETC   ", Feedback(context, "F.ROUTINE"));
    }
    [Fact]
    public void A_success_on_another_file_preserves_the_first_INFDS_and_PSDS_error_snapshot()
    {
        Data("OTHER");
        Compile("**free\ndcl-f DATA usropn infds(F);\ndcl-f OTHER rename(ROW:OTHERROW) prefix(O_) infds(G);\n" + Infds +
            "dcl-ds G qualified;\nS *status;\nend-ds;\ndcl-ds P psds qualified;\nS *status;\nLAST char(10) pos(175);\nINFO char(35) pos(209);\nend-ds;\n" +
            "read(e) DATA;\nread OTHER;\nif F.STATUS=1211 and G.S=0 and P.S=0 and P.LAST='DATA' and %status=0;\nsndpgmmsg 'INDEPENDENT FEEDBACK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "INDEPENDENT FEEDBACK");
    }
    [Fact]
    public void A_program_operand_error_updates_PSDS_and_does_not_become_a_file_error()
    {
        Compile("**free\ndcl-f DATA keyed infds(F);\n" + Infds + "dcl-ds P psds qualified;\nS *status;\nend-ds;\n" +
            "monitor;\nchain(e) (1/0) DATA;\non-error 102;\nif P.S=102 and F.STATUS=0 and not %error;\nsndpgmmsg 'PROGRAM FEEDBACK';\nendif;\nendmon;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "PROGRAM FEEDBACK");
    }
    [Fact]
    public void PSDS_last_file_feedback_is_available_without_declaring_INFDS()
    {
        Compile("**free\ndcl-f DATA usropn;\ndcl-ds P psds qualified;\nS *status;\nLAST char(10) pos(175);\nFILE_STATUS zoned(5) pos(209);\nOP char(6) pos(214);\nend-ds;\n" +
            "read(e) DATA;\nif P.S=0 and P.LAST='DATA' and P.FILE_STATUS=1211 and P.OP='READ F';\nsndpgmmsg 'IMPLICIT FEEDBACK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "IMPLICIT FEEDBACK");
    }
    [Fact]
    public void Successful_file_input_refreshes_global_status_without_clearing_PSDS_program_error()
    {
        Compile("**free\ndcl-f DATA infds(F);\n" + Infds + "dcl-ds P psds qualified;\nS *status;\nend-ds;\n" +
            "monitor;\neval ID=1/0;\non-error 102;\nendmon;\nread DATA;\nif P.S=102 and F.STATUS=0 and %status=0;\nsndpgmmsg 'PROGRAM STATUS RETAINED';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "PROGRAM STATUS RETAINED");
    }
    [Theory]
    [InlineData("chain 20 DATA;\neval TEXT='changed';\nupdate ROW;", "UPDATR")]
    [InlineData("chain 20 DATA;\nunlock DATA;", "UNLCKF")]
    [InlineData("chain 20 DATA;\ndelete ROW;", "DELETR")]
    [InlineData("setgt 20 DATA;\nreadpe 20 DATA;", "REDPEF")]
    public void INFDS_uses_native_abbreviations_for_long_operation_names(string operation, string opcode)
    {
        using var session = Session(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var context = new RpgInterpreter(FeedbackHost()).Run(Parse("**free\ndcl-f DATA keyed usage(*delete) infds(F);\n" + Infds + operation));
        Assert.Equal(opcode, Feedback(context, "F.OP")); Assert.Equal(0m, Feedback(context, "F.STATUS"));
    }
    [Fact]
    public void Retained_INFDS_tracks_the_retained_cursor_and_closes_with_its_storage_owner()
    {
        var program = Parse("**free\ndcl-f DATA infds(F);\n" + Infds + "read DATA;\nreturn;");
        var context = new RpgInterpreter(FeedbackHost(true)).Run(program);
        try
        {
            for (var i = 0; i < 3; i++) new RpgInterpreter(FeedbackHost(true)).Run(program, existingContext: context);
            Assert.Equal(11m, Feedback(context, "F.STATUS")); Assert.Equal(true, Feedback(context, "F.EOF"));
            Assert.Equal(true, Feedback(context, "F.OPENED")); Assert.Equal("READ F", Feedback(context, "F.OP"));
        }
        finally { context.CloseDiskFiles(); }
        Assert.Equal(false, Feedback(context, "F.OPENED"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_INFDS_PSDS_bindings_survive_removed_source_and_report_the_live_job(int ccsid)
    {
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        Compile("**free\ndcl-f DATA keyed infds(F);\n" + Infds + "dcl-ds P psds qualified;\nS *status;\nNAME *proc;\nJOB char(10) pos(244);\nend-ds;\n" +
            "chain 20 DATA;\nif F.OP='CHAINF' and F.STATUS=0 and P.NAME='APP' and P.JOB='QDFTJOB';\nsndpgmmsg 'SAVED FEEDBACK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "SAVED FEEDBACK");
    }
    [Theory]
    [InlineData("dcl-f DATA infds(MISSING);\n", "")]
    [InlineData("dcl-f DATA infds(F) infds(F);\n", Infds)]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-s F char(80);\n")]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-ds F psds;\nS *status;\nend-ds;\n")]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-ds F qualified dim(2);\nS *status;\nend-ds;\n")]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-ds F len(79);\nS *status;\nend-ds;\n")]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-ds F len(81);\nS *status;\nend-ds;\n")]
    [InlineData("dcl-f DATA infds(F);\n", "dcl-ds F;\nSCREEN *size;\nend-ds;\n")]
    [InlineData("dcl-f DATA infds(F);\ndcl-f OTHER infds(F);\n", Infds)]
    public void Invalid_INFDS_bindings_fail_at_the_original_declaration(string files, string structures)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => Parse("**free\n" + files + structures)).Location); }
}
