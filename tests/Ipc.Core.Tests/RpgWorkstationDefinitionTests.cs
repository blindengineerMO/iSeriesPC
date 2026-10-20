using System.Text.Json;
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
    public void Workstation_definition_copies_keep_exact_buffers_after_source_and_description_removal(int ccsid)
    {
        Display(Screen,"DESC");Display(Screen,"ACTUAL");
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/DESC') rename(ENTRY:REC) prefix(P_);\ndcl-f COPY likefile(MODEL) extfile('QGPL/ACTUAL');\ndcl-ds BUFFER likerec(REC:*all) inz;\ndcl-pr SHOW;\nP likerec(COPY.REC:*all) options(*exact);\nend-pr;\neval BUFFER.P_NAME='COPY';\ncallp SHOW(BUFFER);\nseton LR;\ndcl-proc SHOW;\ndcl-pi *n;\nP likerec(COPY.REC:*all) options(*exact);\nend-pi;\nexfmt COPY.REC P;\nif P.P_NAME='DONE' and P.P_AMOUNT=2.5;\nsndpgmmsg 'WORKSTN COPY OK';\nendif;\nend-proc;");
        _sources.RemoveMember("QGPL","SOURCE","APP");_system.Objects.Delete("QGPL","DESC",ObjectType.File);
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),_timeout.Token,new Terminal(d=>{
            Assert.Contains("COPY",d.Session.Buffer.RowText(4));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "DONE")d.Session.Handle(new(AidKey.None,Character:ch));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.Tab));d.Session.Handle(new(AidKey.None,Edit:CursorEdit.FieldEraseToEnd));foreach(var ch in "2.5")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Pf6));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN COPY OK",result.Message);
    }

    [Fact]
    public void Workstation_definition_template_only_programs_need_no_terminal_or_live_description()
    {
        Display(Screen);Compile("**free\ndcl-f VERY_LONG_MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='TEMPLATE';\nsndpgmmsg BUFFER.NAME;\nseton LR;");
        _system.Objects.Delete("QGPL","SCREEN",ObjectType.File);
        var result=new CommandService(_system).Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("TEMPLATE",result.Message);
    }

    [Theory]
    [InlineData("exfmt ENTRY BUFFER;")] [InlineData("open MODEL;")] [InlineData("close MODEL;")]
    [InlineData("eval N=%open(MODEL);")] [InlineData("callp OTHER(MODEL);")]
    public void Workstation_definition_templates_reject_runtime_uses(string operation)
    {
        Display(Screen);var result=Create("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\ndcl-s N ind;\n"+operation);Assert.True(result.IsError);Assert.Contains("TEMPLATE",result.Message);
    }

    [Theory]
    [InlineData("usropn")] [InlineData("indds(FLAGS)")] [InlineData("infds(FEEDBACK)")] [InlineData("infsr(FAIL)")]
    public void Workstation_definition_templates_reject_noninherited_runtime_settings(string option)
    {
        Display(Screen);var result=Create("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN') "+option+";\nreturn;");Assert.True(result.IsError);Assert.Contains("TEMPLATE",result.Message);
    }

    [Theory]
    [InlineData("qualified")] [InlineData("workstn")] [InlineData("usage(*output)")] [InlineData("prefix(X_)")] [InlineData("extdesc('QGPL/SCREEN')")] [InlineData("rename(ENTRY:RENAMED)")]
    public void Workstation_definition_copies_reject_inherited_attribute_overrides(string option)
    {
        Display(Screen);var result=Create("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) "+option+";\nreturn;");Assert.True(result.IsError);Assert.Contains("LIKEFILE",result.Message);
    }

    [Fact]
    public void Workstation_definition_qualified_files_do_not_import_fields_or_allow_scalar_IO()
    {
        Display(Screen);var source="**free\ndcl-f SCREEN workstn qualified extdesc('QGPL/SCREEN') extfile(*extdesc);\ndcl-ds BUFFER likerec(SCREEN.ENTRY:*all) inz;\n";
        var result=Create(source+"exfmt SCREEN.ENTRY;");Assert.True(result.IsError);Assert.Contains("result data structures",result.Message);
        Compile(source+"exfmt SCREEN.ENTRY BUFFER;\nsndpgmmsg BUFFER.NAME;\nseton LR;","GOOD");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{foreach(var ch in "QUALIFIED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        result=session.Execute("CALL QGPL/GOOD");Assert.False(result.IsError,result.Message);Assert.Contains("QUALIFIED",result.Message);
    }

    [Fact]
    public void Workstation_definition_subfile_copies_bind_their_own_RRN_and_control_names()
    {
        Display(Rows+Dds("INFO",1,usage:'O',row:2,column:2));
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN') sfile(ROWS);\ndcl-f COPY likefile(MODEL) extfile(*extdesc) sfile(ROWS:RRN);\ndcl-s RRN packed(4:0) inz(1);\ndcl-ds BUFFER likerec(ROWS:*all) inz;\ndcl-ds CONTROL likerec(COPY.CTL:*all) inz;\neval BUFFER.VALUE='ONE';\neval BUFFER.SECRET='HIDE';\nwrite COPY.ROWS BUFFER;\nexfmt COPY.CTL CONTROL;\nreadc COPY.ROWS BUFFER;\nif RRN=1 and BUFFER.VALUE='XONE' and BUFFER.SECRET='HIDE';\nsndpgmmsg 'WORKSTN COPY SUBFILE OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{d.Session.Handle(new(AidKey.None,Character:'X'));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN COPY SUBFILE OK",result.Message);
    }

    [Theory]
    [InlineData("sfile(ROWS:RRN)","sfile(ROWS:RRN)")]
    [InlineData("sfile(ROWS)","")]
    [InlineData("sfile(ROWS)","sfile(COPY.ROWS:RRN)")]
    public void Workstation_definition_subfiles_reject_template_RRNs_or_missing_copy_bindings(string template,string copy)
    {
        Display(Rows);var result=Create("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN') "+template+";\ndcl-f COPY likefile(MODEL) extfile(*extdesc) "+copy+";\ndcl-s RRN packed(4:0);\nreturn;");Assert.True(result.IsError);
    }

    [Fact]
    public void Workstation_definition_copies_keep_independent_pending_writes_and_open_states()
    {
        Display(Screen);
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f FIRST likefile(MODEL) extfile(*extdesc);\ndcl-f SECOND likefile(FIRST) usropn;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\neval BUFFER.NAME='FIRST';\nwrite FIRST.ENTRY BUFFER;\nopen SECOND;\neval BUFFER.NAME='SECOND';\nwrite SECOND.ENTRY BUFFER;\nread FIRST BUFFER;\nclose FIRST;\nif not %open(FIRST) and %open(SECOND);\nread SECOND BUFFER;\nsndpgmmsg 'WORKSTN HANDLES OK';\nendif;\nseton LR;");
        var calls=0;using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{Assert.Contains(++calls==1?"FIRST":"SECOND",d.Session.Buffer.RowText(4));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN HANDLES OK",result.Message);Assert.Equal(2,calls);
    }

    [Fact]
    public void Workstation_definition_copies_do_not_inherit_USROPN_or_INDDS()
    {
        Display(Dds(keywords:"INDARA CF06(06)")+Dds("ENTRY",record:true)+Dds("NAME",10,usage:'B',row:4,column:2));
        Compile("**free\ndcl-f MODEL workstn usropn extdesc('QGPL/SCREEN') extfile(*extdesc) indds(FLAGS);\ndcl-f COPY likefile(MODEL);\ndcl-ds FLAGS len(99) qualified;\nF6 ind pos(6);\nend-ds;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nexfmt COPY.ENTRY BUFFER;\nif not %open(MODEL) and %open(COPY) and *in06 and not FLAGS.F6;\nsndpgmmsg 'WORKSTN INHERITANCE OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Pf6))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN INHERITANCE OK",result.Message);
    }

    [Fact]
    public void Workstation_definition_copies_can_bind_their_own_INDDS()
    {
        Display(Dds(keywords:"INDARA CF06(06)")+Dds("ENTRY",record:true)+Dds("NAME",10,usage:'B',row:4,column:2));
        Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) extfile(*extdesc) indds(FLAGS);\ndcl-ds FLAGS len(99) qualified;\nF6 ind pos(6);\nend-ds;\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\nexfmt COPY.ENTRY BUFFER;\nif FLAGS.F6 and not *in06;\nsndpgmmsg 'WORKSTN COPY INDDS OK';\nendif;\nseton LR;");
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>d.Session.Handle(new(AidKey.Pf6))));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN COPY INDDS OK",result.Message);
    }

    [Fact]
    public void Workstation_definition_copies_bind_their_own_feedback_and_error_routine()
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;\ndcl-f COPY likefile(MODEL) usropn infds(INFO) infsr(FAIL);\ndcl-ds BUFFER likerec(ENTRY:*all) inz;\ndcl-ds INFO qualified;\nSTATUS *status;\nOP *opcode;\nREC *record;\nend-ds;",
            "write COPY.ENTRY BUFFER;\nsndpgmmsg 'BAD CONTINUATION';\nreturn;\nbegsr FAIL;\nsndpgmmsg %char(INFO.STATUS)+':'+%trim(INFO.OP)+':'+%trim(INFO.REC);\nreturn;\nendsr;");
        var messages=new List<string>();new RpgInterpreter(new() { SendMessage=messages.Add,OpenWorkstation=_=>throw new InvalidOperationException("No implicit open expected.") }).Run(program);
        Assert.Equal(new[]{"1211:WRITER:ENTRY"},messages);
    }

    private static RpgProgram WorkstationDefinitionProgram(string declarations,string body="return;",Action? resolved=null)
        => RpgCompiler.Compile("QGPL","DEFINITIONS","**free\n"+declarations+"\n"+body,workstationResolver:request=>{
            resolved?.Invoke();RpgDisplayRecord[] records=[new("ENTRY","ENTRY",[new("NAME","NAME",RpgFieldKind.Character,10,0,true,true)],false,false,null)];
            return RpgWorkstationBindings.Derive(request,new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false });
        });

    [Fact]
    public void Workstation_definition_transitive_templates_resolve_one_root_and_keep_forward_return_identity()
    {
        var calls=0;var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f MIDDLE likefile(MODEL) template;\ndcl-f COPY likefile(MIDDLE) extfile('QGPL/SCREEN');\ndcl-s PROBE like(GET);\ndcl-ds BUFFER likerec(ENTRY:*all) inz;",
            "eval BUFFER=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n likerec(COPY.ENTRY:*all) rtnparm end-pi;\ndcl-ds LOCAL likerec(MIDDLE.ENTRY:*all) inz;\neval LOCAL.NAME='RETURN';\nreturn LOCAL;\nend-proc;",()=>calls++);
        Assert.Equal(1,calls);Assert.Equal(10,program.FindField("PROBE")!.Length);Assert.DoesNotContain(program.Fields,f=>f.Name=="NAME");Assert.Equal(new[]{"COPY"},program.FileSpecs);
        var context=new RpgInterpreter(new() { OpenWorkstation=_=>new RecordWorkstation((_,_)=>throw new InvalidOperationException()) }).Run(program);Assert.Equal("RETURN    ",RpgExpressionParser.Parse("BUFFER.NAME").Eval(context));
    }

    [Theory]
    [InlineData("default")] [InlineData("constant")] [InlineData("override")]
    public void Workstation_definition_runtime_targets_follow_constant_inheritance_rules(string mode)
    {
        var root=mode=="default"?"":" extfile('QGPL/ACTUAL')";var child=mode=="override"?" extfile('QGPL/OTHER')":"";
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template extdesc('QGPL/SCREEN')"+root+";\ndcl-f COPY likefile(MODEL)"+child+";");
        Assert.Equal(mode=="default"?"COPY":mode=="constant"?"QGPL/ACTUAL":"QGPL/OTHER",program.Workstations[1].Request.RuntimeFile);
    }

    [Theory]
    [InlineData("version")] [InlineData("family")] [InlineData("parent")] [InlineData("qualified")] [InlineData("shape")]
    public void Workstation_definition_saved_metadata_rejects_corrupt_family_graphs(string defect)
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) extfile(*extdesc);");
        var json=JsonNode.Parse(RpgWorkstationBindings.Serialize(program))!;var child=json["files"]![1]!;
        Assert.Equal(3,json["version"]!.GetValue<int>());
        switch(defect)
        {
            case "version":json["version"]=2;break;
            case "family":child["family"]="OTHER";break;
            case "parent":child["request"]!["likeFile"]="COPY";break;
            case "qualified":child["request"]!["qualified"]=false;break;
            case "shape":
                var layout=program.Workstations[1].Layout;var records=layout.Records.Select(r=>r with { Fields=r.Fields.Select(f=>f with { Length=9 }).ToArray() }).ToArray();
                child["layout"]!["records"]![0]!["fields"]![0]!["length"]=9;child["layout"]!["signature"]=RpgWorkstationLayout.SignatureFor(records,false);break;
        }
        Assert.Throws<InvalidDataException>(()=>RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString()));
    }

    [Fact]
    public void Workstation_definition_restore_verifies_derived_requests_that_do_not_call_the_resolver()
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) extfile(*extdesc);");
        var json=JsonNode.Parse(RpgWorkstationBindings.Serialize(program))!;json["files"]![1]!["request"]!["runtimeFile"]="QGPL/OTHER";
        var saved=RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString());
        var restored=RpgCompiler.Compile("QGPL","DEFINITIONS",program.Source.Text,workstationResolver:saved.Resolve);Assert.Throws<InvalidDataException>(()=>saved.Verify(restored));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Workstation_definition_native_fixed_templates_and_copies_use_result_buffers(int shift)
    {
        Display(Screen);
        var chars=Enumerable.Repeat(' ',43+shift).ToArray();chars[5+shift]='F';"MODEL".CopyTo(0,chars,6+shift,5);chars[16+shift]='C';chars[17+shift]='F';chars[21+shift]='E';"WORKSTN".CopyTo(0,chars,35+shift,7);
        var copy=Enumerable.Repeat(' ',43+shift).ToArray();copy[5+shift]='F';"COPY".CopyTo(0,copy,6+shift,4);
        Compile(new string(chars)+"TEMPLATE EXTDESC('QGPL/SCREEN')\n"+new string(copy)+"LIKEFILE(MODEL) EXTFILE(*EXTDESC)\n"+
            RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(ENTRY:*ALL) INZ",shift:shift)+RpgFixedCalculationTests.C("EXFMT",factor2:"COPY.ENTRY",result:"BUFFER")+RpgFixedCalculationTests.C("DSPLY",factor1:"BUFFER.NAME")+RpgFixedCalculationTests.C("SETON",eq:"LR"));
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,new Terminal(d=>{foreach(var ch in "FIXED")d.Session.Handle(new(AidKey.None,Character:ch));return d.Session.Handle(new(AidKey.Enter));}));
        var result=session.Execute("CALL QGPL/APP");Assert.False(result.IsError,result.Message);Assert.Contains("FIXED",result.Message);
    }

    [Fact]
    public void Workstation_definition_unrelated_qualified_families_cannot_supply_exact_buffers()
    {
        Assert.Contains("*EXACT",Assert.Throws<RpgCompileException>(()=>WorkstationDefinitionProgram(
            "dcl-f FIRST workstn qualified template extdesc('QGPL/SCREEN');\ndcl-f SECOND workstn qualified template extdesc('QGPL/SCREEN');\ndcl-pr RUN extpgm('OTHER');\nP likerec(FIRST.ENTRY:*all) options(*exact);\nend-pr;\ndcl-ds BUFFER likerec(SECOND.ENTRY:*all) inz;","callp RUN(BUFFER);")).Message);
    }

    [Fact]
    public void Workstation_definition_changed_saved_copy_targets_reject_before_terminal_open()
    {
        Display(Screen);Compile("**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) extfile(*extdesc);\nsndpgmmsg 'SHOULD NOT EXECUTE';\nseton LR;");
        var descriptor=_system.Objects.GetRequired("QGPL","APP",ObjectType.Program);var json=JsonNode.Parse(descriptor.ExtendedAttributes![RpgWorkstationBindings.AttributeName])!;
        json["files"]![1]!["request"]!["runtimeFile"]="QGPL/OTHER";descriptor.ExtendedAttributes=new Dictionary<string,string>(descriptor.ExtendedAttributes) { [RpgWorkstationBindings.AttributeName]=json.ToJsonString() };_system.Objects.Update(descriptor);
        var result=new CommandService(_system).Execute("CALL QGPL/APP");Assert.True(result.IsError);Assert.Contains("bindings",result.Message);Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void Workstation_definition_ordinary_bindings_retain_legacy_versions(int version)
    {
        var program=WorkstationDefinitionProgram("dcl-f SCREEN workstn;");var json=JsonNode.Parse(RpgWorkstationBindings.Serialize(program))!;Assert.Equal(2,json["version"]!.GetValue<int>());
        Assert.Null(json["files"]![0]!["request"]!["isTemplate"]);Assert.Null(json["files"]![0]!["family"]);
        if(version==1)
        {
            json["version"]=1;json["files"]![0]!["layout"]!.AsObject().Remove("indicatorArea");
            json["files"]![0]!["layout"]!["signature"]=RpgWorkstationLayout.SignatureFor(program.Workstations[0].Layout.Records);
        }
        var saved=RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString());
        var restored=RpgCompiler.Compile("QGPL","DEFINITIONS",program.Source.Text,workstationResolver:saved.Resolve);saved.Verify(restored);
    }

    [Fact]
    public void Workstation_definition_version_three_preserves_legacy_host_indicator_metadata()
    {
        RpgDisplayRecord[] records=[new("ENTRY","ENTRY",[new("NAME","NAME",RpgFieldKind.Character,10,0,true,true)],false,false,null)];
        var program=RpgCompiler.Compile("QGPL","LEGACY","**free\ndcl-f MODEL workstn template;\ndcl-f COPY likefile(MODEL);\nreturn;",
            workstationResolver:_=>new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records),records));
        var saved=RpgWorkstationBindings.Restore(program.Source!.Text,RpgWorkstationBindings.Serialize(program));Assert.Equal(3,saved.Version);Assert.All(saved.Files,f=>Assert.Null(f.Layout.IndicatorArea));
        var restored=RpgCompiler.Compile("QGPL","LEGACY",program.Source.Text,workstationResolver:saved.Resolve);saved.Verify(restored);
    }

    [Theory]
    [InlineData("field")] [InlineData("record")]
    public void Workstation_definition_saved_root_names_must_match_prefix_and_rename_requests(string defect)
    {
        var program=WorkstationDefinitionProgram("dcl-f MODEL workstn template;");var json=JsonNode.Parse(RpgWorkstationBindings.Serialize(program))!;
        var layout=json["files"]![0]!["layout"]!;var record=program.Workstations[0].Layout.Records[0];
        if(defect=="field") { layout["records"]![0]!["fields"]![0]!["name"]="OTHER";record=record with { Fields=record.Fields.Select(f=>f with { Name="OTHER" }).ToArray() }; }
        else { layout["records"]![0]!["name"]="OTHER";record=record with { Name="OTHER" }; }
        layout["signature"]=RpgWorkstationLayout.SignatureFor([record],false);
        Assert.Throws<InvalidDataException>(()=>RpgWorkstationBindings.Restore(program.Source!.Text,json.ToJsonString()));
    }

    [Fact]
    public void Workstation_definition_metadata_serialization_bounds_large_copy_families()
    {
        var source="**free\ndcl-f MODEL workstn template;\n"+string.Join('\n',Enumerable.Range(1,15).Select(i=>$"dcl-f COPY{i} likefile(MODEL) template;"))+"\nreturn;";
        var fields=Enumerable.Range(1,1024).Select(i=>new RpgDisplayField("F"+i,"F"+i,RpgFieldKind.Character,1,0,true,true)).ToArray();
        RpgDisplayRecord[] records=[new("ENTRY","ENTRY",fields,false,false,null)];
        var program=RpgCompiler.Compile("QGPL","BOUND",source,workstationResolver:_=>new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false });
        Assert.Throws<InvalidDataException>(()=>RpgWorkstationBindings.Serialize(program));
    }

    [Fact]
    public void Workstation_definition_limits_count_each_device_after_LIKEFILE_inheritance()
    {
        var source="**free\n"+string.Join('\n',Enumerable.Range(1,9).Select(i=>$"dcl-f D{i} qualified template extdesc('QGPL/DATA');"))+"\ndcl-f MODEL workstn template;\n"+
            string.Join('\n',Enumerable.Range(1,8).Select(i=>$"dcl-f COPY{i} likefile(MODEL) template;"))+"\nreturn;";
        RpgDisplayRecord[] records=[new("ENTRY","ENTRY",[new("NAME","NAME",RpgFieldKind.Character,10,0,true,true)],false,false,null)];
        var program=RpgCompiler.Compile("QGPL","MIXED",source,workstationResolver:_=>new("QGPL/SCREEN",RpgWorkstationLayout.SignatureFor(records,false),records) { IndicatorArea=false },
            diskResolver:request=>{
                RpgDiskField[] fields=[new("F","F",RpgFieldKind.Character,1,0,37,0,false,1,1)];var record=RpgDiskBindings.RecordName(request,"ROW");
                return new("QGPL/DATA",RpgDiskLayout.SignatureFor("ROW",record,1,2,fields),"ROW",record,1,2,fields);
            });
        Assert.Equal(9,program.Workstations.Count);Assert.Equal(9,program.DiskFiles.Count);
    }
}
