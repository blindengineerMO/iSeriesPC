using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Core.Compilation;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgBindingDirectoryTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly BindingDirectoryStore _directories;
    private static string MainSource(string name = "NEXT") => $"**free\ndcl-pr {name} int(10);\nend-pr;\ndcl-s N int(10);\neval N={name}();\nsndpgmmsg %char(N);\nreturn;";
    private static string Provider(string name = "NEXT", int value = 7) => $"**free\nctl-opt nomain;\ndcl-proc {name} export;\ndcl-pi *n int(10) end-pi;\nreturn {value};\nend-proc;";
    private static string Wrapper => "**free\nctl-opt nomain;\ndcl-pr NEXT int(10);\nend-pr;\ndcl-proc OUTER export;\ndcl-pi *n int(10) end-pi;\nreturn NEXT()+1;\nend-proc;";
    public RpgBindingDirectoryTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "BindingFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240); _directories = new(_system.Connections);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private void Module(string name, string source, string library = "QGPL")
    { Save(name, source); Ok(Command($"CRTRPGMOD MODULE({library}/{name}) SRCFILE(QGPL/SOURCE)")); }
    private void Service(string name, string source)
    {
        var module = "M" + name; Module(module, source); Ok(Command($"CRTSRVPGM SRVPGM(QGPL/{name}) MODULE({module}) EXPORT(*ALL)"));
    }
    private void Directory(string name, params BindingDirectoryEntry[] entries)
    { _directories.Create("QGPL", name); if (entries.Length > 0) _directories.Add("QGPL", name, entries); }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, string program, int expected)
    { var result = session.Execute("CALL QGPL/" + program); Ok(result); Assert.Equal(expected.ToString(), result.Message?.Trim()); }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Directory_modules_are_selected_and_captured_in_both_program_creation_paths(int ccsid, bool boundSource)
    {
        Module("MAIN", MainSource()); Module("HELPER", Provider()); Directory("BINDINGS", new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module));
        Ok(Command(boundSource ? "CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) SRCMBR(MAIN) BNDDIR(BINDINGS) ACTGRP(CALLERS)" : "CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDDIR(BINDINGS) ACTGRP(CALLERS)"));
        var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!); Assert.Equal(2, image.Modules.Count); Assert.Empty(image.Services);
        _system.Objects.Delete("QGPL", "BINDINGS", ObjectType.BindingDirectory); _system.Objects.Delete("QGPL", "HELPER", ObjectType.Module);
        using var session = Session(ccsid); Count(session, "CALLER", 7);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Directory_services_remain_dynamic_references_after_directory_deletion(int ccsid)
    {
        Service("LEAF", Provider()); Module("MAIN", MainSource()); Directory("BINDINGS", new BindingDirectoryEntry("*LIBL", "LEAF", ObjectType.ServiceProgram));
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDDIR(BINDINGS) ACTGRP(CALLERS)"));
        var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!); Assert.Single(image.Modules); Assert.Equal("LEAF", Assert.Single(image.Services).Name);
        _system.Objects.Delete("QGPL", "BINDINGS", ObjectType.BindingDirectory); using var session = Session(ccsid); Count(session, "CALLER", 7);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Service_creation_can_discover_static_modules_or_dynamic_service_dependencies(bool service)
    {
        Module("WRAPPER", Wrapper);
        if (service) Service("LEAF", Provider()); else Module("HELPER", Provider());
        Directory("BINDINGS", new BindingDirectoryEntry("QGPL", service ? "LEAF" : "HELPER", service ? ObjectType.ServiceProgram : ObjectType.Module));
        Ok(Command("CRTSRVPGM SRVPGM(QGPL/PARENT) MODULE(WRAPPER) BNDDIR(BINDINGS) EXPORT(*ALL)"));
        var image = RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", "PARENT", ObjectType.ServiceProgram).Source!);
        Assert.Equal(service ? 2 : 1, image.Version); Assert.Equal(service ? 1 : 2, image.Modules.Count);
        Save("CALLER", MainSource("OUTER")); Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) BNDSRVPGM(PARENT)"));
        using var session = Session(); Count(session, "CALLER", 8);
    }
    [Fact]
    public void Discovery_revisits_earlier_providers_for_imports_introduced_by_selected_modules()
    {
        Module("MAIN", MainSource("OUTER")); Module("HELPER", Provider()); Module("WRAPPER", Wrapper);
        Directory("BINDINGS", new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module), new BindingDirectoryEntry("QGPL", "WRAPPER", ObjectType.Module));
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDDIR(BINDINGS)"));
        var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!); Assert.Equal(["MAIN", "WRAPPER", "HELPER"], image.Modules.Select(m => m.Name));
        using var session = Session(); Count(session, "CALLER", 8);
    }
    [Fact]
    public void Explicit_services_and_ordered_directories_preserve_provider_precedence()
    {
        Service("FIRST", Provider(value: 11)); Service("SECOND", Provider(value: 22)); Module("MAIN", MainSource());
        Directory("ONE", new BindingDirectoryEntry("QGPL", "SECOND", ObjectType.ServiceProgram)); Directory("TWO", new BindingDirectoryEntry("QGPL", "FIRST", ObjectType.ServiceProgram));
        Ok(Command("CRTPGM PGM(QGPL/DIRONLY) MODULE(MAIN) BNDDIR(ONE TWO)"));
        Ok(Command("CRTPGM PGM(QGPL/EXPLICIT) MODULE(MAIN) BNDSRVPGM(FIRST) BNDDIR(ONE TWO)"));
        using var session = Session(); Count(session, "DIRONLY", 22); Count(session, "EXPLICIT", 11);
    }
    [Fact]
    public void Missing_candidates_are_diagnostic_and_unused_trailing_candidates_are_not_opened()
    {
        Module("MAIN", MainSource()); Module("HELPER", Provider());
        Directory("BINDINGS", new BindingDirectoryEntry("QGPL", "MISSING", ObjectType.Module), new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module), new BindingDirectoryEntry("QGPL", "UNUSED", ObjectType.Module));
        var result = Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDDIR(BINDINGS)"); Ok(result); Assert.Contains("CPD5D1D", result.Message); Assert.DoesNotContain("UNUSED", result.Message);
        using var session = Session(); Count(session, "CALLER", 7);
    }
    [Fact]
    public void Duplicate_directory_and_resolved_object_entries_do_not_duplicate_bound_modules()
    {
        Module("MAIN", MainSource("OUTER")); Module("HELPER", Provider()); Module("WRAPPER", Wrapper);
        Directory("ONE", new BindingDirectoryEntry("*LIBL", "HELPER", ObjectType.Module), new BindingDirectoryEntry("QGPL", "WRAPPER", ObjectType.Module));
        Directory("TWO", new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module));
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDDIR(ONE ONE TWO)"));
        Assert.Equal(3, RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!).Modules.Count);
    }
    [Fact]
    public void Stored_library_list_entries_resolve_using_each_jobs_current_library_list()
    {
        Ok(Command("CRTLIB LIB(FIRSTLIB)")); Ok(Command("CRTLIB LIB(SECONDLIB)"));
        Module("PROVIDER", Provider(value: 11), "FIRSTLIB"); Module("PROVIDER", Provider(value: 22), "SECONDLIB"); Module("MAIN", MainSource());
        Directory("BINDINGS", new BindingDirectoryEntry("*LIBL", "PROVIDER", ObjectType.Module)); using var first = Session(); using var second = Session();
        Ok(first.Execute("CHGLIBL LIBL(FIRSTLIB QGPL)")); Ok(second.Execute("CHGLIBL LIBL(SECONDLIB QGPL)"));
        Ok(first.Execute("CRTPGM PGM(QGPL/ONE) MODULE(QGPL/MAIN) BNDDIR(QGPL/BINDINGS)")); Ok(second.Execute("CRTPGM PGM(QGPL/TWO) MODULE(QGPL/MAIN) BNDDIR(QGPL/BINDINGS)"));
        Count(first, "ONE", 11); Count(second, "TWO", 22);
    }
    [Fact]
    public void Directory_and_candidate_use_authority_are_required_at_bind_time()
    {
        Module("MAIN", MainSource()); Module("HELPER", Provider()); Directory("BINDINGS", new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module));
        _system.Security.Profiles.Create(new UserProfile { Name = "BINDUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("BINDUSER"), "BindingUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "BINDUSER", Authorities.AllBits); _system.Security.Authority.Grant("QGPL", "BINDINGS", ObjectType.BindingDirectory, "BINDUSER", AuthorityBit.None);
        using var session = Session(user: "BINDUSER"); Assert.True(session.Execute("CRTPGM PGM(QGPL/DENIED) MODULE(MAIN) BNDDIR(BINDINGS)").IsError);
        _system.Security.Authority.Grant("QGPL", "BINDINGS", ObjectType.BindingDirectory, "BINDUSER", Authorities.UseBits); _system.Security.Authority.Grant("QGPL", "HELPER", ObjectType.Module, "BINDUSER", AuthorityBit.None);
        Assert.True(session.Execute("CRTPGM PGM(QGPL/DENIED) MODULE(MAIN) BNDDIR(BINDINGS)").IsError); Assert.False(_system.Objects.Exists("QGPL", "DENIED", ObjectType.Program));
    }
    [Fact]
    public void Selected_directory_module_integrity_is_verified()
    {
        Module("MAIN", MainSource()); Module("HELPER", Provider()); Directory("BINDINGS", new BindingDirectoryEntry("QGPL", "HELPER", ObjectType.Module));
        var ca = _system.Certificates.CreateAuthority("DIRCA", "Directory CA"); var signer = _system.Certificates.Issue("DIRSIGN", "Directory signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "HELPER", ObjectType.Module, signer.Id);
        var changed = _system.Objects.GetRequired("QGPL", "HELPER", ObjectType.Module); changed.Source += " "; _system.Objects.Update(changed);
        var result = Command("CRTPGM PGM(QGPL/BAD) MODULE(MAIN) BNDDIR(BINDINGS)"); Assert.True(result.IsError); Assert.Contains("integrity", result.Message); Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.Program));
    }
    [Theory]
    [InlineData("*NONE")] [InlineData("'*NONE'")]
    public void None_directory_preserves_static_compilation(string value)
    {
        Save("CALLER", "**free\nsndpgmmsg 'STATIC';\nreturn;"); Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) BNDDIR(" + value + ")"));
        var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("STATIC", result.Message?.Trim());
    }
    [Fact]
    public void Too_many_directories_reject_before_program_creation()
    {
        Module("MAIN", MainSource()); var result = Command("CRTPGM PGM(QGPL/BAD) MODULE(MAIN) BNDDIR(" + string.Join(' ', Enumerable.Range(1, 17).Select(i => "DIR" + i)) + ")");
        Assert.True(result.IsError); Assert.Contains("16", result.Message); Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.Program));
    }
    [Fact]
    public void Legacy_semantic_procedure_nodes_do_not_hide_native_imports_or_bound_targets()
    {
        var main = RpgCompiler.CompileModule("QGPL", "MAIN", new SourceDocument("QGPL/SOURCE(MAIN)", MainSource()));
        main.Subprocedures.Add(new RpgSubprocedure { Name = "NEXT" });
        Assert.Contains("NEXT", RpgCompiler.UnresolvedModuleImports([main], []));
        var helper = RpgCompiler.CompileModule("QGPL", "HELPER", new SourceDocument("QGPL/SOURCE(HELPER)", Provider()));
        RpgCompiler.BindModules([main, helper], main); var context = new RpgInterpreter(new RpgHost()).Run(main); Assert.Equal(7L, context.ReadValue("N"));
    }
    public void Dispose() => _system.Dispose();
}
