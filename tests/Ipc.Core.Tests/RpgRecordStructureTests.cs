using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEREC_input_transfers_the_record_without_changing_imported_scalar_fields(int ccsid)
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds BUFFER likerec(ROW);\nread DATA BUFFER;\nif BUFFER.ID=101 and BUFFER.TEXT='first' and ID=0 and TEXT=*blanks and %size(BUFFER)=25;\nsndpgmmsg 'RECORD INPUT OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(ccsid); Ok(session.Execute("CALL QGPL/APP"),"RECORD INPUT OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEREC_all_buffers_support_keyed_input_update_and_output(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed usage(*update:*delete:*output);\ndcl-ds BUFFER likerec(ROW:*all);\ndcl-ds KEYS likerec(ROW:*key) inz;\neval KEYS.KEY=10;\nchain %kds(KEYS) DATA BUFFER;\neval BUFFER.TEXT='changed';\nupdate ROW BUFFER;\neval BUFFER.ID=999;\neval BUFFER.KEY=99;\nwrite ROW BUFFER;\nchain 99 DATA BUFFER;\nif BUFFER.ID=999 and BUFFER.TEXT='changed' and KEY=0;\nsndpgmmsg 'RECORD WRITE OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(ccsid); Ok(session.Execute("CALL QGPL/APP"),"RECORD WRITE OK");
    }

    [Fact]
    public void LIKEREC_array_receivers_resolve_indices_once_and_preserve_other_elements()
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds BUFFERS likerec(ROW) dim(2) inz;\ndcl-s COUNT int(3);\neval BUFFERS(1).ID=7;\nread DATA BUFFERS(NEXT());\nif COUNT=1 and BUFFERS(1).ID=7 and BUFFERS(2).ID=101;\nsndpgmmsg 'RECORD INDEX OK';\nendif;\nseton LR;\ndcl-proc NEXT;\ndcl-pi *n int(3) end-pi;\neval COUNT=COUNT+1;\nreturn 2;\nend-proc;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD INDEX OK");
    }

    [Theory]
    [InlineData("*bad")] [InlineData("*null:*null")] [InlineData("*input:*null:*null")]
    public void LIKEREC_rejects_unimplemented_or_unknown_extract_types(string extract)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA;\ndcl-ds BUFFER likerec(ROW:"+extract+");\nseton LR;"));

    [Fact]
    public void LIKEREC_requires_the_internal_renamed_record_and_applies_PREFIX_to_subfields()
    {
        var program=Parse("**free\ndcl-f DATA rename(ROW:ITEM) prefix(P_);\ndcl-ds BUFFER likerec(ITEM) inz;\nseton LR;");
        var ds=program.FindDs("BUFFER")!; Assert.Equal(25,ds.Length); Assert.True(ds.Qualified); Assert.NotNull(ds.Find("P_ID")); Assert.Null(ds.Find("ID"));
        Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA rename(ROW:ITEM);\ndcl-ds BUFFER likerec(ROW);"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_LIKEREC_declarations_and_fixed_result_fields_use_native_record_io(int shift)
    {
        Compile("     FDATA      IF   E             DISK\n"+D("BUFFER","DS",keywords:"LIKEREC(ROW)",shift:shift)+RpgFixedCalculationTests.C("READ",factor2:"DATA",result:"BUFFER")+RpgFixedCalculationTests.C("DSPLY",factor1:"BUFFER.ID")+RpgFixedCalculationTests.C("SETON",eq:"LR"));
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"101");
    }

    [Fact]
    public void LIKEREC_templates_supply_inherited_layouts_and_local_procedure_interfaces()
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds BUFFER likeds(MODEL);\ndcl-ds MODEL likerec(ROW) template;\nread DATA BUFFER;\nif GET(BUFFER)=101;\nsndpgmmsg 'RECORD INTERFACE OK';\nendif;\nseton LR;\ndcl-proc GET;\ndcl-pi *n int(20);\nP likerec(ROW) const options(*exact);\nend-pi;\nreturn P.ID;\nend-proc;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD INTERFACE OK");
    }
    [Theory]
    [InlineData("setll 20 DATA;\nreade 20 DATA BUFFER;",101)]
    [InlineData("setgt 30 DATA;\nreadpe 30 DATA BUFFER;",103)]
    [InlineData("setgt 30 DATA;\nreadp DATA BUFFER;",103)]
    public void LIKEREC_buffers_receive_directional_and_equal_key_reads(string operations,long expected)
    {
        Compile("**free\ndcl-f DATA keyed;\ndcl-ds BUFFER likerec(ROW);\n"+operations+"\nsndpgmmsg %char(BUFFER.ID);\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),expected.ToString());
    }

    [Theory]
    [InlineData("*OUTPUT","read DATA BUFFER;")]
    [InlineData("*INPUT","write ROW BUFFER;")]
    [InlineData("*KEY","read DATA BUFFER;")]
    public void LIKEREC_extract_modes_restrict_result_io(string extract,string operation)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA keyed usage(*update:*output);\ndcl-ds BUFFER likerec(ROW:"+extract+");\n"+operation));

    [Theory]
    [InlineData("dcl-ds BUFFER qualified len(25);\nX char(25);\nend-ds;")]
    [InlineData("dcl-ds BUFFER likerec(ROW) dim(2);")]
    [InlineData("dcl-ds BUFFER likerec(ROW) template;")]
    public void LIKEREC_io_rejects_unrelated_storage_unindexed_arrays_and_templates(string definition)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA;\n"+definition+"\nread DATA BUFFER;"));

    [Fact]
    public void LIKEREC_record_origin_rejects_an_equally_sized_different_record()
    {
        Data("OTHER"); Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA;\ndcl-f OTHER rename(ROW:ALTROW) prefix(O_);\ndcl-ds BUFFER likerec(ALTROW);\nread DATA BUFFER;"));
    }

    [Fact]
    public void LIKEREC_not_found_and_eof_leave_the_receiver_unchanged()
    {
        Compile("**free\ndcl-f DATA keyed;\ndcl-ds BUFFER likerec(ROW) inz;\neval BUFFER.ID=7;\nchain 999 DATA BUFFER;\nif not %found(DATA) and BUFFER.ID=7;\nsetgt 999 DATA;\nread DATA BUFFER;\nif %eof(DATA) and BUFFER.ID=7;\nsndpgmmsg 'RECORD MISS OK';\nendif;\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD MISS OK");
    }

    [Fact]
    public void LIKEREC_invalid_receiver_index_fails_before_advancing_the_cursor()
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds BUFFERS likerec(ROW) dim(2) inz;\ndcl-s INDEX int(3);\nmonitor;\nread DATA BUFFERS(INDEX);\non-error 121;\nread DATA BUFFERS(1);\nif BUFFERS(1).ID=101;\nsndpgmmsg 'RECORD ADDRESS OK';\nendif;\nendmon;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD ADDRESS OK");
    }

    [Fact]
    public void LIKEREC_character_conversion_failure_preserves_all_receiver_bytes_and_releases_the_lock()
    {
        _files.Insert("QGPL","DATA","DATA","ROW",new Dictionary<string,object?> { ["ID"]=888L,["KEY"]=88m,["TEXT"]=new string('é',12) });
        Compile("**free\ndcl-f DATA keyed usage(*update);\ndcl-ds BUFFER likerec(ROW) inz;\neval BUFFER.ID=7;\neval BUFFER.TEXT='old';\nchain(e) 88 DATA BUFFER;\nif %error and %status(DATA)=452 and BUFFER.ID=7 and BUFFER.TEXT='old' and ID=0;\nsndpgmmsg 'RECORD ATOMIC OK';\nendif;\nseton LR;");
        using var session=Session(1208); Ok(session.Execute("CALL QGPL/APP"),"RECORD ATOMIC OK");
        using var identity=Ipc.Services.Events.OperationIdentity.Enter("QSECOFR",session.Job.Key);
        Assert.Empty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL","DATA",Ipc.Core.Objects.ObjectType.File)));
    }

    [Fact]
    public void LIKEREC_mutable_procedure_parameters_receive_live_record_bytes()
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds A likerec(ROW) inz;\ndcl-ds B likerec(ROW) inz;\ncallp LOAD(A);\ncallp LOAD(B);\nif A.ID=101 and B.ID=102;\nsndpgmmsg 'RECORD BORROW OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nP likerec(ROW);\nend-pi;\nread DATA P;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD BORROW OK");
    }

    [Fact]
    public void LIKEREC_CONST_parameters_cannot_be_used_as_input_receivers()
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nP likerec(ROW) const;\nend-pi;\nread DATA P;\nend-proc;"));

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LIKEREC_direct_returns_keep_the_record_layout_for_rtnparm_and_implicit_prototypes(int ccsid)
    {
        Compile("**free\ndcl-f DATA;\ndcl-ds BUFFER likerec(ROW);\neval BUFFER=LOAD();\nif BUFFER.ID=101 and BUFFER.KEY=20;\nsndpgmmsg 'RECORD RETURN OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n likerec(ROW) rtnparm end-pi;\ndcl-ds LOCAL likerec(ROW);\nread DATA LOCAL;\nreturn LOCAL;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(ccsid); Ok(session.Execute("CALL QGPL/APP"),"RECORD RETURN OK");
    }

    [Fact]
    public void LIKEREC_forward_field_metadata_and_multiple_structures_share_one_descriptor_snapshot()
    {
        var calls=0;
        var p=Ipc.Rpg.Parsing.RpgCompiler.Compile("QGPL","APP","**free\ndcl-f DATA;\ndcl-s TEXTCOPY like(BUFFER.TEXT);\ndcl-ds BUFFER likerec(ROW);\ndcl-c WIDTH %size(BUFFER);\ndcl-ds OTHER likerec(ROW:*all);\nseton LR;",diskResolver:request=> { calls++;return Describe(request); });
        Assert.Equal(1,calls); Assert.Equal(12,p.FindField("TEXTCOPY")!.Length); Assert.Equal(25m,new RpgRuntimeContext(p,new()).ReadValue("WIDTH"));
    }

    [Theory]
    [InlineData("len(30)")] [InlineData("inz(*likeds)")] [InlineData("likeds(OTHER)")] [InlineData("likerec(ROW)")]
    public void LIKEREC_rejects_conflicting_definition_keywords(string options)
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA;\ndcl-ds BUFFER likerec(ROW) "+options+";"));

    [Theory]
    [InlineData("")] [InlineData(":*output")]
    public void LIKEREC_DISK_write_accepts_implicit_input_or_explicit_output_layouts(string extract)
    {
        Compile("**free\ndcl-f DATA keyed usage(*input:*output);\ndcl-ds BUFFER likerec(ROW"+extract+") inz;\ndcl-ds RESULTBUF likerec(ROW);\neval BUFFER.ID=901;\neval BUFFER.KEY=91;\neval BUFFER.TEXT='new';\nwrite ROW BUFFER;\nchain 91 DATA RESULTBUF;\nif RESULTBUF.ID=901 and RESULTBUF.TEXT='new';\nsndpgmmsg 'RECORD MODES OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD MODES OK");
    }

    [Fact]
    public void LIKEREC_update_accepts_an_output_layout_without_FILE_OUTPUT_addition()
    {
        Compile("**free\ndcl-f DATA keyed usage(*update);\ndcl-ds INPUTBUF likerec(ROW);\ndcl-ds OUTPUTBUF likerec(ROW:*output);\nchain 20 DATA INPUTBUF;\neval OUTPUTBUF=INPUTBUF;\neval OUTPUTBUF.TEXT='updated';\nupdate ROW OUTPUTBUF;\nchain 20 DATA INPUTBUF;\nif INPUTBUF.TEXT='updated';\nsndpgmmsg 'RECORD UPDATE MODE OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD UPDATE MODE OK");
    }

    [Fact]
    public void LIKEREC_invalid_numeric_output_fails_before_file_mutation()
    {
        Compile("**free\ndcl-f DATA usage(*output);\ndcl-ds BUFFER likerec(ROW:*output) inz;\neval %subst(BUFFER:9:5)=X'0000000000';\nmonitor;\nwrite ROW BUFFER;\non-error 907;\nsndpgmmsg 'RECORD OUTPUT ATOMIC OK';\nendmon;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD OUTPUT ATOMIC OK"); Assert.Equal(3,_files.ReadKeyed("QGPL","DATA","DATA").Count());
    }

    [Fact]
    public void LIKEREC_key_buffers_pack_prefixed_fields_in_key_order_for_KDS()
    {
        var format=new Ipc.Db.Definitions.RecordFormat { Name="KREC", Fields=[
            new() { Name="TEXT",Type=Ipc.Db.Definitions.FieldType.Alpha,Length=4,Sequence=2 },
            new() { Name="N",Type=Ipc.Db.Definitions.FieldType.Binary,Length=2,Sequence=1 },
            new() { Name="TOTAL",Type=Ipc.Db.Definitions.FieldType.Packed,Length=5,Decimals=2 }] };
        format.AssignPositions(); _files.CreatePhysicalFile("QGPL","META",new() { Name="META",Attribute=Ipc.Db.Definitions.FileAttribute.Physical,Formats=[format] },"");
        _files.Insert("QGPL","META","META","KREC",new Dictionary<string,object?> { ["TEXT"]="AB",["N"]=2L,["TOTAL"]=7.5m });
        Compile("**free\ndcl-f META keyed prefix(P_);\ndcl-ds KEYS likerec(KREC:*key) inz;\ndcl-ds BUFFER likerec(KREC);\neval KEYS.P_N=2;\neval KEYS.P_TEXT='AB';\nchain %kds(KEYS) META BUFFER;\nif BUFFER.P_TOTAL=7.5 and %size(KEYS)=6;\nsndpgmmsg 'RECORD KEYS OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECORD KEYS OK");
    }

    [Fact]
    public void LIKEREC_feedback_extent_changes_cannot_be_used_as_a_record_receiver()
        => Assert.Throws<RpgCompileException>(()=>Parse("**free\ndcl-f DATA infds(BUFFER);\ndcl-ds BUFFER likerec(ROW);\nread DATA BUFFER;"));

    [Fact]
    public void LIKEREC_receiver_syntax_errors_keep_include_provenance_and_prevent_object_creation()
    {
        _files.SaveSourceMember("QGPL","SOURCE","RECBAD","dcl-ds BUFFER likerec(ROW);\nread DATA BUFFER[;",null);
        var result=Create("**free\ndcl-f DATA;\n/copy QGPL/SOURCE,RECBAD\nseton LR;");
        Assert.True(result.IsError); Assert.Contains("RECBAD",result.Message); Assert.False(_system.Objects.Exists("QGPL","APP",Ipc.Core.Objects.ObjectType.Program));
    }

    [Fact]
    public void LIKEREC_saved_descriptors_survive_removal_of_the_description_file_and_source()
    {
        Data("ACTUAL");
        Compile("**free\ndcl-f INPUTFILE extdesc('QGPL/DATA') extfile('QGPL/ACTUAL');\ndcl-ds BUFFER likerec(ROW);\nread INPUTFILE BUFFER;\nif BUFFER.ID=101 and BUFFER.TEXT='first';\nsndpgmmsg 'RECORD SNAPSHOT OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP"); _files.DeleteFile("QGPL","DATA");
        using var session=Session(1208); Ok(session.Execute("CALL QGPL/APP"),"RECORD SNAPSHOT OK");
    }

}
