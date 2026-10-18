using Ipc.Core.Objects;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_data_templates_supply_initialized_queue_API_buffers_after_source_removal(int ccsid)
    {
        var queues = new DataQueueStore(_system.Connections); queues.Create(new("QGPL", "TMPLQ"), 4);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA likeds(ROOT) const options(*exact);\nend-pr;\ndcl-ds COPY likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified template inz;\nTEXT char(4) inz('AB');\nend-ds;\ncallp SEND('TMPLQ':'QGPL':%size(ROOT):COPY);\nsndpgmmsg 'TEMPLATE API OK';\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "TEMPLATE API OK"); Assert.Equal("AB  ", queues.Receive(new("QGPL", "TMPLQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_local_template_return_interfaces_keep_defaults_without_allocating_template_arrays(int ccsid)
    {
        Compile("**free\ndcl-ds OUTPUT likeds(ROOT);\ndcl-ds ROOT qualified template;\nX int(3);\nend-ds;\neval OUTPUT=MAKE();\nif OUTPUT.X=7;\nsndpgmmsg 'TEMPLATE RETURN OK';\nendif;\nseton LR;\ndcl-proc MAKE;\ndcl-pi *n likeds(LOCAL) rtnparm end-pi;\ndcl-ds COPY likeds(LOCAL) inz(*likeds);\ndcl-ds LOCAL qualified template dim(32767) inz;\nX int(3) inz(7);\nend-ds;\nreturn COPY;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "TEMPLATE RETURN OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_large_scalar_templates_use_metadata_without_runtime_storage(int ccsid)
    {
        Compile("**free\ndcl-s ROOT char(32767) dim(32767) template;\ndcl-s COPY like(ROOT) inz('AB');\ndcl-s N int(20);\neval N=%size(ROOT:*all);\nif N=1073676289 and %subst(COPY:1:2)='AB';\nsndpgmmsg 'TEMPLATE SIZE OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "TEMPLATE SIZE OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Template_data_access_rejects_saved_objects_with_include_provenance(int ccsid)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "TMPLBODY", "dcl-s ROOT char(4) template;\ncallp QSNDDTAQ(ROOT);", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "TMPLBAD", "**free\n/copy QGPL/SOURCE,TMPLBODY\nseton LR;", null);
        using var session = Session(ccsid); var result = session.Execute("CRTBNDRPG PGM(QGPL/TMPLBAD) SRCFILE(QGPL/SOURCE)");
        Assert.True(result.IsError); Assert.Contains("TEMPLATE", result.Message); Assert.Contains("TMPLBODY", result.Message); Assert.False(_system.Objects.Exists("QGPL", "TMPLBAD", ObjectType.Program));
    }

    [Fact]
    public void INFDS_cannot_bind_metadata_only_template_storage()
    {
        var result = Create("**free\ndcl-f DATA disk usropn infds(ROOT);\ndcl-ds ROOT qualified template;\nX char(80);\nend-ds;\nseton LR;");
        Assert.True(result.IsError); Assert.Contains("INFDS", result.Message); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
