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

public sealed class RpgServiceCommandTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Helper = "**free\nctl-opt nomain;\ndcl-proc FIRST export;\ndcl-pi *n;\nend-pi;\nend-proc;\ndcl-proc SECOND export;\ndcl-pi *n;\nend-pi;\nend-proc;\ndcl-proc PRIVATE;\ndcl-pi *n;\nend-pi;\nend-proc;";
    private const string Binder = "STRPGMEXP SIGNATURE('API V2')\nEXPORT SYMBOL(FIRST)\nEXPORT SYMBOL(SECOND)\nENDPGMEXP\nSTRPGMEXP PGMLVL(*PRV) SIGNATURE('API V1')\nEXPORT SYMBOL(FIRST)\nENDPGMEXP";
    public RpgServiceCommandTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ServiceFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.CreateSourceFile("QGPL", "QSRVSRC", sourceWidth: 240);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Module(string name = "HELPER", string source = Helper)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", name, source, null); Ok(Command($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Create(string options = "EXPORT(*ALL)", string name = "SERVICE", string modules = "QGPL/HELPER")
        => Ok(Command($"CRTSRVPGM SRVPGM(QGPL/{name}) MODULE({modules}) {options}"));
    private RpgServiceImage Image(string name = "SERVICE") => RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", name, ObjectType.ServiceProgram).Source!);
    private ExecutionSession Session(string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user), CancellationToken.None);

    [Fact]
    public void Export_all_captures_only_exported_native_procedures_in_module_order()
    {
        Module(); Module("OTHER", Helper.Replace("FIRST", "THIRD").Replace("SECOND", "FOURTH"));
        Create(modules: "QGPL/OTHER QGPL/HELPER"); var image = Image();
        Assert.Null(image.BinderSource); Assert.Equal(["THIRD", "FOURTH", "FIRST", "SECOND"], image.Levels[0].Exports);
        var definition = image.Restore(); Assert.Equal(4, definition.Procedures.Count); Assert.All(definition.Modules, m => Assert.False(m.IsBoundProgram));
        var result = Command("DSPSRVPGM SRVPGM(QGPL/SERVICE)"); Ok(result); Assert.Contains(image.Levels[0].Signature, result.Message);
        Assert.Contains("QGPL/OTHER, QGPL/HELPER", result.Message); Assert.Contains("ACTGRP: *CALLER", result.Message);
    }
    [Fact]
    public void Default_binder_file_and_member_capture_current_and_previous_levels_after_sources_are_removed()
    {
        Module(); _files.SaveSourceMember("QGPL", "QSRVSRC", "SERVICE", Binder, null); Create(options: "ACTGRP(SHARED)");
        _files.RemoveMember("QGPL", "SOURCE", "HELPER"); _files.RemoveMember("QGPL", "QSRVSRC", "SERVICE"); _system.Objects.Delete("QGPL", "HELPER", ObjectType.Module);
        var image = Image(); Assert.Equal("QGPL/QSRVSRC(SERVICE)", image.BinderSource!.Identity); Assert.Equal(2, image.Restore().Levels.Count);
        var result = Command("DSPSRVPGM SRVPGM(SERVICE)"); Ok(result); Assert.Contains("*PRV=", result.Message); Assert.Contains("ACTGRP: SHARED", result.Message);
    }
    [Fact]
    public void Binder_source_can_hide_and_reorder_exports_without_executing_module_main()
    {
        Module(source: Helper.Replace("ctl-opt nomain;", "sndpgmmsg 'MUST NOT EXECUTE';\nreturn;"));
        _files.SaveSourceMember("QGPL", "SOURCE", "API", "STRPGMEXP\nEXPORT SYMBOL(SECOND)\nENDPGMEXP", null);
        Create("SRCFILE(QGPL/SOURCE) SRCMBR(API)"); var definition = Image().Restore();
        Assert.Equal(["SECOND"], definition.Procedures.Keys); Assert.DoesNotContain("MUST NOT EXECUTE", Command("DSPSRVPGM SRVPGM(SERVICE)").Message);
    }
    [Fact]
    public void Strict_UTF8_stream_binder_source_is_saved_and_does_not_need_the_original_file()
    {
        Module(); var path = Path.Combine(Path.GetTempPath(), "ipc-binder-" + Guid.NewGuid().ToString("N") + ".bnd");
        try
        {
            File.WriteAllText(path, Binder); Create("SRCSTMF('" + path + "')"); File.Delete(path);
            Assert.Equal(path, Image().BinderSource!.Identity); Ok(Command("DSPSRVPGM SRVPGM(SERVICE)"));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("MISSING")] [InlineData("PRIVATE")] [InlineData("first")]
    public void Binder_symbols_must_resolve_to_case_sensitive_exported_module_procedures(string symbol)
    {
        Module(); _files.SaveSourceMember("QGPL", "QSRVSRC", "SERVICE", $"STRPGMEXP\nEXPORT SYMBOL('{symbol}')\nENDPGMEXP", null);
        Assert.True(Command("CRTSRVPGM SRVPGM(SERVICE) MODULE(HELPER)").IsError); Assert.False(_system.Objects.Exists("QGPL", "SERVICE", ObjectType.ServiceProgram));
    }
    [Theory]
    [InlineData("ACTGRP(*NEW)")] [InlineData("EXPORT(*INVALID)")]
    [InlineData("EXPORT(*ALL) SRCFILE(QGPL/QSRVSRC)")] [InlineData("EXPORT(*ALL) SRCMBR(SERVICE)")]
    [InlineData("EXPORT(*ALL) SRCSTMF('/tmp/unused.bnd')")]
    [InlineData("SRCSTMF('/tmp/unused.bnd') SRCFILE(QGPL/QSRVSRC)")]
    [InlineData("SRCSTMF('/tmp/unused.bnd') SRCMBR(SERVICE)")]
    [InlineData("BNDSRVPGM(QGPL/OTHER)")] [InlineData("REPLACE(*YES)")]
    public void Invalid_or_unsupported_options_do_not_create_a_service(string options)
    {
        Module(); Assert.True(Command("CRTSRVPGM SRVPGM(SERVICE) MODULE(HELPER) " + options).IsError);
        Assert.False(_system.Objects.Exists("QGPL", "SERVICE", ObjectType.ServiceProgram));
    }
    [Theory]
    [InlineData("CRTSRVPGM")] [InlineData("CRTSRVPGM SRVPGM(SERVICE)")]
    [InlineData("CRTSRVPGM MODULE(HELPER)")] [InlineData("DSPSRVPGM")]
    [InlineData("DSPSRVPGM SRVPGM(MISSING)")]
    public void Required_operands_and_missing_objects_report_errors(string command) => Assert.True(Command(command).IsError);
    [Fact]
    public void Duplicate_modules_duplicate_exports_unresolved_imports_and_empty_exports_reject_before_creation()
    {
        Module(); Assert.True(Command("CRTSRVPGM SRVPGM(DUPMOD) MODULE(HELPER HELPER) EXPORT(*ALL)").IsError);
        Module("OTHER"); Assert.True(Command("CRTSRVPGM SRVPGM(DUPEXP) MODULE(HELPER OTHER) EXPORT(*ALL)").IsError);
        Module("IMPORTER", "**free\nctl-opt nomain;\ndcl-pr MISSING;\nend-pr;");
        Assert.True(Command("CRTSRVPGM SRVPGM(IMPORTS) MODULE(IMPORTER) EXPORT(*ALL)").IsError);
        Module("EMPTY", "**free\nctl-opt nomain;\ndcl-s N int(10);");
        Assert.True(Command("CRTSRVPGM SRVPGM(NOEXPORT) MODULE(EMPTY) EXPORT(*ALL)").IsError);
        Assert.All(new[] { "DUPMOD", "DUPEXP", "IMPORTS", "NOEXPORT" }, n => Assert.False(_system.Objects.Exists("QGPL", n, ObjectType.ServiceProgram)));
    }
    [Fact]
    public void Existing_service_images_remain_intact_when_creation_is_repeated()
    {
        Module(); Create(); var before = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram).Source;
        Assert.True(Command("CRTSRVPGM SRVPGM(SERVICE) MODULE(HELPER) EXPORT(*ALL)").IsError);
        Assert.Equal(before, _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram).Source);
    }
    [Theory]
    [InlineData("version")] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("case")]
    [InlineData("module-unknown")] [InlineData("null-module")] [InlineData("many-modules")] [InlineData("module-case")]
    [InlineData("source")] [InlineData("null-level")] [InlineData("many-levels")] [InlineData("many-exports")]
    [InlineData("null-exports")] [InlineData("export")] [InlineData("signature")] [InlineData("signature-case")]
    [InlineData("null-signature")] [InlineData("previous-first")] [InlineData("binder")]
    public void Malformed_or_inconsistent_saved_images_reject_inspection(string defect)
    {
        Module(); Create(); var descriptor = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram); var root = JsonNode.Parse(descriptor.Source!)!.AsObject();
        switch (defect)
        {
            case "version": root["version"] = 2; break;
            case "unknown": root["extra"] = true; break;
            case "case": root["Version"] = 1; break;
            case "module-unknown": root["modules"]![0]!["extra"] = true; break;
            case "null-module": root["modules"]![0] = null; break;
            case "many-modules": while (root["modules"]!.AsArray().Count < 17) root["modules"]!.AsArray().Add(root["modules"]![0]!.DeepClone()); break;
            case "module-case": root["modules"]![0]!["name"] = "helper"; break;
            case "source": root["modules"]![0]!["source"] = "**free\nsndpgmmsg 'UNSAFE';"; break;
            case "null-level": root["levels"]![0] = null; break;
            case "many-levels": while (root["levels"]!.AsArray().Count < 17) root["levels"]!.AsArray().Add(root["levels"]![0]!.DeepClone()); break;
            case "many-exports": while (root["levels"]![0]!["exports"]!.AsArray().Count < 257) root["levels"]![0]!["exports"]!.AsArray().Add("ANOTHER"); break;
            case "null-exports": root["levels"]![0]!["exports"] = null; break;
            case "export": root["levels"]![0]!["exports"]![0] = "PRIVATE"; break;
            case "signature": root["levels"]![0]!["signature"] = new string('0', 32); break;
            case "signature-case": root["levels"]![0]!["signature"] = root["levels"]![0]!["signature"]!.GetValue<string>().ToLowerInvariant(); break;
            case "null-signature": root["levels"]![0]!["signature"] = null; break;
            case "previous-first": root["levels"]![0]!["level"] = "*PRV"; break;
            case "binder": root["binderSource"] = new JsonObject { ["identity"] = "QGPL/QSRVSRC(SERVICE)", ["text"] = "STRPGMEXP\nEXPORT SYMBOL(SECOND)\nENDPGMEXP" }; break;
        }
        descriptor.Source = defect == "duplicate" ? root.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal) : root.ToJsonString();
        _system.Objects.Update(descriptor); var result = Command("DSPSRVPGM SRVPGM(SERVICE)"); Assert.True(result.IsError, result.Message); Assert.DoesNotContain("UNSAFE", result.Message);
    }
    [Fact]
    public void Creation_and_inspection_enforce_module_source_service_and_destination_authority()
    {
        Module(); Create(); _files.SaveSourceMember("QGPL", "QSRVSRC", "DENIED", Binder, null); _system.Security.Profiles.Create(new UserProfile { Name = "SRVUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SRVUSER"), "ServiceUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SRVUSER", Authorities.AllBits);
        using var session = Session("SRVUSER");
        _system.Security.Authority.Grant("QGPL", "HELPER", ObjectType.Module, "SRVUSER", AuthorityBit.None);
        Assert.True(session.Execute("CRTSRVPGM SRVPGM(DENIED) MODULE(HELPER) EXPORT(*ALL)").IsError);
        _system.Security.Authority.Grant("QGPL", "HELPER", ObjectType.Module, "SRVUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "QSRVSRC", ObjectType.File, "SRVUSER", AuthorityBit.None);
        Assert.True(session.Execute("CRTSRVPGM SRVPGM(DENIED) MODULE(HELPER)").IsError);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "SRVUSER", AuthorityBit.None);
        Assert.True(session.Execute("DSPSRVPGM SRVPGM(SERVICE)").IsError);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SRVUSER", Authorities.UseBits);
        Assert.True(session.Execute("CRTSRVPGM SRVPGM(DENIED) MODULE(HELPER) EXPORT(*ALL)").IsError);
        Assert.False(_system.Objects.Exists("QGPL", "DENIED", ObjectType.ServiceProgram));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Signed_module_and_service_integrity_is_checked_before_creation_or_inspection(bool service)
    {
        Module(); Create(); var ca = _system.Certificates.CreateAuthority("SRVCA", "Service signing CA"); var signer = _system.Certificates.Issue("SRVSIGN", "Service signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); var name = service ? "SERVICE" : "HELPER"; var type = service ? ObjectType.ServiceProgram : ObjectType.Module;
        _system.ObjectSigning.Sign("QGPL", name, type, signer.Id); var changed = _system.Objects.GetRequired("QGPL", name, type); changed.Source += " "; _system.Objects.Update(changed);
        var result = Command(service ? "DSPSRVPGM SRVPGM(SERVICE)" : "CRTSRVPGM SRVPGM(ALTERED) MODULE(HELPER) EXPORT(*ALL)");
        Assert.True(result.IsError); Assert.Contains("integrity", result.Message, StringComparison.OrdinalIgnoreCase); Assert.False(_system.Objects.Exists("QGPL", "ALTERED", ObjectType.ServiceProgram));
    }
    [Fact]
    public void Current_library_creation_library_list_lookup_and_QTEMP_isolation_use_job_context()
    {
        Module(); using var first = Session(); using var second = Session();
        Ok(first.Execute("CRTLIB LIB(SRVLIB)")); Ok(first.Execute("CHGCURLIB CURLIB(SRVLIB)"));
        Ok(first.Execute("CRTSRVPGM SRVPGM(SERVICE) MODULE(QGPL/HELPER) EXPORT(*ALL)"));
        Assert.True(_system.Objects.Exists("SRVLIB", "SERVICE", ObjectType.ServiceProgram)); Ok(first.Execute("DSPSRVPGM SRVPGM(SERVICE)"));
        Ok(first.Execute("CRTSRVPGM SRVPGM(QTEMP/PRIVATE) MODULE(QGPL/HELPER) EXPORT(*ALL)"));
        Ok(first.Execute("DSPSRVPGM SRVPGM(QTEMP/PRIVATE)")); Assert.True(second.Execute("DSPSRVPGM SRVPGM(QTEMP/PRIVATE)").IsError);
    }
    [Fact]
    public void Service_objects_cannot_be_called_as_programs_and_unused_service_bindings_remain_static()
    {
        Module(); Create(); Assert.True(Command("CALL QGPL/SERVICE").IsError);
        Module("MAIN", "**free\nreturn;"); Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) BNDSRVPGM(SERVICE)"));
        Assert.Empty(RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!).Services);
    }
    public void Dispose() => _system.Dispose();
}
