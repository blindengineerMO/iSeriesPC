using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Console.Session;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RECNO_tracks_open_input_and_bounds_in_the_actual_access_path(bool keyed)
    {
        var source = "**free\ndcl-f DATA " + (keyed ? "keyed " : "") + "recno(RRN);\ndcl-s RRN packed(10:0) inz(99);\n" +
            "if RRN<>0;\nreturn;\nendif;\nread DATA;\nif RRN<>" + (keyed ? "2" : "1") + ";\nreturn;\nendif;\nsetll " + (keyed ? "20" : "2") + " DATA;\n" +
            "if RRN<>" + (keyed ? "1" : "2") + ";\nreturn;\nendif;\nread DATA;\nif RRN<>" + (keyed ? "1" : "2") + ";\nreturn;\nendif;\n" +
            "setgt " + (keyed ? "20" : "2") + " DATA;\nif RRN=3;\nsndpgmmsg 'RECNO POSITION OK';\nendif;\nseton LR;";
        Compile(source); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RECNO POSITION OK");
    }
    [Fact]
    public void RECNO_updates_even_when_input_uses_a_result_DS_and_is_preserved_on_a_miss()
    {
        Compile("**free\ndcl-f DATA recno(RRN);\ndcl-s RRN int(20);\ndcl-ds BUFFER likerec(ROW);\nchain 3 DATA BUFFER;\nif RRN<>3 or BUFFER.ID<>103;\nreturn;\nendif;\nchain 99 DATA BUFFER;\nif RRN=3 and BUFFER.ID=103;\nsndpgmmsg 'RECNO DS MISS OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RECNO DS MISS OK");
    }
    [Fact]
    public void RECNO_accepts_a_qualified_native_numeric_field()
    {
        Compile("**free\ndcl-f DATA recno(POSITION.RRN);\ndcl-ds POSITION qualified;\nRRN packed(10:0);\nend-ds;\nread DATA;\nif POSITION.RRN=1;\nsndpgmmsg 'RECNO QUALIFIED OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RECNO QUALIFIED OK");
    }
    [Theory]
    [InlineData("dcl-s RRN char(10);", "RRN")]
    [InlineData("dcl-s RRN packed(10:2);", "RRN")]
    [InlineData("dcl-s RRN int(10) dim(2);", "RRN")]
    [InlineData("dcl-s RRN float(8);", "RRN")]
    [InlineData("dcl-c RRN 3;", "RRN")]
    [InlineData("dcl-s RRN int(10) template;", "RRN")]
    [InlineData("", "MISSING")]
    [InlineData("dcl-s RRN int(10);", "RRN+1")]
    public void RECNO_rejects_undefined_non_scalar_fractional_readonly_and_expression_targets(string declaration, string name)
    { Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA recno(" + name + ");\n" + declaration + "\nreturn;")); }
    [Fact]
    public void Relative_WRITE_fills_an_initialized_deleted_slot_without_moving_the_input_cursor()
    {
        _files.InitializeMember("QGPL", "DATA", totalRecords: 5, deleted: true);
        Compile("**free\ndcl-f DATA usage(*input:*output) recno(RRN);\ndcl-s RRN packed(10:0);\nread DATA;\neval RRN=5;\neval ID=105;\neval KEY=50;\neval TEXT='relative';\nwrite ROW;\nread DATA;\nif ID<>102 or RRN<>2;\nreturn;\nendif;\nchain 5 DATA;\nif ID=105 and RRN=5;\nsndpgmmsg 'RELATIVE WRITE OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RELATIVE WRITE OK"); Assert.Equal(4, Count); Assert.Equal("relative", Text(105));
    }
    [Theory]
    [InlineData("*OUTPUT", "1")]
    [InlineData("*OUTPUT", "4")]
    [InlineData("*INPUT:*OUTPUT", "1")]
    [InlineData("*INPUT:*OUTPUT", "4")]
    public void Relative_WRITE_rejects_active_and_unallocated_positions_for_every_output_mode(string usage, string number)
    {
        Compile("**free\ndcl-f DATA usage(" + usage + ") recno(RRN);\ndcl-s RRN packed(10:0);\neval RRN=" + number + ";\neval ID=105;\nwrite(e) ROW;\nif %error;\nsndpgmmsg 'RELATIVE REJECTION OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RELATIVE REJECTION OK"); Assert.Equal(3, Count); Assert.Equal("first", Text(101));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4294967289")]
    public void Relative_WRITE_rejects_invalid_runtime_RECNO_values_without_record_mutation(string number)
    {
        Compile("**free\ndcl-f DATA usage(*output) recno(RRN);\ndcl-s RRN packed(12:0);\neval RRN=" + number + ";\nwrite(e) ROW;\nif %error;\nsndpgmmsg 'RECNO VALUE REJECTED';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "RECNO VALUE REJECTED"); Assert.Equal(3, Count);
    }
    [Fact]
    public void RECNO_metadata_uses_version_eight_and_cannot_be_downgraded_to_an_older_saved_image()
    {
        var program = Parse("**free\ndcl-f DATA recno(RRN);\ndcl-s RRN int(20);\nread DATA;\nseton LR;");
        var metadata = JsonNode.Parse(RpgDiskBindings.Serialize(program))!.AsObject(); Assert.Equal(8, metadata["version"]!.GetValue<int>());
        metadata["version"] = 7; Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source!.Text, metadata.ToJsonString()));
    }
    [Fact]
    public void RECNO_explicit_open_resets_input_feedback_and_reopen_restarts_position()
    {
        Compile("**free\ndcl-f DATA usropn recno(RRN);\ndcl-s RRN packed(10:0) inz(99);\nif RRN<>99;\nreturn;\nendif;\nopen DATA;\nif RRN<>0;\nreturn;\nendif;\nchain 3 DATA;\nclose DATA;\nif RRN<>3;\nreturn;\nendif;\nopen DATA;\nif RRN<>0;\nreturn;\nendif;\nread DATA;\nif RRN=1;\nsndpgmmsg 'RECNO REOPEN OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO REOPEN OK");
    }
    [Fact]
    public void RECNO_output_only_open_preserves_the_requested_deleted_slot()
    {
        _files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:true);
        Compile("**free\ndcl-f DATA usage(*output) recno(RRN);\ndcl-s RRN packed(10:0) inz(5);\neval ID=105;\neval TEXT='output slot';\nwrite ROW;\nif RRN=5;\nsndpgmmsg 'RECNO OUTPUT OPEN OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO OUTPUT OPEN OK"); Assert.Equal("output slot",Text(105));
    }
    [Fact]
    public void RECNO_does_not_inherit_through_LIKEFILE_but_accepts_a_new_association()
    {
        var program=Parse("**free\ndcl-f MODEL template recno(ORIGINAL) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') recno(ACTUAL);\ndcl-f PLAIN likefile(MODEL) extfile('QGPL/DATA');\ndcl-s ORIGINAL packed(10:0);\ndcl-s ACTUAL packed(10:0);\nseton LR;");
        Assert.Equal("ACTUAL",program.DiskFiles.Single(f=>f.Request.Name=="COPY").Request.Recno); Assert.Null(program.DiskFiles.Single(f=>f.Request.Name=="PLAIN").Request.Recno);
    }
    [Fact]
    public void RECNO_borrowed_file_reads_update_the_actual_owner_despite_a_shadowing_local()
    {
        Compile("**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') recno(RRN);\ndcl-s RRN packed(10:0);\ncallp LOAD(COPY);\nif RRN=2;\nsndpgmmsg 'RECNO OWNER OK';\nendif;\nseton LR;\ndcl-proc LOAD;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-s RRN char(4) inz('safe');\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nread F BUFFER;\nif RRN<>'safe';\nreturn;\nendif;\nend-proc;");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO OWNER OK");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RECNO_local_files_use_the_current_invocation_field_and_respect_cursor_storage(bool retained)
    {
        Compile("**free\ndcl-s A int(20);\ndcl-s B int(20);\neval A=NEXTROW();\neval B=NEXTROW();\nif A=1 and B="+(retained?"2":"1")+";\nsndpgmmsg 'RECNO LOCAL OK';\nendif;\nseton LR;\ndcl-proc NEXTROW;\ndcl-f LOCAL "+(retained?"static ":"")+"extdesc('QGPL/DATA') extfile(*extdesc) recno(RRN);\ndcl-pi *n int(20) end-pi;\ndcl-s RRN packed(10:0);\ndcl-ds BUFFER likerec(ROW);\nread LOCAL BUFFER;\nreturn RRN;\nend-proc;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO LOCAL OK");
    }
    [Fact]
    public void RECNO_missed_bounds_and_end_of_file_preserve_the_last_feedback()
    {
        Compile("**free\ndcl-f DATA recno(RRN);\ndcl-s RRN packed(10:0);\nchain 3 DATA;\nread DATA;\nif not %eof(DATA) or RRN<>3;\nreturn;\nendif;\nsetll 99 DATA;\nif RRN<>3;\nreturn;\nendif;\nchain 1 DATA;\nif RRN=1;\nsndpgmmsg 'RECNO EOF OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO EOF OK");
    }
    [Fact]
    public void Relative_WRITE_uses_the_borrowed_files_actual_RECNO_field()
    {
        _files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:true);
        Compile("**free\ndcl-f MODEL qualified template usage(*output) extdesc('QGPL/DATA');\ndcl-f COPY likefile(MODEL) extfile('QGPL/DATA') recno(RRN);\ndcl-s RRN packed(10:0) inz(5);\ncallp SAVE(COPY);\nsndpgmmsg 'RECNO BORROWED WRITE OK';\nseton LR;\ndcl-proc SAVE;\ndcl-pi *n;\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW:*output);\neval BUFFER.ID=105;\neval BUFFER.KEY=50;\neval BUFFER.TEXT='borrowed';\nwrite F.ROW BUFFER;\nend-proc;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO BORROWED WRITE OK"); Assert.Equal("borrowed",Text(105));
    }
    [Fact]
    public void RECNO_accepts_the_maximum_sparse_relative_record_without_enumerating_holes()
    {
        Assert.False(new Ipc.Console.Session.CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) SIZE(*NOMAX)").IsError);
        _files.InitializeMember("QGPL","DATA",totalRecords:4294967288,deleted:true);
        Compile("**free\ndcl-f DATA usage(*input:*output) recno(RRN);\ndcl-s RRN packed(10:0);\neval RRN=4294967288;\neval ID=105;\neval TEXT='maximum';\nwrite ROW;\nchain 4294967288 DATA;\nif ID=105 and RRN=4294967288;\nsndpgmmsg 'RECNO MAXIMUM OK';\nendif;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO MAXIMUM OK"); Assert.Equal(4,Count);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RECNO_fixed_F_spec_feedback_survives_saved_program_reload(bool shifted)
    {
        var chars=Enumerable.Repeat(' ',80).ToArray();var shift=shifted?1:0;
        chars[5+shift]='F'; "DATA".CopyTo(0,chars,6+shift,4); chars[16+shift]='I'; chars[17+shift]='F'; chars[21+shift]='E'; "DISK".CopyTo(0,chars,35+shift,4);
        Compile(new string(chars,0,43+shift)+"RECNO(RRN)\n       dcl-s RRN packed(10:0);\n/free\nread DATA;\nif RRN=1;\nsndpgmmsg 'RECNO FIXED OK';\nendif;\nseton LR;\n/end-free");
        _files.RemoveMember("QGPL","SOURCE","APP"); using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO FIXED OK");
    }
    [Fact]
    public void RECNO_feedback_overflow_does_not_overwrite_imported_record_fields()
    {
        Assert.False(new Ipc.Console.Session.CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) SIZE(*NOMAX)").IsError);
        _files.InitializeMember("QGPL","DATA",totalRecords:1000,deleted:true);
        using(var writer=_files.OpenRecordWriter("QGPL","DATA","DATA"))writer.WriteRelative(1000,new Dictionary<string,object?>{["ID"]=105L});
        Compile("**free\ndcl-f DATA recno(RRN);\ndcl-s RRN packed(3:0);\nchain 1 DATA;\nmonitor;\nchain 1000 DATA;\non-error 103;\nif ID=101 and RRN=1;\nsndpgmmsg 'RECNO OVERFLOW GUARDED';\nendif;\nendmon;\nseton LR;");
        using var session=Session(); Ok(session.Execute("CALL QGPL/APP"),"RECNO OVERFLOW GUARDED");
    }
    [Fact]
    public void RECNO_retained_open_preserves_feedback_until_reclaim_opens_again()
    {
        Compile("**free\ndcl-f DATA recno(RRN);\ndcl-s RRN packed(10:0);\nif RRN=1;\nsndpgmmsg 'RECNO RETAINED OK';\nendif;\nread DATA;\nreturn;","APP","RECNOGROUP");
        using var session=Session(); Assert.False(session.Execute("CALL QGPL/APP").IsError);
        Ok(session.Execute("CALL QGPL/APP"),"RECNO RETAINED OK");
        Ok(session.Execute("RCLACTGRP ACTGRP(RECNOGROUP)"),"");
        var reopened=session.Execute("CALL QGPL/APP"); Assert.False(reopened.IsError,reopened.Message); Assert.DoesNotContain("RECNO RETAINED OK",reopened.Message);
    }
}
