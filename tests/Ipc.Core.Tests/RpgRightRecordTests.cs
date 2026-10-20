namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false, false)] [InlineData(37, false, true)] [InlineData(37, true, false)] [InlineData(37, true, true)]
    [InlineData(1208, false, false)] [InlineData(1208, false, true)] [InlineData(1208, true, false)] [InlineData(1208, true, true)]
    public void Right_adjusted_record_outputs_preserve_null_attributes_and_leading_blanks(int ccsid, bool receiver, bool isNull)
    {
        NullData(); var prefix = receiver ? "DATA." : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update);\n" + (receiver ? "dcl-ds DATA likerec(NULLROW:*all) inz;\n" : "") +
            "chain 10 NULLDATA" + (receiver ? " DATA" : "") + ";\neval %nullind(" + prefix + "NTEXT)=" + (isNull ? "*on" : "*off") + ";\nevalr " + prefix + "NTEXT='RIGHT';\nif " + prefix + "NTEXT='   RIGHT' and %nullind(" + prefix + "NTEXT)=" + (isNull ? "*on" : "*off") + ";\nupdate NULLROW" + (receiver ? " DATA" : "") + ";\nsndpgmmsg 'RIGHT ADJUSTED NULL OUTPUT';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "RIGHT ADJUSTED NULL OUTPUT");
        var text = _files.ReadAll("QGPL", "NULLDATA", "NULLDATA")[0]["NTEXT"];
        if (isNull) Assert.Null(text); else Assert.Equal("   RIGHT", Assert.IsType<string>(text));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Right_adjusted_stored_program_parameters_rebind_and_keep_maps_across_retained_calls(int ccsid)
    {
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-pi *n;\nP char(5) options(*nullind);\nend-pi;\ndcl-s COUNT int(10);\neval COUNT=COUNT+1;\nevalr P=%char(COUNT);\nreturn;", "CALLEE", "RIGHTGROUP");
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('QGPL/CALLEE');\nP char(5) options(*nullind);\nend-pr;\ndcl-s TEXT char(5) nullind;\neval %nullind(TEXT)=*on;\ncallp TARGET(TEXT);\ncallp TARGET(TEXT);\nif TEXT='    2' and %nullind(TEXT);\nsndpgmmsg 'RIGHT ADJUSTED RETAINED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "CALLEE");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "RIGHT ADJUSTED RETAINED");
    }
}
