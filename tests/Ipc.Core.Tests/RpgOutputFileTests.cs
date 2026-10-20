using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private static string OutputFile(string usage = "*OUTPUT", string extra = "") => "**free\ndcl-f DATA keyed usage(" + usage + ") " + extra + ";\n";
    private const string OutputValues = "eval ID=104;\neval KEY=15.25;\neval TEXT='added';\nwrite ROW;\n";
    [Theory]
    [InlineData(false, 4L)]
    [InlineData(true, 3L)]
    public void Declared_WRITE_obeys_the_physical_deleted_record_policy(bool reuse, long expectedNumber)
    {
        var changed = new Ipc.Console.Session.CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) REUSEDLT(" + (reuse ? "*YES" : "*NO") + ")");
        Assert.False(changed.IsError, changed.Message);
        using (var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false)) Assert.True(cursor.DeleteRecord(3));
        Compile(OutputFile() + OutputValues + "seton LR;"); using var session = Session();
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        using var verify = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.True(verify.ChainRecord(expectedNumber)); Assert.Equal(104L, verify.Current!["ID"]);
        Assert.Equal(3, Count); Assert.Empty(Locks());
    }
    [Theory]
    [InlineData("*OUTPUT")]
    [InlineData("*INPUT:*OUTPUT")]
    [InlineData("*UPDATE:*OUTPUT")]
    [InlineData("*DELETE:*OUTPUT")]
    [InlineData("*INPUT:*UPDATE:*DELETE:*OUTPUT")]
    [InlineData("*OUTPUT:*INPUT")]
    public void Output_usage_adds_a_new_record_using_all_imported_fields(string usage)
    {
        Compile(OutputFile(usage) + OutputValues + "seton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Equal(4, Count); Assert.Equal("added", Text(104));
        Assert.Equal(15.25m, _files.ReadAll("QGPL", "DATA", "DATA").Single(r => (long)r["ID"]! == 104)["KEY"]); Assert.Empty(Locks());
    }
    [Fact]
    public void WRITE_maps_prefixed_fields_and_renamed_formats_to_the_external_record()
    {
        Compile(OutputFile(extra: "prefix(P_) rename(ROW:ITEM)") + "eval P_ID=104;\neval P_KEY=22.50;\neval P_TEXT='mapped';\nwrite ITEM;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("mapped", Text(104));
    }
    [Theory]
    [InlineData('O', ' ', false)]
    [InlineData('O', 'A', false)]
    [InlineData('O', ' ', true)]
    [InlineData('I', 'A', false)]
    [InlineData('U', 'A', false)]
    [InlineData('U', 'A', true)]
    public void Native_fixed_output_and_A_addition_enable_WRITE(char type, char addition, bool shifted)
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); var shift = shifted ? 1 : 0;
        chars[5 + shift] = 'F'; "DATA".CopyTo(0, chars, 6 + shift, 4); chars[16 + shift] = type;
        chars[17 + shift] = type == 'O' ? ' ' : 'F'; chars[19 + shift] = addition; chars[21 + shift] = 'E'; chars[33 + shift] = 'K';
        "DISK".CopyTo(0, chars, 35 + shift, 4);
        Compile(new string(chars) + "\n/free\n" + OutputValues + "seton LR;\n/end-free");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("added", Text(104));
    }
    [Theory]
    [InlineData("read DATA;")]
    [InlineData("readp DATA;")]
    [InlineData("reade 20 DATA;")]
    [InlineData("readpe 20 DATA;")]
    [InlineData("chain 20 DATA;")]
    [InlineData("setll 20 DATA;")]
    [InlineData("setgt 20 DATA;")]
    [InlineData("update ROW;")]
    [InlineData("delete ROW;")]
    [InlineData("unlock DATA;")]
    [InlineData("write DATA;")]
    [InlineData("write ROW BUFFER;")]
    [InlineData("force DATA;")]
    public void Output_only_usage_rejects_input_mutation_file_operands_and_unsupported_transfers(string operation)
    { Assert.Equal(3, Assert.Throws<RpgCompileException>(() => Parse(OutputFile() + operation)).Location!.Line); }
    [Fact]
    public void WRITE_does_not_replace_the_selected_input_record_or_sequential_position()
    {
        Compile(OutputFile("*INPUT:*OUTPUT") + "chain 10 DATA;\n" + OutputValues + "read DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "104"); Assert.Equal("*BLANKS", Text(102));
    }
    [Fact]
    public void WRITE_does_not_supply_an_update_target_without_locked_input()
    {
        Compile(OutputFile("*UPDATE:*OUTPUT") + OutputValues + "update ROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Equal(4, Count); Assert.Equal("added", Text(104)); Assert.Empty(Locks());
    }
    [Fact]
    public void WRITE_keeps_the_prior_locked_input_target_for_a_subsequent_UPDATE()
    {
        Compile(OutputFile("*UPDATE:*OUTPUT") + "chain 20 DATA;\n" + OutputValues + "eval ID=101;\neval KEY=20;\neval TEXT='updated';\nupdate ROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Equal("updated", Text(101)); Assert.Equal("added", Text(104)); Assert.Empty(Locks());
    }
    [Fact]
    public void Output_handles_need_Add_and_ObjectOperate_without_Read_Update_or_Delete()
    {
        Compile(OutputFile() + OutputValues + "seton LR;");
        _system.Security.Profiles.Create(new() { Name = "ADDUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ADDUSER"), "OutputAccessFixture42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "ADDUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("ADDUSER"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("added", Text(104));
        using var owner = OperationIdentity.Enter("ADDUSER", session.Job.Key);
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "DATA", "DATA")).MessageId);
    }
    [Fact]
    public void Output_open_requires_Add_even_when_no_WRITE_is_executed()
    {
        Compile(OutputFile() + "seton LR;");
        _system.Security.Profiles.Create(new() { Name = "READUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READUSER"), "OutputOpenAccess42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "READUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READUSER", Authorities.UseBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("READUSER"), CancellationToken.None);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message); Assert.Empty(Locks());
    }
    [Fact]
    public void Output_USROPN_explicit_close_and_RETURN_retain_the_native_open_state()
    {
        Compile(OutputFile(extra: "usropn") + "dcl-s CALLS int(10);\neval CALLS=CALLS+1;\nif CALLS=1;\nopen DATA;\nreturn;\nendif;\n" +
            OutputValues + "close DATA;\nif not %open(DATA);\nsndpgmmsg 'CLOSED';\nendif;\nseton LR;", group: "OUTFILES");
        using var session = Session(); Assert.False(session.Execute("CALL QGPL/APP").IsError);
        Assert.Contains(Locks(), l => l.Lifetime == "OutputOpen"); Ok(session.Execute("CALL QGPL/APP"), "CLOSED"); Assert.Equal("added", Text(104)); Assert.Empty(Locks());
    }
    [Fact]
    public void Retained_output_rechecks_Add_authority_when_the_program_is_called_again()
    {
        Compile(OutputFile() + "dcl-s CALLS int(10);\neval CALLS=CALLS+1;\nif CALLS=1;\nreturn;\nendif;\n" + OutputValues + "seton LR;", group: "OUTFILES");
        _system.Security.Profiles.Create(new() { Name = "ADDUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ADDUSER"), "OutputRevocation42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "ADDUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("ADDUSER"), CancellationToken.None);
        var first = session.Execute("CALL QGPL/APP"); Assert.False(first.IsError, first.Message);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message); Assert.Equal(3, Count);
    }
    [Fact]
    public void Output_modes_have_version_three_metadata_and_cannot_be_downgraded_to_prior_versions()
    {
        var program = Parse(OutputFile() + OutputValues); var json = JsonNode.Parse(RpgDiskBindings.Serialize(program))!;
        Assert.Equal(3, json["version"]!.GetValue<int>()); var binding = RpgDiskBindings.Restore(program.Source!.Text, json.ToJsonString()).Files.Single();
        Assert.True(binding.Request.Output); Assert.False(binding.Request.Input);
        foreach (var version in new[] { 1, 2 }) { json["version"] = version; Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text, json.ToJsonString())); }
    }
    [Fact]
    public void Saved_version_two_update_metadata_defaults_to_input_without_output()
    {
        var program = Parse(UpdateFile() + "read DATA;"); var json = JsonNode.Parse(RpgDiskBindings.Serialize(program))!; json["version"] = 2;
        var request = json["files"]![0]!["request"]!.AsObject(); request.Remove("input"); request.Remove("output");
        var restored = RpgDiskBindings.Restore(program.Source!.Text, json.ToJsonString());
        var compiled = RpgCompiler.Compile("QGPL", "APP", program.Source.Text, diskResolver: restored.Resolve);
        Assert.True(compiled.DiskFiles.Single().Request.Input); Assert.True(compiled.DiskFiles.Single().Request.Update); Assert.False(compiled.DiskFiles.Single().Request.Output);
    }
    [Fact]
    public void Saved_mode_changes_cannot_enable_WRITE_in_an_input_source_program()
    {
        var program = Parse("**free\ndcl-f DATA keyed;\nread DATA;"); var json = JsonNode.Parse(RpgDiskBindings.Serialize(program))!;
        json["files"]![0]!["request"]!["output"] = true;
        var restored = RpgDiskBindings.Restore(program.Source!.Text, json.ToJsonString());
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "APP", program.Source.Text, diskResolver: restored.Resolve));
    }
    [Fact]
    public void Output_does_not_eagerly_decode_existing_records()
    {
        using (var connection = _system.Connections.Open())
        { using var sql = connection.CreateCommand(); sql.CommandText = "UPDATE \"QGPL.DATA.DATA\" SET ID='malformed' WHERE ID=101"; sql.ExecuteNonQuery(); }
        Compile(OutputFile() + OutputValues + "seton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal(4, Count);
    }
}
