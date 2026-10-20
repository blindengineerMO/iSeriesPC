using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Stored_programs_borrow_and_detach_null_maps_across_retained_activation_calls(int ccsid)
    {
        NullData();
        const string model = "dcl-ds MODEL qualified extname('QGPL/NULLDATA':*all) template end-ds;\n";
        Compile("**free\nctl-opt alwnull(*usrctl);\n" + model + "dcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\ndcl-s COUNT int(10);\nif %nullind(P.NTEXT);\neval COUNT=COUNT+1;\neval P.NKEY=COUNT;\neval P.NTEXT='stored';\neval %nullind(P.NTEXT)=*off;\nendif;\nreturn;", "CALLEE", "NULLPARAM");
        Compile("**free\nctl-opt alwnull(*usrctl);\n" + model + "dcl-pr TARGET extpgm('QGPL/CALLEE');\nP likeds(MODEL) options(*nullind);\nend-pr;\ndcl-ds DATA likeds(MODEL) inz;\neval %nullind(DATA.NTEXT)=*on;\ncallp TARGET(DATA);\neval %nullind(DATA.NTEXT)=*on;\ncallp TARGET(DATA);\nif DATA.NTEXT='stored' and DATA.NKEY=2 and not %nullind(DATA.NTEXT);\nsndpgmmsg 'NULL PARAMETER RETAINED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "CALLEE"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL PARAMETER RETAINED");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Borrowed_parameter_maps_drive_record_input_and_update(int ccsid)
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update);\ndcl-ds MODEL likerec(NULLROW:*all) template;\ndcl-ds DATA likeds(MODEL) inz;\ncallp LOAD(DATA);\nif DATA.NID=1 and %nullind(DATA.NTEXT);\neval DATA.NTEXT='updated';\neval %nullind(DATA.NTEXT)=*off;\ncallp SAVE(DATA);\nsndpgmmsg 'NULL PARAMETER RECORD IO';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\nchain 10 NULLDATA P;\nend-proc;\ndcl-proc SAVE;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\nupdate NULLROW P;\nend-proc;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL PARAMETER RECORD IO"); Assert.Equal("updated", Assert.IsType<string>(_files.ReadAll("QGPL", "NULLDATA", "NULLDATA")[0]["NTEXT"]).TrimEnd());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Separate_formal_indicator_maps_receive_record_attributes_independently_of_actual_automatic_maps(bool readOnly)
    {
        NullData();
        var operation = readOnly ? "monitor;\nchain 10 NULLDATA P;\non-error;\nreturn;\nendmon;" : "chain 10 NULLDATA P;";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds MODEL likerec(NULLROW:*all) template;\ndcl-ds DATA likeds(MODEL) inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) inz;\neval DATA.NID=99;\ncallp LOAD(DATA:FLAGS);\nif " + (readOnly ? "DATA.NID=99 and not FLAGS.NTEXT" : "DATA.NID=1 and FLAGS.NTEXT and not %nullind(DATA.NTEXT)") + ";\nsndpgmmsg 'NULL EXPLICIT PARAMETER MAP';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nP likeds(MODEL) nullind(M);\nM likerec(NULLROW:*all:*null) " + (readOnly ? "const" : "") + ";\nend-pi;\n" + operation + "\nend-proc;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL EXPLICIT PARAMETER MAP");
    }
}
