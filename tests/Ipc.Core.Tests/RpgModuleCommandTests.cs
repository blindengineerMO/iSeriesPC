using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgModuleCommandTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Helper = "**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc BUMP export;\ndcl-pi *n;\nP int(10);\nend-pi;\neval COUNT=COUNT+1;\neval P=COUNT;\nend-proc;";
    private const string Main = "**free\ndcl-pr BUMP;\nP int(10);\nend-pr;\ndcl-s N int(10);\ncallp BUMP(N);\nsndpgmmsg %char(N);\nreturn;";
    public RpgModuleCommandTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ModuleFixture27");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Module(string name, string source)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", name, source, null); Ok(Command($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Seed(string main = Main) { Module("MAIN", main); Module("HELPER", Helper); }
    private void Bind(string name = "BOUND", string modules = "QGPL/MAIN QGPL/HELPER", string options = "") => Ok(Command($"CRTPGM PGM(QGPL/{name}) MODULE({modules}) ACTGRP(MODBIND) {options}"));
    private ExecutionSession Session(int ccsid = 37) => new(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Bound_programs_execute_captured_modules_after_source_and_module_deletion(int ccsid)
    {
        Seed(); Bind();
        foreach (var name in new[] { "MAIN", "HELPER" }) { _files.RemoveMember("QGPL", "SOURCE", name); _system.Objects.Delete("QGPL", name, ObjectType.Module); }
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/BOUND"); Ok(result); Assert.EndsWith("1", result.Message?.Trim());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Module_globals_survive_return_and_entry_LR_until_activation_reclaim(bool lr)
    {
        Seed(lr ? Main.Replace("return;", "seton LR;") : Main); Bind(); using var session = Session();
        Assert.EndsWith("1", session.Execute("CALL QGPL/BOUND").Message?.Trim()); Assert.EndsWith("2", session.Execute("CALL QGPL/BOUND").Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(MODBIND)")); Assert.EndsWith("1", session.Execute("CALL QGPL/BOUND").Message?.Trim());
    }
    [Fact]
    public void Programs_bound_from_the_same_entry_module_have_independent_activation_resources()
    {
        Seed(); Bind("FIRST"); Bind("SECOND"); using var session = Session();
        Assert.EndsWith("1", session.Execute("CALL QGPL/FIRST").Message?.Trim()); Assert.EndsWith("1", session.Execute("CALL QGPL/SECOND").Message?.Trim()); Assert.EndsWith("2", session.Execute("CALL QGPL/FIRST").Message?.Trim());
    }
    [Theory]
    [InlineData("*FIRST")] [InlineData("QGPL/MAIN")]
    public void Entry_selection_accepts_first_eligible_or_an_explicit_supplied_main_module(string entry)
    {
        Seed(); Bind(modules: "QGPL/HELPER QGPL/MAIN", options: "ENTMOD(" + entry + ")"); var result = Command("CALL QGPL/BOUND"); Ok(result); Assert.EndsWith("1", result.Message?.Trim());
    }
    [Theory]
    [InlineData("QGPL/HELPER")] [InlineData("QGPL/OTHER")]
    public void Invalid_entry_selection_does_not_publish_a_program(string entry)
    {
        Seed(); Module("OTHER", "**free\nreturn;"); var result = Command("CRTPGM PGM(QGPL/BOUND) MODULE(QGPL/MAIN QGPL/HELPER) ENTMOD(" + entry + ")");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "BOUND", ObjectType.Program));
    }
    [Fact]
    public void DSPMOD_lists_snapshot_imports_exports_and_entry_eligibility()
    {
        Seed(); var main = Command("DSPMOD MODULE(QGPL/MAIN)"); Ok(main); Assert.Contains("imports: BUMP", main.Message); Assert.Contains("main: yes", main.Message);
        var helper = Command("DSPMOD MODULE(QGPL/HELPER)"); Ok(helper); Assert.Contains("exports: BUMP", helper.Message); Assert.Contains("main: no", helper.Message);
    }
    [Theory]
    [InlineData("CRTRPGMOD")] [InlineData("CRTPGM")] [InlineData("DSPMOD")]
    public void Module_commands_report_missing_operands(string command) => Assert.True(Command(command).IsError);

    [Theory]
    [InlineData("version")] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("module-unknown")]
    [InlineData("source")] [InlineData("entry")] [InlineData("null-module")]
    [InlineData("case-property")] [InlineData("module-identity")] [InlineData("many-modules")]
    public void Malformed_bound_images_reject_before_execution(string defect)
    {
        Seed(); Bind(); var descriptor = _system.Objects.GetRequired("QGPL", "BOUND", ObjectType.Program); var root = JsonNode.Parse(descriptor.Source!)!.AsObject();
        switch (defect)
        {
            case "version": root["version"] = 2; break;
            case "case-property": root["Version"] = 1; break;
            case "module-identity": root["modules"]![0]!["name"] = "main"; root["entry"] = "QGPL/main"; break;
            case "many-modules": while (root["modules"]!.AsArray().Count < 17) root["modules"]!.AsArray().Add(root["modules"]![0]!.DeepClone()); break;
            case "unknown": root["extra"] = true; break;
            case "module-unknown": root["modules"]![0]!["extra"] = true; break;
            case "source": root["modules"]![0]!["source"] = "**free\nsndpgmmsg 'UNSAFE';"; break;
            case "entry": root["entry"] = "QGPL/HELPER"; break;
            case "null-module": root["modules"]![0] = null; break;
        }
        descriptor.Source = defect == "duplicate" ? root.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal) : root.ToJsonString();
        _system.Objects.Update(descriptor); var result = Command("CALL QGPL/BOUND"); Assert.True(result.IsError, result.Message); Assert.DoesNotContain("UNSAFE", result.Message);
    }
    [Fact]
    public void Module_authority_is_required_at_bind_time_and_captured_code_uses_program_authority_at_call_time()
    {
        Seed(); Bind(); _system.Security.Profiles.Create(new UserProfile { Name = "MODUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("MODUSER"), "ModuleUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "MODUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "HELPER", ObjectType.Module, "MODUSER", AuthorityBit.None);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("MODUSER"), CancellationToken.None);
        Assert.True(session.Execute("CRTPGM PGM(QGPL/DENIED) MODULE(QGPL/MAIN QGPL/HELPER)").IsError); Assert.False(_system.Objects.Exists("QGPL", "DENIED", ObjectType.Program));
        var result = session.Execute("CALL QGPL/BOUND"); Ok(result); Assert.EndsWith("1", result.Message?.Trim());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Module_and_bound_program_integrity_is_checked_before_binding_or_execution(bool program)
    {
        Seed(); Bind(); var ca = _system.Certificates.CreateAuthority("MODCA", "Module signing CA"); var signer = _system.Certificates.Issue("MODSIGN", "Module signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var name = program ? "BOUND" : "HELPER"; var type = program ? ObjectType.Program : ObjectType.Module;
        _system.ObjectSigning.Sign("QGPL", name, type, signer.Id); var changed = _system.Objects.GetRequired("QGPL", name, type); changed.Source += " "; _system.Objects.Update(changed);
        var result = Command(program ? "CALL QGPL/BOUND" : "CRTPGM PGM(QGPL/ALTERED) MODULE(QGPL/MAIN QGPL/HELPER)");
        Assert.True(result.IsError); Assert.Contains("integrity", result.Message, StringComparison.OrdinalIgnoreCase); Assert.False(_system.Objects.Exists("QGPL", "ALTERED", ObjectType.Program));
    }
    [Fact]
    public void Unqualified_module_and_program_creation_use_the_jobs_current_library()
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "CURMOD", "**free\nsndpgmmsg 'CURRENT MODULE';\nreturn;", null);
        using var session = Session(); Ok(session.Execute("CRTLIB LIB(MODLIB)")); Ok(session.Execute("CHGCURLIB CURLIB(MODLIB)"));
        Ok(session.Execute("CRTRPGMOD MODULE(CURMOD) SRCFILE(QGPL/SOURCE)")); Assert.True(_system.Objects.Exists("MODLIB", "CURMOD", ObjectType.Module));
        Ok(session.Execute("CRTPGM PGM(CURPGM) MODULE(MODLIB/CURMOD)")); Assert.True(_system.Objects.Exists("MODLIB", "CURPGM", ObjectType.Program));
        var result = session.Execute("CALL MODLIB/CURPGM"); Ok(result); Assert.Contains("CURRENT MODULE", result.Message);
    }
    [Fact]
    public void QTEMP_module_and_bound_program_objects_remain_private_to_their_job()
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "MAIN", Main, null); _files.SaveSourceMember("QGPL", "SOURCE", "HELPER", Helper, null);
        using var first = Session(); using var second = Session();
        Ok(first.Execute("CRTRPGMOD MODULE(QTEMP/MMAIN) SRCFILE(QGPL/SOURCE) SRCMBR(MAIN)"));
        Ok(first.Execute("CRTRPGMOD MODULE(QTEMP/MHELP) SRCFILE(QGPL/SOURCE) SRCMBR(HELPER)"));
        Ok(first.Execute("CRTPGM PGM(QTEMP/BOUND) MODULE(QTEMP/MMAIN QTEMP/MHELP)"));
        var result = first.Execute("CALL QTEMP/BOUND"); Ok(result); Assert.EndsWith("1", result.Message?.Trim());
        Assert.True(second.Execute("DSPMOD MODULE(QTEMP/MHELP)").IsError); Assert.True(second.Execute("CALL QTEMP/BOUND").IsError);
    }
    public void Dispose() => _system.Dispose();
}
