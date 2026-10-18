using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
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
    private static string UpdateFile(string usage = "*UPDATE", string extra = "") => "**free\ndcl-f DATA keyed usage(" + usage + ") " + extra + ";\n";
    private string Text(long id) => ((string)_files.ReadAll("QGPL", "DATA", "DATA").Single(row => (long)row["ID"]! == id)["TEXT"]!).TrimEnd();
    private long Count => _files.RowCount("QGPL", "DATA", "DATA");
    private IReadOnlyList<JobLockInfo> Locks() => new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File));
    [Theory]
    [InlineData("*UPDATE")]
    [InlineData("*INPUT:*UPDATE")]
    [InlineData("*DELETE")]
    [InlineData("*UPDATE:*DELETE")]
    [InlineData("*INPUT:*UPDATE:*DELETE")]
    public void Declared_update_usage_imports_input_and_writes_the_locked_record(string usage)
    {
        Compile(UpdateFile(usage) + "chain 10 DATA;\neval TEXT='changed';\nupdate ROW;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102"); Assert.Equal("changed", Text(102)); Assert.Equal("first", Text(101));
        Assert.Empty(Locks());
    }
    [Fact]
    public void UPDATE_maps_prefixed_fields_and_renamed_record_formats_back_to_external_fields()
    {
        Compile(UpdateFile(extra: "rename(ROW:ITEM) prefix(P_)") + "chain 20 ITEM;\neval P_TEXT='mapped';\nupdate ITEM;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("mapped", Text(101));
    }
    [Theory]
    [InlineData("*DELETE")]
    [InlineData("*UPDATE:*DELETE")]
    public void Delete_usage_permits_current_and_keyed_one_record_deletion(string usage)
    {
        Compile(UpdateFile(usage) + "chain 20 DATA;\ndelete ROW;\ndelete 10 DATA;\nread DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "103"); Assert.Equal(1, Count); Assert.Empty(Locks());
    }
    [Fact]
    public void Native_fixed_U_file_implies_update_and_delete_without_file_addition()
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[5] = 'F'; "DATA".CopyTo(0, chars, 6, 4);
        chars[16] = 'U'; chars[17] = 'F'; chars[21] = 'E'; chars[33] = 'K'; "DISK".CopyTo(0, chars, 35, 4);
        Compile(new string(chars) + "\n/free\nchain 20 DATA;\neval TEXT='fixed';\nupdate ROW;\ndelete 10 DATA;\nseton LR;\n/end-free");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("fixed", Text(101)); Assert.Equal(2, Count);
    }
    [Fact]
    public void Arrival_update_declarations_use_RRNs_instead_of_the_files_keys()
    {
        Compile("**free\ndcl-f DATA usage(*delete);\nchain 1 DATA;\neval TEXT='RRN';\nupdate ROW;\ndelete 2 DATA;\nread DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "103"); Assert.Equal("RRN", Text(101)); Assert.Equal(2, Count);
    }
    [Theory]
    [InlineData("chain(n) 20 DATA;")]
    [InlineData("setll 20 DATA;\nread(n) DATA;")]
    [InlineData("setgt 20 DATA;\nreadp(n) DATA;")]
    [InlineData("setll 20 DATA;\nreade(n) 20 DATA;")]
    [InlineData("setgt 20 DATA;\nreadpe(n) 20 DATA;")]
    public void N_input_returns_fields_without_a_lock_and_cannot_supply_an_UPDATE_target(string input)
    {
        Compile(UpdateFile() + input + "\neval TEXT='must fail';\nupdate ROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("Read and lock", result.Message);
        Assert.Equal("first", Text(101)); Assert.Empty(Locks());
    }
    [Fact]
    public void UNLOCK_keeps_input_and_position_but_requires_new_locked_input_before_update()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nunlock DATA;\nread DATA;\neval TEXT='after unlock';\nupdate ROW;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "103"); Assert.Equal("after unlock", Text(103)); Assert.Equal("first", Text(101));
    }
    [Theory]
    [InlineData("unlock DATA;")]
    [InlineData("read(n) DATA;")]
    [InlineData("chain(n) 10 DATA;")]
    public void Input_only_declarations_reject_update_only_operations_and_extenders(string operation)
    { Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA keyed;\n" + operation)); }
    [Theory]
    [InlineData("read(ee) DATA;")]
    [InlineData("read(nn) DATA;")]
    [InlineData("read(nen) DATA;")]
    [InlineData("setll(n) 10 DATA;")]
    [InlineData("update(n) ROW;")]
    [InlineData("unlock(n) DATA;")]
    public void Unsupported_extenders_fail_explicitly_at_the_statement_line(string operation)
    { Assert.Equal(3, Assert.Throws<RpgCompileException>(() => Parse(UpdateFile() + operation)).Location!.Line); }
    [Theory]
    [InlineData("delete ROW;")]
    [InlineData("delete 10 DATA;")]
    [InlineData("write ROW;")]
    [InlineData("update DATA;")]
    [InlineData("unlock ROW;")]
    public void Update_usage_enforces_delete_permission_and_external_format_operands(string operation)
    { Assert.Throws<RpgCompileException>(() => Parse(UpdateFile() + operation)); }
    [Fact]
    public void A_successful_UPDATE_consumes_its_locked_input_target()
    {
        Compile(UpdateFile() + "chain 20 DATA;\neval TEXT='one';\nupdate ROW;\neval TEXT='two';\nupdate ROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Equal("one", Text(101)); Assert.Empty(Locks());
    }
    [Fact]
    public void UNLOCK_without_fresh_input_prevents_UPDATE()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nunlock DATA;\nupdate ROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("Read and lock", result.Message); Assert.Equal("first", Text(101));
    }
    [Fact]
    public void Saved_update_bindings_preserve_prior_versions_and_version_one_input_bindings_remain_readable()
    {
        var update = Parse(UpdateFile("*DELETE") + "read DATA;"); var saved = JsonNode.Parse(RpgDiskBindings.Serialize(update))!;
        Assert.Equal(3, saved["version"]!.GetValue<int>()); var restored = RpgDiskBindings.Restore(update.Source!.Text, saved.ToJsonString());
        Assert.True(restored.Files.Single().Request.Update); Assert.True(restored.Files.Single().Request.Delete);
        saved["version"] = 1; Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(update.Source.Text, saved.ToJsonString()));
        var input = Parse("**free\ndcl-f DATA;\nread DATA;"); saved = JsonNode.Parse(RpgDiskBindings.Serialize(input))!; saved["version"] = 1;
        var request = saved["files"]![0]!["request"]!.AsObject(); request.Remove("update"); request.Remove("delete");
        restored = RpgDiskBindings.Restore(input.Source!.Text, saved.ToJsonString()); Assert.False(restored.Files.Single().Request.Update);
        Assert.Single(RpgCompiler.Compile("QGPL", "APP", input.Source.Text, diskResolver: restored.Resolve).DiskFiles);
    }
    [Fact]
    public void Changing_usage_in_saved_metadata_cannot_upgrade_an_input_program()
    {
        var input = Parse("**free\ndcl-f DATA;\nread DATA;"); var saved = JsonNode.Parse(RpgDiskBindings.Serialize(input))!;
        saved["files"]![0]!["request"]!["update"] = true;
        var restored = RpgDiskBindings.Restore(input.Source!.Text, saved.ToJsonString());
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "APP", input.Source.Text, diskResolver: restored.Resolve));
    }
    [Fact]
    public void Declared_update_and_delete_preserve_duplicate_key_siblings()
    {
        _files.Insert("QGPL", "DATA", "DATA", "ROW", new Dictionary<string, object?> { ["ID"] = 104L, ["KEY"] = 20m, ["TEXT"] = "sibling" });
        Compile(UpdateFile("*DELETE") + "chain 20 DATA;\neval TEXT='selected';\nupdate ROW;\ndelete 20 DATA;\nread DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "104"); Assert.Equal("sibling", Text(104)); Assert.Equal(3, Count);
    }
    [Fact]
    public void Declared_logical_update_and_delete_target_the_physical_record_without_losing_hidden_fields()
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "VROW", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "KEY"),
            LogicalFileTests.Line(' ', "TEXT"), LogicalFileTests.Line('K', "KEY"));
        var definition = new Ipc.Db.Dds.LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
        Compile("**free\ndcl-f VIEW keyed usage(*delete) rename(VROW:ITEM) prefix(V_);\nchain 20 VIEW;\neval V_TEXT='logical';\nupdate ITEM;\ndelete 10 VIEW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Equal("logical", Text(101)); Assert.Equal(2, Count); Assert.Empty(Locks());
    }
    [Fact]
    public void Update_execution_requires_an_executing_job_even_with_a_SQLite_provider()
    {
        var program = Parse(UpdateFile() + "read DATA;\nseton LR;");
        var host = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" };
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }

}
