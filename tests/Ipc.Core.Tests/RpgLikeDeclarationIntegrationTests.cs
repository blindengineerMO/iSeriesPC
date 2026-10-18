namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_LIKE_program_interfaces_rebind_native_receivers_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s ROOT int(3);\ndcl-pi *n;\nP like(ROOT);\nend-pi;\neval P=P+1;\nreturn;", "LIKEPI", "LIKEARGS");
        Compile("**free\ndcl-s ROOT int(3);\ndcl-pr RUN extpgm('LIKEPI');\nP like(ROOT) options(*exact);\nend-pr;\ndcl-s A like(ROOT) inz(7);\ndcl-s B like(ROOT) inz(9);\ncallp RUN(A);\ncallp RUN(B);\nif A=8 and B=10;\nsndpgmmsg 'SAVED LIKE PI OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "LIKEPI" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED LIKE PI OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_LIKE_types_and_character_options_supply_real_queue_bytes(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "LIKEQ"), 4);
        Compile("**free\ndcl-s ROOT char(4);\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA like(ROOT) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A like(ROOT:+4) inz('AB');\ncallp SEND('LIKEQ':'QGPL':4:A);\nif A='AB';\nsndpgmmsg 'LIKE API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "LIKE API OK"); Assert.Equal("  AB", queues.Receive(new("QGPL", "LIKEQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_LIKE_structure_subfields_and_return_types_keep_ancestry(int ccsid)
    {
        Compile("**free\ndcl-ds ROOT qualified;\nX packed(5:2);\nend-ds;\ndcl-ds A likeds(ROOT);\ndcl-s N like(ROOT.X);\neval A.X=12.34;\neval N=GET(A.X);\nif N=12.34;\nsndpgmmsg 'SAVED LIKE ROOT OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n like(ROOT.X) rtnparm;\nP like(ROOT.X) const options(*exact);\nend-pi;\nreturn P;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED LIKE ROOT OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Unrelated_LIKE_exact_types_reject_program_creation(int ccsid)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "LIKEBAD", "**free\ndcl-s ROOT int(3);\ndcl-s A int(3);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP like(ROOT) const options(*exact);\nend-pi;\nreturn;\nend-proc;", null);
        using var session = Session(ccsid); var result = session.Execute("CRTBNDRPG PGM(QGPL/LIKEBAD) SRCFILE(QGPL/SOURCE)"); Assert.True(result.IsError); Assert.Contains("*EXACT", result.Message); Assert.False(_system.Objects.Exists("QGPL", "LIKEBAD", Ipc.Core.Objects.ObjectType.Program));
    }
}
