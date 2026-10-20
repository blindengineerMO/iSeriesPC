using Ipc.Core.Work;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEFILE_parameters_share_the_callers_cursor_and_qualified_record_data(int ccsid)
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-s A int(20);\ndcl-s B int(20);\neval A=LOAD(COPY);\neval B=LOAD(COPY);\nif A=101 and B=102 and %open(COPY);\nsndpgmmsg 'FILE PARAMETER OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP");using var session=Session(ccsid);Ok(session.Execute("CALL QGPL/APP"),"FILE PARAMETER OK");
    }

    [Fact]
    public void LIKEFILE_parameters_open_and_close_the_callers_USROPN_file()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn;\ncallp USEFILE(COPY);\nif not %open(COPY);\nsndpgmmsg 'FILE PARAMETER OPEN OK';\nendif;\nseton LR;\ndcl-proc USEFILE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nif not %open(F);\nopen F;\nendif;\nread F BUFFER;\nclose F;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE PARAMETER OPEN OK");
    }

    [Theory]
    [InlineData("const")] [InlineData("value")] [InlineData("dim(2)")] [InlineData("options(*exact)")]
    public void LIKEFILE_parameters_reject_value_storage_options(string options)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL) "+options+";\nend-pi;\nend-proc;"));
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEFILE_main_PI_parameters_share_file_state_between_saved_RPG_programs(int ccsid)
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'FILE MAIN PI OK';\nendif;\nseton LR;","FILECALLEE");
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('FILECALLEE');\nF likefile(MODEL);\nend-pr;\ndcl-ds BUFFER likerec(MODEL.ROW);\ncallp LOAD(COPY);\nread COPY BUFFER;\nif BUFFER.ID=102;\nsndpgmmsg 'FILE CALLER STATE OK';\nendif;\nseton LR;");
        foreach(var name in new[]{"APP","FILECALLEE"})_files.RemoveMember("QGPL","SOURCE",name);
        using var session=Session(ccsid);Ok(session.Execute("CALL QGPL/APP"),"FILE CALLER STATE OK");
    }

    [Fact]
    public void LIKEFILE_parameter_reads_share_EOF_FOUND_EQUAL_and_STATUS_with_the_caller()
    {
        Compile("**free\ndcl-f MODEL qualified template keyed block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ncallp TESTFILE(COPY);\nif %eof(COPY) and not %found(COPY) and %status(COPY)=11;\nsndpgmmsg 'FILE SHARED STATUS OK';\nendif;\nseton LR;\ndcl-proc TESTFILE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nsetll 20 F;\nif not %equal(F);\nreturn;\nendif;\nchain 999 F BUFFER;\nsetgt 999 F;\nread F BUFFER;\nif not %eof(F) or %found(F) or %status(F)<>11;\nsetll 10 F;\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE SHARED STATUS OK");
    }

    [Fact]
    public void LIKEFILE_parameter_update_consumes_the_callers_update_lock()
    {
        Compile("**free\ndcl-f MODEL qualified template keyed usage(*update) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(MODEL.ROW);\nchain 20 COPY BUFFER;\ncallp CHANGE(COPY:BUFFER);\nmonitor;\nupdate COPY.ROW BUFFER;\non-error 1221;\nsndpgmmsg 'FILE SHARED LOCK OK';\nendmon;\nseton LR;\ndcl-proc CHANGE;\ndcl-pi *n;\nF likefile(MODEL);\nP likerec(MODEL.ROW);\nend-pi;\neval P.TEXT='changed';\nupdate F.ROW P;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE SHARED LOCK OK");
    }

    [Theory]
    [InlineData("")] [InlineData("*omit")]
    public void LIKEFILE_optional_parameters_can_be_guarded_without_accessing_missing_storage(string argument)
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-s N int(3);\neval N=TESTFILE("+argument+");\nif N=7;\nsndpgmmsg 'FILE OPTIONAL OK';\nendif;\nseton LR;\ndcl-proc TESTFILE;\ndcl-pi *n int(3);\nF likefile(MODEL) options(*nopass:*omit);\nend-pi;\nif %parms=0 or %omitted(F);\nreturn 7;\nendif;\nreturn 0;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE OPTIONAL OK");
    }

    [Fact]
    public void LIKEFILE_arguments_require_the_same_family_and_a_runtime_file()
    {
        const string header="**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f OTHER template block(*no) extdesc('QGPL/DATA') rename(ROW:ALT);\ndcl-f COPY likefile(OTHER) extfile('QGPL/DATA');\ndcl-pr LOAD;\nF likefile(MODEL);\nend-pr;\n";
        foreach(var argument in new[]{"MODEL","COPY","7","'COPY'"})
            Assert.Throws<RpgCompileException>(()=>Parse(header+"callp LOAD("+argument+");\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\nend-proc;"));
    }

    [Fact]
    public void LIKEFILE_parameter_parent_blocking_and_source_order_must_be_known()
    {
        Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\ndcl-pr LOAD;\nF likefile(MODEL);\nend-pr;"));
        Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-pr LOAD;\nF likefile(MODEL);\nend-pr;\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');"));
    }

    [Fact]
    public void LIKEFILE_parameter_forwarding_keeps_the_same_cursor()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-s N int(20);\neval N=FORWARD(COPY);\nif N=101;\nsndpgmmsg 'FILE FORWARD OK';\nendif;\nseton LR;\ndcl-proc FORWARD;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\nreturn LOAD(F);\nend-proc;\ndcl-proc LOAD;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE FORWARD OK");
    }

    [Fact]
    public void LIKEFILE_file_errors_invoke_the_callers_INFSR_with_caller_storage()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn infds(INFO) infsr(REPAIR);\ndcl-ds INFO qualified;\nSTATUS *status;\nend-ds;\ncallp LOAD(COPY);\nsndpgmmsg 'SHOULD NOT EXECUTE';\nseton LR;\nbegsr REPAIR;\nif INFO.STATUS=1211;\nsndpgmmsg 'FILE OWNER INFSR OK';\nendif;\nreturn;\nendsr;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nend-proc;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FILE OWNER INFSR OK");Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Fact]
    public void LIKEFILE_owner_INFSR_can_branch_back_to_the_callers_main_frame()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn infsr(REPAIR);\ndcl-ds BUFFER likerec(MODEL.ROW);\ncallp LOAD(COPY);\ntag RECOVERED;\nread COPY BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'FILE OWNER GOTO OK';\nendif;\nseton LR;\nbegsr REPAIR;\nopen COPY;\ngoto RECOVERED;\nendsr;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds LOCAL likerec(F.ROW);\nread F LOCAL;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE OWNER GOTO OK");
    }

    [Fact]
    public void LIKEFILE_return_record_metadata_can_reference_a_later_file_parameter()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(MODEL.ROW);\neval BUFFER=LOAD(COPY);\nif BUFFER.ID=101;\nsndpgmmsg 'FILE FORWARD RETURN OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n likerec(F.ROW) rtnparm;\nF likefile(MODEL);\nend-pi;\ndcl-ds LOCAL likerec(F.ROW);\nif %parms<>2 or %parmnum(F)<>2;\nreturn LOCAL;\nendif;\nread F LOCAL;\nreturn LOCAL;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE FORWARD RETURN OK");
    }

    [Theory]
    [InlineData("eval N=%size(F);")] [InlineData("eval N=%len(F);")]
    [InlineData("clear F;")] [InlineData("eval F='x';")]
    [InlineData("eval N=%size(F(1));")] [InlineData("sndpgmmsg F;")]
    public void LIKEFILE_parameters_have_no_scalar_or_array_data_storage(string operation)
        => Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\nseton LR;\ndcl-proc USEFILE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-s N int(10);\n"+operation+"\nend-proc;"));

    [Theory]
    [InlineData("dcl-s N char(%size(F));")] [InlineData("dcl-s N like(F);")]
    public void LIKEFILE_parameters_cannot_define_data_layouts(string declaration)
        => Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\nseton LR;\ndcl-proc USEFILE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\n"+declaration+"\nend-proc;"));

    [Theory]
    [InlineData("")] [InlineData("*omit")]
    public void LIKEFILE_missing_parameter_IO_fails_without_touching_a_file(string argument)
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\nmonitor;\ncallp USEFILE("+argument+");\non-error 202;\nsndpgmmsg 'FILE MISSING GUARDED';\nendmon;\nseton LR;\ndcl-proc USEFILE;\ndcl-pi *n;\nF likefile(MODEL) options(*nopass:*omit);\nend-pi;\nopen F;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE MISSING GUARDED");
    }

    [Fact]
    public void LIKEFILE_borrowed_arguments_expire_without_exposing_bytes_or_storage()
    {
        var program=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn;\ndcl-pr USEFILE extpgm('TARGET');\nF likefile(MODEL);\nend-pr;\ncallp USEFILE(COPY);");
        ProgramArgument? retained=null;
        var host=new RpgHost { TypedProgramCaller=(_,_,_,borrow)=> {
            retained=Assert.IsType<ProgramArgument>(Assert.Single(borrow!()));
            Assert.Equal("*FILE",retained.Type);Assert.Equal(0,retained.Length);Assert.False(retained.HasBuffer);
            Assert.Null(retained.Storage);Assert.Null(retained.Memory);Assert.Null(retained.ToBuffer());Assert.NotNull(retained.Value);
            return new() { Success=true };
        } };
        new RpgInterpreter(host).Run(program);
        Assert.Throws<ObjectDisposedException>(()=>retained!.Value);
    }

    [Fact]
    public void LIKEFILE_main_PI_rebinds_retained_callees_to_each_new_actual()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nsndpgmmsg %char(BUFFER.ID);\nreturn;","FILERET","FILEGROUP");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f A likefile(MODEL) extfile('QGPL/DATA');\ndcl-f B likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('FILERET');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(A);\ncallp LOAD(B);\ncallp LOAD(A);\nseton LR;");
        foreach(var name in new[]{"APP","FILERET"})_files.RemoveMember("QGPL","SOURCE",name);
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"101\n101\n102");
    }

    [Fact]
    public void LIKEFILE_local_parameters_can_shadow_a_global_file_name()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ncallp LOAD(COPY);\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nCOPY likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(COPY.ROW);\nread COPY BUFFER;\nif BUFFER.ID=101 and %parmnum(COPY)=1;\nsndpgmmsg 'FILE SHADOW OK';\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE SHADOW OK");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LIKEFILE_error_extenders_and_MONITOR_precede_the_owners_INFSR(bool extender)
    {
        var operation=extender ? "read(e) F BUFFER;\nif %error and %status(F)=1211;\nsndpgmmsg 'FILE ERROR HANDLED';\nendif;" :
            "monitor;\nread F BUFFER;\non-error 1211;\nsndpgmmsg 'FILE ERROR HANDLED';\nendmon;";
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn infsr(REPAIR);\ncallp LOAD(COPY);\nseton LR;\nbegsr REPAIR;\nsndpgmmsg 'WRONG INFSR';\nreturn;\nendsr;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\n"+operation+"\nend-proc;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FILE ERROR HANDLED");Assert.DoesNotContain("WRONG INFSR",result.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LIKEFILE_saved_program_errors_return_or_branch_to_the_owning_RPG_program(bool branch)
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nseton LR;","FILEERR");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn infsr(REPAIR);\ndcl-pr LOAD extpgm('FILEERR');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nsndpgmmsg 'SHOULD NOT EXECUTE';\ntag RECOVERED;\nsndpgmmsg 'FILE OWNER BRANCH';\nseton LR;\nbegsr REPAIR;\nsndpgmmsg 'FILE OWNER ERROR';\n"+(branch?"goto RECOVERED;":"return;")+"\nendsr;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FILE OWNER ERROR");Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
        Assert.Equal(branch,result.Message!.Contains("FILE OWNER BRANCH",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void LIKEFILE_native_fixed_main_PI_parameters_support_both_column_origins(int shift)
    {
        var header=Enumerable.Repeat(' ',43+shift).ToArray();header[5+shift]='F';"MODEL".CopyTo(0,header,6+shift,5);
        header[16+shift]='I';header[17+shift]='F';header[21+shift]='E';"DISK".CopyTo(0,header,35+shift,4);
        var continuation=Enumerable.Repeat(' ',43+shift).ToArray();continuation[5+shift]='F';
        var source=new string(header)+"TEMPLATE BLOCK(*NO)\n"+new string(continuation)+"EXTDESC('QGPL/DATA')\n"+
            RpgFixedStructureTests.D(definition:"PI",shift:shift)+RpgFixedStructureTests.D("F",keywords:"LIKEFILE(MODEL)",shift:shift)+
            RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(F.ROW)",shift:shift)+
            "/free\nread F BUFFER;\nsndpgmmsg %char(BUFFER.ID);\nseton LR;\n/end-free";
        Compile(source,"FILEFIX");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('FILEFIX');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nseton LR;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"101");
    }

    [Fact]
    public void LIKEFILE_main_PI_bindings_use_version_five_and_verify_parameter_flags()
    {
        var program=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\nseton LR;");
        var json=RpgDiskBindings.Serialize(program);var restored=RpgDiskBindings.Restore(program.Source!.Text,json);
        Assert.Equal(5,restored.Version);restored.Verify(program);
        var node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["version"]=4;
        Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString()));
        node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["files"]!.AsArray().Single(f=>f!["request"]!["name"]!.GetValue<string>()=="F")!["request"]!["isParameter"]=false;
        restored=RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString());Assert.Throws<InvalidDataException>(()=>restored.Verify(program));
        node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["files"]!.AsArray().Single(f=>f!["request"]!["name"]!.GetValue<string>()=="F")!["request"]!["qualified"]=false;
        Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString()));
    }

    [Theory]
    [InlineData("TARGET")] [InlineData("QWCRSVAL")]
    public void LIKEFILE_arguments_reject_CL_and_API_targets_before_execution(string target)
    {
        ClExternalCallTests.Create(_system,"TARGET","CLP","PGM\nSNDPGMMSG MSG('SHOULD NOT EXECUTE')\nENDPGM");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('"+target+"');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nseton LR;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Assert.True(result.IsError);Assert.Contains("only between RPG",result.Message);Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEFILE_saved_parameters_survive_source_and_description_removal(int ccsid)
    {
        Data("ACTUAL");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nif BUFFER.ID=101 and BUFFER.TEXT='first';\nsndpgmmsg 'FILE SNAPSHOT PARAMETER OK';\nendif;\nseton LR;","FILESNAP");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/ACTUAL');\ndcl-pr LOAD extpgm('FILESNAP');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nseton LR;");
        foreach(var name in new[]{"APP","FILESNAP"})_files.RemoveMember("QGPL","SOURCE",name);
        _files.DeleteFile("QGPL","DATA");using var session=Session(ccsid);Ok(session.Execute("CALL QGPL/APP"),"FILE SNAPSHOT PARAMETER OK");
    }

    [Fact]
    public void LIKEFILE_main_PI_rejects_incompatible_usage_before_the_callee_runs()
    {
        Compile("**free\ndcl-f MODEL template usage(*update) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\nsndpgmmsg 'SHOULD NOT EXECUTE';\nseton LR;","FILEBAD");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('FILEBAD');\nF likefile(MODEL);\nend-pr;\ncallp(e) LOAD(COPY);\nif %error and %status=202;\nsndpgmmsg 'FILE BAD SIGNATURE OK';\nendif;\nseton LR;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FILE BAD SIGNATURE OK");Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Fact]
    public void LIKEFILE_parameter_names_do_not_capture_member_names_literals_or_exponents()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ncallp LOAD(COPY);\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nE3 likefile(MODEL);\nend-pi;\ndcl-ds BUFFER qualified;\nE3 int(10);\nend-ds;\neval BUFFER . E3=1E3;\nif BUFFER . E3=1000 and 'E3'='E3' and %open(E3);\nsndpgmmsg 'FILE NAME SCANNING OK';\nendif;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE NAME SCANNING OK");
    }

    [Fact]
    public void LIKEFILE_parameter_shadows_keep_the_actual_owners_INFSR()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn infsr(REPAIR);\ncallp LOAD(COPY);\nseton LR;\nbegsr REPAIR;\nsndpgmmsg 'FILE SHADOW ERROR OK';\nreturn;\nendsr;\ndcl-proc LOAD;\ndcl-pi *n;\nCOPY likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(COPY.ROW);\nread COPY BUFFER;\nend-proc;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE SHADOW ERROR OK");
    }

    [Fact]
    public void LIKEFILE_direct_runtime_data_access_guards_precede_storage_access()
    {
        var program=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL) options(*nopass);\nend-pi;\nseton LR;");
        var context=new RpgRuntimeContext(program,new());
        Assert.Equal(105,Assert.Throws<RpgRuntimeException>(()=>context.ReadValue("F")).StatusCode);
        Assert.Equal(105,Assert.Throws<RpgRuntimeException>(()=>context.WriteValue("F","x")).StatusCode);
        Assert.Equal(105,Assert.Throws<RpgRuntimeException>(()=>context.EvalBuiltin("SIZE",[new Ipc.Rpg.Parsing.RpgFieldRef("F")])).StatusCode);
    }

    [Fact]
    public void LIKEFILE_file_and_data_actuals_with_the_same_name_follow_the_formal_type()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-s COPY char(4) inz('KEEP');\ncallp ECHO(COPY);\ncallp LOAD(COPY);\nseton LR;\ndcl-proc ECHO;\ndcl-pi *n;\nP char(4) const;\nend-pi;\nsndpgmmsg P;\nend-proc;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'FILE FORMAL TYPE OK';\nendif;\nend-proc;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FILE FORMAL TYPE OK");Assert.Contains("KEEP",result.Message);
    }

    [Fact]
    public void LIKEFILE_main_parameters_share_the_existing_visible_file_limit()
    {
        var declarations="**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\n"+
            string.Concat(Enumerable.Range(1,15).Select(i=>"dcl-f COPY"+i+" template likefile(MODEL);\n"));
        var error=Assert.Throws<RpgCompileException>(()=>Parse(declarations+"dcl-pi *n;\nF likefile(MODEL);\nend-pi;\nseton LR;"));
        Assert.Contains("16 visible DISK files",error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LIKEFILE_parameters_cannot_shadow_their_own_parent_definition(bool local)
    {
        var source="**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\n"+
            (local?"seton LR;\ndcl-proc LOAD;\n":"")+"dcl-pi *n;\nMODEL likefile(MODEL);\nend-pi;\n"+(local?"end-proc;":"seton LR;");
        Assert.Contains("own parent",Assert.Throws<RpgCompileException>(()=>Parse(source)).Message);
    }

    [Fact]
    public void LIKEFILE_global_file_parameters_remain_bound_under_local_data_shadows()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-pi *n end-pi;\ndcl-s F char(4) inz('KEEP');\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nif F='KEEP' and %size(F)=4 and BUFFER.ID=101 and %open(F);\nsndpgmmsg 'FILE GLOBAL SHADOW OK';\nendif;\nend-proc;","FILEHIDE");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD extpgm('FILEHIDE');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nseton LR;");
        using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"FILE GLOBAL SHADOW OK");
    }

    [Fact]
    public void LIKEFILE_record_metadata_caches_keep_equal_layouts_from_different_families_separate()
    {
        var source="**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f OTHER qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f A likefile(MODEL) extfile('QGPL/DATA');\ndcl-f B likefile(OTHER) extfile('QGPL/DATA');\ncallp FIRSTFILE(A);\ncallp SECONDFILE(B);\nseton LR;\n";
        foreach(var (procedure,parent) in new[]{("FIRSTFILE","MODEL"),("SECONDFILE","OTHER")})
            source+="dcl-proc "+procedure+";\ndcl-pi *n;\nF likefile("+parent+");\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg '"+procedure+" FAMILY OK';\nendif;\nend-proc;\n";
        Compile(source);using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"FIRSTFILE FAMILY OK");Assert.Contains("SECONDFILE FAMILY OK",result.Message);
    }

    [Fact]
    public void LIKEFILE_semantic_host_arguments_expire_at_the_call_boundary()
    {
        var caller=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') usropn;\ndcl-pr LOAD extpgm('TARGET');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);");
        var callee=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\nif not %open(F);\nsndpgmmsg 'FILE REFERENCE LIVE';\nendif;\nseton LR;");
        object? retained=null;var entered=0;
        var calleeHost=new RpgHost { SendMessage=_=>entered++ };
        var host=new RpgHost { TypedProgramCaller=(_,_,arguments,_)=> {
            retained=Assert.Single(arguments);new RpgInterpreter(calleeHost).Run(callee,[retained]);
            return new() { Success=true };
        } };
        new RpgInterpreter(host).Run(caller);Assert.Equal(1,entered);
        Assert.Throws<ObjectDisposedException>(()=>new RpgInterpreter(calleeHost).Run(callee,[retained]));Assert.Equal(1,entered);
    }

    [Fact]
    public void LIKEFILE_saved_owner_RETURN_unwinds_the_callee_before_continuing_its_statements()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nsndpgmmsg 'BAD CALLEE CONTINUATION';\nseton LR;","FILEFAIL");
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) usropn infsr(REPAIR);\ndcl-pr LOAD extpgm('FILEFAIL');\nF likefile(MODEL);\nend-pr;\ncallp LOAD(COPY);\nsndpgmmsg 'BAD CALLER CONTINUATION';\nseton LR;\nbegsr REPAIR;\nsndpgmmsg 'SAVED FILE OWNER RETURN OK';\nreturn;\nendsr;");
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Ok(result,"SAVED FILE OWNER RETURN OK");Assert.DoesNotContain("BAD",result.Message);
    }

}
