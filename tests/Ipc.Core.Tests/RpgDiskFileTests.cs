using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public RpgDiskFileTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DiskFileFixture39");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        Data("DATA");
    }
    private void Data(string name, bool keyed = true, int width = 12, bool nullable = false)
    {
        var format = new RecordFormat { Name = "ROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "KEY", Type = FieldType.Packed, Length = 9, Decimals = 2, Sequence = keyed ? 1 : 0 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = width, NullCapable = nullable }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", name, new() { Name = name, Attribute = FileAttribute.Physical, RecordLockWaitSeconds = 0, Formats = [format] }, "");
        foreach (var (id, key, text) in new[] { (101L, 20m, "first"), (102L, 10m, "*BLANKS"), (103L, 30m, "third") })
            _files.Insert("QGPL", name, name, "ROW", new Dictionary<string, object?> { ["ID"] = id, ["KEY"] = key, ["TEXT"] = text });
    }
    private RpgDiskLayout Describe(RpgDiskRequest request)
    {
        var parts = request.DescriptionFile.Split('/'); var name = parts[^1];
        return RpgDiskLayouts.Describe(request, "QGPL", name, _files.GetDefinition("QGPL", name)!);
    }
    private RpgProgram Parse(string source) => RpgCompiler.Compile("QGPL", "APP", source, diskResolver: Describe);
    private CommandResult Create(string source, string name = "APP", string group = "*NEW")
    {
        _files.SaveSourceMember("QGPL", "SOURCE", name, source, null);
        return new CommandService(_system).Execute($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) ACTGRP({group})");
    }
    private void Compile(string source, string name = "APP", string group = "*NEW")
    { var result = Create(source, name, group); Assert.False(result.IsError, result.Message); }
    private ExecutionSession Session() => new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    private static string Body => "read DATA;\nsndpgmmsg %char(ID)+':'+%trim(TEXT);\nseton LR;";
    private static void Ok(CommandResult result, string text)
    { Assert.False(result.IsError, result.Message); Assert.Contains(text, result.Message); }
    public void Dispose() => _system.Dispose();

    [Theory]
    [InlineData("dcl-f DATA;", "101:first")]
    [InlineData("dcl-f DATA disk;", "101:first")]
    [InlineData("dcl-f DATA disk(*ext) usage(*input);", "101:first")]
    [InlineData("dcl-f DATA keyed;", "102:*BLANKS")]
    public void Native_free_disk_defaults_and_explicit_keyed_access_import_the_external_fields(string declaration, string expected)
    {
        Compile("**free\n" + declaration + "\n" + Body);
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), expected);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_and_historical_fixed_F_specs_use_K_column_and_continuation_keywords(bool shifted)
    {
        var chars = Enumerable.Repeat(' ', 90).ToArray(); var shift = shifted ? 1 : 0;
        chars[5 + shift] = 'F'; "DATA".CopyTo(0, chars, 6 + shift, 4); chars[16 + shift] = 'I'; chars[17 + shift] = 'F';
        chars[21 + shift] = 'E'; chars[33 + shift] = 'K'; "DISK".CopyTo(0, chars, 35 + shift, 4);
        var continuation = Enumerable.Repeat(' ', 43 + shift).ToArray(); continuation[5 + shift] = 'F';
        var source = new string(chars).TrimEnd() + "\n" + new string(continuation) + "RENAME(ROW:ITEM) PREFIX(P_)\n/free\nread ITEM;\n" +
            "sndpgmmsg %char(P_ID)+':'+%trim(P_TEXT);\nseton LR;\n/end-free";
        Compile(source); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102:*BLANKS");
    }
    [Fact]
    public void EXTDESC_EXTFILE_RENAME_PREFIX_and_long_program_file_names_bind_independently()
    {
        Data("ACTUAL"); Compile("**free\ndcl-f LONG_PROGRAM_FILE disk extdesc('QGPL/DATA') extfile('QGPL/ACTUAL') keyed rename(ROW:ITEM) prefix(P_);\n" +
            "chain 10 ITEM;\nif %found(LONG_PROGRAM_FILE);\nsndpgmmsg %char(P_ID)+':'+%trim(P_TEXT);\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102:*BLANKS");
    }
    [Fact]
    public void Unkeyed_declaration_on_a_keyed_PF_uses_real_relative_record_numbers()
    {
        using (var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA")) cursor.DeleteRecord(2);
        Compile("**free\ndcl-f DATA;\nchain 2 DATA;\nif not %found(DATA);\nsetll 2 DATA;\nread ROW;\nsndpgmmsg %char(ID);\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "103");
    }
    [Fact]
    public void USROPN_and_CLOSE_require_an_explicit_OPEN_and_report_OPEN_status()
    {
        Compile("**free\ndcl-f DATA usropn;\nif not %open(DATA);\nopen DATA;\nendif;\nread DATA;\nclose DATA;\n" +
            "if not %open(DATA);\nopen DATA;\nread DATA;\nsndpgmmsg %char(ID);\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
    [Theory]
    [InlineData("usropn", "read DATA;", "not open")]
    [InlineData("", "close DATA;\nread DATA;", "not open")]
    [InlineData("", "open DATA;", "already open")]
    [InlineData("usropn", "close DATA;", "not open")]
    public void Invalid_open_state_does_not_silently_reopen_the_file(string options, string operations, string error)
    {
        Compile("**free\ndcl-f DATA " + options + ";\n" + operations + "\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains(error, result.Message);
    }
    [Theory]
    [InlineData("usage(*update:*update)")]
    [InlineData("usage(*delete:*delete)")]
    [InlineData("usage(*output:*output)")]
    [InlineData("usage(*input:*output:*unknown)")]
    [InlineData("disk(80)")]
    [InlineData("keyed(*char:10)")]
    [InlineData("commit")]
    [InlineData("recno(ID)")]
    [InlineData("extfile(DATA)")]
    [InlineData("extdesc('QGPL/DATA') extdesc('QGPL/DATA')")]
    [InlineData("keyed disk")]
    [InlineData("rename(ROW:ITEM) extdesc('QGPL/DATA')")]
    public void Unsupported_open_modes_and_keywords_fail_at_the_declaration_line(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA " + options + ";\nread DATA;"));
        Assert.Equal(2, error.Location!.Line);
    }
    [Theory]
    [InlineData("write DATA;")]
    [InlineData("update ROW;")]
    [InlineData("delete 1 DATA;")]
    [InlineData("read UNKNOWN;")]
    [InlineData("open ROW;")]
    [InlineData("reade 1 DATA;")]
    [InlineData("readpe 1 DATA;")]
    public void Input_declarations_reject_writes_undeclared_files_and_invalid_access_at_compile_time(string operation)
    {
        var error = Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA;\n" + operation)); Assert.Equal(3, error.Location!.Line);
    }
    [Fact]
    public void KEYED_requires_keys_and_null_capable_external_fields_fail_explicitly()
    {
        Data("PLAIN", keyed: false); Data("NULLABLE", nullable: true);
        Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f PLAIN keyed;\nread PLAIN;"));
        Assert.Contains("null-capable", Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f NULLABLE;\nread NULLABLE;")).Message);
    }
    [Fact]
    public void External_fields_cannot_alias_incompatible_scalars_or_other_record_names()
    {
        Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-s ID char(20);\ndcl-f DATA;\nread DATA;"));
        Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA;\ndcl-f OTHER extdesc('QGPL/DATA');\nread DATA;"));
        Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA rename(ROW:OTHER);\ndcl-f OTHER extdesc('QGPL/DATA') rename(ROW:NEXT);\nread DATA;"));
    }
    [Fact]
    public void Include_errors_retain_the_original_declaration_location()
    {
        var included = new SourceDocument("BAD/FILE", "dcl-f DATA usage(*unknown);");
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "APP", new SourceDocument("MAIN", "**free\n/include BAD\nread DATA;"),
            (_, _) => included, diskResolver: Describe));
        Assert.Equal("BAD/FILE", error.Location!.Source); Assert.Equal(1, error.Location.Line);
    }
    [Theory]
    [InlineData("*NEW")]
    [InlineData("DISKS")]
    public void RETURN_retains_position_until_activation_group_reclaim_or_new_group_exit(string group)
    {
        Compile("**free\ndcl-f DATA;\nread DATA;\nsndpgmmsg %char(ID);\nreturn;", group: group);
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "101");
        Ok(session.Execute("CALL QGPL/APP"), group == "*NEW" ? "101" : "102");
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key))
        {
            var held = new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File));
            Assert.Equal(group == "DISKS", held.Count != 0);
        }
        if (group == "DISKS")
        {
            Assert.False(session.Execute("RCLACTGRP ACTGRP(DISKS)").IsError);
            Ok(session.Execute("CALL QGPL/APP"), "101");
        }
    }
    [Fact]
    public void Explicit_CLOSE_remains_closed_on_the_next_call_without_LR()
    {
        Compile("**free\ndcl-f DATA;\nif %open(DATA);\nread DATA;\nsndpgmmsg %char(ID);\nclose DATA;\nelse;\nsndpgmmsg 'CLOSED';\nendif;\nreturn;", group: "DISKS");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "101"); Ok(session.Execute("CALL QGPL/APP"), "CLOSED");
    }
    [Fact]
    public void A_job_override_selects_a_compatible_member_without_rewriting_the_compiled_layout()
    {
        Data("ACTUAL"); _files.AddMember("QGPL", "ACTUAL", "OTHER");
        _files.Insert("QGPL", "ACTUAL", "OTHER", "ROW", new Dictionary<string, object?> { ["ID"] = 999L, ["KEY"] = 1m, ["TEXT"] = "override" });
        Compile("**free\ndcl-f DATA;\n" + Body);
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/ACTUAL) MBR(OTHER) OVRSCOPE(*JOB)").IsError);
        Ok(session.Execute("CALL QGPL/APP"), "999:override");
    }
    [Fact]
    public void An_incompatible_override_fails_before_any_record_is_copied()
    {
        Data("ACTUAL", width: 14); Compile("**free\ndcl-f DATA;\n" + Body);
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/ACTUAL) OVRSCOPE(*JOB)").IsError);
        var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("recompile", result.Message);
    }
    [Fact]
    public void Shared_native_paths_share_compatible_positions_but_keep_keyed_and_arrival_paths_separate()
    {
        var source = "**free\ndcl-f DATA {0};\nread DATA;\nsndpgmmsg %char(ID);\nreturn;";
        Compile(source.Replace("{0}", ""), "FIRST", "DISKS"); Compile(source.Replace("{0}", ""), "SECOND", "DISKS"); Compile(source.Replace("{0}", "keyed"), "KEYED", "DISKS");
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/DATA) SHARE(*YES) OVRSCOPE(*JOB)").IsError);
        Ok(session.Execute("CALL QGPL/FIRST"), "101"); Ok(session.Execute("CALL QGPL/SECOND"), "102"); Ok(session.Execute("CALL QGPL/KEYED"), "102");
        Assert.False(session.Execute("RCLACTGRP ACTGRP(DISKS)").IsError);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }
    [Fact]
    public void Saved_bindings_restore_without_a_live_compile_resolver_and_detect_tampering()
    {
        var program = Parse("**free\ndcl-f DATA keyed;\nread DATA;"); var json = RpgDiskBindings.Serialize(program);
        var saved = RpgDiskBindings.Restore(program.Source!.Text, json);
        var restored = RpgCompiler.Compile("QGPL", "APP", program.Source.Text, diskResolver: saved.Resolve); Assert.Single(restored.DiskFiles);
        Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text + "\n", json));
        Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text, json.Replace("\"version\":3", "\"version\":3,\"version\":3")));
        var document = JsonNode.Parse(json)!; document["extra"] = true;
        Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text, document.ToJsonString()));
        document = JsonNode.Parse(json)!; document["files"]![0]!["layout"]!["fields"]![0]!["length"] = 10;
        Assert.Throws<InvalidDataException>(() => RpgDiskBindings.Restore(program.Source.Text, document.ToJsonString()));
    }
    [Fact]
    public void Changed_source_cannot_reuse_saved_disk_bindings()
    {
        Compile("**free\ndcl-f DATA;\n" + Body); var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        descriptor.Source += "\n";
        descriptor.ExtendedAttributes = descriptor.ExtendedAttributes!.Where(a => a.Key != CompiledSourceMap.AttributeName && a.Key != RpgWorkstationBindings.AttributeName).ToDictionary(a => a.Key, a => a.Value);
        _system.Objects.Update(descriptor);
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("disk bindings", result.Message);
    }
    [Fact]
    public void Retained_disk_cursors_accept_a_new_call_token_after_the_previous_token_is_cancelled()
    {
        using var oldToken = new CancellationTokenSource();
        var program = Parse("**free\ndcl-f DATA;\nread DATA;\nreturn;");
        var first = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", RetainFiles = true, CancellationToken = oldToken.Token };
        var context = new RpgInterpreter(first).Run(program); Assert.Equal(101L, context.ReadValue("ID")); oldToken.Cancel();
        var second = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", RetainFiles = true };
        try { new RpgInterpreter(second).Run(program, existingContext: context); Assert.Equal(102L, context.ReadValue("ID")); }
        finally { context.CloseDiskFiles(); }
    }
    [Theory]
    [InlineData("extfile(*extdesc)")]
    [InlineData("extdesc('qgpl/data')")]
    [InlineData("extfile('*CURLIB/DATA')")]
    public void Invalid_native_external_file_names_and_missing_EXTDESC_fail_explicitly(string options)
        => Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA " + options + ";\nread DATA;"));
    [Fact]
    public void EXTFILE_overrides_use_the_external_name_and_ignore_an_override_for_the_RPG_alias()
    {
        Data("OTHER"); _files.AddMember("QGPL", "OTHER", "CHOSEN");
        _files.Insert("QGPL", "OTHER", "CHOSEN", "ROW", new Dictionary<string, object?> { ["ID"] = 999L, ["KEY"] = 1m, ["TEXT"] = "external" });
        Compile("**free\ndcl-f ALIAS extdesc('QGPL/DATA') extfile('QGPL/DATA');\nread ALIAS;\nsndpgmmsg %char(ID);\nseton LR;");
        using var session = Session();
        Assert.False(session.Execute("OVRDBF FILE(ALIAS) TOFILE(QGPL/OTHER) MBR(CHOSEN) OVRSCOPE(*JOB)").IsError);
        Ok(session.Execute("CALL QGPL/APP"), "101");
        Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/OTHER) MBR(CHOSEN) OVRSCOPE(*JOB)").IsError);
        Ok(session.Execute("CALL QGPL/APP"), "999");
    }
    [Fact]
    public void Compile_time_overrides_bind_the_actual_description_but_EXTDESC_keeps_the_requested_runtime_name()
    {
        Data("OTHER", width: 14);
        _files.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\ndcl-f ALIAS extdesc('QGPL/DATA') extfile(*extdesc);\nread ALIAS;\nsndpgmmsg %char(ID);\nseton LR;", null);
        using var session = Session();
        Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/OTHER) OVRSCOPE(*JOB)").IsError);
        var compiled = session.Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE)"); Assert.False(compiled.IsError, compiled.Message);
        Ok(session.Execute("CALL QGPL/APP"), "101");
        Assert.False(session.Execute("DLTOVR FILE(DATA) LVL(*JOB)").IsError);
        var mismatch = session.Execute("CALL QGPL/APP"); Assert.True(mismatch.IsError); Assert.Contains("recompile", mismatch.Message);
    }
    [Fact]
    public void A_retained_cursor_rechecks_file_authority_on_the_next_call()
    {
        Compile("**free\ndcl-f DATA;\nread DATA;\nsndpgmmsg %char(ID);\nreturn;", group: "DISKS");
        _system.Security.Profiles.Create(new UserProfile { Name = "DISKUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("DISKUSER"), "DiskFileUser39");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "DISKUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "DISKUSER", Authorities.UseBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("DISKUSER"), CancellationToken.None);
        Ok(session.Execute("CALL QGPL/APP"), "101");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "DISKUSER", AuthorityBit.None);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message);
    }
    [Fact]
    public void Replacing_a_file_with_an_incompatible_format_does_not_recompile_the_saved_binding()
    {
        Compile("**free\ndcl-f DATA;\n" + Body);
        _files.DeleteFile("QGPL", "DATA"); Data("DATA", width: 14);
        using var session = Session(); var mismatch = session.Execute("CALL QGPL/APP"); Assert.True(mismatch.IsError); Assert.Contains("recompile", mismatch.Message);
    }
    [Fact]
    public void Missing_input_preserves_prefixed_record_fields_and_reports_the_declared_file_status()
    {
        Compile("**free\ndcl-f DATA keyed prefix(P_);\nchain 20 ROW;\nchain 999 ROW;\nif not %found(DATA) and P_ID=101;\n" +
            "setll 999 DATA;\nread ROW;\nif %eof(DATA) and P_ID=101;\nsndpgmmsg 'UNCHANGED';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "UNCHANGED");
    }
    [Fact]
    public void Automatic_disk_files_open_in_reverse_declaration_order_and_failed_initialization_closes_all_handles()
    {
        var program = Parse("**free\ndcl-f FIRST extdesc('QGPL/DATA') extfile(*extdesc) rename(ROW:FIRSTROW) prefix(F_);\n" +
            "dcl-f SECOND extdesc('QGPL/DATA') extfile(*extdesc) rename(ROW:SECONDROW) prefix(S_);\nreturn;");
        var opened = new List<string>(); var closed = 0;
        var host = new RpgHost { OpenDisk = binding =>
        {
            opened.Add(binding.Request.Name);
            if (binding.Request.Name == "FIRST") throw new RpgRuntimeException("injected open failure");
            var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "DATA", "DATA", keyed: false);
            return new(() => cursor, () => { closed++; cursor.Dispose(); });
        } };
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program)); Assert.Equal(new[] { "SECOND", "FIRST" }, opened); Assert.Equal(1, closed);
    }
    [Fact]
    public void Logical_file_projection_and_selection_are_imported_for_declared_input()
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "VROW", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "ID"),
            LogicalFileTests.Line(' ', "TEXT"), LogicalFileTests.Line('K', "ID"), LogicalFileTests.Line('S', "ID", "COMP(GT 101)"));
        var definition = new Ipc.Db.Dds.LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
        Compile("**free\ndcl-f VIEW keyed;\nread VIEW;\nsndpgmmsg %char(ID)+':'+%trim(TEXT);\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102:*BLANKS");
    }
    [Fact]
    public void KLIST_fields_can_bind_the_imported_external_key_without_duplicate_scalar_declarations()
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[5] = 'F'; "DATA".CopyTo(0, chars, 6, 4);
        chars[16] = 'I'; chars[17] = 'F'; chars[21] = 'E'; chars[33] = 'K'; "DISK".CopyTo(0, chars, 35, 4);
        Compile(string.Join('\n', new string(chars), RpgFileKeyCompilerTests.KeyC("KLIST", "SEARCH"),
            RpgFileKeyCompilerTests.KeyC("KFLD", result: "KEY"), "/free", "eval KEY=10;", "chain SEARCH DATA;", "sndpgmmsg %char(ID);", "seton LR;", "/end-free"));
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "102");
    }
    [Fact]
    public void Mixed_workstation_and_disk_automatic_opens_preserve_reverse_source_order()
    {
        var records = new[] { new RpgDisplayRecord("ENTRY", "ENTRY", [new("NAME", "NAME", RpgFieldKind.Character, 1, 0, true, true)], false, false, null) };
        var layout = new RpgWorkstationLayout("QGPL/SCREEN", RpgWorkstationLayout.SignatureFor(records), records);
        var program = RpgCompiler.Compile("QGPL", "APP", "**free\ndcl-f DATA;\ndcl-f SCREEN workstn;\nreturn;",
            workstationResolver: _ => layout, diskResolver: Describe);
        var opened = new List<string>();
        var host = new RpgHost { OpenWorkstation = _ => { opened.Add("SCREEN"); throw new RpgRuntimeException("stop before disk"); },
            OpenDisk = _ => { opened.Add("DATA"); throw new RpgRuntimeException("wrong order"); } };
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program)); Assert.Equal(new[] { "SCREEN" }, opened);
    }
    [Fact]
    public void An_invalid_shared_open_releases_its_new_path_and_allocations()
    {
        Data("BAD", width: 14); Compile("**free\ndcl-f DATA;\n" + Body);
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/BAD) SHARE(*YES) OVRSCOPE(*JOB)").IsError);
        Assert.True(session.Execute("CALL QGPL/APP").IsError);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "BAD", ObjectType.File)));
        Assert.Equal(0, _system.JobRuntime.Environment(session.Job.Key)!.OpenPathCount);
    }
    [Fact]
    public void Unused_saved_bindings_are_rejected_when_a_program_is_loaded()
    {
        Compile("**free\ndcl-f DATA;\n" + Body);
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        var attributes = new Dictionary<string, string>(descriptor.ExtendedAttributes!);
        var node = JsonNode.Parse(attributes[RpgDiskBindings.AttributeName])!;
        var extra = node["files"]![0]!.DeepClone(); extra["request"]!["name"] = "UNUSED";
        node["files"]!.AsArray().Add(extra); attributes[RpgDiskBindings.AttributeName] = node.ToJsonString();
        descriptor.ExtendedAttributes = attributes; _system.Objects.Update(descriptor);
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("Unused compiled RPG disk", result.Message);
    }
    [Fact]
    public void A_program_reopens_its_saved_disk_binding_from_a_durable_catalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-disk-bindings-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "DurableDiskFixture39");
                var files = new SqliteFileStore(system.Connections, system.Objects);
                files.CreatePhysicalFile("QGPL", "DATA", _files.GetDefinition("QGPL", "DATA")!, "");
                files.Insert("QGPL", "DATA", "DATA", "ROW", new Dictionary<string, object?> { ["ID"] = 456L, ["KEY"] = 1m, ["TEXT"] = "durable" });
                files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
                files.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\ndcl-f DATA;\n" + Body, null);
                var created = new CommandService(system).Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE)"); Assert.False(created.IsError, created.Message);
            }
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
                Ok(session.Execute("CALL QGPL/APP"), "456:durable");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void Cancellation_rebind_cannot_transfer_cursor_ownership()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "DISKUSER" });
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "DISKUSER", Authorities.UseBits);
        DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("DISKUSER")) cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA");
        try { Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.RebindCancellation(default)).MessageId); }
        finally { cursor.Dispose(); }
    }
}
