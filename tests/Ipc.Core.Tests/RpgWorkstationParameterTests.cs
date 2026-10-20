using System.Text.Json.Nodes;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDisplayFileTests
{
    private const string WorkstationParameterFiles="**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) extfile(*extdesc);\n";

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_parameter_local_procedures_borrow_the_caller_handle_and_exact_buffer(int ccsid)
    {
        Display(Screen);Compile(WorkstationParameterFiles+"dcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='CALLER';\ncallp SHOW(COPY:BUFFER);\nif BUFFER.NAME='DONE' and BUFFER.AMOUNT=2.5 and %open(COPY);\nsndpgmmsg 'WORKSTN PARAMETER OK';\nendif;\nseton LR;\ndcl-proc SHOW;\ndcl-pi *n;\nF likefile(MODEL);\nP likerec(F.ENTRY:*all) options(*exact);\nend-pi;\nexfmt F.ENTRY P;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{
            Assert.Contains("CALLER",d.Session.Buffer.RowText(4));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "DONE")d.Session.Handle(new(AidKey.None,Character:ch));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.Tab));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "2.5")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN PARAMETER OK",result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_parameter_saved_programs_share_caller_INDDS_after_source_and_description_removal(int ccsid)
    {
        var dds=Dds(keywords:"INDARA CF06(06)")+Dds("ENTRY",record:true)+Dds("NAME",10,usage:'B',row:4,column:2);Display(dds,"DESC");Display(dds,"ACTUAL");
        Compile("**free\ndcl-f FORM workstn template extdesc('QGPL/DESC');\ndcl-pi *n;\nF likefile(FORM);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nexfmt F.ENTRY BUFFER;\nif BUFFER.NAME='SAVED';\nsndpgmmsg 'SAVED WORKSTN PARAMETER OK';\nendif;\nseton LR;","WSCALLEE");
        Compile(WorkstationParameterFiles.Replace("QGPL/SCREEN","QGPL/DESC").Replace("extfile(*extdesc);","extfile('QGPL/ACTUAL') indds(FLAGS);")+"dcl-ds FLAGS len(99) qualified;\nF6 ind pos(6);\nend-ds;\ndcl-pr SHOW extpgm('WSCALLEE');\nF likefile(MODEL);\nend-pr;\ncallp SHOW(COPY);\nif FLAGS.F6 and not *in06 and %open(COPY);\nsndpgmmsg 'CALLER WORKSTN INDDS OK';\nendif;\nseton LR;");
        foreach(var member in new[]{"APP","WSCALLEE"})_sources.RemoveMember("QGPL","SOURCE",member);
        _system.Objects.Delete("QGPL","DESC",ObjectType.File);
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{foreach(var ch in "SAVED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Pf6));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("SAVED WORKSTN PARAMETER OK",result.Message);Assert.Contains("CALLER WORKSTN INDDS OK",result.Message);
    }

    [Fact]
    public void Workstation_parameter_OPEN_and_CLOSE_use_the_actual_owners_runtime_file()
    {
        Display(Screen);Compile(WorkstationParameterFiles.Replace("extfile(*extdesc);","extfile(*extdesc) usropn;")+"callp WORK(COPY);\nif not %open(COPY);\nsndpgmmsg 'WORKSTN OWNER OPEN OK';\nendif;\nseton LR;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nopen F;\nexfmt F.ENTRY BUFFER;\nclose F;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Enter))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN OWNER OPEN OK",result.Message);
    }

    [Fact]
    public void Workstation_parameter_subfile_operations_update_the_callers_RRN_and_hidden_fields()
    {
        Display(Rows+Dds("INFO",1,usage:'O',row:2,column:2));
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN') sfile(ROWS);\ndcl-f COPY likefile(MODEL) extfile(*extdesc) sfile(ROWS:RRN);\ndcl-s RRN packed(4:0) inz(1);\ncallp WORK(COPY);\nif RRN=1 and %eof(COPY);\nsndpgmmsg 'WORKSTN PARAMETER RRN OK';\nendif;\nseton LR;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROWS:*all) inz;\ndcl-ds CONTROL likerec(F.CTL:*all) inz;\neval BUFFER.VALUE='ONE';\neval BUFFER.SECRET='HIDE';\nwrite F.ROWS BUFFER;\nexfmt F.CTL CONTROL;\nreadc F.ROWS BUFFER;\nif not %eof(F) and BUFFER.VALUE='XONE' and BUFFER.SECRET='HIDE';\nsndpgmmsg 'WORKSTN PARAMETER SUBFILE OK';\nendif;\nreadc F.ROWS BUFFER;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{d.Session.Handle(new(AidKey.None,Character:'X'));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN PARAMETER SUBFILE OK",result.Message);Assert.Contains("WORKSTN PARAMETER RRN OK",result.Message);
    }

    [Fact]
    public void Workstation_parameter_unhandled_errors_run_caller_feedback_and_INFSR()
    {
        Display(Screen);Compile(WorkstationParameterFiles.Replace("extfile(*extdesc);","extfile(*extdesc) usropn infds(INFO) infsr(REPAIR);")+"dcl-ds INFO qualified;\nSTATUS *status;\nOP *opcode;\nREC *record;\nend-ds;\ncallp WORK(COPY);\nsndpgmmsg 'BAD CONTINUATION';\nseton LR;\nbegsr REPAIR;\nif INFO.STATUS=1211 and INFO.OP='EXFMTR' and INFO.REC='ENTRY';\nsndpgmmsg 'WORKSTN PARAMETER OWNER ERROR OK';\nendif;\nreturn;\nendsr;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nexfmt F.ENTRY BUFFER;\nend-proc;");
        var result=new CommandService(_system).Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN PARAMETER OWNER ERROR OK",result.Message);Assert.DoesNotContain("BAD CONTINUATION",result.Message);
    }

    [Fact]
    public void Workstation_parameter_NOPASS_and_OMIT_keep_inquiry_positions_without_opening_templates()
    {
        Display(Screen);Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ncallp CHECK();\ncallp CHECK(*omit);\nseton LR;\ndcl-proc CHECK;\ndcl-pi *n;\nF likefile(MODEL) options(*nopass:*omit);\nend-pi;\nif %parmnum(F)=1 and (%parms=0 or %omitted(F));\nsndpgmmsg 'WORKSTN OPTIONAL OK';\nendif;\nend-proc;");
        var result=new CommandService(_system).Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Equal(2,result.Message!.Split("WORKSTN OPTIONAL OK").Length-1);
    }

    [Theory]
    [InlineData("const")] [InlineData("value")] [InlineData("dim(2)")] [InlineData("options(*exact)")]
    public void Workstation_parameter_invalid_definition_options_reject(string option)
    {
        Display(Screen);var result=Create("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-pr SHOW extpgm('OTHER');\nF likefile(MODEL) "+option+";\nend-pr;\nreturn;");Assert.True(result.IsError);Assert.Contains("LIKEFILE",result.Message);
    }

    [Fact]
    public void Workstation_parameter_forwarding_keeps_the_actual_pending_write()
    {
        Display(Screen);Compile(WorkstationParameterFiles+"dcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='SHARED';\nwrite COPY.ENTRY BUFFER;\ncallp FIRST(COPY);\nseton LR;\ndcl-proc FIRST;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ncallp SECOND(F);\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*input) inz;\nread F BUFFER;\nif BUFFER.NAME='SHARED';\nsndpgmmsg 'WORKSTN FORWARD OK';\nendif;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{Assert.Contains("SHARED",d.Session.Buffer.RowText(4));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN FORWARD OK",result.Message);
    }

    [Theory]
    [InlineData("exfmt(e) F.ENTRY BUFFER;")]
    [InlineData("monitor;\nexfmt F.ENTRY BUFFER;\non-error 1211;\nendmon;")]
    public void Workstation_parameter_E_and_MONITOR_precede_the_actual_owners_INFSR(string operation)
    {
        Display(Screen);Compile(WorkstationParameterFiles.Replace("extfile(*extdesc);","extfile(*extdesc) usropn infsr(FAIL);")+"callp WORK(COPY);\nsndpgmmsg 'WORKSTN ERROR HANDLED';\nseton LR;\nbegsr FAIL;\nsndpgmmsg 'BAD INFSR';\nreturn;\nendsr;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\n"+operation+"\nend-proc;");
        var result=new CommandService(_system).Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN ERROR HANDLED",result.Message);Assert.DoesNotContain("BAD INFSR",result.Message);
    }

    [Fact]
    public void Workstation_parameter_forward_returns_keep_exact_ancestry_and_one_root_resolution()
    {
        var calls=0;var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-s PROBE like(GET);\ndcl-ds BUFFER likerec(ENTRY:*all) inz;",
            "return;\ndcl-proc GET;\ndcl-pi *n likerec(F.ENTRY:*all) rtnparm;\nF likefile(MODEL);\nend-pi;\ndcl-ds LOCAL likerec(F.ENTRY:*all) inz;\nreturn LOCAL;\nend-proc;",()=>calls++);
        Assert.Equal(1,calls);Assert.Equal(10,program.FindField("PROBE")!.Length);
    }

    [Theory]
    [InlineData("eval N=%size(F);")] [InlineData("eval F='X';")] [InlineData("callp OTHER(F);")]
    public void Workstation_parameter_names_cannot_supply_data_storage(string operation)
    {
        Assert.Contains("file",Assert.Throws<RpgCompileException>(()=>WorkstationDefinitionProgram("dcl-f MODEL workstn template;",
            "return;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-s N int(10);\n"+operation+"\nend-proc;")).Message,StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MODEL")] [InlineData("UNRELATED")]
    public void Workstation_parameter_actuals_reject_templates_and_unrelated_families(string actual)
    {
        Assert.Contains(actual=="MODEL"?"TEMPLATE":"LIKEFILE",Assert.Throws<RpgCompileException>(()=>WorkstationDefinitionProgram("dcl-f MODEL workstn qualified template;\ndcl-f OTHER workstn qualified template;\ndcl-f UNRELATED likefile(OTHER);\ndcl-pr RUN extpgm('OTHERPGM');\nF likefile(MODEL);\nend-pr;","callp RUN("+actual+");")).Message);
    }

    [Fact]
    public void Workstation_parameter_main_PI_metadata_uses_version_four_and_verifies_flags()
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;");
        var serialized=RpgWorkstationBindings.Serialize(program);var saved=RpgWorkstationBindings.Restore(program.Source!.Text,serialized);Assert.Equal(4,saved.Version);saved.Verify(program);
        var node=JsonNode.Parse(serialized)!;node["version"]=3;Assert.Throws<InvalidDataException>(()=>RpgWorkstationBindings.Restore(program.Source.Text,node.ToJsonString()));
        node=JsonNode.Parse(serialized)!;node["files"]![1]!["request"]!["isParameter"]=false;
        var changed=RpgWorkstationBindings.Restore(program.Source.Text,node.ToJsonString());Assert.Throws<InvalidDataException>(()=>changed.Verify(program));
    }

    [Fact]
    public void Workstation_parameter_borrowed_cells_expose_no_bytes_and_expire_after_calls()
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-f COPY likefile(MODEL) usropn;\ndcl-pr RUN extpgm('TARGET');\nF likefile(MODEL);\nend-pr;","callp RUN(COPY);");
        ProgramArgument? retained=null;new RpgInterpreter(new() { TypedProgramCaller=(_,_,_,borrow)=>{
            retained=Assert.IsType<ProgramArgument>(Assert.Single(borrow!()));Assert.Equal("*FILE",retained.Type);Assert.Equal(0,retained.Length);Assert.False(retained.HasBuffer);Assert.Null(retained.Storage);Assert.Null(retained.Memory);Assert.Null(retained.ToBuffer());Assert.NotNull(retained.Value);return new() { Success=true };
        } }).Run(program);
        Assert.Throws<ObjectDisposedException>(()=>retained!.Value);
    }

    [Fact]
    public void Workstation_parameter_semantic_references_expire_and_reject_reuse()
    {
        var caller=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-f COPY likefile(MODEL) usropn;\ndcl-pr RUN extpgm('TARGET');\nF likefile(MODEL);\nend-pr;","callp RUN(COPY);");
        var callee=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;","if not %open(F);\nsndpgmmsg 'LIVE';\nendif;\nseton LR;");
        object? retained=null;var entered=0;var host=new RpgHost { SendMessage=_=>entered++ };
        new RpgInterpreter(new() { TypedProgramCaller=(_,_,arguments,_)=>{retained=Assert.Single(arguments);new RpgInterpreter(host).Run(callee,[retained]);return new() { Success=true };} }).Run(caller);
        Assert.Equal(1,entered);Assert.Throws<ObjectDisposedException>(()=>new RpgInterpreter(host).Run(callee,[retained]));Assert.Equal(1,entered);
    }

    [Fact]
    public void Workstation_parameter_file_limit_includes_main_PI_bindings()
    {
        var declarations="dcl-f MODEL workstn qualified template;\n"+string.Join('\n',Enumerable.Range(1,15).Select(i=>$"dcl-f COPY{i} likefile(MODEL) template;"))+"\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;";
        Assert.Contains("16 visible WORKSTN",Assert.Throws<RpgCompileException>(()=>WorkstationDefinitionProgram(declarations)).Message);
    }

    [Fact]
    public void Workstation_parameter_main_PI_rebinds_retained_activations_for_each_actual()
    {
        Display(Screen);Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nread F BUFFER;\nsndpgmmsg BUFFER.NAME;\nreturn;","WSRETAIN");
        Compile(WorkstationParameterFiles+"dcl-f SECOND likefile(MODEL) extfile(*extdesc);\ndcl-pr SHOW extpgm('WSRETAIN');\nF likefile(MODEL);\nend-pr;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='FIRST';\nwrite COPY.ENTRY BUFFER;\neval BUFFER.NAME='SECOND';\nwrite SECOND.ENTRY BUFFER;\ncallp SHOW(COPY);\ncallp SHOW(SECOND);\ncallp SHOW(COPY);\nseton LR;");
        var calls=0;using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{Assert.Contains(++calls==2?"SECOND":"FIRST",d.Session.Buffer.RowText(4));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Equal(3,calls);Assert.Contains("FIRST",result.Message);Assert.Contains("SECOND",result.Message);
    }

    [Fact]
    public void Workstation_parameter_global_bindings_survive_local_data_shadows()
    {
        Display(Screen);Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-pi *n end-pi;\ndcl-s F char(4) inz('KEEP');\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nexfmt F.ENTRY BUFFER;\nif F='KEEP' and %size(F)=4 and %open(F);\nsndpgmmsg 'WORKSTN GLOBAL SHADOW OK';\nendif;\nend-proc;","WSHIDE");
        Compile(WorkstationParameterFiles+"dcl-pr SHOW extpgm('WSHIDE');\nF likefile(MODEL);\nend-pr;\ncallp SHOW(COPY);\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Enter))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN GLOBAL SHADOW OK",result.Message);
    }

    [Fact]
    public void Workstation_parameter_entry_rejects_incompatible_layouts_before_callee_effects()
    {
        Display(Screen);Display(Dds()+Dds("ENTRY",record:true)+Dds("NAME",9,usage:'B',row:4,column:2),"OTHER");
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/OTHER');\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\nsndpgmmsg 'BAD CALLEE EFFECT';\nseton LR;","WSBAD");
        Compile(WorkstationParameterFiles+"dcl-pr SHOW extpgm('WSBAD');\nF likefile(MODEL);\nend-pr;\ncallp SHOW(COPY);\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(_=>throw new InvalidOperationException("No transfer expected.")));
        var result=session.Execute("CALL QGPL/APP");Assert.True(result.IsError);Assert.Contains("compatible borrowed file",result.Message);Assert.DoesNotContain("BAD CALLEE EFFECT",result.Message);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void Workstation_parameter_shadowing_resolves_the_formals_device(bool workstation,bool forward)
    {
        var declarations="**free\ndcl-f DMODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f WMODEL workstn template;\n";
        var global=workstation?"DMODEL":"WMODEL";var local=workstation?"WMODEL":"DMODEL";
        var source=declarations+"dcl-f F likefile("+global+") usropn;\ndcl-f COPY likefile("+local+") usropn;\ncallp WORK(COPY);\nreturn;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile("+local+");\nend-pi;\nif not %open(F);\nsndpgmmsg 'MIXED DEVICE OK';\nendif;\nend-proc;";
        RpgDisplayRecord[] records=[new("ENTRY","ENTRY",[new("NAME","NAME",RpgFieldKind.Character,10,0,true,true)],false,false,null)];
        RpgProgram CompileMixed(string text)=>RpgCompiler.Compile("QGPL","MIXED",text,workstationResolver:request=>RpgWorkstationBindings.Derive(request,new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false }),
            diskResolver:request=>{RpgDiskField[] fields=[new("F","F",RpgFieldKind.Character,1,0,37,0,false,1,1)];var record=RpgDiskBindings.RecordName(request,"ROW");return new("QGPL/DATA",RpgDiskLayout.SignatureFor("ROW",record,1,2,fields),"ROW",record,1,2,fields);});
        var program=CompileMixed(source);
        var messages=new List<string>();new RpgInterpreter(new() { SendMessage=messages.Add }).Run(program);Assert.Equal(new[]{"MIXED DEVICE OK"},messages);
        var hidden=workstation?"ROW":"ENTRY";
        var invalid=forward ? source.Replace("dcl-pi *n;\nF likefile", "dcl-pi *n likerec(F."+hidden+":*all) rtnparm;\nF likefile")
            .Replace("if not %open(F);\nsndpgmmsg 'MIXED DEVICE OK';\nendif;", "dcl-ds RESULT likerec("+(workstation?"DMODEL.ROW":"ENTRY")+":*all) inz;\nreturn RESULT;")
            : source.Replace("if not %open(F);", "dcl-ds BAD likerec(F."+hidden+":*all) inz;\nif not %open(F);");
        Assert.Throws<RpgCompileException>(()=>CompileMixed(invalid));
    }

    [Theory]
    [InlineData("")] [InlineData("*omit")]
    public void Workstation_parameter_missing_IO_is_guarded_without_touching_a_handle(string argument)
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;","monitor;\ncallp WORK("+argument+");\non-error 202;\nsndpgmmsg 'MISSING WORKSTN GUARDED';\nendmon;\nreturn;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL) options(*nopass:*omit);\nend-pi;\nopen F;\nend-proc;");
        var messages=new List<string>();new RpgInterpreter(new() { SendMessage=messages.Add,OpenWorkstation=_=>throw new InvalidOperationException() }).Run(program);Assert.Equal(new[]{"MISSING WORKSTN GUARDED"},messages);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Workstation_parameter_saved_errors_return_or_branch_to_the_owners_frame(bool branch)
    {
        Display(Screen);Compile("**free\ndcl-f FORM workstn template extdesc('QGPL/SCREEN');\ndcl-pi *n;\nF likefile(FORM);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nexfmt F.ENTRY BUFFER;\nsndpgmmsg 'BAD CALLEE CONTINUATION';\nseton LR;","WSFAIL");
        var recovery=branch?"open COPY;\ngoto RECOVERED;":"return;";
        Compile(WorkstationParameterFiles.Replace("extfile(*extdesc);","extfile(*extdesc) usropn infds(INFO) infsr(REPAIR);")+"dcl-pr SHOW extpgm('WSFAIL');\nF likefile(MODEL);\nend-pr;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\ndcl-ds INFO qualified;\nSTATUS *status;\nOP *opcode;\nREC *record;\nend-ds;\ncallp SHOW(COPY);\nsndpgmmsg 'BAD CALLER CONTINUATION';\ntag RECOVERED;\n"+(branch?"exfmt COPY.ENTRY BUFFER;\nsndpgmmsg 'WORKSTN OWNER GOTO OK';\n":"")+"seton LR;\nbegsr REPAIR;\nif INFO.STATUS=1211 and INFO.OP='EXFMTR' and INFO.REC='ENTRY';\nsndpgmmsg 'WORKSTN SAVED OWNER ERROR OK';\nendif;\n"+recovery+"\nendsr;");
        var calls=0;using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{calls++;return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN SAVED OWNER ERROR OK",result.Message);Assert.DoesNotContain("BAD",result.Message);Assert.Equal(branch?1:0,calls);if(branch)Assert.Contains("WORKSTN OWNER GOTO OK",result.Message);
    }
}
