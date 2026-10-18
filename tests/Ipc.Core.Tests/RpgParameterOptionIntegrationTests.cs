namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_character_options_keep_the_original_and_apply_caller_only_prototype_flags(int ccsid)
    {
        Compile("**free\ndcl-pr GET;\nP char(4) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A char(8) inz('AB');\ndcl-s OUT char(4);\ncallp GET(A);\nif OUT='  AB' and A='AB';\nsndpgmmsg 'SAVED OPTIONS OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) const;\nend-pi;\neval OUT=P;\nreturn;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED OPTIONS OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_character_options_pass_actual_transformed_bytes_to_a_queue_API(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "OPTQ"), 4);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(4) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A char(8) inz('AB');\ncallp SEND('OPTQ':'QGPL':4:A);\nif A='AB';\nsndpgmmsg 'API OPTIONS OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "API OPTIONS OK");
        var data = queues.Receive(new("QGPL", "OPTQ")); Assert.Equal("  AB", data!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_LIKEDS_exact_origins_survive_source_removal_and_recompilation(int ccsid)
    {
        Compile("**free\ndcl-ds ROOT qualified;\nX int(3) inz(7);\nend-ds;\ndcl-ds A likeds(ROOT);\ncallp GET(A);\nif A.X=9;\nsndpgmmsg 'SAVED EXACT DS OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) options(*exact);\nend-pi;\neval P.X=9;\nreturn;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED EXACT DS OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_exact_expression_rejection_precedes_external_target_side_effects(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "OPTQ"), 4);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(4) const options(*exact);\nend-pr;\nmonitor;\ncallp(e) SEND('OPTQ':'QGPL':4:'ABCDE');\non-error 105;\nsndpgmmsg 'EXACT REJECTED';\nendmon;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "EXACT REJECTED"); Assert.Equal(0, queues.Inspect(new("QGPL", "OPTQ")).Entries);
    }
}
