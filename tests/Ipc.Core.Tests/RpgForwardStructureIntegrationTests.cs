namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_forward_structure_interfaces_preserve_retained_reference_rebinding(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nP likeds(ROOT);\nend-pi;\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\neval P.X=P.X+1;\nreturn;", "FDSPI", "FDSARGS");
        Compile("**free\ndcl-pr RUN extpgm('FDSPI');\nP likeds(ROOT) options(*exact);\nend-pr;\ndcl-ds A likeds(ROOT) inz(*likeds);\ndcl-ds B likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified inz;\nX int(3) inz(7);\nend-ds;\ncallp RUN(A);\ncallp RUN(B);\nif A.X=8 and B.X=8 and ROOT.X=7;\nsndpgmmsg 'FORWARD DS PI OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "FDSPI" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD DS PI OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_forward_structure_initializers_supply_queue_API_bytes(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "FDSQ"), 4);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA likeds(ROOT) const options(*exact);\nend-pr;\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified inz;\nTEXT char(4) inz('AB');\nend-ds;\ncallp SEND('FDSQ':'QGPL':4:COPY);\nif ROOT.TEXT='AB' and COPY.TEXT='AB';\nsndpgmmsg 'FORWARD DS API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD DS API OK"); Assert.Equal("AB  ", queues.Receive(new("QGPL", "FDSQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_local_forward_structure_returns_preserve_inherited_defaults(int ccsid)
    {
        Compile("**free\ndcl-c INITIAL 4;\ndcl-ds OUTPUT likeds(ROOT);\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\neval OUTPUT=MAKE();\nif OUTPUT.X=7;\nsndpgmmsg 'FORWARD DS RETURN OK';\nendif;\nseton LR;\ndcl-proc MAKE;\ndcl-pi *n likeds(LOCAL) rtnparm end-pi;\ndcl-ds COPY likeds(LOCAL) inz(*likeds);\ndcl-c INITIAL 7;\ndcl-ds LOCAL qualified inz;\nX int(3) inz(INITIAL);\nend-ds;\nreturn COPY;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD DS RETURN OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Forward_feedback_chains_and_interfaces_use_final_extents_independent_of_source_order(int ccsid)
    {
        Compile("**free\ndcl-f DATA disk usropn infds(BANK);\ndcl-pr GET int(3);\nP likeds(COPY) const options(*exact);\nend-pr;\ndcl-ds COPY likeds(SECOND);\ndcl-ds SECOND likeds(BANK);\ndcl-ds BANK qualified;\nSTATUS *status;\nend-ds;\ndcl-s N int(3);\neval N=GET(COPY);\nif N=80 and %size(COPY)=80 and %size(SECOND)=80;\nsndpgmmsg 'FORWARD DS FEEDBACK OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n int(3);\nP likeds(COPY) const;\nend-pi;\nreturn %size(P);\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "FORWARD DS FEEDBACK OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Cyclic_forward_structure_templates_reject_creation_with_include_provenance(int ccsid)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "FDSCYCLE", "dcl-ds A likeds(B) inz(*likeds);\ndcl-ds B likeds(A) inz(*likeds);", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "FDSBAD", "**free\n/copy QGPL/SOURCE,FDSCYCLE\nseton LR;", null);
        using var session = Session(ccsid); var result = session.Execute("CRTBNDRPG PGM(QGPL/FDSBAD) SRCFILE(QGPL/SOURCE)"); Assert.True(result.IsError); Assert.Contains("Cyclic", result.Message); Assert.Contains("FDSCYCLE", result.Message); Assert.False(_system.Objects.Exists("QGPL", "FDSBAD", Ipc.Core.Objects.ObjectType.Program));
    }
    [Fact]
    public void Forward_template_snapshots_do_not_hide_an_explicitly_undersized_INFDS()
    {
        var result = Create("**free\ndcl-f DATA disk usropn infds(BANK);\ndcl-ds COPY likeds(BANK);\ndcl-ds BANK qualified len(40);\nSTATUS *status;\nend-ds;\nseton LR;"); Assert.True(result.IsError); Assert.Contains("at least 80",result.Message); Assert.False(_system.Objects.Exists("QGPL","APP",Ipc.Core.Objects.ObjectType.Program));
    }

}
