using Ipc.Console.Session;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed class RpgPreprocessingTests
{
    private static RpgProgram Compile(string text, Func<SourceDocument, string, SourceDocument>? resolver = null)
        => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", text), resolver);

    [Fact]
    public void Nested_copy_and_include_share_definitions_and_preserve_physical_locations()
    {
        var documents = new Dictionary<string, string> {
            ["FIRST"] = "/DEFINE READY\n/INCLUDE SECOND\n/IF NOT DEFINED(ABSENT)\nsndpgmmsg 'first';\n/ENDIF",
            ["SECOND"] = "\n/TITLE nested listing\n/SPACE 3\n/EJECT\nsndpgmmsg 'nested';"
        };
        var program = Compile("**free\n\n/COPY FIRST\n/IF DEFINED(READY)\nsndpgmmsg 'root';\n/ELSE\n/COPY MISSING\n/ENDIF",
            (_, name) => new(name, documents[name]));
        var nested = program.MainStatements.Single(s => s.Factor1 == "'nested'");
        Assert.Equal("SECOND", nested.Location!.Source); Assert.Equal(5, nested.Location.Line);
        Assert.Equal(new[] { "ROOT:3:1", "FIRST:2:1" }, nested.Location.IncludeStack);
        var messages = new List<string>();
        new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "nested", "first", "root" }, messages);
        var restored = CompiledSourceMap.Restore(program.Source!.Text, CompiledSourceMap.Serialize(program.Source));
        Assert.Equal(documents["SECOND"], restored.Sources["SECOND"]);
        Assert.DoesNotContain("/TITLE", restored.Text);
    }

    [Theory]
    [InlineData("/ELSE", "Unmatched")]
    [InlineData("/ENDIF", "Unmatched")]
    [InlineData("/IF DEFINED(A)", "Unclosed")]
    [InlineData("/IF DEFINED(A)\n/ELSE\n/ELSE\n/ENDIF", "repeated")]
    [InlineData("/IF DEFINED(A)\n/ELSE\n/ELSEIF DEFINED(B)\n/ENDIF", "after /ELSE")]
    [InlineData("/ELSEIF DEFINED(A)", "Unmatched")]
    [InlineData("/IF A\n/ENDIF", "require")]
    [InlineData("/IF *NOT DEFINED(A)\n/ENDIF", "require")]
    [InlineData("/IF DEFINED(A) OR DEFINED(B)\n/ENDIF", "require")]
    [InlineData("/DEFINE BAD-SYMBOL", "requires")]
    [InlineData("/UNDEFINE", "requires")]
    [InlineData("/COPY", "requires")]
    [InlineData("/INCLUDE 'unfinished", "Unclosed")]
    [InlineData("/COPY ONE TWO", "quoted")]
    [InlineData("/EJECT extra", "no argument")]
    [InlineData("/SPACE -1", "unsigned")]
    [InlineData("/SPACE abc", "unsigned")]
    [InlineData("/FREE extra", "no argument")]
    [InlineData("/EOF", "Unsupported RPG")]
    [InlineData("/EXEC SQL", "Unsupported RPG")]
    public void Invalid_directives_fail_at_original_locations(string directive, string diagnostic)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\n/COPY BAD", (_, _) => new("BAD", "\n" + directive)));
        var expectedLine = diagnostic is "repeated" or "after /ELSE" ? 4 : 2;
        Assert.Contains($"BAD:{expectedLine}:1", error.Message); Assert.Contains("ROOT:2:1", error.Message);
        Assert.Contains(diagnostic, error.Message); Assert.NotNull(error.Location);
    }

    [Fact]
    public void Elseif_and_undefine_do_not_execute_inactive_branches_or_resolve_includes()
    {
        var program = Compile("""
            **free
            /DEFINE ONCE
            /IF DEFINED(ABSENT)
            /DEFINE WRONG
            /COPY MISSING
            /ELSEIF DEFINED(ONCE)
            /UNDEFINE ONCE
            sndpgmmsg 'selected';
            /ELSEIF NOT DEFINED(ONCE)
            /DEFINE WRONG
            /ELSE
            /COPY MISSING
            /ENDIF
            /IF NOT DEFINED(WRONG)
            /IF NOT DEFINED(ONCE)
            sndpgmmsg 'done';
            /ENDIF
            /ENDIF
            """, (_, _) => throw new InvalidOperationException("Inactive include was resolved"));
        var messages = new List<string>(); new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "selected", "done" }, messages);
    }

    [Fact]
    public void Fixed_sequence_prefixes_comments_literals_and_free_markers_keep_their_columns()
    {
        var program = Compile("000100/COPY PART\n      /FREE\n// /COPY MISSING\n      * /COPY MISSING\nsndpgmmsg '/* literal // /COPY */';\n      /END-FREE",
            (_, name) => { Assert.Equal("PART", name); return new("PART", "      /FREE\n  sndpgmmsg 'fixed copy';\n      /END-FREE"); });
        var messages = new List<string>(); new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "fixed copy", "/* literal // /COPY */" }, messages);
        var statement = program.MainStatements[0]; Assert.Equal(3, statement.Location!.Column);
        Assert.Equal("ROOT:1:7", Assert.Single(statement.Location.IncludeStack));
    }

    [Theory]
    [InlineData("if 1 = 1;", "IF without")]
    [InlineData("endif;", "ENDIF without")]
    [InlineData("do;", "DO/FOR without")]
    [InlineData("enddo;", "ENDDO without")]
    [InlineData("select;", "SELECT without")]
    [InlineData("endsl;", "ENDSL without")]
    [InlineData("when 1 = 1;", "WHEN/OTHER outside")]
    public void Compiler_block_errors_identify_included_source(string statement, string expected)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\n/COPY BAD", (_, _) => new("BAD", "\n\n" + statement)));
        Assert.Contains("BAD:3:1", error.Message); Assert.Contains("ROOT:2:1", error.Message); Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Runtime_errors_keep_the_original_member_and_include_chain()
    {
        var program = Compile("**free\n/COPY BAD", (_, _) => new("BAD", "\nexsr MISSING;"));
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new RpgHost()).Run(program));
        Assert.Contains("BAD:2:1", error.Message); Assert.Contains("ROOT:2:1", error.Message);
    }

    [Fact]
    public void Cycles_and_conditional_scope_crossing_are_rejected()
    {
        var root = new SourceDocument("ROOT", "/COPY SELF");
        Assert.Contains("cycle", Assert.Throws<SourcePreprocessException>(() => new RpgSourcePreprocessor((_, _) => root).Expand(root)).Message);
        Assert.Contains("Unclosed", Assert.Throws<RpgCompileException>(() => Compile("/COPY PART", (_, _) => new("PART", "/IF DEFINED(A)"))).Message);
        Assert.Contains("Unmatched", Assert.Throws<RpgCompileException>(() => Compile("/IF NOT DEFINED(A)\n/COPY PART\n/ENDIF", (_, _) => new("PART", "/ENDIF"))).Message);
    }

    [Theory]
    [InlineData("bytes", "1 MiB")]
    [InlineData("lines", "10000")]
    [InlineData("width", "8192")]
    [InlineData("symbols", "256")]
    [InlineData("nesting", "64")]
    [InlineData("null", "null character")]
    public void Source_and_conditional_limits_are_enforced(string kind, string expected)
    {
        var source = kind switch {
            "bytes" => new string('x', 1048577),
            "lines" => new string('\n', 10000),
            "width" => new string('x', 8193),
            "symbols" => string.Join('\n', Enumerable.Range(0, 257).Select(i => "/DEFINE SYM" + i)),
            "nesting" => string.Join('\n', Enumerable.Repeat("/IF NOT DEFINED(A)", 65)),
            _ => "text\0text"
        };
        Assert.Contains(expected, Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }

    [Fact]
    public void Include_depth_document_count_and_expansion_are_bounded_and_cancellable()
    {
        var next = 0;
        Assert.Contains("depth", Assert.Throws<RpgCompileException>(() => Compile("/COPY NEXT", (_, _) => new("INC" + ++next, "/COPY NEXT"))).Message);
        Assert.Contains("64 documents", Assert.Throws<RpgCompileException>(() => Compile(string.Join('\n', Enumerable.Range(0, 64).Select(i => "/COPY INC" + i)), (_, name) => new(name, ""))).Message);
        Assert.Contains("bounded", Assert.Throws<RpgCompileException>(() => Compile(string.Join('\n', Enumerable.Repeat("/COPY PART", 60)), (_, _) => new("PART", new string('\n', 999)))).Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RpgCompiler.Compile("QGPL", "MAIN", "**free", cancellation.Token));
        using var midExpansion = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", "/COPY PART"),
            (_, _) => { midExpansion.Cancel(); return new("PART", ""); }, midExpansion.Token));
    }

    [Fact]
    public void Repeated_includes_use_the_first_snapshot()
    {
        var reads = 0;
        var program = Compile("**free\n/COPY PART\n/COPY PART", (_, _) => new("PART", ++reads == 1 ? "sndpgmmsg 'first';" : "sndpgmmsg 'changed';"));
        Assert.Equal(2, program.MainStatements.Count(s => s.Factor1 == "'first'"));
        Assert.Equal(2, reads); Assert.Equal(2, program.Source!.Sources.Count);
    }

    [Fact]
    public void CRTBNDRPG_snapshots_nested_members_and_runs_after_copy_and_source_deletion()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC");
        files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\n/COPY FIRST", null);
        files.SaveSourceMember("QGPL", "QRPGLESRC", "FIRST", "/DEFINE RUN\n/IF DEFINED(RUN)\n/INCLUDE QGPL/QRPGLESRC,SECOND\n/ENDIF", null);
        files.SaveSourceMember("QGPL", "QRPGLESRC", "SECOND", "sndpgmmsg 'original';", null);
        var commands = new CommandService(system);
        var created = commands.Execute("CRTBNDRPG PGM(QGPL/MAIN)"); Assert.False(created.IsError, created.Message);
        var descriptor = system.Objects.GetRequired("QGPL", "MAIN", ObjectType.Program);
        Assert.DoesNotContain("/COPY", descriptor.Source!);
        var map = CompiledSourceMap.Restore(descriptor.Source!, descriptor.ExtendedAttributes![CompiledSourceMap.AttributeName]);
        Assert.Equal(3, map.Sources.Count);
        var old = files.ReadSourceMember("QGPL", "QRPGLESRC", "SECOND");
        files.SaveSourceMember("QGPL", "QRPGLESRC", "SECOND", "sndpgmmsg 'changed';", old.Revision);
        system.ObjectOperations.Relocate(new("QGPL", "MAIN"), ObjectType.Program, new("QGPL", "COPY"), copy: true);
        files.DeleteFile("QGPL", "QRPGLESRC");
        var called = commands.Execute("CALL QGPL/COPY"); Assert.False(called.IsError, called.Message); Assert.Contains("original", called.Message); Assert.DoesNotContain("changed", called.Message);
    }

    [Fact]
    public void Rpg_member_resolution_searches_all_listed_files_and_supports_current_library()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        foreach (var library in new[] { "FIRST", "SECOND", "HIDDEN" }) { system.Libraries.CreateLibrary(library); files.CreateSourceFile(library, "QRPGLESRC"); }
        files.SaveSourceMember("FIRST", "QRPGLESRC", "MAIN", "**free\n/COPY PART", null);
        files.SaveSourceMember("SECOND", "QRPGLESRC", "PART", "sndpgmmsg 'second';", null);
        files.SaveSourceMember("HIDDEN", "QRPGLESRC", "PART", "sndpgmmsg 'hidden';", null);
        files.SaveSourceMember("HIDDEN", "QRPGLESRC", "SECRET", "sndpgmmsg 'hidden';", null);
        var job = system.Jobs.CreateInteractive("QSECOFR", currentLibrary: "FIRST", libraryList: "SECOND");
        var commands = new CommandService(system, job);
        var created = commands.Execute("CRTBNDRPG PGM(FIRST/MAIN) SRCFILE(*CURLIB/QRPGLESRC) SRCMBR(*PGM)"); Assert.False(created.IsError, created.Message);
        Assert.Contains("second", commands.Execute("CALL FIRST/MAIN").Message);
        var resolver = new Ipc.Session.Sources.ProgramSourceResolver(system, job);
        Assert.Equal("FIRST/QRPGLESRC(MAIN)", resolver.ResolveRpg(new("ROOT/FILE(MBR)", ""), "*CURLIB/QRPGLESRC,MAIN").Identity);
        Assert.Equal("SECOND/QRPGLESRC(PART)", resolver.ResolveRpg(new("ROOT/FILE(MBR)", ""), "QRPGLESRC,PART").Identity);
        Assert.Equal("CPF2817", Assert.Throws<CpfException>(() => resolver.ResolveRpg(new("ROOT/FILE(MBR)", ""), "SECRET")).MessageId);
    }

    [Theory]
    [InlineData("/COPY MISSING", "CPF2817")]
    [InlineData("/INCLUDE QGPL/NOFILE,PART", "CPF9801")]
    [InlineData("/COPY QGPL/PRIVATE,PART", "CPF9802")]
    [InlineData("/COPY ../host.rpgle", "IPC0006")]
    public void Missing_unauthorized_and_invalid_includes_never_create_programs(string directive, string code)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC"); files.CreateSourceFile("QGPL", "PRIVATE");
        files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\n" + directive, null);
        files.SaveSourceMember("QGPL", "PRIVATE", "PART", "sndpgmmsg 'private';", null);
        system.Security.Authority.Grant("QGPL", "PRIVATE", ObjectType.File, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER"))
        {
            var result = new CommandService(system).Execute("CRTBNDRPG PGM(QGPL/MAIN)");
            Assert.True(result.IsError); Assert.Contains(code, result.Message); Assert.Contains("QGPL/QRPGLESRC(MAIN):2:1", result.Message);
        }
        Assert.False(system.Objects.Exists("QGPL", "MAIN", ObjectType.Program));
    }

    [Fact]
    public void Stream_includes_use_strict_UTF8_and_relative_paths_and_live_service_authority()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-rpg-include-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var system = IpcSystem.Create(":memory:"); system.Start(); var commands = new CommandService(system);
            var root = Path.Combine(directory, "main.rpgle");
            File.WriteAllText(root, "**free\n/COPY 'part file.rpgle'");
            File.WriteAllText(Path.Combine(directory, "part file.rpgle"), "sndpgmmsg 'stream';");
            var created = commands.Execute($"CRTBNDRPG PGM(QGPL/MAIN) SRCSTMF('{root}')"); Assert.False(created.IsError, created.Message);
            Assert.Contains("stream", commands.Execute("CALL QGPL/MAIN").Message);
            File.WriteAllBytes(Path.Combine(directory, "part file.rpgle"), new byte[] { 0xFF });
            var invalid = commands.Execute($"CRTBNDRPG PGM(QGPL/BAD) SRCSTMF('{root}')"); Assert.True(invalid.IsError); Assert.Contains(root + ":2:1", invalid.Message); Assert.False(system.Objects.Exists("QGPL", "BAD", ObjectType.Program));
            File.WriteAllBytes(root, new byte[] { 0xFF });
            Assert.True(commands.Execute($"CRTBNDRPG PGM(QGPL/BAD) SRCSTMF('{root}')").IsError);
            using (OperationIdentity.Enter("QUSER"))
                Assert.Contains("CPF9802", commands.Execute($"CRTBNDRPG PGM(QGPL/BAD) SRCSTMF('{root}')").Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Compiled_calls_survive_source_authority_revocation_but_recompilation_requires_live_authority()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC"); files.CreateSourceFile("QGPL", "PRIVATE");
        files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\n/COPY QGPL/PRIVATE,PART", null);
        files.SaveSourceMember("QGPL", "PRIVATE", "PART", "sndpgmmsg 'snapshot';", null);
        var created = new CommandService(system).Execute("CRTBNDRPG PGM(QGPL/MAIN)"); Assert.False(created.IsError, created.Message);
        system.Security.Authority.Grant("QGPL", "MAIN", ObjectType.Program, "QUSER", Authorities.UseBits);
        system.Security.Authority.Grant("QGPL", "PRIVATE", ObjectType.File, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER"))
        {
            var commands = new CommandService(system);
            Assert.Contains("snapshot", commands.Execute("CALL QGPL/MAIN").Message);
            var result = commands.Execute("CRTBNDRPG PGM(QGPL/NEW) SRCMBR(MAIN)");
            Assert.True(result.IsError); Assert.Contains("CPF9802", result.Message);
        }
        Assert.False(system.Objects.Exists("QGPL", "NEW", ObjectType.Program));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stale_or_invalid_compiled_maps_are_reported_as_command_failures(bool staleText)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC"); files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\nsndpgmmsg 'snapshot';", null);
        var commands = new CommandService(system); Assert.False(commands.Execute("CRTBNDRPG PGM(QGPL/MAIN)").IsError);
        var descriptor = system.Objects.GetRequired("QGPL", "MAIN", ObjectType.Program);
        if (staleText) descriptor.Source += "\nsndpgmmsg 'changed';";
        else descriptor.ExtendedAttributes = new Dictionary<string, string> { [CompiledSourceMap.AttributeName] = "{}" };
        system.Objects.Update(descriptor);
        var called = commands.Execute("CALL QGPL/MAIN"); Assert.True(called.IsError); Assert.Contains("Invalid or stale compiled source map", called.Message);
    }

    [Fact]
    public void Compiled_runtime_failures_report_original_included_member_after_source_deletion()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC"); files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\n/COPY PART", null);
        files.SaveSourceMember("QGPL", "QRPGLESRC", "PART", "\nexsr MISSING;", null);
        var commands = new CommandService(system); Assert.False(commands.Execute("CRTBNDRPG PGM(QGPL/MAIN)").IsError);
        files.DeleteFile("QGPL", "QRPGLESRC");
        var called = commands.Execute("CALL QGPL/MAIN"); Assert.True(called.IsError);
        Assert.Contains("QGPL/QRPGLESRC(PART):2:1", called.Message); Assert.Contains("QGPL/QRPGLESRC(MAIN):2:1", called.Message);
    }

    [Fact]
    public void Native_failures_keep_the_CPF_identity_and_included_source_location()
    {
        var program = Compile("**free\n/COPY PART", (_, _) => new("PART", "\n  callp PRIVATE();"));
        var host = new RpgHost { ProgramCaller = (_, _, _) => throw new CpfException("CPF9802", "Denied", IpcMessageType.Diagnostic, 40) };
        var error = Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program));
        Assert.Equal("CPF9802", error.MessageId); Assert.Contains("PART:2:3", error.Message); Assert.Contains("ROOT:2:1", error.Message);
        Assert.Equal(IpcMessageType.Diagnostic, error.MessageType); Assert.Equal(40, error.Severity);
    }

    [Fact]
    public void Copy_members_can_contribute_fixed_declarations_and_executable_statements()
    {
        var field = new string(' ', 80).ToCharArray(); field[6] = 'D'; "TEXT".CopyTo(0, field, 7, 4); field[21] = 'A'; field[38] = '8';
        var program = Compile("/COPY DECLS\n/FREE\neval TEXT = 'shared';\n/INCLUDE BODY\n/END-FREE",
            (_, name) => name == "DECLS" ? new("DECLS", new string(field)) : new("BODY", "sndpgmmsg TEXT;"));
        var messages = new List<string>(); new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal("shared  ", Assert.Single(messages)); Assert.Equal("BODY", program.MainStatements.Last().Location!.Source);
    }

    [Fact]
    public void A_program_containing_only_listing_directives_compiles_and_calls_as_an_empty_program()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC"); files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "/TITLE Empty program\n/EJECT\n/SPACE", null);
        var commands = new CommandService(system); var created = commands.Execute("CRTBNDRPG PGM(QGPL/MAIN)"); Assert.False(created.IsError, created.Message);
        Assert.Equal("", system.Objects.GetRequired("QGPL", "MAIN", ObjectType.Program).Source);
        var called = commands.Execute("CALL QGPL/MAIN"); Assert.False(called.IsError, called.Message); Assert.Contains("ended normally", called.Message);
    }
}
