using Ipc.Session;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_multiple_occurrence_structures_retain_selection_until_LR_and_reclaim(int ccsid)
    {
        Compile("**free\ndcl-ds BANK qualified occurs(3) inz;\nCOUNT int(10);\nend-ds;\n" +
            "eval BANK.COUNT=BANK.COUNT+1;\nsndpgmmsg %char(%occur(BANK))+':'+%char(BANK.COUNT);\n" +
            "if %occur(BANK)=3;\nseton LR;\nreturn;\nendif;\neval %occur(BANK)=%occur(BANK)+1;\nreturn;", group: "OCCURRET");
        _files.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "1:1"); Ok(session.Execute("CALL QGPL/APP"), "2:1");
        Ok(session.Execute("CALL QGPL/APP"), "3:1"); Ok(session.Execute("CALL QGPL/APP"), "1:1");
        Ok(session.Execute("RCLACTGRP ACTGRP(OCCURRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "1:1");
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_API_whole_MO_parameter_starts_at_the_first_occurrence(int ccsid)
    {
        Compile("**free\ndcl-ds INFO qualified occurs(2) inz len(128);\nCOUNT int(10);\nend-ds;\n" +
            "dcl-s ERROR char(4) ccsid(*hex) inz(X'00000000');\neval %occur(INFO)=2;\n" +
            "callp QWCRSVAL(INFO:%size(INFO):1:'QCCSID    ':ERROR);\n" +
            "if INFO.COUNT=0;\neval %occur(INFO)=1;\nif INFO.COUNT=1;\nsndpgmmsg 'WHOLE MO API';\nendif;\nendif;\nseton LR;");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "WHOLE MO API");
    }
    [Fact]
    public void KDS_search_reads_subfields_from_the_current_occurrence()
    {
        Compile("**free\ndcl-f DATA disk keyed;\ndcl-ds SEARCH qualified occurs(2) inz;\nVALUE packed(9:2);\nend-ds;\n" +
            "eval SEARCH.VALUE=10;\neval %occur(SEARCH)=2;\neval SEARCH.VALUE=30;\nchain %kds(SEARCH) DATA;\n" +
            "sndpgmmsg %char(ID);\neval %occur(SEARCH)=1;\nchain %kds(SEARCH) DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP");
        Ok(result, "103"); Assert.Contains("102", result.Message);
    }
}

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_multiple_occurrence_INDDS_keeps_CA_CF_responses_in_separate_banks(int ccsid)
    {
        Display(IndicatorScreen); Compile(File("indds(KEYS)") + Keys.Replace("qualified;", "qualified occurs(2);") +
            "dcl-s SECOND ind;\neval %occur(KEYS)=2;\neval KEYS.SHOW=*on;\neval NAME='ORIGINAL';\nexfmt ENTRY;\n" +
            "eval SECOND=KEYS.F6;\neval %occur(KEYS)=1;\nexfmt ENTRY;\n" +
            "if SECOND and KEYS.F3 and not KEYS.F6 and not *in03 and not *in06;\n" +
            "eval %occur(KEYS)=2;\nif KEYS.F6 and not KEYS.F3;\nsndpgmmsg 'SEPARATE OCCURRENCES';\nendif;\nendif;\nseton LR;");
        _sources.RemoveMember("QGPL", "SOURCE", "APP");
        var calls = 0; var terminal = new Terminal(display =>
        {
            calls++;
            if (calls == 1) Assert.Contains("LOCAL INDICATOR VISIBLE", display.Session.Buffer.RowText(2));
            else Assert.DoesNotContain("LOCAL INDICATOR VISIBLE", display.Session.Buffer.RowText(2));
            return display.Session.Handle(new(calls == 1 ? AidKey.Pf6 : AidKey.Pf3));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, terminal);
        Assert.Equal(ccsid, session.Job.Ccsid);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Contains("SEPARATE OCCURRENCES", result.Message); Assert.Equal(2, calls);
    }
}
