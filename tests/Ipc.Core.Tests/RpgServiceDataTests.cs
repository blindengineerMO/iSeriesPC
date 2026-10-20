using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgServiceDataTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string DataOnly = "**free\nctl-opt nomain;\ndcl-s SHARED int(10) inz(10) export;";
    private const string Counter = DataOnly + "\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\neval SHARED=SHARED+1;\nreturn SHARED;\nend-proc;";
    private const string Main = "**free\ndcl-s LOCAL int(10) import('SHARED');\neval LOCAL=LOCAL+1;\nsndpgmmsg %char(LOCAL);\nreturn;";
    public RpgServiceDataTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DataFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private void Module(string name, string source, string library = "QGPL")
    {
        Save(name, source); if (_system.Objects.Exists(library, name, ObjectType.Module)) _system.Objects.Delete(library, name, ObjectType.Module);
        Ok(Command($"CRTRPGMOD MODULE({library}/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Service(string source = DataOnly, string group = "*CALLER", string library = "QGPL", string dependencies = "*NONE", string name = "SERVICE")
    {
        Module("M" + name, source, library); if (_system.Objects.Exists(library, name, ObjectType.ServiceProgram)) _system.Objects.Delete(library, name, ObjectType.ServiceProgram);
        Ok(Command($"CRTSRVPGM SRVPGM({library}/{name}) MODULE({library}/M{name}) EXPORT(*ALL) ACTGRP({group}) BNDSRVPGM({dependencies}) ALWLIBUPD(*YES)"));
    }
    private void Program(string name = "CALLER", string source = Main, string group = "CALLERS", string services = "(SERVICE *IMMED)", string directories = "*NONE")
    {
        Save(name, source); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) ACTGRP({group}) BNDSRVPGM({services}) BNDDIR({directories}) ALWLIBUPD(*YES)"));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, int count, string name = "CALLER")
    { var result = session.Execute("CALL QGPL/" + name); Ok(result); Assert.Equal(count.ToString(), result.Message?.Trim()); }
    private ObjectDescriptor Target(bool service = false) => _system.Objects.GetRequired("QGPL", service ? "SERVICE" : "CALLER", service ? ObjectType.ServiceProgram : ObjectType.Program);
    private void Marker()
    { Save("MARK", "PGM\nCRTDTAARA DTAARA(QGPL/EFFECT) TYPE(*CHAR) LEN(1)\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/MARK) SRCFILE(QGPL/SOURCE)")); }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Data_only_services_expose_live_activation_owned_storage(int ccsid)
    {
        Service(); Program(); var image = RpgServiceImage.Deserialize(Target(true).Source!); Assert.Equal(5, image.Version); Assert.Equal(["SHARED"], image.DataExports); Assert.Empty(image.Restore().Procedures);
        var caller = RpgBoundProgram.Deserialize(Target().Source!); Assert.Equal(5, caller.Version); Assert.Equal(["SHARED"], Assert.Single(caller.Services).DataExports);
        var display = Command("DSPSRVPGM SRVPGM(SERVICE)"); Ok(display); Assert.Contains("data: SHARED", display.Message);
        _system.Objects.Delete("QGPL", "MSERVICE", ObjectType.Module); _files.RemoveMember("QGPL", "SOURCE", "MSERVICE"); using var session = Session(ccsid); Count(session, 11); Count(session, 12);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Procedures_and_data_imports_share_the_same_service_module_globals(int ccsid)
    {
        Service(Counter); Program(source: Main.Replace("eval LOCAL=LOCAL+1;", "dcl-pr NEXT int(10);\nend-pr;\ndcl-s RESULT int(10);\neval LOCAL=LOCAL+1;\neval RESULT=NEXT();").Replace("%char(LOCAL)", "%char(RESULT)"));
        using var session = Session(ccsid); Count(session, 12); Count(session, 14);
    }
    [Fact]
    public void Named_service_data_survives_caller_LR_and_caller_reclaim_and_refreshes_after_service_reclaim()
    {
        Service(group: "SERVICES"); Program(source: Main.Replace("return;", "seton LR;")); Program("SECOND", group: "OTHER");
        using var session = Session(); Count(session, 11); Count(session, 12); Count(session, 13, "SECOND");
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 14);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, 11, "SECOND"); Count(session, 12);
    }
    [Fact]
    public void Caller_service_data_is_isolated_between_groups_and_jobs()
    {
        Service(); Program(); Program("SECOND", group: "OTHER"); using var first = Session(); using var second = Session();
        Count(first, 11); Count(first, 11, "SECOND"); Count(second, 11); Count(first, 12);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Binding_directories_discover_data_providers_and_survive_directory_deletion(bool sourceReference)
    {
        Service(); var store = new BindingDirectoryStore(_system.Connections); store.Create("QGPL", "DATADIR"); store.Add("QGPL", "DATADIR", [new("QGPL", "SERVICE", ObjectType.ServiceProgram, "*DEFER")]);
        Program(source: sourceReference ? Main.Replace("**free", "**free\nctl-opt bnddir('QGPL/DATADIR');") : Main, services: "*NONE", directories: sourceReference ? "*NONE" : "DATADIR");
        _system.Objects.Delete("QGPL", "DATADIR", ObjectType.BindingDirectory); using var session = Session(); Count(session, 11);
    }
    [Theory]
    [InlineData("missing")] [InlineData("layout")] [InlineData("kind")] [InlineData("tamper")] [InlineData("trust")]
    public void Deferred_data_imports_are_activated_and_checked_before_caller_effects(string defect)
    {
        Service(); Marker(); Program(source: Main.Replace("eval LOCAL=", "call 'QGPL/MARK';\neval LOCAL="), services: "(SERVICE *DEFER)");
        if (defect == "missing") _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        if (defect == "layout") Service(DataOnly.Replace("int(10) inz(10)", "char(4) inz('BAD')"));
        if (defect == "kind") Service("**free\nctl-opt nomain;\ndcl-proc SHARED export;\ndcl-pi *n int(10) end-pi;\nreturn 1;\nend-proc;");
        if (defect is "tamper" or "trust")
        {
            var ca = _system.Certificates.CreateAuthority("DATACA", "Data CA"); var signer = _system.Certificates.Issue("DATASIGN", "Data signer", ca.Id, CertificatePurpose.ObjectSigning);
            _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
            if (defect == "trust") _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
            else { var descriptor = Target(true); descriptor.Source += " "; _system.Objects.Update(descriptor); }
        }
        using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError); Assert.False(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Fact]
    public void A_deferred_procedure_only_import_does_not_activate_unused_data_exports()
    {
        Service(Counter); Program(source: "**free\ndcl-pr NEXT int(10);\nend-pr;\ndcl-s N int(10);\nif 1=0;\neval N=NEXT();\nendif;\nsndpgmmsg 'READY';\nreturn;", services: "(SERVICE *DEFER)");
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); using var session = Session(); var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("READY", result.Message?.Trim());
    }
    [Fact]
    public void Transitive_service_data_imports_activate_in_the_parents_group()
    {
        Service(); Service("**free\nctl-opt nomain;\ndcl-s VALUE int(10) import('SHARED');\ndcl-proc GET export;\ndcl-pi *n int(10) end-pi;\neval VALUE=VALUE+1;\nreturn VALUE;\nend-proc;", "PARENTS", dependencies: "(SERVICE *DEFER)", name: "PARENT");
        Program(source: "**free\ndcl-pr GET int(10);\nend-pr;\ndcl-s N int(10);\neval N=GET();\nsndpgmmsg %char(N);\nreturn;", services: "PARENT");
        Program("DIRECT", group: "PARENTS"); using var session = Session(); Count(session, 11); Count(session, 12, "DIRECT");
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 13);
    }
    [Fact]
    public void Live_authority_is_rechecked_without_losing_service_data()
    {
        Service(group: "SERVICES"); Program(); _system.Security.Profiles.Create(new UserProfile { Name = "DATAUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("DATAUSER"), "DataUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "DATAUSER", Authorities.AllBits); using var session = Session(user: "DATAUSER"); Count(session, 11);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "DATAUSER", AuthorityBit.None); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "DATAUSER", Authorities.UseBits); Count(session, 12);
    }
    [Fact]
    public void Service_updates_keep_old_data_until_the_owning_group_is_reclaimed()
    {
        Service(group: "SERVICES"); Program(); using var session = Session(); Count(session, 11); Module("MSERVICE", DataOnly.Replace("inz(10)", "inz(100)"));
        Ok(Command("UPDSRVPGM SRVPGM(SERVICE) MODULE(MSERVICE)")); Count(session, 12); Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, 101);
    }
    [Fact]
    public void Current_export_updates_cannot_change_a_data_symbol_to_a_procedure()
    {
        Service(); var before = Target(true); Module("MSERVICE", "**free\nctl-opt nomain;\ndcl-proc SHARED export;\ndcl-pi *n int(10) end-pi;\nreturn 1;\nend-proc;");
        Assert.True(Command("UPDSRVPGM SRVPGM(SERVICE) MODULE(MSERVICE)").IsError); Assert.Equal(before.Source, Target(true).Source); Assert.Equal(before.Changed, Target(true).Changed);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Dynamic_structure_arrays_share_bytes_and_automatic_null_maps_after_description_deletion(int ccsid)
    {
        const string layout = "dcl-ds CHILD dim(2);\nN int(10);\nTEXT char(4) nullind;\nend-ds;\n";
        Service("**free\nctl-opt nomain alwnull(*usrctl);\ndcl-ds SHARED qualified dim(2) export;\n" + layout + "end-ds;", "SERVICES");
        Program(source: "**free\nctl-opt alwnull(*usrctl);\ndcl-ds LOCAL qualified dim(2) import('SHARED');\n" + layout + "end-ds;\neval LOCAL(2).CHILD(1).N=77;\neval LOCAL(2).CHILD(2).TEXT='DATA';\neval %nullind(LOCAL(2).CHILD(2).TEXT)=*on;\nsndpgmmsg 'SET';\nreturn;");
        Program("SECOND", "**free\nctl-opt alwnull(*usrctl);\ndcl-ds COPY qualified dim(2) import('SHARED');\n" + layout + "end-ds;\nif COPY(2).CHILD(1).N=77 and COPY(2).CHILD(2).TEXT='DATA' and %nullind(COPY(2).CHILD(2).TEXT);\nsndpgmmsg 'SHARED STRUCTURE';\nendif;\nreturn;", group: "OTHER");
        _system.Objects.Delete("QGPL", "MSERVICE", ObjectType.Module); _files.RemoveMember("QGPL", "SOURCE", "MSERVICE");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/CALLER")); var result = session.Execute("CALL QGPL/SECOND"); Ok(result); Assert.Equal("SHARED STRUCTURE", result.Message?.Trim());
    }
    [Fact]
    public void Library_list_relocation_preserves_data_kind_metadata_and_resolves_a_new_provider_after_reclaim()
    {
        Service(); Ok(Command("CRTLIB LIB(DATALIB)")); Service(DataOnly.Replace("inz(10)", "inz(100)"), library: "DATALIB"); Program(); using var session = Session(); Count(session, 11);
        Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(DATALIB)")); Count(session, 12);
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 101);
        Ok(session.Execute("CHGCURLIB CURLIB(DATALIB)")); Ok(session.Execute("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        var image = RpgBoundProgram.Deserialize(Target().Source!); Assert.Equal(5, image.Version); Assert.Equal("*LIBL", Assert.Single(image.Services).LookupLibrary); Assert.Equal(["SHARED"], image.Services[0].DataExports);
        Ok(session.Execute("CHGCURLIB CURLIB(QGPL)")); Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 11);
    }
    [Fact]
    public void An_imported_field_can_be_borrowed_by_an_ordinary_CL_program()
    {
        Service(); Save("ADD", "PGM PARM(&VALUE)\nDCL VAR(&VALUE) TYPE(*CHAR) LEN(4)\nCHGVAR VAR(&VALUE) VALUE('LIVE')\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/ADD) SRCFILE(QGPL/SOURCE)"));
        Service(DataOnly.Replace("int(10) inz(10)", "char(4) inz('INIT')"));
        Program(source: "**free\ndcl-s LOCAL char(4) import('SHARED');\ndcl-pr ADD extpgm('QGPL/ADD');\nP char(4);\nend-pr;\ncallp ADD(LOCAL);\nsndpgmmsg LOCAL;\nreturn;");
        using var session = Session(); var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("LIVE", result.Message?.Trim());
    }
    [Fact]
    public void Directory_module_data_exports_are_selected_and_captured_without_live_module_dependencies()
    {
        Module("DSTORE", DataOnly); var store = new BindingDirectoryStore(_system.Connections); store.Create("QGPL", "DATADIR"); store.Add("QGPL", "DATADIR", [new("QGPL", "DSTORE", ObjectType.Module)]);
        Program(source: Main.Replace("return;", "seton LR;"), services: "*NONE", directories: "DATADIR");
        _system.Objects.Delete("QGPL", "DSTORE", ObjectType.Module); _system.Objects.Delete("QGPL", "DATADIR", ObjectType.BindingDirectory);
        using var session = Session(); Count(session, 11); Count(session, 12);
    }
    [Fact]
    public void A_previous_signature_preserves_data_export_positions_for_an_older_caller()
    {
        Service(); Program(); var signature = RpgServiceImage.Deserialize(Target(true).Source!).Levels[0].Signature;
        Module("MSERVICE", DataOnly.Replace("inz(10)", "inz(100)") + "\ndcl-s EXTRA char(4) export;");
        Save("BINDER", "STRPGMEXP PGMLVL(*CURRENT) SIGNATURE('NEW')\nEXPORT SYMBOL(SHARED)\nEXPORT SYMBOL(EXTRA)\nENDPGMEXP\n" +
            "STRPGMEXP PGMLVL(*PRV) SIGNATURE(X'" + signature + "')\nEXPORT SYMBOL(SHARED)\nENDPGMEXP");
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        Ok(Command("CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(MSERVICE) EXPORT(*SRCFILE) SRCFILE(QGPL/SOURCE) SRCMBR(BINDER)"));
        using var session = Session(); Count(session, 101);
    }
    [Fact]
    public void Exported_program_data_survives_unmonitored_errors_but_ordinary_globals_restart()
    {
        Module("MAIN", "**free\ndcl-s COUNT int(10) inz(10) export;\ndcl-s N int(10) inz(7);\neval COUNT=COUNT+1;\nif COUNT=11;\neval N=0;\neval N=10/N;\nendif;\nif N=7;\nsndpgmmsg %char(COUNT);\nendif;\nreturn;");
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) ACTGRP(CALLERS)")); using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError); Count(session, 12);
    }
    [Theory]
    [InlineData("null", false)] [InlineData("unknown", false)] [InlineData("duplicate", false)] [InlineData("kind", false)] [InlineData("version", false)] [InlineData("count", false)]
    [InlineData("null", true)] [InlineData("unknown", true)] [InlineData("duplicate", true)] [InlineData("kind", true)] [InlineData("version", true)] [InlineData("count", true)]
    public void Malformed_or_forged_data_export_metadata_rejects_before_execution(string defect, bool service)
    {
        Service(Counter); Program();
        foreach (var descriptor in new[] { Target(service) })
        {
            var root = JsonNode.Parse(descriptor.Source!)!; var owner = descriptor.ObjectType == ObjectType.Program ? root["services"]![0]! : root;
            if (defect == "version") root["version"] = 4;
            else if (defect == "null") owner["dataExports"] = null;
            else if (defect == "count") { while (owner["dataExports"]!.AsArray().Count < 257) owner["dataExports"]!.AsArray().Add("SHARED"); }
            else if (defect == "duplicate") owner["dataExports"]!.AsArray().Add("SHARED");
            else owner["dataExports"]![0] = defect == "kind" ? "NEXT" : "MISSING";
            descriptor.Source = root.ToJsonString(); _system.Objects.Update(descriptor);
        }
        using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    public void Dispose() => _system.Dispose();
}
