using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Compilation;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgBindingControlTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly BindingDirectoryStore _directories;
    private const string MainSource = "dcl-pr NEXT int(10);\nend-pr;\ndcl-s N int(10);\neval N=NEXT();\nsndpgmmsg %char(N);\nreturn;";
    private static string Provider(int value) => $"**free\nctl-opt nomain;\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\nreturn {value};\nend-proc;";
    public RpgBindingControlTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ControlFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240); _directories = new(_system.Connections);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private void Module(string name, string source)
    { Save(name, source); Ok(Command($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)")); }
    private void Directory(string name, string module)
    { _directories.Create("QGPL", name); _directories.Add("QGPL", name, [new("QGPL", module, ObjectType.Module)]); }
    private static RpgProgram Parse(string text) => RpgCompiler.CompileModule("QGPL", "CONTROL", new SourceDocument("QGPL/SOURCE(CONTROL)", text));
    [Fact]
    public void Free_controls_capture_literal_directories_with_other_global_controls()
    {
        var program = Parse("**free\nctl-opt nomain alwnull(*usrctl) bnddir('first':'QGPL/second');\ndcl-s N int(10);");
        Assert.Equal(["FIRST", "QGPL/SECOND"], program.BindingDirectories); Assert.True(program.NoMain);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Traditional_H_controls_support_both_existing_column_origins(bool shifted)
    {
        var source = (shifted ? "      H" : "     H") + " BNDDIR('QGPL/FIRST') NOMAIN\n";
        Assert.Equal(["QGPL/FIRST"], Parse(source).BindingDirectories);
    }
    [Theory]
    [InlineData("BNDDIR")] [InlineData("BNDDIR()")]
    [InlineData("BNDDIR(FIRST)")] [InlineData("BNDDIR('QGPL/BAD-NAME')")]
    [InlineData("BNDDIR('A/B/C')")] [InlineData("BNDDIR('*NONE')")]
    [InlineData("BNDDIR('FIRST') BNDDIR('SECOND')")]
    [InlineData("BNDDIR('FIRST' 'SECOND')")]
    public void Invalid_source_directory_controls_reject_at_the_original_line(string control)
    {
        var error = Assert.Throws<RpgCompileException>(() => Parse("**free\nctl-opt " + control + ";\nreturn;")); Assert.Contains("2", error.Message);
    }
    [Fact]
    public void Controls_must_precede_declarations_and_cannot_appear_inside_procedures()
    {
        Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-s N int(10);\nctl-opt bnddir('FIRST');\nreturn;"));
        Assert.Throws<RpgCompileException>(() => Parse("**free\nreturn;\ndcl-proc P;\ndcl-pi *n end-pi;\nctl-opt bnddir('FIRST');\nend-proc;"));
    }
    [Fact]
    public void Source_directory_counts_are_bounded()
        => Assert.Throws<RpgCompileException>(() => Parse("**free\nctl-opt bnddir(" + string.Join(':', Enumerable.Range(1, 17).Select(i => "'DIR" + i + "'")) + ");\nreturn;"));
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CRTBNDRPG_discovers_embedded_directories_and_captures_selected_modules(int ccsid)
    {
        Module("HELPER", Provider(7)); Directory("BINDINGS", "HELPER"); Save("CALLER", "**free\nctl-opt bnddir('BINDINGS');\n" + MainSource);
        Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)"));
        var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!); Assert.Equal(2, image.Modules.Count);
        _system.Objects.Delete("QGPL", "BINDINGS", ObjectType.BindingDirectory); _system.Objects.Delete("QGPL", "HELPER", ObjectType.Module);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("7", result.Message?.Trim());
    }
    [Fact]
    public void Command_directories_are_searched_before_embedded_directories_without_overriding_them()
    {
        Module("FIRST", Provider(11)); Module("SECOND", Provider(22)); Directory("SOURCEBD", "FIRST"); Directory("COMMANDBD", "SECOND");
        Save("CALLER", "**free\nctl-opt bnddir('SOURCEBD');\n" + MainSource);
        Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) BNDDIR(COMMANDBD)"));
        var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("22", result.Message?.Trim());
    }
    [Fact]
    public void Module_reference_directories_are_deferred_until_binding_and_resolve_transitive_imports()
    {
        Module("HELPER", Provider(7));
        Module("WRAPPER", "**free\nctl-opt nomain bnddir('LEAFDIR');\ndcl-pr NEXT int(10);\nend-pr;\ndcl-proc OUTER export;\ndcl-pi *n int(10) end-pi;\nreturn NEXT()+1;\nend-proc;");
        Module("MAIN", "**free\nctl-opt bnddir('WRAPDIR');\n" + MainSource.Replace("NEXT", "OUTER"));
        Directory("LEAFDIR", "HELPER"); Directory("WRAPDIR", "WRAPPER");
        var display = Command("DSPMOD MODULE(WRAPPER)"); Ok(display); Assert.Contains("directories: LEAFDIR", display.Message);
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN)")); var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("8", result.Message?.Trim());
    }
    [Fact]
    public void Includes_and_multiline_controls_preserve_source_maps_and_directory_metadata()
    {
        Module("HELPER", Provider(7)); Directory("BINDINGS", "HELPER"); Save("OPTIONS", "ctl-opt\n bnddir('BINDINGS');");
        Save("CALLER", "**free\n/include QGPL/SOURCE,OPTIONS\n" + MainSource);
        Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)")); _files.RemoveMember("QGPL", "SOURCE", "OPTIONS");
        var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("7", result.Message?.Trim());
    }
    [Fact]
    public void Resolved_imports_do_not_open_unused_module_reference_directories()
    {
        Save("CALLER", "**free\nctl-opt bnddir('MISSING');\nsndpgmmsg 'LOCAL';\nreturn;");
        Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)")); var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("LOCAL", result.Message?.Trim());
    }
    public void Dispose() => _system.Dispose();
}
