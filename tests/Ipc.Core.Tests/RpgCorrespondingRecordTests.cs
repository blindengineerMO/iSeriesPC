using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false, false)] [InlineData(37, false, true)] [InlineData(37, true, false)] [InlineData(37, true, true)]
    [InlineData(1208, false, false)] [InlineData(1208, false, true)] [InlineData(1208, true, false)] [InlineData(1208, true, true)]
    public void Saved_corresponding_assignments_publish_automatic_and_named_null_maps_to_record_output(int ccsid, bool named, bool isNull)
    {
        NullData();
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update);\ndcl-ds A likerec(NULLROW:*all) inz;\ndcl-ds B likerec(NULLROW:*all) " + (named ? "nullind(FLAGS) " : "") + "inz;\n" +
            (named ? "dcl-ds FLAGS likerec(NULLROW:*all:*null) inz;\n" : "") + "chain 10 NULLDATA A;\neval A.NTEXT='copied';\neval %nullind(A.NTEXT)=" + (isNull ? "*on" : "*off") + ";\neval-corr B=A;\nif B.NTEXT='copied' and %nullind(B.NTEXT)=" + (isNull ? "*on" : "*off") + ";\nupdate NULLROW B;\nsndpgmmsg 'CORRESPONDING NULL OUTPUT';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "CORRESPONDING NULL OUTPUT");
        var text = _files.ReadAll("QGPL", "NULLDATA", "NULLDATA")[0]["NTEXT"];
        if (isNull) Assert.Null(text); else Assert.Equal("copied", Assert.IsType<string>(text).TrimEnd());
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Same_external_family_with_different_prefixes_does_not_trigger_an_image_copy(int ccsid)
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-ds A qualified extname('QGPL/NULLDATA':*all) prefix('A_') inz end-ds;\ndcl-ds B qualified extname('QGPL/NULLDATA':*all) prefix('B_') inz end-ds;\neval A.A_NID=7;\neval B.B_NID=99;\neval %nullind(B.B_NTEXT)=*on;\neval-corr B=A;\nif B.B_NID=99 and %nullind(B.B_NTEXT);\nsndpgmmsg 'CORRESPONDING LOCAL NAMES';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.DeleteFile("QGPL", "NULLDATA");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "CORRESPONDING LOCAL NAMES");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Corresponding_key_extracts_from_the_same_family_use_fields_instead_of_record_offsets(int ccsid)
    {
        NullData(true); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds A likerec(NULLROW:*all) inz;\ndcl-ds KEYS likerec(NULLROW:*key) inz;\neval A.NID=77;\neval A.NKEY=10;\neval %nullind(A.NKEY)=*on;\neval-corr KEYS=A;\nif KEYS.NKEY=10 and %nullind(KEYS.NKEY);\nsndpgmmsg 'CORRESPONDING KEY EXTRACT';\nendif;\nseton LR;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "CORRESPONDING KEY EXTRACT");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Corresponding_assignments_preflight_explicit_readonly_maps_before_a_related_data_move(bool readOnly)
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds MODEL likerec(NULLROW:*all) template;\ndcl-ds A likeds(MODEL) inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) inz;\neval A.NID=99;\ncallp COPY(A:FLAGS);\nif " + (readOnly ? "A.NID=99 and not FLAGS.NTEXT" : "A.NID=1 and FLAGS.NTEXT and not %nullind(A.NTEXT)") + ";\nsndpgmmsg 'CORRESPONDING MAP PROTECTION';\nendif;\nseton LR;\ndcl-proc COPY;\ndcl-pi *n;\nP likeds(MODEL) nullind(M);\nM likerec(NULLROW:*all:*null) " + (readOnly ? "const" : "") + ";\nend-pi;\ndcl-ds S likeds(MODEL) inz;\nchain 10 NULLDATA S;\nmonitor;\neval-corr P=S;\non-error;\nreturn;\nendmon;\nend-proc;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "CORRESPONDING MAP PROTECTION");
    }
}
