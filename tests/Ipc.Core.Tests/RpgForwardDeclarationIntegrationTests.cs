namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_forward_implicit_returns_and_local_widths_survive_source_removal(int ccsid)
    {
        Compile("**free\ndcl-c WIDTH 4;\ndcl-s A like(MAKE);\neval A=MAKE();\nif A='ABCDEF' and %size(A)=6;\nsndpgmmsg 'FORWARD RETURN OK';\nendif;\nseton LR;\ndcl-proc MAKE;\ndcl-pi *n like(ROOT) rtnparm end-pi;\ndcl-c WIDTH 6;\ndcl-s ROOT char(WIDTH);\nreturn 'ABCDEF';\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD RETURN OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_forward_prototype_types_supply_transformed_queue_bytes(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "FORWARDQ"), 4);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA like(ROOT) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A like(ROOT:+4) inz('AB');\ndcl-s ROOT char(4);\ncallp SEND('FORWARDQ':'QGPL':4:A);\nif A='AB';\nsndpgmmsg 'FORWARD API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD API OK"); Assert.Equal("  AB", queues.Receive(new("QGPL", "FORWARDQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Forward_feedback_structure_types_keep_the_final_INFDS_width(int ccsid)
    {
        Compile("**free\ndcl-f DATA disk usropn infds(BANK);\ndcl-s A like(COPY);\ndcl-ds BANK qualified;\nS *status;\nend-ds;\ndcl-ds COPY likeds(BANK);\nif %size(A)=80 and %size(COPY)=80;\nsndpgmmsg 'FORWARD FEEDBACK OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD FEEDBACK OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Cyclic_forward_types_prevent_object_creation_with_include_locations(int ccsid)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "FWDCYCLE", "dcl-s A like(B);\ndcl-s B like(A);", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "FORWARDBAD", "**free\n/copy QGPL/SOURCE,FWDCYCLE\nseton LR;", null);
        using var session = Session(ccsid); var result = session.Execute("CRTBNDRPG PGM(QGPL/FORWARDBAD) SRCFILE(QGPL/SOURCE)"); Assert.True(result.IsError); Assert.Contains("Cyclic", result.Message); Assert.Contains("FWDCYCLE", result.Message); Assert.False(_system.Objects.Exists("QGPL", "FORWARDBAD", Ipc.Core.Objects.ObjectType.Program));
    }
}
