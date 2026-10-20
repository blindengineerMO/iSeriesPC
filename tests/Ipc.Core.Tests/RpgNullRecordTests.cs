using System.Text.Json.Nodes;
using Ipc.Db.Definitions;
using Ipc.Rpg.Model;
using Ipc.Session;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private void NullData(bool nullKey = false, string? defaultText = null)
    {
        var format = new RecordFormat { Name = "NULLROW", Fields = [new() { Name = "NID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "NKEY", Type = FieldType.Packed, Length = 5, Sequence = 1, NullCapable = nullKey },
            new() { Name = "NTEXT", Type = FieldType.Alpha, Length = 8, NullCapable = true, DefaultValue = defaultText is null ? null : new(defaultText) }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "NULLDATA", new() { Name = "NULLDATA", Attribute = FileAttribute.Physical, Formats = [format], RecordLockWaitSeconds = 0 }, "");
        _files.Insert("QGPL", "NULLDATA", "NULLDATA", "NULLROW", new Dictionary<string, object?> { ["NID"] = 1L, ["NKEY"] = nullKey ? null : 10m, ["NTEXT"] = null });
        _files.Insert("QGPL", "NULLDATA", "NULLDATA", "NULLROW", new Dictionary<string, object?> { ["NID"] = 2L, ["NKEY"] = 20m, ["NTEXT"] = "second" });
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nullable_implicit_reads_updates_and_writes_preserve_separate_indicators(int ccsid)
    {
        NullData(); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update:*output);\nchain 10 NULLDATA;\nif NID=1 and %nullind(NTEXT);\neval NTEXT='data';\nupdate NULLROW;\neval NID=3;\neval NKEY=30;\nwrite NULLROW;\neval %nullind(NTEXT)=*off;\neval NID=4;\neval NKEY=40;\nwrite NULLROW;\nchain 20 NULLDATA;\nif not %nullind(NTEXT) and NTEXT='second';\nsndpgmmsg 'NULL IMPLICIT OK';\nendif;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL IMPLICIT OK");
        var rows = _files.ReadAll("QGPL", "NULLDATA", "NULLDATA"); Assert.Null(rows[0]["NTEXT"]); Assert.Null(rows[2]["NTEXT"]); Assert.Equal("data", Assert.IsType<string>(rows[3]["NTEXT"]).TrimEnd());
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Nullable_result_structures_and_nested_key_searches_use_saved_layouts(int ccsid, bool external)
    {
        NullData(true); var declaration = external ? "extname('QGPL/NULLDATA':*all) prefix(X_)" : "likerec(NULLDATA.NULLROW:*all)"; var prefix = external ? "X_" : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed qualified usage(*update:*output);\ndcl-ds MODEL qualified " + declaration + " template;\n" + (external ? "end-ds;\n" : "") + "dcl-ds BOX qualified inz;\nROW likeds(MODEL) dim(2);\nend-ds;\ndcl-ds KEYS likerec(NULLDATA.NULLROW:*key) inz;\neval %nullind(KEYS.NKEY)=*on;\nchain %kds(KEYS) NULLDATA BOX.ROW(2);\nif BOX.ROW(2)." + prefix + "NID=1 and %nullind(BOX.ROW(2)." + prefix + "NTEXT);\neval BOX.ROW(2)." + prefix + "NTEXT='present';\neval %nullind(BOX.ROW(2)." + prefix + "NTEXT)=*off;\nupdate NULLDATA.NULLROW BOX.ROW(2);\nchain %kds(KEYS) NULLDATA BOX.ROW(1);\nif not %nullind(BOX.ROW(1)." + prefix + "NTEXT) and BOX.ROW(1)." + prefix + "NTEXT='present';\nsndpgmmsg 'NULL RESULT OK';\nendif;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL RESULT OK");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void No_null_mode_mapping_errors_leave_scalar_or_result_receivers_unchanged(bool resultBuffer)
    {
        NullData(); var receiver = resultBuffer ? "BUFFER." : "";
        Compile("**free\nctl-opt alwnull(*no);\ndcl-f NULLDATA keyed;\n" + (resultBuffer ? "dcl-ds BUFFER likerec(NULLROW) inz;\n" : "") + "eval " + receiver + "NID=99;\neval " + receiver + "NTEXT='KEEP';\nchain(e) 10 NULLDATA" + (resultBuffer ? " BUFFER" : "") + ";\nif %error and %status(NULLDATA)=1299 and " + receiver + "NID=99 and " + receiver + "NTEXT='KEEP';\nsndpgmmsg 'NULL MAPPING ATOMIC';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL MAPPING ATOMIC");
    }
    [Theory]
    [InlineData(null, "")] [InlineData("DEFAULT", "DEFAULT")]
    public void Input_only_mode_substitutes_defaults_for_null_fields(string? supplied, string expected)
    {
        NullData(defaultText: supplied); Compile("**free\nctl-opt alwnull(*inputonly);\ndcl-f NULLDATA;\nread NULLDATA;\nif NID=1 and NTEXT='" + expected + "';\nsndpgmmsg 'NULL DEFAULT OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL DEFAULT OK");
    }
    [Theory]
    [InlineData("write NULLROW;", false)] [InlineData("update NULLROW;", false)] [InlineData("chain 1 NULLDATA;", true)]
    public void Input_only_mode_rejects_nullable_output_and_key_searches(string operation, bool nullKey)
    {
        NullData(nullKey); Assert.Throws<RpgCompileException>(() => Parse("**free\nctl-opt alwnull(*inputonly);\ndcl-f NULLDATA keyed usage(*update:*output);\n" + operation));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Scalar_and_expression_list_searches_honor_nullable_field_flags(bool list)
    {
        NullData(true); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\neval NKEY=5;\neval %nullind(NKEY)=*on;\nchain " + (list ? "(NKEY)" : "NKEY") + " NULLDATA;\nif %found(NULLDATA) and NID=1 and %nullind(NKEY);\nsndpgmmsg 'NULL SCALAR KEY OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL SCALAR KEY OK");
    }
    [Fact]
    public void Traditional_KFLD_uses_its_explicit_indicator_for_null_searches()
    {
        NullData(true); Compile("     H ALWNULL(*USRCTL)\n       dcl-f NULLDATA keyed;\n" + RpgFileKeyCompilerTests.KeyC("KLIST", "NULLKEY") + "\n" + RpgFileKeyCompilerTests.KeyC("KFLD", result: "NKEY", factor2: "*IN02") + "\n/free\neval *in02=*on;\nchain NULLKEY NULLDATA;\nif NID=1 and %found(NULLDATA);\nsndpgmmsg 'NULL KLIST OK';\nendif;\nseton LR;\n/end-free");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL KLIST OK");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Null_result_reads_change_only_selected_receiver_indicators_and_not_found_changes_neither(bool external)
    {
        NullData(); var declaration = external ? "extname('QGPL/NULLDATA':*all)" : "likerec(NULLROW:*all)";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed;\ndcl-ds BUFFER qualified " + declaration + " inz;\n" + (external ? "end-ds;\n" : "") + "eval NTEXT='KEEP';\nchain 10 NULLDATA BUFFER;\nif %nullind(BUFFER.NTEXT) and not %nullind(NTEXT) and NTEXT='KEEP';\neval BUFFER.NID=77;\nchain 99 NULLDATA BUFFER;\nif not %found(NULLDATA) and BUFFER.NID=77 and %nullind(BUFFER.NTEXT);\nsndpgmmsg 'NULL RECEIVER ISOLATED';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL RECEIVER ISOLATED");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Nonnullable_input_clears_an_indicator_in_a_shared_native_field(bool nullableFirst)
    {
        NullData(); var definition = _files.GetDefinition("QGPL", "NULLDATA")!; definition.PrimaryFormat.Find("NTEXT")!.NullCapable = false;
        _files.CreatePhysicalFile("QGPL", "NONNULL", definition, "");
        _files.Insert("QGPL", "NONNULL", "NONNULL", "NULLROW", new Dictionary<string, object?> { ["NID"] = 3L, ["NKEY"] = 30m, ["NTEXT"] = "valid" });
        Compile("**free\nctl-opt alwnull(*usrctl);\n" + (nullableFirst ? "dcl-f NULLDATA keyed;\ndcl-f NONNULL rename(NULLROW:OTHERROW);\n" : "dcl-f NONNULL rename(NULLROW:OTHERROW);\ndcl-f NULLDATA keyed;\n") + "eval %nullind(NTEXT)=*on;\nread NONNULL;\nif not %nullind(NTEXT) and NID=3 and NTEXT='valid';\nsndpgmmsg 'NULL INPUT RESET';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL INPUT RESET");
    }
    [Fact]
    public void Null_output_still_validates_packed_data_before_writing()
    {
        NullData(true); Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*output);\ndcl-ds BUFFER likerec(NULLROW:*all) inz;\neval %nullind(BUFFER.NKEY)=*on;\neval %subst(BUFFER:5:3)='BAD';\nmonitor;\nwrite NULLROW BUFFER;\non-error;\nsndpgmmsg 'NULL OUTPUT VALIDATED';\nendmon;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL OUTPUT VALIDATED"); Assert.Equal(2, _files.ReadAll("QGPL", "NULLDATA", "NULLDATA").Count);
    }
    [Fact]
    public void Procedure_STATIC_flags_survive_LR_restart_in_a_named_activation()
    {
        Compile("**free\nctl-opt alwnull(*usrctl);\nif SEEN();\nsndpgmmsg 'NULL STATIC RETAINED';\nelse;\nsndpgmmsg 'NULL STATIC FIRST';\nendif;\nseton LR;\ndcl-proc SEEN;\ndcl-pi *n ind end-pi;\ndcl-s N int(10) static nullind;\ndcl-s WAS ind;\neval WAS=%nullind(N);\neval %nullind(N)=*on;\nreturn WAS;\nend-proc;", group: "NULLSTATIC");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL STATIC FIRST"); Ok(session.Execute("CALL QGPL/APP"), "NULL STATIC RETAINED");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Borrowed_and_local_files_keep_nullable_result_indicators_in_the_executing_frame(bool borrowed)
    {
        NullData();
        var global = borrowed ? "dcl-f NULLDATA keyed qualified block(*no);\n" : "";
        var local = borrowed ? "" : "dcl-f LOCAL keyed extdesc('QGPL/NULLDATA') extfile(*extdesc);\n";
        var parameters = borrowed ? "dcl-pi *n ind;\nF likefile(NULLDATA);\nend-pi;\n" : "dcl-pi *n ind end-pi;\n";
        var name = borrowed ? "F" : "LOCAL"; var format = borrowed ? "F.NULLROW" : "NULLROW";
        Compile("**free\nctl-opt alwnull(*usrctl);\n" + global + "if LOAD(" + (borrowed ? "NULLDATA" : "") + ");\nsndpgmmsg 'NULL LOCAL FILE OK';\nendif;\nseton LR;\ndcl-proc LOAD;\n" + local + parameters + "dcl-ds BUFFER likerec(" + format + ") inz;\nchain 10 " + name + " BUFFER;\nreturn %nullind(BUFFER.NTEXT);\nend-proc;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL LOCAL FILE OK");
    }
    [Fact]
    public void Input_only_defaults_are_live_after_replacing_a_compile_time_description()
    {
        NullData(defaultText: "OLD");
        Compile("**free\nctl-opt alwnull(*inputonly);\ndcl-f NULLDATA;\nread NULLDATA;\nif NTEXT='NEW';\nsndpgmmsg 'NULL LIVE DEFAULT';\nendif;\nseton LR;");
        var definition = _files.GetDefinition("QGPL", "NULLDATA")!; definition.PrimaryFormat.Find("NTEXT")!.DefaultValue = new("NEW");
        _files.DeleteFile("QGPL", "NULLDATA"); _files.CreatePhysicalFile("QGPL", "NULLDATA", definition, "");
        _files.Insert("QGPL", "NULLDATA", "NULLDATA", "NULLROW", new Dictionary<string, object?> { ["NID"] = 1L, ["NKEY"] = 10m, ["NTEXT"] = null });
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL LIVE DEFAULT");
    }
    [Theory]
    [InlineData(FieldType.Alpha, 8, 37)] [InlineData(FieldType.Alpha, 8, 1208)]
    [InlineData(FieldType.Zoned, 5, 37)] [InlineData(FieldType.Zoned, 5, 1208)]
    [InlineData(FieldType.Packed, 5, 37)] [InlineData(FieldType.Packed, 5, 1208)]
    [InlineData(FieldType.Binary, 4, 37)] [InlineData(FieldType.Binary, 4, 1208)]
    [InlineData(FieldType.Float, 8, 37)] [InlineData(FieldType.Float, 8, 1208)]
    [InlineData(FieldType.Logic, 1, 37)] [InlineData(FieldType.Logic, 1, 1208)]
    [InlineData(FieldType.Date, 10, 37)] [InlineData(FieldType.Date, 10, 1208)]
    [InlineData(FieldType.Time, 8, 37)] [InlineData(FieldType.Time, 8, 1208)]
    [InlineData(FieldType.Timestamp, 26, 37)] [InlineData(FieldType.Timestamp, 26, 1208)]
    public void Nullable_database_types_round_trip_through_valid_native_placeholders(FieldType type, int length, int ccsid)
    {
        var format = new RecordFormat { Name = "TYPEROW", Fields = [new() { Name = "NVALUE", Type = type, Length = length, NullCapable = true }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "NULLTYPES", new() { Name = "NULLTYPES", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _files.Insert("QGPL", "NULLTYPES", "NULLTYPES", "TYPEROW", new Dictionary<string, object?> { ["NVALUE"] = null });
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLTYPES usage(*input:*output);\nread NULLTYPES;\nif %nullind(NVALUE);\nwrite TYPEROW;\nsndpgmmsg 'NULL TYPE OK';\nendif;\nseton LR;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL TYPE OK"); var rows = _files.ReadAll("QGPL", "NULLTYPES", "NULLTYPES"); Assert.Equal(2, rows.Count); Assert.All(rows, row => Assert.Null(row["NVALUE"]));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nullable_EXTNAME_and_DISK_descriptions_can_be_removed_after_compilation(int ccsid)
    {
        NullData(); _files.CreatePhysicalFile("QGPL", "DESCR", _files.GetDefinition("QGPL", "NULLDATA")!, "");
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f INPUT keyed extdesc('QGPL/DESCR') extfile('QGPL/NULLDATA');\ndcl-ds BUFFER qualified extname('QGPL/DESCR':*all) inz;\nend-ds;\nchain 10 INPUT BUFFER;\nif BUFFER.NID=1 and %nullind(BUFFER.NTEXT);\nsndpgmmsg 'NULL SAVED DESCRIPTIONS';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.DeleteFile("QGPL", "DESCR"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NULL SAVED DESCRIPTIONS");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Failed_conversion_preserves_native_data_and_null_flags(bool resultBuffer)
    {
        var format = new RecordFormat { Name = "WIDEROW", Fields = [new() { Name = "NID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "NTEXT", Type = FieldType.Alpha, Length = 8, Ccsid = 1208, NullCapable = true }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "WIDEDATA", new() { Name = "WIDEDATA", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _files.Insert("QGPL", "WIDEDATA", "WIDEDATA", "WIDEROW", new Dictionary<string, object?> { ["NID"] = 1L, ["NTEXT"] = "😀" });
        var prefix = resultBuffer ? "BUFFER." : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f WIDEDATA;\n" + (resultBuffer ? "dcl-ds BUFFER likerec(WIDEROW) inz;\n" : "") + "eval " + prefix + "NID=99;\neval " + prefix + "NTEXT='KEEP';\neval %nullind(" + prefix + "NTEXT)=*on;\nmonitor;\nread WIDEDATA" + (resultBuffer ? " BUFFER" : "") + ";\non-error;\nif " + prefix + "NID=99 and " + prefix + "NTEXT='KEEP' and %nullind(" + prefix + "NTEXT);\nsndpgmmsg 'NULL CONVERSION ATOMIC';\nendif;\nendmon;\nseton LR;");
        using var session = Session(37); Ok(session.Execute("CALL QGPL/APP"), "NULL CONVERSION ATOMIC");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Single_element_nullable_EXTFLD_arrays_route_their_element_indicator_through_record_IO(bool resultBuffer)
    {
        NullData(); var prefix = resultBuffer ? "BUFFER." : "";
        Compile("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA usage(*input:*output);\ndcl-ds BUFFER " + (resultBuffer ? "qualified " : "") + "extname('QGPL/NULLDATA':*all) inz;\nNTEXT extfld dim(1);\nend-ds;\nread NULLDATA" + (resultBuffer ? " BUFFER" : "") + ";\nif %nullind(" + prefix + "NTEXT(1));\neval " + prefix + "NTEXT(1)='hidden';\nwrite NULLROW" + (resultBuffer ? " BUFFER" : "") + ";\nsndpgmmsg 'NULL SINGLETON ARRAY';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NULL SINGLETON ARRAY"); var rows = _files.ReadAll("QGPL", "NULLDATA", "NULLDATA"); Assert.Equal(3, rows.Count); Assert.Null(rows[2]["NTEXT"]);
    }
    [Fact]
    public void Null_snapshot_versions_reject_nullable_fields_in_older_envelopes()
    {
        NullData(); var program = Parse("**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA;\nread NULLDATA;"); var json = RpgDiskBindings.Serialize(program);
        Assert.Equal(7, RpgDiskBindings.Restore(program.Source!.Text, json).Version);
        var node = JsonNode.Parse(json)!; node["version"] = 6; Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text, node.ToJsonString()));
    }
}
