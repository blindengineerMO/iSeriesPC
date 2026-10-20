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
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_record_EXFMT_uses_a_LIKEREC_ALL_buffer_and_preserves_imported_scalars(int ccsid)
    {
        Display(Screen);Compile(File()+"dcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='START';\neval BUFFER.AMOUNT=1.25;\nexfmt ENTRY BUFFER;\nif BUFFER.NAME='DONE' and BUFFER.AMOUNT=2.5 and NAME='' and AMOUNT=0 and *in06;\nsndpgmmsg 'WORKSTN RECORD OK';\nendif;\nseton LR;");
        var terminal=new Terminal(display=> { Assert.Contains("START",display.Session.Buffer.RowText(4));display.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "DONE")display.Session.Handle(new(AidKey.None,Character:ch));display.Session.Handle(new(AidKey.None,Edit:CursorEdit.Tab));display.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "2.5")display.Session.Handle(new(AidKey.None,Character:ch));return display.Session.Handle(new(AidKey.Pf6)); });
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,terminal);
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN RECORD OK",result.Message);
    }

    [Theory]
    [InlineData("*INPUT",10)] [InlineData("*OUTPUT",11)] [InlineData("*ALL",14)]
    public void Workstation_record_extracts_select_fields_and_use_zoned_numeric_storage(string extract,int length)
    {
        Display(Dds()+Dds("REC",record:true)+Dds("QUESTION",4,usage:'O',row:2,column:2)+Dds("ANSWER",3,usage:'I',row:3,column:2)+Dds("NUMBER",7,type:'Y',decimals:2,usage:'B',row:4,column:2));
        Compile(File()+"dcl-ds BUFFER likerec(REC:"+extract+") inz;\nif %size(BUFFER)="+length+" and %size(BUFFER.NUMBER)=7;\nsndpgmmsg 'WORKSTN EXTRACT OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Enter))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN EXTRACT OK",result.Message);
    }

    [Fact]
    public void Workstation_record_WRITE_OUTPUT_then_READ_INPUT_keeps_output_only_fields_separate()
    {
        Display(Dds()+Dds("REC",record:true)+Dds("QUESTION",4,usage:'O',row:2,column:2)+Dds("ANSWER",3,usage:'I',row:3,column:2));
        Compile(File()+"dcl-ds OUT likerec(REC:*output) inz;\ndcl-ds IN likerec(REC:*input) inz;\neval OUT.QUESTION='ASK';\nwrite REC OUT;\nread SCREEN IN;\nif IN.ANSWER='YES' and OUT.QUESTION='ASK' and ANSWER='';\nsndpgmmsg 'WORKSTN SPLIT OK';\nendif;\nseton LR;");
        var terminal=new Terminal(d=>{Assert.Contains("ASK",d.Session.Buffer.RowText(2));foreach(var ch in "YES")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));});
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,terminal);var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN SPLIT OK",result.Message);
    }

    [Theory]
    [InlineData("*input","exfmt ENTRY BUFFER;")]
    [InlineData("*output","read ENTRY BUFFER;")]
    [InlineData("*input","write ENTRY BUFFER;")]
    [InlineData("*key","read ENTRY BUFFER;")]
    public void Workstation_record_I_O_rejects_incompatible_extract_modes(string extract,string operation)
    {
        Display(Screen);var result=Create(File()+"dcl-ds BUFFER likerec(ENTRY:"+extract+") inz;\n"+operation);Assert.True(result.IsError);Assert.Contains("WORKSTN",result.Message);
    }

    [Fact]
    public void Workstation_record_CA_returns_preserve_all_buffer_fields_and_publish_indicators()
    {
        Display(Screen);Compile(File()+"dcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='START';\neval BUFFER.AMOUNT=7;\nexfmt ENTRY BUFFER;\nif *in03 and BUFFER.NAME='START' and BUFFER.AMOUNT=7;\nsndpgmmsg 'WORKSTN CA OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{d.Session.Handle(new(AidKey.None,Character:'X'));return d.Session.Handle(new(AidKey.Pf3));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN CA OK",result.Message);
    }

    [Fact]
    public void Workstation_record_indexed_receivers_are_captured_once()
    {
        Display(Screen);Compile(File()+"dcl-ds BUFFER likerec(ENTRY:*all) dim(2) inz;\ndcl-s INDEX int(10) inz(1);\ndcl-s CALLS int(10);\neval BUFFER(1).NAME='ONE';\neval BUFFER(2).NAME='TWO';\nexfmt ENTRY BUFFER(CHOOSE());\nif CALLS=1 and BUFFER(1).NAME='DONE' and BUFFER(2).NAME='TWO';\nsndpgmmsg 'WORKSTN ADDRESS OK';\nendif;\nseton LR;\ndcl-proc CHOOSE;\ndcl-pi *n int(10) end-pi;\neval CALLS=CALLS+1;\neval INDEX=3-INDEX;\nreturn 1;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "DONE")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN ADDRESS OK",result.Message);
    }
    [Fact]
    public void Workstation_record_subfile_buffers_keep_hidden_fields_RRNs_and_EOF_bytes()
    {
        Display(Rows);Compile(File("sfile(ROWS:RRN)")+"dcl-s RRN packed(4:0);\ndcl-ds BUFFER likerec(ROWS:*all) inz;\neval RRN=1;\neval BUFFER.VALUE='ONE';\neval BUFFER.SECRET='HIDE';\nwrite ROWS BUFFER;\nexfmt CTL;\nreadc ROWS BUFFER;\nif RRN=1 and BUFFER.VALUE='XONE' and BUFFER.SECRET='HIDE' and VALUE='' and SECRET='';\nsndpgmmsg 'WORKSTN SUBFILE OK';\nendif;\neval BUFFER.VALUE='UPDATED';\nupdate ROWS BUFFER;\nreadc ROWS BUFFER;\nif %eof(SCREEN) and BUFFER.VALUE='UPDATED';\nchain 1 ROWS BUFFER;\nif %found(SCREEN) and BUFFER.VALUE='UPDATED';\nsndpgmmsg 'WORKSTN SUBFILE STATUS OK';\nendif;\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{Assert.Contains("ONE",d.Session.Buffer.RowText(4));d.Session.Handle(new(AidKey.None,Character:'X'));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN SUBFILE OK",result.Message);Assert.Contains("WORKSTN SUBFILE STATUS OK",result.Message);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Workstation_record_native_fixed_results_support_both_definition_origins(int shift)
    {
        Display(Screen);
        var chars=Enumerable.Repeat(' ',43+shift).ToArray();chars[5+shift]='F';"SCREEN".CopyTo(0,chars,6+shift,6);chars[16+shift]='C';chars[17+shift]='F';chars[21+shift]='E';"WORKSTN".CopyTo(0,chars,35+shift,7);
        var source=new string(chars)+"EXTDESC('QGPL/SCREEN') EXTFILE(*EXTDESC)\n"+
            RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(ENTRY:*ALL) INZ",shift:shift)+
            RpgFixedCalculationTests.C("EXFMT",factor2:"ENTRY",result:"BUFFER")+RpgFixedCalculationTests.C("DSPLY",factor1:"BUFFER.NAME")+RpgFixedCalculationTests.C("SETON",eq:"LR");
        Compile(source);using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{foreach(var ch in "FIXED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("FIXED",result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_record_saved_programs_restore_prefixed_renamed_layouts_after_source_removal(int ccsid)
    {
        Display(Screen);Compile(File("rename(ENTRY:REC) prefix(P_)")+"dcl-ds BUFFER likerec(REC:*all) inz;\nexfmt REC BUFFER;\nif BUFFER.P_NAME='SAVED';\nsndpgmmsg 'WORKSTN SAVED BUFFER OK';\nendif;\nseton LR;");
        _sources.RemoveMember("QGPL","SOURCE","APP");using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{foreach(var ch in "SAVED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN SAVED BUFFER OK",result.Message);
    }

    [Fact]
    public void Workstation_record_value_returns_and_exact_parameters_keep_record_ancestry()
    {
        Display(Screen);Compile(File()+"dcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER=GET();\ncallp SHOW(BUFFER);\nseton LR;\ndcl-proc GET;\ndcl-pi *n likerec(ENTRY:*all) rtnparm end-pi;\ndcl-ds LOCAL likerec(ENTRY:*all) inz;\neval LOCAL.NAME='VALUE';\nreturn LOCAL;\nend-proc;\ndcl-proc SHOW;\ndcl-pi *n;\nP likerec(ENTRY:*all) options(*exact);\nend-pi;\nexfmt ENTRY P;\nif P.NAME='RETURN';\nsndpgmmsg 'WORKSTN INTERFACE OK';\nendif;\nend-proc;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{Assert.Contains("VALUE",d.Session.Buffer.RowText(4));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "RETURN")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN INTERFACE OK",result.Message);
    }

    [Theory]
    [InlineData("const")] [InlineData("dim(2)")]
    public void Workstation_record_input_rejects_readonly_and_whole_array_receivers(string option)
    {
        Display(Screen);
        var source=option=="const" ? File()+"return;\ndcl-proc LOAD;\ndcl-pi *n;\nP likerec(ENTRY:*all) const;\nend-pi;\nexfmt ENTRY P;\nend-proc;" : File()+"dcl-ds BUFFER likerec(ENTRY:*all) "+option+" inz;\nexfmt ENTRY BUFFER;";
        var result=Create(source);Assert.True(result.IsError);Assert.Contains(option=="const"?"CONST":"WORKSTN",result.Message);
    }

    private sealed class RecordWorkstation(Func<RpgWorkstationOperation,IReadOnlyDictionary<string,object?>,RpgWorkstationResult> transfer) : IRpgWorkstation
    {
        internal int Transfers {get;private set;}
        internal bool Acknowledged {get;private set;}
        public RpgWorkstationResult Transfer(RpgWorkstationOperation operation,string record,IReadOnlyDictionary<string,object?> values,IReadOnlyList<bool> indicators,int? relativeRecord=null,Action<RpgWorkstationResult>? validate=null)
        { Transfers++;var result=transfer(operation,values);validate?.Invoke(result);Acknowledged=true;return result; }
        public void Dispose() { }
    }
    private static RpgProgram WorkstationRecordProgram(string source,bool subfile=false)
        => RpgCompiler.Compile("QGPL","BUFFERTEST","**free\ndcl-f SCREEN workstn "+(subfile?"sfile(REC:RRN)":"")+";\n"+(subfile?"dcl-s RRN packed(4:0) inz(7);\n":"")+source,workstationResolver:_=> {
            RpgDisplayRecord[] records=[new("REC","REC",[new("TEXT","TEXT",RpgFieldKind.Character,4,0,true,true),new("NUMBER","NUMBER",RpgFieldKind.Packed,5,2,true,true)],subfile,false,null,subfile?10:0)];
            return new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false };
        });

    [Theory]
    [InlineData("overflow")] [InlineData("missing")]
    public void Workstation_record_conversion_failures_preserve_bytes_before_host_acknowledgment(string failure)
    {
        var program=WorkstationRecordProgram("dcl-ds BUFFER likerec(REC:*all) inz;\neval BUFFER.TEXT='KEEP';\neval BUFFER.NUMBER=17.25;\nmonitor;\nexfmt REC BUFFER;\non-error *all;\nendmon;");
        var file=new RecordWorkstation((_,values)=> {
            Assert.Equal("KEEP",values["TEXT"]);Assert.Equal(17.25m,values["NUMBER"]);
            var result=new Dictionary<string,object?> { ["NUMBER"]=44.50m };if(failure=="overflow")result["TEXT"]="TOOLONG";
            return new(result,new Dictionary<int,bool>());
        });
        var context=new RpgInterpreter(new() { OpenWorkstation=_=>file }).Run(program);
        Assert.Equal("KEEP",RpgExpressionParser.Parse("BUFFER.TEXT").Eval(context));Assert.Equal(17.25m,RpgExpressionParser.Parse("BUFFER.NUMBER").Eval(context));Assert.False(file.Acknowledged);Assert.Equal(1,file.Transfers);
    }

    [Fact]
    public void Workstation_record_direct_runtime_readonly_guards_precede_terminal_transfer()
    {
        var program=WorkstationRecordProgram("dcl-pi *n;\nP likerec(REC:*all) const;\nend-pi;\ndcl-ds BUFFER likerec(REC:*all) inz;\nexfmt REC BUFFER;");
        program.MainStatements.Single().RecordDataStructure="P";
        var bytes=new ProgramBuffer(new byte[9],37);var argument=new ProgramArgument(()=>bytes,_=>throw new InvalidOperationException(),v=>v,"*CHAR",9,0,()=>bytes) { IsReadOnly=true };
        var file=new RecordWorkstation((_,_)=>throw new InvalidOperationException("No transfer expected."));
        Assert.Equal(105,Assert.Throws<RpgRuntimeException>(()=>new RpgInterpreter(new() { OpenWorkstation=_=>file }).Run(program,[argument])).StatusCode);Assert.Equal(0,file.Transfers);
    }

    [Fact]
    public void Workstation_record_failed_subfile_conversion_preserves_RRN_indicators_and_pending_input()
    {
        var program=WorkstationRecordProgram("dcl-ds BUFFER likerec(REC:*input) inz;\neval BUFFER.TEXT='KEEP';\neval BUFFER.NUMBER=17.25;\nmonitor;\nreadc REC BUFFER;\non-error *all;\nendmon;",subfile:true);
        var file=new RecordWorkstation((_,_)=>new(new Dictionary<string,object?> { ["TEXT"]="TOOLONG",["NUMBER"]=44.50m },new Dictionary<int,bool> { [6]=true },RelativeRecord:1));
        var context=new RpgInterpreter(new() { OpenWorkstation=_=>file }).Run(program);
        Assert.Equal("KEEP",RpgExpressionParser.Parse("BUFFER.TEXT").Eval(context));Assert.Equal(7m,context.ReadValue("RRN"));Assert.False(context.Indicators[6]);Assert.False(file.Acknowledged);
    }

    [Fact]
    public void Workstation_record_WRITE_accepts_a_readonly_OUTPUT_parameter_without_changing_it()
    {
        var program=WorkstationRecordProgram("dcl-pi *n;\nP likerec(REC:*output) const;\nend-pi;\nwrite REC P;");
        var image=Convert.FromHexString("D2C5C5D7F0F1F7F2F5");var buffer=new ProgramBuffer(image,37);
        var argument=new ProgramArgument(()=>buffer,_=>throw new InvalidOperationException(),v=>v,"*CHAR",9,0,()=>buffer) { IsReadOnly=true };
        var file=new RecordWorkstation((_,values)=> { Assert.Equal("KEEP",values["TEXT"]);Assert.Equal(17.25m,values["NUMBER"]);return new(new Dictionary<string,object?>(),new Dictionary<int,bool>()); });
        new RpgInterpreter(new() { OpenWorkstation=_=>file }).Run(program,[argument]);Assert.True(file.Acknowledged);Assert.Equal(image,buffer.ToArray());
    }

    [Fact]
    public void Workstation_record_equal_layouts_from_different_formats_cannot_supply_IO_receivers()
    {
        Display(Dds()+Dds("FIRST",record:true)+Dds("NAME",4,usage:'B',row:2,column:2)+Dds("SECOND",record:true)+Dds("NAME",4,usage:'B',row:3,column:2));
        var result=Create(File()+"dcl-ds BUFFER likerec(FIRST:*all) inz;\nexfmt SECOND BUFFER;");Assert.True(result.IsError);Assert.Contains("matching LIKEREC",result.Message);
    }

    [Fact]
    public void Workstation_record_file_name_input_rejects_multiformat_buffers_without_a_nested_layout()
    {
        Display(Dds()+Dds("FIRST",record:true)+Dds("NAME",4,usage:'B',row:2,column:2)+Dds("SECOND",record:true)+Dds("NAME",4,usage:'B',row:3,column:2));
        var result=Create(File()+"dcl-ds BUFFER likerec(FIRST:*input) inz;\nread SCREEN BUFFER;");Assert.True(result.IsError);Assert.Contains("one format",result.Message);
    }

    [Fact]
    public void Workstation_record_buffer_syntax_errors_keep_include_source_locations()
    {
        Display(Screen);_sources.SaveSourceMember("QGPL","SOURCE","BADBUF","dcl-ds BUFFER likerec(ENTRY:*all) inz;\nexfmt ENTRY BUFFER[;",null);
        var result=Create(File()+"/copy QGPL/SOURCE,BADBUF\nseton LR;");Assert.True(result.IsError);Assert.Contains("BADBUF",result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_record_subfile_message_keys_keep_opaque_bytes(int ccsid)
    {
        RpgDisplayRecord[] records=[new("ROWS","ROWS",[new("KEY","KEY",RpgFieldKind.Character,4,0,false,true,true)],true,true,null,10)];
        var program=RpgCompiler.Compile("QGPL","KEYBUF","**free\ndcl-f SCREEN workstn sfile(ROWS:RRN);\ndcl-s RRN packed(4:0);\ndcl-ds BUFFER likerec(ROWS:*all) inz;\nreadc ROWS BUFFER;",workstationResolver:_=>new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false });
        var expected=Convert.FromHexString("80FF01F0");var file=new RecordWorkstation((_,_)=>new(new Dictionary<string,object?> { ["KEY"]=new ProgramBuffer(expected,ccsid) },new Dictionary<int,bool>(),RelativeRecord:3));
        var context=new RpgInterpreter(new() { Ccsid=()=>ccsid,OpenWorkstation=_=>file }).Run(program);
        Assert.Equal(expected,Assert.IsType<ProgramBuffer>(RpgExpressionParser.Parse("BUFFER.KEY").Eval(context)).ToArray());Assert.Equal(3m,context.ReadValue("RRN"));Assert.True(file.Acknowledged);
    }

    [Fact]
    public void Workstation_record_date_and_time_fields_transfer_through_native_buffers()
    {
        Display(Dds()+Dds("REC",record:true)+Dds("DATE",10,type:'L',usage:'B',row:2,column:2)+Dds("TIME",8,type:'T',usage:'B',row:3,column:2));
        Compile(File()+"dcl-ds BUFFER likerec(REC:*all) inz;\neval BUFFER.DATE=%date('2026-10-18');\neval BUFFER.TIME=%time('12:34:56');\nexfmt REC BUFFER;\nif BUFFER.DATE=%date('2026-10-18') and BUFFER.TIME=%time('12:34:56');\nsndpgmmsg 'WORKSTN DATE BUFFER OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Enter))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN DATE BUFFER OK",result.Message);
    }

    [Fact]
    public void Workstation_record_forward_return_metadata_reuses_one_resolver_and_detaches_callbacks()
    {
        var calls=0;RpgDisplayRecord[] records=[new("REC","REC",[new("TEXT","TEXT",RpgFieldKind.Character,4,0,true,true)],false,false,null)];
        var program=RpgCompiler.Compile("QGPL","FORWARD","**free\ndcl-f SCREEN workstn;\ndcl-s PROBE like(GET);\nreturn;\ndcl-proc GET;\ndcl-pi *n likerec(REC:*all) rtnparm end-pi;\ndcl-ds BUFFER likerec(REC:*all) inz;\nreturn BUFFER;\nend-proc;",workstationResolver:_=>{calls++;return new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false };});
        Assert.Equal(1,calls);Assert.Equal(4,program.FindField("PROBE")!.Length);
        foreach(var property in typeof(RpgProgram).GetProperties(global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic).Where(p=>p.Name.EndsWith("Lookup",StringComparison.Ordinal)))
            Assert.Null(property.GetValue(program));
    }

}
