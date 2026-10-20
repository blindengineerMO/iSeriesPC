using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Named_external_maps_receive_null_attributes_and_supply_record_updates(int ccsid, bool extname)
    {
        NullData();
        var data = extname ? "extname('QGPL/NULLDATA':*all) prefix(D_)" : "likerec(NULLROW:*all)";
        var flags = extname ? "extname('QGPL/NULLDATA':*all:*null) prefix(F_)" : "likerec(NULLROW:*all:*null)";
        var dp = extname ? "D_" : ""; var fp = extname ? "F_" : ""; var end = extname ? "end-ds;\n" : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update:*output);\ndcl-ds BUFFER qualified " + data + " nullind(FLAGS) inz;\n" + end + "dcl-ds FLAGS qualified " + flags + " inz;\n" + end + "eval FLAGS." + fp + "NID=*on;\nchain 10 NULLDATA BUFFER;\nif FLAGS." + fp + "NTEXT and %nullind(BUFFER." + dp + "NTEXT) and not FLAGS." + fp + "NID;\neval BUFFER." + dp + "NTEXT='named';\neval FLAGS." + fp + "NTEXT=*off;\nupdate NULLROW BUFFER;\neval BUFFER." + dp + "NKEY=30;\neval %nullind(BUFFER." + dp + "NTEXT)=*on;\nwrite NULLROW BUFFER;\nchain 10 NULLDATA BUFFER;\nif BUFFER." + dp + "NTEXT='named' and not FLAGS." + fp + "NTEXT;\nsndpgmmsg 'NULL NAMED MAP IO';\nendif;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL NAMED MAP IO");
        var rows = _files.ReadAll("QGPL", "NULLDATA", "NULLDATA"); Assert.Equal("named", Assert.IsType<string>(rows[0]["NTEXT"]).TrimEnd()); Assert.Null(rows[2]["NTEXT"]);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Named_map_arrays_route_each_nested_selected_buffer_to_its_sibling_flags(bool nested)
    {
        NullData();
        var declarations = nested ? "dcl-ds BOX qualified dim(2) inz;\nDATA likerec(NULLROW:*all) dim(2) nullind(FLAGS);\nFLAGS likerec(NULLROW:*all:*null) dim(2);\nend-ds;\n" : "dcl-ds DATA likerec(NULLROW:*all) dim(2) nullind(FLAGS) inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) dim(2) inz;\n";
        var prefix = nested ? "BOX(2)." : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\n" + declarations + "chain 10 NULLDATA " + prefix + "DATA(2);\nif " + prefix + "FLAGS(2).NTEXT and not " + prefix + "FLAGS(1).NTEXT;\neval " + prefix + "FLAGS(2).NTEXT=*off;\nif not %nullind(" + prefix + "DATA(2).NTEXT);\nsndpgmmsg 'NULL ARRAY MAP IO';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL ARRAY MAP IO");
    }
    [Theory]
    [InlineData("*all:*null")] [InlineData("*null")]
    public void Null_map_buffers_reject_record_input_before_execution(string extract)
    {
        NullData(); Assert.Throws<RpgCompileException>(() => Parse("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA;\ndcl-ds FLAGS likerec(NULLROW:" + extract + ");\nread NULLDATA FLAGS;"));
    }
    [Theory]
    [InlineData("dcl-ds DATA likerec(NULLROW:*all) nullind(FLAGS);\ndcl-ds FLAGS likerec(NULLROW:*input:*null);\n")]
    [InlineData("dcl-ds DATA likerec(NULLROW:*all) dim(2) nullind(FLAGS);\ndcl-ds FLAGS likerec(NULLROW:*all:*null);\n")]
    [InlineData("dcl-ds DATA likerec(NULLROW:*all) nullind(FLAGS);\ndcl-ds OTHER likerec(NULLROW:*all) nullind(FLAGS);\ndcl-ds FLAGS likerec(NULLROW:*all:*null);\n")]
    public void Named_maps_require_matching_extract_shape_and_unique_ownership(string declarations)
    {
        NullData(); Assert.Throws<RpgCompileException>(() => Parse("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA;\n" + declarations));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Named_KEY_maps_supply_null_key_searches(bool external)
    {
        NullData(true); var data = external ? "extname('QGPL/NULLDATA':*key)" : "likerec(NULLROW:*key)"; var map = external ? "extname('QGPL/NULLDATA':*key:*null)" : "likerec(NULLROW:*key:*null)"; var end = external ? "end-ds;\n" : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds KEYS qualified " + data + " nullind(FLAGS) inz;\n" + end + "dcl-ds FLAGS qualified " + map + " inz;\n" + end + "dcl-ds BUFFER likerec(NULLROW) inz;\neval FLAGS.NKEY=*on;\nchain %kds(KEYS) NULLDATA BUFFER;\nif %found(NULLDATA) and BUFFER.NID=1;\nsndpgmmsg 'NULL NAMED KEY MAP';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL NAMED KEY MAP");
    }
    [Fact]
    public void Named_STATIC_maps_survive_LR_in_retained_activations()
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA template;\nif SEEN();\nsndpgmmsg 'NAMED MAP RETAINED';\nelse;\nsndpgmmsg 'NAMED MAP FIRST';\nendif;\nseton LR;\ndcl-proc SEEN;\ndcl-pi *n ind end-pi;\ndcl-ds DATA likerec(NULLROW:*all) nullind(FLAGS) static inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) static inz;\ndcl-s WAS ind;\neval WAS=%nullind(DATA.NTEXT);\neval %nullind(DATA.NTEXT)=*on;\nreturn WAS;\nend-proc;", group: "NAMEDNULL");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NAMED MAP FIRST"); Ok(session.Execute("CALL QGPL/APP"), "NAMED MAP RETAINED");
    }
    [Fact]
    public void LIKEDS_does_not_inherit_an_outer_map_name_but_keeps_automatic_null_capability()
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA;\ndcl-ds DATA likerec(NULLROW:*all) nullind(FLAGS) inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) inz;\ndcl-ds COPY likeds(DATA) inz;\neval %nullind(COPY.NTEXT)=*on;\nif not FLAGS.NTEXT and %nullind(COPY.NTEXT);\nsndpgmmsg 'NULL COPY MAP INDEPENDENT';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL COPY MAP INDEPENDENT");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Named_maps_survive_removal_of_source_and_separate_external_descriptions(int ccsid)
    {
        NullData(); _files.CreatePhysicalFile("QGPL", "DESCR", _files.GetDefinition("QGPL", "NULLDATA")!, "");
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f INPUT keyed extdesc('QGPL/DESCR') extfile('QGPL/NULLDATA');\ndcl-ds DATA qualified extname('QGPL/DESCR':*all) nullind(FLAGS) inz end-ds;\ndcl-ds FLAGS qualified extname('QGPL/DESCR':*all:*null) inz end-ds;\nchain 10 INPUT DATA;\nif FLAGS.NTEXT and %nullind(DATA.NTEXT);\nsndpgmmsg 'NULL MAP SAVED DESCRIPTIONS';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.DeleteFile("QGPL", "DESCR"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL MAP SAVED DESCRIPTIONS");
    }
    [Fact]
    public void Named_occurrence_maps_follow_the_data_occurrence_without_changing_map_selection()
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds DATA likerec(NULLROW:*all) occurs(2) nullind(FLAGS) inz;\ndcl-ds FLAGS likerec(NULLROW:*all:*null) occurs(2) inz;\neval %occur(DATA)=2;\nchain 10 NULLDATA DATA;\nif %nullind(DATA.NTEXT) and not FLAGS.NTEXT and %occur(FLAGS)=1;\neval %occur(FLAGS)=2;\nif FLAGS.NTEXT;\nsndpgmmsg 'NULL OCCURRENCE MAP';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL OCCURRENCE MAP");
    }

}

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Workstation_null_extracts_clear_selected_nonnullable_input_map_bits(int ccsid)
    {
        Display(Screen); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f SCREEN workstn;\ndcl-ds DATA likerec(ENTRY:*all) nullind(FLAGS) inz;\ndcl-ds FLAGS likerec(ENTRY:*all:*null) inz;\neval FLAGS.NAME=*on;\neval DATA.NAME='MAP';\nexfmt ENTRY DATA;\nif DATA.NAME='NEW' and not FLAGS.NAME;\nsndpgmmsg 'NULL WORKSTN MAP';\nendif;\nseton LR;");
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token,
            new Terminal(d => { d.Session.Handle(new(Ipc.Terminal.AidKey.None, Edit: Ipc.Terminal.CursorEdit.FieldEraseToEnd)); foreach (var ch in "NEW") d.Session.Handle(new(Ipc.Terminal.AidKey.None, Character: ch)); return d.Session.Handle(new(Ipc.Terminal.AidKey.Enter)); }));
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("NULL WORKSTN MAP", result.Message);
    }
}
