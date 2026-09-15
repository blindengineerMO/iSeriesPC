using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Console.Session;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed class ClPreprocessingTests
{
    [Fact]
    public void Nested_includes_and_conditions_preserve_original_lines_and_compile_to_executable_statements()
    {
        var documents = new Dictionary<string, string> {
            ["MAIN"] = "PGM\n\n/DEFINE ACTIVE\n/IF DEFINED(ACTIVE)\n/INCLUDE FIRST\n/ELSE\nINVALID INACTIVE SYNTAX(\n/ENDIF\nENDPGM",
            ["FIRST"] = "/* ignored /INCLUDE MISSING */\n/INCLUDE SECOND\n/IF NOT DEFINED(MISSING)\nSNDPGMMSG MSG('done')\n/ENDIF",
            ["SECOND"] = "\nSNDPGMMSG MSG('nested')"
        };
        var program = new ClCompiler().Compile("MAIN", "QGPL", new SourceDocument("MAIN", documents["MAIN"]), (_, reference) => new(reference, documents[reference]));
        var nested = program.Statements.Single(s => s.Value == "nested");
        Assert.Equal("SECOND", nested.Location!.Source); Assert.Equal(2, nested.Location.Line);
        Assert.Equal(new[] { "MAIN:5:1", "FIRST:2:1" }, nested.Location.IncludeStack);
        var messages = new List<string>(); var runtime = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add);
        Assert.False(runtime.Run(program).IsError); Assert.Equal(new[] { "nested", "done" }, messages);
        Assert.Equal(3, program.Source!.Sources.Count);
        var restored = CompiledSourceMap.Restore(program.Source.Text, CompiledSourceMap.Serialize(program.Source));
        Assert.Equal(documents["FIRST"], restored.Sources["FIRST"]);
    }

    [Theory]
    [InlineData("/ELSE", "Unmatched")]
    [InlineData("/ENDIF", "Unmatched")]
    [InlineData("/IF DEFINED(A)\n/ELSE\n/ELSE\n/ENDIF", "repeated")]
    [InlineData("/IF DEFINED(A)", "Unclosed")]
    [InlineData("/IF UNKNOWN\n/ENDIF", "Undefined")]
    [InlineData("/IF DEFINED(A) OR DEFINED(B)\n/ENDIF", "requires")]
    [InlineData("/DEFINE BAD-VALUE", "requires")]
    [InlineData("/COPY OTHER", "RPG directives")]
    [InlineData("/TITLE Other", "RPG directives")]
    [InlineData("/INCLUDE 'bad", "Unclosed")]
    public void Malformed_or_RPG_only_directives_fail_with_source_locations(string source, string diagnostic)
    {
        var error = Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("BAD", "QGPL", new SourceDocument("QGPL/QCLSRC(BAD)", source)));
        Assert.Contains("QGPL/QCLSRC(BAD):", error.Message); Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public void Inactive_includes_and_comment_text_do_not_access_sources()
    {
        var source = "PGM\n/* multiline comment\n/INCLUDE UNREADABLE\n*/\n/IF DEFINED(ABSENT)\n/INCLUDE UNREADABLE\n/ENDIF\nSNDPGMMSG MSG('/* literal */')\nENDPGM";
        var result = new ClCompiler().Compile("MAIN", "QGPL", new SourceDocument("MAIN", source), (_, _) => throw new InvalidOperationException("Should not resolve"));
        Assert.Contains(result.Statements, s => s.Value == "/* literal */");
    }

    [Fact]
    public void Cycles_unbalanced_include_scope_limits_and_cancellation_are_rejected()
    {
        var root = new SourceDocument("ROOT", "/INCLUDE SELF");
        Assert.Contains("cycle", Assert.Throws<SourcePreprocessException>(() => new SourcePreprocessor((_, _) => root).Expand(root)).Message);
        Assert.Contains("Unclosed", Assert.Throws<SourcePreprocessException>(() => new SourcePreprocessor((_, _) => new("INCLUDE", "/IF DEFINED(A)")).Expand(root)).Message);
        Assert.Throws<SourcePreprocessException>(() => new SourcePreprocessor().Expand(new("BIG", new string('x', 1048577))));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SourcePreprocessor().Expand(root, cancellation.Token));
        var next = 0;
        Assert.Contains("depth", Assert.Throws<SourcePreprocessException>(() => new SourcePreprocessor((_, _) => new("INC" + ++next, "/INCLUDE NEXT")).Expand(root)).Message);
    }

    [Fact]
    public void Compiler_and_runtime_failures_identify_the_included_member()
    {
        var root = new SourceDocument("ROOT", "PGM\n/INCLUDE BAD\nENDPGM");
        var syntax = Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("MAIN", "QGPL", root, (_, _) => new("BAD", "\nSNDPGMMSG MSG('unterminated)")));
        Assert.Contains("BAD:2:1", syntax.Message); Assert.Contains("ROOT:2:1", syntax.Message);
        var block = Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("MAIN", "QGPL", root, (_, _) => new("BAD", "IF COND(1)")));
        Assert.Contains("BAD:1:1", block.Message);
        var program = new ClCompiler().Compile("MAIN", "QGPL", root, (_, _) => new("BAD", "MISSINGCMD"));
        var runtime = new ClInterpreter((_, _) => null, _ => CommandResult.Error("IPC0001: unavailable"));
        var result = runtime.Run(program); Assert.True(result.IsError); Assert.Contains("BAD:1:1", result.Message);
    }

    [Fact]
    public void Source_map_rejects_tampered_text_duplicate_properties_and_invalid_locations()
    {
        var expanded = new SourcePreprocessor().Expand(new("ROOT", "PGM\nENDPGM"));
        var map = CompiledSourceMap.Serialize(expanded);
        Assert.Throws<InvalidDataException>(() => CompiledSourceMap.Restore(expanded.Text + "\nBAD", map));
        Assert.Throws<InvalidDataException>(() => CompiledSourceMap.Restore(expanded.Text, map.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => CompiledSourceMap.Restore(expanded.Text, map.Replace("\"line\":1", "\"line\":999", StringComparison.Ordinal)));
    }

    [Fact]
    public void CRTCLPGM_snapshots_includes_and_survives_source_change_copy_and_runtime_errors()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QCLSRC"); files.SaveSourceMember("QGPL", "QCLSRC", "MAIN", "PGM\n/DEFINE RUN\n/IF DEFINED(RUN)\n/INCLUDE PART\n/ENDIF\nENDPGM", null);
        files.SaveSourceMember("QGPL", "QCLSRC", "PART", "SNDPGMMSG MSG('original include')", null);
        var commands = new CommandService(system);
        var created = commands.Execute("CRTCLPGM PGM(QGPL/MAIN) SRCFILE(QGPL/QCLSRC)"); Assert.False(created.IsError, created.Message);
        var descriptor = system.Objects.GetRequired("QGPL", "MAIN", ObjectType.Program);
        Assert.DoesNotContain("/INCLUDE", descriptor.Source!); Assert.True(descriptor.ExtendedAttributes!.ContainsKey(CompiledSourceMap.AttributeName));
        var old = files.ReadSourceMember("QGPL", "QCLSRC", "PART"); files.SaveSourceMember("QGPL", "QCLSRC", "PART", "SNDPGMMSG MSG('changed include')", old.Revision);
        system.ObjectOperations.Relocate(new("QGPL", "MAIN"), ObjectType.Program, new("QGPL", "COPY"), copy: true);
        files.DeleteFile("QGPL", "QCLSRC");
        var called = commands.Execute("CALL PGM(QGPL/COPY)"); Assert.False(called.IsError, called.Message); Assert.Contains("original include", called.Message); Assert.DoesNotContain("changed", called.Message);
    }

    [Fact]
    public void Unauthorized_include_does_not_create_a_program()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QCLSRC"); files.CreateSourceFile("QGPL", "PRIVATE");
        files.SaveSourceMember("QGPL", "QCLSRC", "MAIN", "PGM\n/INCLUDE QGPL/PRIVATE(PART)\nENDPGM", null);
        files.SaveSourceMember("QGPL", "PRIVATE", "PART", "SNDPGMMSG MSG('private')", null);
        system.Security.Authority.Grant("QGPL", "PRIVATE", ObjectType.File, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER"))
        {
            var result = new CommandService(system).Execute("CRTCLPGM PGM(QGPL/MAIN) SRCFILE(QGPL/QCLSRC)");
            Assert.True(result.IsError); Assert.Contains("CPF9802", result.Message); Assert.Contains("QGPL/QCLSRC(MAIN):2:1", result.Message);
        }
        Assert.False(system.Objects.Exists("QGPL", "MAIN", ObjectType.Program));
    }

    [Fact]
    public void Repeated_includes_use_one_source_snapshot_and_expansion_is_bounded()
    {
        var reads = 0;
        var root = new SourceDocument("ROOT", "PGM\n/INCLUDE PART\n/INCLUDE PART\nENDPGM");
        var program = new ClCompiler().Compile("MAIN", "QGPL", root, (_, _) => new("PART", ++reads == 1 ? "SNDPGMMSG MSG('first')" : "SNDPGMMSG MSG('changed')"));
        Assert.Equal(2, program.Statements.Count(s => s.Value == "first"));
        var many = new SourceDocument("ROOT", string.Join('\n', Enumerable.Repeat("/INCLUDE PART", 60)));
        Assert.Contains("bounded", Assert.Throws<SourcePreprocessException>(() => new SourcePreprocessor((_, _) => new("PART", new string('\n', 999))).Expand(many)).Message);
    }

    [Fact]
    public void Relative_UTF8_stream_includes_preserve_paths_and_reject_FIFO_sources()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-include-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var system = IpcSystem.Create(":memory:"); system.Start();
            var path = Path.Combine(directory, "main.cl"); File.WriteAllText(path, "PGM\n/INCLUDE 'part file.cl'\nENDPGM");
            File.WriteAllText(Path.Combine(directory, "part file.cl"), "SNDPGMMSG MSG('stream include')");
            var resolver = new Ipc.Session.Sources.ProgramSourceResolver(system);
            var compiled = new ClCompiler().Compile("MAIN", "QGPL", resolver.Stream(path), resolver.Resolve);
            Assert.Contains(compiled.Statements, s => s.Location!.Source.EndsWith("part file.cl", StringComparison.Ordinal));
            File.WriteAllBytes(path, new byte[] { 0xFF });
            Assert.Throws<global::System.Text.DecoderFallbackException>(() => resolver.Stream(path));
            if (OperatingSystem.IsLinux())
            {
                var pipe = Path.Combine(directory, "fifo");
                var start = new global::System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false }; start.ArgumentList.Add(pipe);
                using var process = global::System.Diagnostics.Process.Start(start)!; Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
                Assert.Throws<InvalidDataException>(() => resolver.Stream(pipe));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
