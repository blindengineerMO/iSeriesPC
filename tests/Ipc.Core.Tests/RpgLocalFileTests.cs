using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Procedure_local_automatic_files_restart_their_cursor_on_each_call(int ccsid)
    {
        Compile("**free\ndcl-s A int(20);\ndcl-s B int(20);\neval A=NEXTROW();\neval B=NEXTROW();\nif A=101 and B=101;\nsndpgmmsg 'LOCAL AUTOMATIC OK';\nendif;\nseton LR;\ndcl-proc NEXTROW;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n int(20) end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP");using var session=Session(ccsid);Ok(session.Execute("CALL QGPL/APP"),"LOCAL AUTOMATIC OK");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Procedure_local_static_files_retain_the_cursor_across_calls_and_LR(bool lr)
    {
        Compile("**free\ndcl-s N int(20);\neval N=NEXTROW();\nsndpgmmsg %char(N);\n"+(lr?"seton LR;":"return;")+"\ndcl-proc NEXTROW;\ndcl-f LOCAL static extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n int(20) end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nreturn BUFFER.ID;\nend-proc;","APP","LOCALFILES");
        using var session=Session();Assert.EndsWith("101",session.Execute("CALL QGPL/APP").Message?.Trim());Assert.EndsWith("102",session.Execute("CALL QGPL/APP").Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(LOCALFILES)"),"");Assert.EndsWith("101",session.Execute("CALL QGPL/APP").Message?.Trim());
    }

    [Fact]
    public void Procedure_local_static_CLOSE_does_not_implicitly_reopen_on_the_next_call()
    {
        Compile("**free\ncallp USEFILE();\ncallp USEFILE();\nseton LR;\ndcl-proc USEFILE;\ndcl-f LOCAL static extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\nif %open(LOCAL);\nclose LOCAL;\nelse;\nsndpgmmsg 'LOCAL STATIC CLOSE OK';\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL STATIC CLOSE OK");
    }

    [Fact]
    public void Procedure_local_LIKEFILE_copies_can_borrow_to_another_procedure()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-s N int(20);\neval N=CALLER();\nif N=101;\nsndpgmmsg 'LOCAL FILE ARGUMENT OK';\nendif;\nseton LR;\ndcl-proc CALLER;\ndcl-f LOCAL likefile(MODEL) extfile('QGPL/DATA');\ndcl-pi *n int(20) end-pi;\nreturn LOAD(LOCAL);\nend-proc;\ndcl-proc LOAD;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL FILE ARGUMENT OK");
    }

    [Fact]
    public void Procedure_local_records_require_data_structures_without_importing_scalar_fields()
    {
        Assert.Throws<RpgCompileException>(()=>Parse("**free\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\nread LOCAL;\nend-proc;"));
        var program=Parse("**free\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nend-proc;");
        Assert.Equal(6,RpgDiskBindings.Restore(program.Source!.Text,RpgDiskBindings.Serialize(program)).Version);
        Compile("**free\ndcl-s ID int(20) inz(777);\ncallp LOAD();\nif ID=777;\nsndpgmmsg 'LOCAL SCALARS PRESERVED';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL SCALARS PRESERVED");
    }

    [Fact]
    public void Procedure_local_equal_file_and_record_names_do_not_share_state_between_procedures()
    {
        var source="**free\ncallp FIRSTFILE();\ncallp SECONDFILE();\nseton LR;\n";
        foreach(var procedure in new[]{"FIRSTFILE","SECONDFILE"})
            source+="dcl-proc "+procedure+";\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg '"+procedure+" LOCAL OK';\nendif;\nend-proc;\n";
        Compile(source);using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FIRSTFILE LOCAL OK");Assert.Contains("SECONDFILE LOCAL OK",result.Message);
    }

    [Fact]
    public void Procedure_local_automatic_update_files_release_locks_after_abnormal_return()
    {
        Compile("**free\nmonitor;\ncallp LOAD();\non-error 202;\nsndpgmmsg 'LOCAL ERROR HANDLED';\nendmon;\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL usage(*update) keyed extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\ndcl-s N int(10);\nchain 20 LOCAL BUFFER;\neval N=1/0;\nend-proc;","APP","LOCALERROR");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL ERROR HANDLED");
        using var identity=OperationIdentity.Enter("QSECOFR",session.Job.Key);Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL","DATA",ObjectType.File)));
    }
    [Fact]
    public void Procedure_local_LIKEFILE_default_runtime_names_follow_the_local_copy()
    {
        Data("LOCAL");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ncallp LOAD();\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL likefile(MODEL);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(LOCAL.ROW);\nread LOCAL BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'LOCAL DEFAULT NAME OK';\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL DEFAULT NAME OK");
    }

    [Theory]
    [InlineData(false,303)] [InlineData(true,306)]
    public void Procedure_local_recursion_has_automatic_cursors_and_shared_static_cursors(bool isStatic,int expected)
    {
        Compile("**free\ndcl-s N int(20);\neval N=LOAD(3);\nif N="+expected+";\nsndpgmmsg 'LOCAL RECURSION OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL "+(isStatic?"static ":"")+"extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n int(20);\nDEPTH int(3) value;\nend-pi;\ndcl-ds BUFFER likerec(ROW);\ndcl-s CHILD int(20);\nread LOCAL BUFFER;\nif DEPTH>1;\neval CHILD=LOAD(DEPTH-1);\nendif;\nreturn BUFFER.ID+CHILD;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL RECURSION OK");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Procedure_local_INFDS_and_INFSR_keep_the_owners_frame_through_file_parameters(bool borrowed)
    {
        var body=borrowed?"callp USEFILE(LOCAL);":"read LOCAL BUFFER;";
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-s N int(20);\neval N=LOAD();\nif N=-1;\nsndpgmmsg 'LOCAL OWNER ERROR OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL likefile(MODEL) extfile('QGPL/DATA') usropn infds(INFO) infsr(REPAIR);\ndcl-pi *n int(20) end-pi;\ndcl-ds INFO qualified;\nSTATUS *status;\nend-ds;\ndcl-ds BUFFER likerec(LOCAL.ROW);\n"+body+"\nreturn 99;\nbegsr REPAIR;\nif INFO.STATUS=1211;\nreturn -1;\nendif;\nreturn -2;\nendsr;\nend-proc;\ndcl-proc USEFILE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL OWNER ERROR OK");
    }

    [Fact]
    public void Procedure_local_static_feedback_and_named_status_survive_between_invocations()
    {
        Compile("**free\ncallp LOAD();\ncallp LOAD();\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL static usropn extdesc('QGPL/DATA') extfile(*extdesc) infds(INFO);\ndcl-pi *n end-pi;\ndcl-ds INFO qualified static;\nSTATUS *status;\nend-ds;\ndcl-ds BUFFER likerec(ROW);\nif %open(LOCAL);\nif %eof(LOCAL) and %status(LOCAL)=11 and INFO.STATUS=11;\nsndpgmmsg 'LOCAL STATIC FEEDBACK OK';\nendif;\nclose LOCAL;\nreturn;\nendif;\nopen LOCAL;\ndou %eof(LOCAL);\nread LOCAL BUFFER;\nenddo;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL STATIC FEEDBACK OK");
    }

    [Theory]
    [InlineData("static", "")] [InlineData("", "static")]
    public void Procedure_local_INFDS_rejects_mismatched_storage(string fileStorage,string dataStorage)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL "+fileStorage+" extdesc('QGPL/DATA') extfile(*extdesc) infds(INFO);\ndcl-pi *n end-pi;\ndcl-ds INFO qualified "+dataStorage+";\nSTATUS *status;\nend-ds;\nend-proc;"));

    [Theory]
    [InlineData("**free\ndcl-f DATA static;")]
    [InlineData("**free\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL template static extdesc('QGPL/DATA');\nend-proc;")]
    [InlineData("**free\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL static(1) extdesc('QGPL/DATA') extfile(*extdesc);\nend-proc;")]
    public void Procedure_local_STATIC_rejects_invalid_file_locations_and_options(string source)
        => Assert.Throws<RpgCompileException>(()=>Parse(source));

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Procedure_local_saved_bindings_survive_source_and_description_removal(int ccsid)
    {
        Data("ACTUAL");
        Compile("**free\ncallp LOAD();\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile('QGPL/ACTUAL');\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'LOCAL SNAPSHOT OK';\nendif;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP");_files.DeleteFile("QGPL","DATA");using var session=Session(ccsid);Ok(session.Execute("CALL QGPL/APP"),"LOCAL SNAPSHOT OK");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Procedure_local_native_fixed_files_support_both_column_origins(int shift)
    {
        string P(bool begin) { var c=Enumerable.Repeat(' ',80+shift).ToArray();c[5+shift]='P';"LOAD".CopyTo(0,c,6+shift,4);c[23+shift]=begin?'B':'E';return new string(c)+"\n"; }
        string F(string name,string keywords="") { var c=Enumerable.Repeat(' ',43+shift).ToArray();c[5+shift]='F';name.CopyTo(0,c,6+shift,name.Length);if(name.Length>0){c[16+shift]='I';c[17+shift]='F';c[21+shift]='E';"DISK".CopyTo(0,c,35+shift,4);}return new string(c)+keywords+"\n"; }
        var source=RpgFixedStructureTests.D("N","S",length:"20",type:'I',decimals:"0",shift:shift)+
            RpgFixedCalculationTests.C("EVAL",expression:"N=LOAD()")+RpgFixedCalculationTests.C("DSPLY",factor1:"N")+RpgFixedCalculationTests.C("SETON",eq:"LR")+
            P(true)+F("LOCAL","STATIC")+F("","EXTDESC('QGPL/DATA')")+F("","EXTFILE(*EXTDESC)")+
            RpgFixedStructureTests.D("LOAD","PI",length:"20",type:'I',decimals:"0",shift:shift)+RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(ROW)",shift:shift)+
            RpgFixedCalculationTests.C("READ",factor2:"LOCAL",result:"BUFFER")+RpgFixedCalculationTests.C("RETURN",expression:"BUFFER.ID")+P(false);
        Compile(source);using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"101");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Procedure_local_static_locks_release_at_activation_group_end(bool newGroup)
    {
        Compile("**free\ncallp LOAD();\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL static keyed usage(*update) extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nchain 20 LOCAL BUFFER;\nend-proc;","APP",newGroup?"*NEW":"STATICLOCK");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"");using var identity=OperationIdentity.Enter("QSECOFR",session.Job.Key);
        var locks=new JobLockStore(_system.Connections);Assert.Equal(!newGroup,locks.Inspect(new("QGPL","DATA",ObjectType.File)).Count>0);
        if(!newGroup){Ok(session.Execute("RCLACTGRP ACTGRP(STATICLOCK)"),"");Assert.Empty(locks.Inspect(new("QGPL","DATA",ObjectType.File)));}
    }

    [Fact]
    public void Procedure_local_forward_LIKE_of_a_record_return_uses_the_local_description()
    {
        Compile("**free\ndcl-s RESULT like(LOAD);\neval RESULT=LOAD();\nif %size(RESULT)=25 and %subst(RESULT:1:8)=x'0000000000000065';\nsndpgmmsg 'LOCAL FORWARD RETURN OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL qualified extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n likerec(LOCAL.ROW) rtnparm end-pi;\ndcl-ds BUFFER likerec(LOCAL.ROW);\nread LOCAL BUFFER;\nreturn BUFFER;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL FORWARD RETURN OK");
    }

    [Fact]
    public void Procedure_local_forward_record_returns_preserve_the_global_parent_family()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-s PROBE like(LOAD);\ndcl-ds RESULT likerec(MODEL.ROW);\neval RESULT=LOAD();\ncallp CHECK(RESULT);\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL likefile(MODEL) extfile('QGPL/DATA');\ndcl-pi *n likerec(LOCAL.ROW) rtnparm end-pi;\ndcl-ds BUFFER likerec(LOCAL.ROW);\nread LOCAL BUFFER;\nreturn BUFFER;\nend-proc;\ndcl-proc CHECK;\ndcl-pi *n;\nP likerec(MODEL.ROW) options(*exact);\nend-pi;\nif P.ID=101 and %size(PROBE)=25;\nsndpgmmsg 'LOCAL FORWARD FAMILY OK';\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL FORWARD FAMILY OK");
    }

    [Fact]
    public void Procedure_local_files_shadow_global_file_parameters_without_using_their_handles()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f F extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread F BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'LOCAL FILE SHADOW OK';\nendif;\nend-proc;","LOCALHIDE");
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('LOCALHIDE');\nF likefile(MODEL);\nend-pr;\ndcl-ds BUFFER likerec(MODEL.ROW);\nread COPY BUFFER;\ncallp LOAD(COPY);\nread COPY BUFFER;\nif BUFFER.ID=102;\nsndpgmmsg 'LOCAL CALLER CURSOR OK';\nendif;\nseton LR;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"LOCAL FILE SHADOW OK");Assert.Contains("LOCAL CALLER CURSOR OK",result.Message);
    }

    [Fact]
    public void Procedure_local_file_shadows_do_not_invoke_the_global_files_INFSR()
    {
        Compile("**free\ndcl-f F extdesc('QGPL/DATA') extfile(*extdesc) usropn infsr(FAILED);\ncallp LOAD();\nif not %open(F);\nsndpgmmsg 'LOCAL GLOBAL HANDLER OK';\nendif;\nseton LR;\nbegsr FAILED;\nsndpgmmsg 'SHOULD NOT EXECUTE';\nreturn;\nendsr;\ndcl-proc LOAD;\ndcl-f F extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\nread F BUFFER;\nend-proc;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"LOCAL GLOBAL HANDLER OK");Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Theory]
    [InlineData("version")] [InlineData("procedure")] [InlineData("duplicate")]
    public void Procedure_local_version_six_bindings_validate_scope_and_saved_requests(string change)
    {
        var source="**free\nreturn;\n";
        foreach(var procedure in new[]{"FIRSTFILE","SECONDFILE"})source+="dcl-proc "+procedure+";\ndcl-f LOCAL static extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\nend-proc;\n";
        var program=Parse(source);var json=RpgDiskBindings.Serialize(program);var restored=RpgDiskBindings.Restore(program.Source!.Text,json);Assert.Equal(6,restored.Version);restored.Verify(program);
        var node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;
        if(change=="version")node["version"]=5;
        else if(change=="procedure")node["files"]![0]!["request"]!["procedure"]="OTHER";
        else node["files"]![1]!["request"]!["procedure"]="FIRSTFILE";
        if(change=="procedure"){restored=RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString());Assert.Throws<InvalidDataException>(()=>restored.Verify(program));}
        else Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString()));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void Procedure_local_implicit_open_errors_run_local_INFSR_with_RETURN_or_GOTO(bool isStatic,bool branch)
    {
        var storage=isStatic?"static ":"";
        Compile("**free\ndcl-s N int(20);\neval N=LOAD();\nif N=-1;\nsndpgmmsg 'LOCAL IMPLICIT ERROR OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-f LOCAL "+storage+"extdesc('QGPL/DATA') extfile('QGPL/MISSING') infds(INFO) infsr(REPAIR);\ndcl-pi *n int(20) end-pi;\ndcl-ds INFO qualified "+storage+";\nSTATUS *status;\nend-ds;\nsndpgmmsg 'SHOULD NOT EXECUTE';\nreturn 99;\ntag RECOVERED;\nreturn -1;\nbegsr REPAIR;\nif INFO.STATUS=1216;\n"+(branch?"goto RECOVERED;":"return -1;")+"\nendif;\nreturn -2;\nendsr;\nend-proc;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"LOCAL IMPLICIT ERROR OK");Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Fact]
    public void Procedure_local_STATIC_structures_are_allocated_once_without_charging_every_recursive_frame()
    {
        Compile("**free\ndcl-s N int(20);\neval N=LOAD(80);\nif N=1048544;\nsndpgmmsg 'LOCAL STATIC STORAGE OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n int(20);\nDEPTH int(10) value;\nend-pi;\ndcl-ds MEMORY qualified static dim(32);\nPAYLOAD char(32767);\nend-ds;\nif DEPTH=0;\nreturn %size(MEMORY:*all);\nendif;\nreturn LOAD(DEPTH-1);\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"LOCAL STATIC STORAGE OK");
    }

    [Theory]
    [InlineData("open LOCAL;")] [InlineData("read LOCAL;")] [InlineData("read ROW;")]
    public void Procedure_local_files_and_records_cannot_be_used_from_the_main_scope(string operation)
        => Assert.Contains("not visible",Assert.Throws<RpgCompileException>(()=>Parse("**free\n"+operation+"\nreturn;\ndcl-proc LOAD;\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\nend-proc;")).Message);

    [Fact]
    public void Procedure_local_metadata_serialization_enforces_the_byte_bound_across_all_scopes()
    {
        var fields=Enumerable.Range(1,1024).Select(n=>new RpgDiskField("F"+n,"F"+n,RpgFieldKind.Character,1,0,37,0,false,n,1)).ToArray();
        RpgDiskLayout DescribeWide(RpgDiskRequest request)
        {
            var record=RpgDiskBindings.RecordName(request,"ROW");
            return new("QGPL/DATA",RpgDiskLayout.SignatureFor("ROW",record,1024,1,fields),"ROW",record,1024,1,fields);
        }
        var source="**free\nreturn;\n"+string.Concat(Enumerable.Range(1,32).Select(n=>"dcl-proc LOAD"+n+";\ndcl-f LOCAL extdesc('QGPL/DATA') extfile(*extdesc);\ndcl-pi *n end-pi;\nend-proc;\n"));
        var program=Ipc.Rpg.Parsing.RpgCompiler.Compile("QGPL","WIDE",source,diskResolver:DescribeWide);
        Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Serialize(program));
    }

    [Fact]
    public void Procedure_local_STATIC_data_survives_LR_while_cycle_variables_are_reinitialized()
    {
        Compile("**free\ndcl-s GLOBAL int(10) inz(0);\ndcl-s N int(10);\neval GLOBAL=GLOBAL+1;\neval N=LOAD();\nif GLOBAL=1;\nsndpgmmsg %char(N);\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n int(10) end-pi;\ndcl-ds MEMORY qualified static inz;\nCOUNT int(10);\nend-ds;\neval MEMORY.COUNT=MEMORY.COUNT+1;\nreturn MEMORY.COUNT;\nend-proc;","APP","CYCSTATIC");
        using var session=Session();Assert.EndsWith("1",session.Execute("CALL QGPL/APP").Message?.Trim());Assert.EndsWith("2",session.Execute("CALL QGPL/APP").Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(CYCSTATIC)"),"");Assert.EndsWith("1",session.Execute("CALL QGPL/APP").Message?.Trim());
    }

    [Fact]
    public void Procedure_local_scope_keeps_global_named_file_feedback_shared_with_the_main_procedure()
    {
        Compile("**free\ndcl-f GLOBALFILE extdesc('QGPL/DATA') extfile(*extdesc);\ncallp LOAD();\nif %eof(GLOBALFILE) and %status(GLOBALFILE)=11 and not %found(GLOBALFILE);\nsndpgmmsg 'GLOBAL FILE FEEDBACK OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ROW);\ndou %eof(GLOBALFILE);\nread GLOBALFILE BUFFER;\nenddo;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"GLOBAL FILE FEEDBACK OK");
    }

}
