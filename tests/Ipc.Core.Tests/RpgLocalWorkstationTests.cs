using System.Text.Json.Nodes;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Local_WORKSTN_files_exchange_native_buffers_without_importing_fields(int ccsid)
    {
        Display(Screen);Compile("**free\ncallp SHOW();\nseton LR;\ndcl-proc SHOW;\ndcl-f LOCAL workstn extdesc('QGPL/SCREEN') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\ndcl-s NAME int(10) inz(7);\neval BUFFER.NAME='LOCAL';\nexfmt ENTRY BUFFER;\nif BUFFER.NAME='DONE' and BUFFER.AMOUNT=2.5 and NAME=7 and %open(LOCAL);\nsndpgmmsg 'LOCAL WORKSTN OK';\nendif;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{
            Assert.Contains("LOCAL",d.Session.Buffer.RowText(4));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "DONE")d.Session.Handle(new(AidKey.None,Character:ch));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.Tab));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "2.5")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("LOCAL WORKSTN OK",result.Message);
    }

    private sealed class LocalWorkstation : IRpgWorkstation
    {
        internal int Disposals;
        internal readonly List<RpgWorkstationOperation> Operations=[];
        internal readonly List<string> Outputs=[];
        private IReadOnlyDictionary<string,object?> _values=new Dictionary<string,object?>();
        public RpgWorkstationResult Transfer(RpgWorkstationOperation operation,string record,IReadOnlyDictionary<string,object?> values,IReadOnlyList<bool> indicators,int? relativeRecord=null,Action<RpgWorkstationResult>? validate=null)
        {
            Assert.Equal(0,Disposals);Operations.Add(operation);
            if(operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Exchange){_values=values;Outputs.Add((string)values["NAME"]!);}
            var result=new RpgWorkstationResult(operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.Exchange?_values:new Dictionary<string,object?>(),new Dictionary<int,bool>());validate?.Invoke(result);return result;
        }
        public void Dispose()=>Disposals++;
    }
    private static RpgProgram LocalWorkstationProgram(string source,Action<RpgWorkstationRequest>? resolve=null,Func<RpgDiskRequest,RpgDiskLayout>? diskResolver=null)
        =>RpgCompiler.Compile("QGPL","LOCALTEST","**free\n"+source,workstationResolver:request=>{
            resolve?.Invoke(request);RpgDisplayRecord[] records=[new("ENTRY","ENTRY",[new("NAME","NAME",RpgFieldKind.Character,10,0,true,true)],false,false,null)];
            return RpgWorkstationBindings.Derive(request,new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records){IndicatorArea=false});
        },diskResolver:diskResolver);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_WORKSTN_automatic_and_STATIC_handles_have_distinct_lifetimes(bool isStatic)
    {
        var program=LocalWorkstationProgram("callp SHOW();\ncallp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn "+(isStatic?"static":"")+";\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='CALL';\nwrite ENTRY BUFFER;\nend-proc;");
        var files=new List<LocalWorkstation>();var rebound=0;var context=new RpgInterpreter(new(){RetainWorkstations=true,OpenWorkstation=_=>{var f=new LocalWorkstation();files.Add(f);return f;},RebindWorkstation=_=>rebound++}).Run(program);
        Assert.Equal(isStatic?1:2,files.Count);Assert.All(files,f=>Assert.Equal(isStatic?0:1,f.Disposals));Assert.Equal(isStatic?1:0,rebound);
        context.CloseWorkstations();Assert.All(files,f=>Assert.Equal(1,f.Disposals));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_WORKSTN_recursive_calls_keep_automatic_sessions_or_share_STATIC_state(bool isStatic)
    {
        var program=LocalWorkstationProgram("callp SHOW(2);\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn "+(isStatic?"static":"")+";\ndcl-pi *n;\nN int(10) value;\nend-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME=%char(N);\nwrite ENTRY BUFFER;\nif N>0;\ncallp SHOW(N-1);\nendif;\nread LOCAL BUFFER;\nsndpgmmsg %trim(BUFFER.NAME);\nend-proc;");
        var files=new List<LocalWorkstation>();var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add,OpenWorkstation=_=>{var f=new LocalWorkstation();files.Add(f);return f;}}).Run(program);
        Assert.Equal(isStatic?1:3,files.Count);Assert.Equal(isStatic?new[]{"0","0","0"}:new[]{"0","1","2"},messages);Assert.All(files,f=>Assert.Equal(1,f.Disposals));
    }

    [Fact]
    public void Local_WORKSTN_automatic_handles_close_when_a_procedure_throws()
    {
        var program=LocalWorkstationProgram("monitor;\ncallp SHOW();\non-error *all;\nendmon;\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn;\ndcl-pi *n end-pi;\ndcl-s N int(10);\neval N=1/0;\nend-proc;");
        var file=new LocalWorkstation();new RpgInterpreter(new(){OpenWorkstation=_=>file}).Run(program);Assert.Equal(1,file.Disposals);
    }

    [Fact]
    public void Local_WORKSTN_STATIC_close_does_not_reopen_on_a_later_call()
    {
        var program=LocalWorkstationProgram("callp SHOW();\ncallp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static;\ndcl-pi *n end-pi;\nif %open(LOCAL);\nclose LOCAL;\nsndpgmmsg 'CLOSED';\nelse;\nsndpgmmsg 'STILL CLOSED';\nendif;\nend-proc;");
        var files=new List<LocalWorkstation>();var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add,OpenWorkstation=_=>{var f=new LocalWorkstation();files.Add(f);return f;}}).Run(program);
        Assert.Single(files);Assert.Equal(new[]{"CLOSED","STILL CLOSED"},messages);Assert.Equal(1,files[0].Disposals);
    }

    [Fact]
    public void Local_WORKSTN_STATIC_handles_survive_LR_and_cycle_restart_until_activation_cleanup()
    {
        var program=LocalWorkstationProgram("callp SHOW();\nseton LR;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static;\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nwrite ENTRY BUFFER;\nend-proc;");
        var files=new List<LocalWorkstation>();var host=new RpgHost{RetainWorkstations=true,OpenWorkstation=_=>{var f=new LocalWorkstation();files.Add(f);return f;}};
        var first=new RpgInterpreter(host).Run(program);Assert.Equal(0,Assert.Single(files).Disposals);var second=first.RestartCycle();new RpgInterpreter(host).Run(program,existingContext:second);Assert.Single(files);Assert.Equal(2,files[0].Operations.Count);
        second.CloseWorkstations();Assert.Equal(1,files[0].Disposals);
    }

    [Theory]
    [InlineData("exfmt ENTRY;")] [InlineData("write ENTRY;")]
    public void Local_WORKSTN_data_IO_requires_a_result_structure(string operation)
        =>Assert.Throws<RpgCompileException>(()=>LocalWorkstationProgram("return;\ndcl-proc SHOW;\ndcl-f LOCAL workstn;\ndcl-pi *n end-pi;\n"+operation+"\nend-proc;"));

    [Fact]
    public void Local_WORKSTN_private_names_reject_outside_the_owning_procedure()
        =>Assert.Throws<RpgCompileException>(()=>LocalWorkstationProgram("open LOCAL;\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn;\ndcl-pi *n end-pi;\nend-proc;"));

    [Fact]
    public void Local_WORKSTN_STATIC_feedback_and_named_status_survive_later_calls()
    {
        var program=LocalWorkstationProgram("callp SHOW();\ncallp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static usropn infds(INFO);\ndcl-pi *n end-pi;\ndcl-s ONCE ind static;\ndcl-ds INFO qualified static;\nSTATUS *status;\nend-ds;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nif ONCE;\nif INFO.STATUS=1211 and %status(LOCAL)=1211;\nsndpgmmsg 'STATIC DISPLAY STATUS OK';\nendif;\nelse;\neval ONCE=*on;\nread(e) LOCAL BUFFER;\nendif;\nend-proc;");
        var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add,OpenWorkstation=_=>throw new InvalidOperationException()}).Run(program);Assert.Equal(new[]{"STATIC DISPLAY STATUS OK"},messages);
    }

    [Fact]
    public void Local_WORKSTN_LIKEFILE_copies_borrow_through_parameters_with_the_actual_local_owner()
    {
        var program=LocalWorkstationProgram("dcl-f MODEL workstn template;\ncallp OWNER();\nreturn;\ndcl-proc OWNER;\ndcl-f LOCAL likefile(MODEL);\ndcl-pi *n end-pi;\ncallp SHOW(LOCAL);\nif not %open(LOCAL);\nsndpgmmsg 'LOCAL DISPLAY OWNER OK';\nendif;\nend-proc;\ndcl-proc SHOW;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\neval BUFFER.NAME='BORROW';\nwrite F.ENTRY BUFFER;\nclose F;\nend-proc;");
        var file=new LocalWorkstation();var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add,OpenWorkstation=_=>file}).Run(program);Assert.Equal(new[]{"LOCAL DISPLAY OWNER OK"},messages);Assert.Equal("BORROW    ",Assert.Single(file.Outputs));Assert.Equal(1,file.Disposals);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_WORKSTN_INFSR_handles_operation_and_implicit_open_errors(bool implicitOpen)
    {
        var declarations="dcl-f LOCAL workstn "+(implicitOpen?"":"usropn ")+"infds(INFO) infsr(REPAIR);\ndcl-pi *n int(10) end-pi;\ndcl-ds INFO qualified;\nSTATUS *status;\nend-ds;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\n";
        var program=LocalWorkstationProgram("dcl-s N int(10);\neval N=SHOW();\nif N=-1;\nsndpgmmsg 'LOCAL DISPLAY ERROR OK';\nendif;\nreturn;\ndcl-proc SHOW;\n"+declarations+(implicitOpen?"":"read LOCAL BUFFER;\n")+"return 99;\nbegsr REPAIR;\nif INFO.STATUS="+(implicitOpen?"1216":"1211")+";\nreturn -1;\nendif;\nreturn -2;\nendsr;\nend-proc;");
        var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add,OpenWorkstation=_=>throw new Ipc.Core.Messages.CpfException("CPF4131","No display available.")}).Run(program);Assert.Equal(new[]{"LOCAL DISPLAY ERROR OK"},messages);
    }

    [Fact]
    public void Local_WORKSTN_forward_return_metadata_resolves_local_descriptions_once()
    {
        var count=0;var program=LocalWorkstationProgram("dcl-s PROBE like(GET);\nreturn;\ndcl-proc GET;\ndcl-f LOCAL workstn;\ndcl-pi *n likerec(ENTRY:*all) rtnparm end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nreturn BUFFER;\nend-proc;",_=>count++);
        Assert.Equal(1,count);Assert.Equal(10,program.FindField("PROBE")!.Length);
    }

    [Fact]
    public void Local_WORKSTN_equal_layouts_in_different_procedures_keep_separate_ancestry_and_handles()
    {
        var program=LocalWorkstationProgram("callp FIRST();\ncallp SECOND();\nreturn;\n"+string.Join('\n',new[]{"FIRST","SECOND"}.Select(name=>"dcl-proc "+name+";\ndcl-f LOCAL workstn;\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='"+name+"';\nwrite ENTRY BUFFER;\nend-proc;")));
        var files=new List<LocalWorkstation>();new RpgInterpreter(new(){OpenWorkstation=_=>{var f=new LocalWorkstation();files.Add(f);return f;}}).Run(program);
        Assert.Equal(2,files.Count);Assert.Equal("FIRST     ",Assert.Single(files[0].Outputs));Assert.Equal("SECOND    ",Assert.Single(files[1].Outputs));
        var saved=RpgWorkstationBindings.Restore(program.Source!.Text,RpgWorkstationBindings.Serialize(program));Assert.Equal(5,saved.Version);saved.Verify(program);Assert.Equal(2,saved.Files.Count);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Local_WORKSTN_saved_programs_restore_scoped_bindings_after_description_removal(int ccsid)
    {
        Display(Screen,"DESC");Display(Screen,"ACTUAL");Compile("**free\ncallp SHOW();\nseton LR;\ndcl-proc SHOW;\ndcl-f LOCAL workstn extdesc('QGPL/DESC') extfile('QGPL/ACTUAL');\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nexfmt ENTRY BUFFER;\nif BUFFER.NAME='SAVED';\nsndpgmmsg 'SAVED LOCAL DISPLAY OK';\nendif;\nend-proc;");
        _sources.RemoveMember("QGPL","SOURCE","APP");_system.Objects.Delete("QGPL","DESC",ObjectType.File);
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{foreach(var ch in "SAVED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("SAVED LOCAL DISPLAY OK",result.Message);
    }

    [Theory]
    [InlineData("static")] [InlineData("static(1)")]
    public void Local_WORKSTN_STATIC_requires_valid_scope_and_matching_INFDS_storage(string option)
    {
        if(option=="static")Assert.Throws<RpgCompileException>(()=>LocalWorkstationProgram("return;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static infds(INFO);\ndcl-pi *n end-pi;\ndcl-ds INFO qualified;\nSTATUS *status;\nend-ds;\nend-proc;"));
        else Assert.Throws<RpgCompileException>(()=>LocalWorkstationProgram("return;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static(1);\ndcl-pi *n end-pi;\nend-proc;"));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void Local_WORKSTN_and_DISK_files_shadow_global_file_or_record_names(bool workstation,bool sameName)
    {
        var format=new Ipc.Db.Definitions.RecordFormat{Name="ENTRY",Fields=[new(){Name="NAME",Type=Ipc.Db.Definitions.FieldType.Alpha,Length=10}]};format.AssignPositions();
        _sources.CreatePhysicalFile("QGPL","DATA",new(){Name="DATA",Attribute=Ipc.Db.Definitions.FileAttribute.Physical,Formats=[format]},"");
        _sources.Insert("QGPL","DATA","DATA","ENTRY",new Dictionary<string,object?>{["NAME"]="DATABASE"});Display(Screen);
        var global=sameName?"SHARED":"GLOBAL";var local=sameName?"SHARED":"LOCAL";
        var globalType=workstation?"disk extdesc('QGPL/DATA')":"workstn extdesc('QGPL/SCREEN')";
        var localType=workstation?"workstn extdesc('QGPL/SCREEN')":"disk extdesc('QGPL/DATA')";
        Compile("**free\ndcl-f "+global+" "+globalType+" usropn;\ncallp SHOW();\nif not %open("+global+");\nsndpgmmsg 'GLOBAL FILE CLOSED';\nendif;\nseton LR;\ndcl-proc SHOW;\ndcl-f "+local+" "+localType+" extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\n"+(workstation?"exfmt ENTRY BUFFER;":"read "+local+" BUFFER;")+"\nif BUFFER.NAME='"+(workstation?"DISPLAY":"DATABASE")+"' and %trim(NAME)='';\nsndpgmmsg 'SCOPED DEVICE OK';\nendif;\nend-proc;");
        var calls=0;using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{calls++;foreach(var ch in "DISPLAY")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("SCOPED DEVICE OK",result.Message);Assert.Contains("GLOBAL FILE CLOSED",result.Message);Assert.Equal(workstation?1:0,calls);
    }

    [Fact]
    public void Local_WORKSTN_copies_do_not_inherit_STATIC_or_USROPN()
    {
        var program=LocalWorkstationProgram("callp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f MODEL workstn static usropn;\ndcl-f COPY likefile(MODEL);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(COPY.ENTRY:*all) inz;\nwrite COPY.ENTRY BUFFER;\nend-proc;");
        var requests=new List<RpgWorkstationRequest>();var file=new LocalWorkstation();new RpgInterpreter(new(){OpenWorkstation=b=>{requests.Add(b.Request);return file;}}).Run(program);
        var request=Assert.Single(requests);Assert.Equal("COPY",request.Name);Assert.False(request.Static);Assert.False(request.UserOpen);Assert.Equal(1,file.Disposals);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_WORKSTN_failed_initialization_releases_already_opened_automatic_or_STATIC_handles(bool isStatic)
    {
        var program=LocalWorkstationProgram("callp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f FIRST workstn rename(ENTRY:ONE);\ndcl-f SECOND workstn "+(isStatic?"static":"")+" rename(ENTRY:TWO);\ndcl-pi *n end-pi;\nend-proc;");
        var file=new LocalWorkstation();Assert.Throws<Ipc.Core.Messages.CpfException>(()=>new RpgInterpreter(new(){RetainWorkstations=true,OpenWorkstation=b=>b.Request.Name=="FIRST"?throw new Ipc.Core.Messages.CpfException("CPF4131","Unavailable"):file}).Run(program));Assert.Equal(1,file.Disposals);
    }

    [Fact]
    public void Local_WORKSTN_subfiles_and_INDDS_keep_the_actual_procedures_associated_variables()
    {
        Display(Rows.Replace("CA03(03) CF06(06)","INDARA CA03(03) CF06(06)")+Dds("INFO",1,usage:'O',row:2,column:2));
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN') sfile(ROWS);\ncallp OWNER();\nseton LR;\ndcl-proc OWNER;\ndcl-f LOCAL likefile(MODEL) extfile(*extdesc) sfile(ROWS:RRN) indds(FLAGS);\ndcl-pi *n end-pi;\ndcl-s RRN packed(4:0) inz(1);\ndcl-ds FLAGS qualified len(99);\nF6 ind pos(6);\nend-ds;\ncallp WORK(LOCAL);\nif RRN=1 and FLAGS.F6 and not *in06;\nsndpgmmsg 'LOCAL DISPLAY ASSOCIATIONS OK';\nendif;\nend-proc;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROWS:*all) inz;\ndcl-ds CONTROL likerec(F.CTL:*all) inz;\neval BUFFER.VALUE='ONE';\neval BUFFER.SECRET='HIDE';\nwrite F.ROWS BUFFER;\nexfmt F.CTL CONTROL;\nreadc F.ROWS BUFFER;\nif BUFFER.VALUE='XONE' and BUFFER.SECRET='HIDE';\nsndpgmmsg 'LOCAL DISPLAY SUBFILE OK';\nendif;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{d.Session.Handle(new(AidKey.None,Character:'X'));return d.Session.Handle(new(AidKey.Pf6));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("LOCAL DISPLAY SUBFILE OK",result.Message);Assert.Contains("LOCAL DISPLAY ASSOCIATIONS OK",result.Message);
    }

    [Theory]
    [InlineData("version")] [InlineData("scope")] [InlineData("static")] [InlineData("target")]
    public void Local_WORKSTN_saved_metadata_rejects_changed_scope_storage_and_runtime_targets(string defect)
    {
        var program=LocalWorkstationProgram("return;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static;\ndcl-pi *n end-pi;\nend-proc;");
        var json=JsonNode.Parse(RpgWorkstationBindings.Serialize(program))!;Assert.Equal(5,json["version"]!.GetValue<int>());
        var request=json["files"]![0]!["request"]!;
        switch(defect){case "version":json["version"]=4;break;case "scope":request["procedure"]="OTHER";break;case "static":request["static"]=false;break;case "target":request["runtimeFile"]="QGPL/OTHER";break;}
        if(defect=="version")Assert.Throws<InvalidDataException>(()=>RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString()));
        else{var saved=RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString());Assert.Throws<RpgCompileException>(()=>{var restored=RpgCompiler.Compile("QGPL","LOCALTEST",program.Source.Text,workstationResolver:saved.Resolve);saved.Verify(restored);});}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_WORKSTN_and_DISK_copies_keep_the_scanned_global_parent_before_a_later_local_shadow(bool workstation)
    {
        var kind=workstation?"workstn":"disk block(*no)";
        var source="dcl-f PARENT "+kind+" qualified template;\ncallp SHOW();\nreturn;\ndcl-proc SHOW;\ndcl-f FIRST likefile(PARENT) template;\ndcl-f PARENT "+kind+" qualified template;\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(FIRST.ENTRY:*all) inz;\nsndpgmmsg %char(%size(BUFFER));\nend-proc;";
        var program=LocalWorkstationProgram(source,diskResolver:request=>{RpgDiskField[] fields=[new("NAME","NAME",RpgFieldKind.Character,10,0,37,0,false,1,10)];var record=RpgDiskBindings.RecordName(request,"ENTRY");return new("QGPL/DATA",RpgDiskLayout.SignatureFor("ENTRY",record,10,2,fields),"ENTRY",record,10,2,fields);});
        var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add}).Run(program);Assert.Equal(new[]{"10"},messages);
        if(workstation)RpgWorkstationBindings.Restore(program.Source!.Text,RpgWorkstationBindings.Serialize(program)).Verify(program);
        else RpgDiskBindings.Restore(program.Source!.Text,RpgDiskBindings.Serialize(program)).Verify(program);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Local_WORKSTN_native_fixed_definitions_support_both_column_origins(int shift)
    {
        Display(Screen);
        string P(bool begin){var c=Enumerable.Repeat(' ',80+shift).ToArray();c[5+shift]='P';"SHOW".CopyTo(0,c,6+shift,4);c[23+shift]=begin?'B':'E';return new string(c)+"\n";}
        var f=Enumerable.Repeat(' ',43+shift).ToArray();f[5+shift]='F';"LOCAL".CopyTo(0,f,6+shift,5);f[16+shift]='C';f[17+shift]='F';f[21+shift]='E';"WORKSTN".CopyTo(0,f,35+shift,7);
        Compile(RpgFixedStructureTests.D("SHOW","PR",shift:shift)+RpgFixedCalculationTests.C("CALLP",expression:"SHOW()")+RpgFixedCalculationTests.C("SETON",eq:"LR")+P(true)+new string(f)+"STATIC EXTDESC('QGPL/SCREEN') EXTFILE(*EXTDESC)\n"+
            RpgFixedStructureTests.D("SHOW","PI",shift:shift)+RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(ENTRY:*ALL) INZ",shift:shift)+RpgFixedCalculationTests.C("EXFMT",factor2:"ENTRY",result:"BUFFER")+RpgFixedCalculationTests.C("DSPLY",factor1:"BUFFER.NAME")+RpgFixedCalculationTests.C("RETURN")+P(false));
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{foreach(var ch in "FIXED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("FIXED",result.Message);
    }

    [Theory]
    [InlineData("*NEW")] [InlineData("reclaim")] [InlineData("job")]
    public void Local_WORKSTN_STATIC_object_locks_release_at_activation_or_job_end(string mode)
    {
        Display(Screen);_sources.SaveSourceMember("QGPL","SOURCE","APP","**free\ncallp SHOW();\nseton LR;\ndcl-proc SHOW;\ndcl-f LOCAL workstn static extdesc('QGPL/SCREEN') extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nwrite ENTRY BUFFER;\nend-proc;",null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE) ACTGRP("+(mode=="*NEW"?"*NEW":"WSSTATIC")+")");Assert.False(created.IsError,created.Message);
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(_=>throw new InvalidOperationException()));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);var locks=new Ipc.Services.Work.JobLockStore(_system.Connections);var resource=new Ipc.Services.Work.JobLockResource("QGPL","SCREEN",ObjectType.File);
        Assert.Equal(mode!="*NEW",locks.Inspect(resource).Count>0);
        if(mode=="reclaim"){result=session.Execute("RCLACTGRP ACTGRP(WSSTATIC)");Assert.False(result.IsError,result.Message);}else if(mode=="job")session.Dispose();
        Assert.Empty(locks.Inspect(resource));
    }

    [Fact]
    public void Local_WORKSTN_borrowed_errors_unwind_to_the_local_owners_INFSR()
    {
        var program=LocalWorkstationProgram("dcl-f MODEL workstn template;\ndcl-s N int(10);\neval N=OWNER();\nif N=-1;\nsndpgmmsg 'LOCAL BORROWED ERROR OK';\nendif;\nreturn;\ndcl-proc OWNER;\ndcl-f LOCAL likefile(MODEL) usropn infds(INFO) infsr(REPAIR);\ndcl-pi *n int(10) end-pi;\ndcl-ds INFO qualified;\nSTATUS *status;\nend-ds;\ncallp WORK(LOCAL);\nreturn 99;\nbegsr REPAIR;\nif INFO.STATUS=1211;\nreturn -1;\nendif;\nreturn -2;\nendsr;\nend-proc;\ndcl-proc WORK;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ENTRY:*all) inz;\nexfmt F.ENTRY BUFFER;\nend-proc;");
        var messages=new List<string>();new RpgInterpreter(new(){SendMessage=messages.Add}).Run(program);Assert.Equal(new[]{"LOCAL BORROWED ERROR OK"},messages);
    }
}
