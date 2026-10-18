using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void File_templates_and_LIKEFILE_copies_use_independent_cursors_and_qualified_record_buffers(int ccsid)
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f FIRST likefile(MODEL) extfile('QGPL/DATA');\ndcl-f SECOND likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds A likerec(ROW);\ndcl-ds B likerec(SECOND.ROW);\nread FIRST A;\nread FIRST.ROW A;\nread SECOND B;\nif A.ID=102 and B.ID=101;\nsndpgmmsg 'FILE COPIES OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(ccsid); Ok(session.Execute("CALL QGPL/APP"),"FILE COPIES OK");
    }

    [Fact]
    public void Template_only_programs_restore_descriptors_without_opening_the_deleted_description_file()
    {
        Compile("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\ndcl-ds BUFFER likerec(ROW) inz;\nif %size(BUFFER)=25 and BUFFER.ID=0;\nsndpgmmsg 'FILE TEMPLATE OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP"); _files.DeleteFile("QGPL","DATA");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"FILE TEMPLATE OK");
    }

    [Fact]
    public void Qualified_and_template_file_definitions_do_not_import_scalar_fields()
    {
        var program=Parse("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(MODEL.ROW);\nseton LR;");
        Assert.Null(program.FindField("ID"));Assert.Null(program.FindField("KEY"));Assert.Null(program.FindField("TEXT"));
        Assert.Equal("MODEL.ROW",program.DiskFiles[0].Layout.Record); Assert.Equal("COPY.ROW",program.DiskFiles[1].Layout.Record);
        Assert.True(program.DiskFiles[0].Request.IsTemplate); Assert.False(program.DiskFiles[1].Request.IsTemplate);
    }

    [Theory]
    [InlineData("usage(*output)")] [InlineData("keyed")] [InlineData("disk")] [InlineData("qualified")]
    [InlineData("prefix(P_)")] [InlineData("rename(ROW:ALT)")] [InlineData("extdesc('QGPL/DATA')")] [InlineData("block(*no)")]
    public void LIKEFILE_rejects_overrides_of_inherited_properties(string option)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) "+option+";"));

    [Theory]
    [InlineData("usropn")] [InlineData("infds(BUFFER)")] [InlineData("infsr(FAIL)")]
    public void File_templates_reject_noninherited_runtime_settings(string option)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA') "+option+";"));

    [Fact]
    public void LIKEFILE_requires_an_earlier_parent_and_explicit_unblocked_input()
    {
        Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f COPY likefile(MODEL);\ndcl-f MODEL template extdesc('QGPL/DATA');"));
        Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL);"));
    }

    [Fact]
    public void Qualified_DISK_files_require_result_structures_for_data_operations()
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA qualified;\nread DATA;"));
    [Theory]
    [InlineData("read MODEL;")] [InlineData("open MODEL;")] [InlineData("close MODEL;")]
    [InlineData("dsply %open(MODEL);")] [InlineData("dsply %eof(MODEL);")]
    [InlineData("dsply %size(ROW);")] [InlineData("dsply MODEL;")]
    public void Template_file_names_and_record_names_cannot_supply_runtime_data_or_status(string operation)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\n"+operation));

    [Fact]
    public void LIKEFILE_inherits_constant_EXTFILE_but_not_USROPN()
    {
        Compile("**free\ndcl-f MODEL block(*no) usropn extdesc('QGPL/DATA') extfile('QGPL/DATA');\ndcl-f COPY likefile(MODEL);\ndcl-ds BUFFER likerec(COPY.ROW);\nread COPY BUFFER;\nif not %open(MODEL) and %open(COPY) and BUFFER.ID=101;\nsndpgmmsg 'FILE INHERIT OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"FILE INHERIT OK");
    }

    [Fact]
    public void LIKEFILE_defaults_to_the_new_runtime_file_name_when_EXTFILE_was_not_specified()
    {
        Data("COPY"); _files.Insert("QGPL","COPY","COPY","ROW",new Dictionary<string,object?> { ["ID"]=777L,["KEY"]=1m,["TEXT"]="copy" });
        Compile("**free\ndcl-f MODEL template keyed block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL);\ndcl-ds BUFFER likerec(ROW);\nread COPY BUFFER;\nif BUFFER.ID=777;\nsndpgmmsg 'FILE DEFAULT NAME OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"FILE DEFAULT NAME OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEFILE_inherits_renames_prefixes_usage_and_exact_record_ancestry(int ccsid)
    {
        Compile("**free\ndcl-f MODEL qualified template keyed usage(*update:*delete:*output) extdesc('QGPL/DATA') rename(ROW:ITEM) prefix(P_);\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(MODEL.ITEM:*all);\nchain 20 COPY.ITEM BUFFER;\neval BUFFER.P_TEXT='changed';\nupdate COPY.ITEM BUFFER;\neval BUFFER.P_ID=999;\neval BUFFER.P_KEY=99;\nwrite COPY.ITEM BUFFER;\nchain 99 COPY BUFFER;\nif GET(BUFFER)=999 and BUFFER.P_TEXT='changed';\nsndpgmmsg 'FILE FAMILY OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n int(20);\nP likerec(COPY.ITEM:*all) const options(*exact);\nend-pi;\nreturn P.P_ID;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(ccsid); Ok(session.Execute("CALL QGPL/APP"),"FILE FAMILY OK");
    }

    [Fact]
    public void LIKEFILE_chains_do_not_inherit_TEMPLATE_and_allow_distinct_template_intermediates()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA') extfile('QGPL/DATA');\ndcl-f MIDDLE likefile(MODEL) template;\ndcl-f COPY likefile(MIDDLE);\ndcl-ds BUFFER likerec(MIDDLE.ROW);\nread COPY BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'FILE CHAIN OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"FILE CHAIN OK");
    }

    [Fact]
    public void Long_template_file_symbols_need_only_a_compile_time_description()
    {
        var p=Parse("**free\ndcl-f LONG_TEMPLATE_NAME template extdesc('QGPL/DATA') block(*no);\ndcl-f COPY likefile(LONG_TEMPLATE_NAME) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(COPY.ROW);\nseton LR;");
        Assert.True(p.DiskFiles[0].Request.IsTemplate); Assert.Equal("*EXTDESC",p.DiskFiles[0].Request.RuntimeFile);
    }

    [Fact]
    public void File_template_metadata_uses_version_four_and_rejects_changed_family_links()
    {
        const string source="**free\ndcl-f MODEL template extdesc('QGPL/DATA') block(*no);\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\nseton LR;";
        var program=Parse(source);var json=RpgDiskBindings.Serialize(program);var restored=RpgDiskBindings.Restore(program.Source!.Text,json); Assert.Equal(4,restored.Version);restored.Verify(program);
        var node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["files"]![1]!["family"]="OTHER:BAD";
        Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString()));
        node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["version"]=3;
        Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source.Text,node.ToJsonString()));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Native_fixed_file_templates_and_blank_attribute_LIKEFILE_headers_support_both_origins(int shift)
    {
        string Header(string name,bool parent=false,string keywords="")
        {
            var chars=Enumerable.Repeat(' ',43+shift).ToArray();chars[5+shift]='F';name.CopyTo(0,chars,6+shift,name.Length);
            if(parent){chars[16+shift]='I';chars[17+shift]='F';chars[21+shift]='E';"DISK".CopyTo(0,chars,35+shift,4);}
            return new string(chars)+keywords+"\n";
        }
        var source=Header("MODEL",true)+Header("",keywords:"TEMPLATE BLOCK(*NO)")+Header("",keywords:"EXTDESC('QGPL/DATA')")+
            Header("COPY")+Header("",keywords:"LIKEFILE(MODEL) EXTFILE('QGPL/DATA')")+
            RpgFixedStructureTests.D("BUFFER","DS",keywords:"LIKEREC(ROW)",shift:shift)+
            RpgFixedCalculationTests.C("READ",factor2:"COPY",result:"BUFFER")+RpgFixedCalculationTests.C("DSPLY",factor1:"BUFFER.ID")+RpgFixedCalculationTests.C("SETON",eq:"LR");
        Compile(source);using var session=Session();Ok(session.Execute("CALL QGPL/APP"),"101");
    }

    [Fact]
    public void A_changed_saved_LIKEFILE_runtime_target_is_rejected_before_execution()
    {
        Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-ds BUFFER likerec(ROW);\nread COPY BUFFER;\nsndpgmmsg 'SHOULD NOT EXECUTE';\nseton LR;");
        var descriptor=_system.Objects.GetRequired("QGPL","APP",ObjectType.Program);
        var node=global::System.Text.Json.Nodes.JsonNode.Parse(descriptor.ExtendedAttributes![RpgDiskBindings.AttributeName])!;
        node["files"]![1]!["request"]!["runtimeFile"]="QGPL/OTHER";descriptor.ExtendedAttributes=new Dictionary<string,string>(descriptor.ExtendedAttributes!) { [RpgDiskBindings.AttributeName]=node.ToJsonString() };_system.Objects.Update(descriptor);
        using var session=Session();var result=session.Execute("CALL QGPL/APP");Assert.True(result.IsError);Assert.Contains("bindings",result.Message);Assert.DoesNotContain("SHOULD NOT EXECUTE",result.Message);
    }

    [Fact]
    public void LIKEFILE_family_cycles_and_missing_parents_are_not_valid_saved_metadata()
    {
        var program=Parse("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\nseton LR;");
        var json=RpgDiskBindings.Serialize(program);
        foreach(var parent in new[]{"COPY","MISSING"})
        {
            var node=global::System.Text.Json.Nodes.JsonNode.Parse(json)!;node["files"]![1]!["request"]!["likeFile"]=parent;
            Assert.Throws<InvalidDataException>(()=>RpgDiskBindings.Restore(program.Source!.Text,node.ToJsonString()));
        }
    }

    [Fact]
    public void File_status_builtins_cannot_bypass_template_rules_through_a_data_name_shadow()
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\ndcl-s MODEL char(4);\nif %open(MODEL);\nendif;"));

    [Fact]
    public void Direct_runtime_template_status_reads_fail_without_opening_any_file()
    {
        var program=Parse("**free\ndcl-f MODEL template extdesc('QGPL/DATA');\nseton LR;");
        var context=new RpgRuntimeContext(program,new());
        Assert.Equal(105,Assert.Throws<RpgRuntimeException>(()=>Ipc.Rpg.Parsing.RpgExpressionParser.Parse("%open(MODEL)").Eval(context)).StatusCode);
    }

    [Fact]
    public void Saved_LIKEFILE_descriptors_survive_deleted_source_and_compile_time_files()
    {
        Data("ACTUAL");Compile("**free\ndcl-f MODEL template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/ACTUAL');\ndcl-ds BUFFER likerec(ROW);\nread COPY BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'FILE SNAPSHOT OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP");_files.DeleteFile("QGPL","DATA");using var session=Session(1208);Ok(session.Execute("CALL QGPL/APP"),"FILE SNAPSHOT OK");
    }

}
