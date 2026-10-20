using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgUpdatePolicyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Main = "**free\ndcl-pr NEXT int(10);\nend-pr;\ndcl-s N int(10);\neval N=NEXT();\nsndpgmmsg %char(N);\nreturn;";
    private const string Wrapper = "**free\nctl-opt nomain;\ndcl-pr NEXT int(10);\nend-pr;\ndcl-proc FORWARD export;\ndcl-pi *n int(10) end-pi;\nreturn NEXT();\nend-proc;";
    private static string Counter(int step) => $"**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+{step};\nreturn COUNT;\nend-proc;";
    public RpgUpdatePolicyTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PolicyFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        Ok(Command("CRTLIB LIB(FIRSTLIB)")); Ok(Command("CRTLIB LIB(SECONDLIB)"));
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private void Module(string name, string source, string library = "QGPL")
    {
        Save(name, source); if (_system.Objects.Exists(library, name, ObjectType.Module)) _system.Objects.Delete(library, name, ObjectType.Module);
        Ok(Command($"CRTRPGMOD MODULE({library}/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Service(string library = "FIRSTLIB", int step = 1, string group = "*CALLER", string name = "SERVICE", string? source = null, string dependencies = "*NONE", string allow = "*YES")
    {
        Module("M" + name, source ?? Counter(step), library);
        Ok(Command($"CRTSRVPGM SRVPGM({library}/{name}) MODULE({library}/M{name}) EXPORT(*ALL) ACTGRP({group}) BNDSRVPGM({dependencies}) ALWLIBUPD({allow})"));
    }
    private void Program(string name = "CALLER", string provider = "FIRSTLIB/SERVICE", string group = "CALLERS", string? allow = "*YES", string? source = null, string mode = "*IMMED")
    {
        Save(name, source ?? Main); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM(({provider} {mode})) ACTGRP({group})" + (allow is null ? "" : " ALWLIBUPD(" + allow + ")")));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, int expected, string name = "CALLER")
    { var result = session.Execute("CALL QGPL/" + name); Ok(result); Assert.Equal(expected.ToString(), result.Message?.Trim()); }
    private static void Libraries(ExecutionSession session, string library) => Ok(session.Execute("CHGLIBL LIBL(" + library + " QGPL)"));
    private ObjectDescriptor Target(string name = "CALLER", bool service = false) => _system.Objects.GetRequired("QGPL", name, service ? ObjectType.ServiceProgram : ObjectType.Program);
    private RpgBoundProgram Image(string name = "CALLER") => RpgBoundProgram.Deserialize(Target(name).Source!);
    private void Unchanged(ObjectDescriptor before)
    { var after = _system.Objects.GetRequired(before.Library, before.Name, before.ObjectType); Assert.Equal(before.Source, after.Source); Assert.Equal(before.Changed, after.Changed); Assert.Equal(before.ExtendedAttributes, after.ExtendedAttributes); }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Named_program_group_updates_select_the_new_group_and_keep_old_group_storage_until_reclaim(int ccsid)
    {
        Module("MAIN", Main); Module("HELPER", Counter(1)); Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN HELPER) ACTGRP(OLDGROUP)"));
        using var session = Session(ccsid); Count(session, 1); var before = Target();
        Module("HELPER", Counter(10)); Ok(Command("UPDPGM PGM(CALLER) MODULE(HELPER) ACTGRP(NEWGROUP)"));
        Assert.Equal("NEWGROUP", Target().ExtendedAttributes!["ipc.program.activationGroup"]); Count(session, 10);
        Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE) ACTGRP(OLDGROUP)")); Count(session, 2);
        Ok(session.Execute("RCLACTGRP ACTGRP(OLDGROUP)")); Count(session, 10);
        Assert.Equal(before.Created, Target().Created); Assert.Equal(before.Owner, Target().Owner);
    }
    [Theory]
    [InlineData("*CALLER", "RENAMED")] [InlineData("*NEW", "RENAMED")]
    [InlineData("ORIGINAL", "*CALLER")] [InlineData("ORIGINAL", "*NEW")]
    [InlineData("ORIGINAL", "BAD-NAME")]
    public void Invalid_program_group_transitions_leave_the_object_unchanged(string original, string requested)
    {
        Service(); Program(group: original); var before = Target();
        Assert.True(Command($"UPDPGM PGM(CALLER) MODULE(*NONE) ACTGRP({requested})").IsError); Unchanged(before);
    }
    [Fact]
    public void Named_service_group_changes_preserve_old_connections_and_share_the_new_group()
    {
        Service(group: "OLDGROUP"); Program(); Program("SECOND", group: "OTHER"); using var session = Session(); Count(session, 1);
        Module("MSERVICE", Counter(10), "FIRSTLIB"); Ok(Command("UPDSRVPGM SRVPGM(FIRSTLIB/SERVICE) MODULE(FIRSTLIB/MSERVICE) ACTGRP(NEWGROUP)"));
        Count(session, 2); Count(session, 10, "SECOND");
        Ok(session.Execute("RCLACTGRP ACTGRP(OLDGROUP)")); Count(session, 20);
    }
    [Theory]
    [InlineData("*CALLER", "RENAMED")] [InlineData("ORIGINAL", "*CALLER")] [InlineData("ORIGINAL", "*NEW")]
    public void Invalid_service_group_transitions_reject_atomically(string original, string requested)
    {
        Service("QGPL", group: original); var before = Target("SERVICE", true);
        Assert.True(Command($"UPDSRVPGM SRVPGM(SERVICE) MODULE(*NONE) ACTGRP({requested})").IsError); Unchanged(before);
    }
    [Theory]
    [InlineData("CRTPGM", "*YES")] [InlineData("CRTPGM", "*NO")]
    [InlineData("CRTBNDRPG", "*YES")] [InlineData("CRTBNDRPG", "*NO")]
    [InlineData("CRTSRVPGM", "*YES")] [InlineData("CRTSRVPGM", "*NO")]
    public void Creation_commands_capture_the_library_update_flag(string command, string allow)
    {
        Module("MAIN", "**free\nreturn;"); Module("HELPER", Counter(1)); Save("CALLER", "**free\nreturn;");
        var text = command switch { "CRTPGM" => "CRTPGM PGM(QGPL/CALLER) MODULE(MAIN)", "CRTSRVPGM" => "CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) EXPORT(*ALL)", _ => "CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)" };
        Ok(Command(text + " ALWLIBUPD(" + allow + ")")); Assert.Equal(allow, Target(command == "CRTSRVPGM" ? "SERVICE" : "CALLER", command == "CRTSRVPGM").ExtendedAttributes!["ipc.program.allowLibraryUpdate"]);
    }
    [Theory]
    [InlineData("CRTPGM")] [InlineData("CRTBNDRPG")] [InlineData("CRTSRVPGM")]
    public void Invalid_library_update_flags_prevent_creation(string command)
    {
        Module("MAIN", "**free\nreturn;"); Module("HELPER", Counter(1)); Save("CALLER", "**free\nreturn;");
        var text = command switch { "CRTPGM" => "CRTPGM PGM(QGPL/CALLER) MODULE(MAIN)", "CRTSRVPGM" => "CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) EXPORT(*ALL)", _ => "CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)" };
        Assert.True(Command(text + " ALWLIBUPD(*BAD)").IsError); Assert.False(_system.Objects.Exists("QGPL", command == "CRTSRVPGM" ? "SERVICE" : "CALLER", command == "CRTSRVPGM" ? ObjectType.ServiceProgram : ObjectType.Program));
    }
    [Theory]
    [InlineData(null)] [InlineData("*NO")]
    public void Library_relocation_is_forbidden_unless_creation_allowed_it(string? allow)
    {
        Service(); Service("SECONDLIB", 10); Program(allow: allow); var before = Target();
        foreach (var policy in new[] { "SECONDLIB", "*LIBL" }) { Assert.True(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(" + policy + ")").IsError); Unchanged(before); }
        Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*SAME)"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Explicit_library_relocation_preserves_old_activations_and_binds_new_ones_to_the_new_provider(int ccsid)
    {
        Service(); Service("SECONDLIB", 10); Program(); using var old = Session(ccsid); Count(old, 1);
        Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(SECONDLIB)")); Assert.Equal("SECONDLIB", Assert.Single(Image().Services).Library); Assert.Equal(2, Image().Version);
        Count(old, 2); using var fresh = Session(ccsid); Count(fresh, 10);
        Ok(old.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(old, 10);
    }
    [Fact]
    public void Missing_preferred_objects_preserve_each_original_library()
    {
        Service(); Program(); Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(SECONDLIB)"));
        Assert.Equal("FIRSTLIB", Assert.Single(Image().Services).Library); Assert.Null(Image().Services[0].LookupLibrary);
        using var session = Session(); Count(session, 1);
    }
    [Theory]
    [InlineData("*CURLIB")] [InlineData("*BAD")] [InlineData("BAD-NAME")]
    public void Invalid_library_policies_fail_without_publishing(string policy)
    {
        Service(); Program(); var before = Target(); Assert.True(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(" + policy + ")").IsError); Unchanged(before);
    }
    [Theory]
    [InlineData("signature")] [InlineData("interface")] [InlineData("signing")]
    public void An_invalid_preferred_provider_never_falls_back_or_publishes_activation_metadata(string defect)
    {
        Service(); Program(group: "ORIGINAL");
        var source = defect == "signature" ? Counter(10).Replace("NEXT", "OTHER") : defect == "interface" ? Counter(10).Replace("int(10) end-pi", "char(4) end-pi").Replace("return COUNT;", "return 'BAD';") : Counter(10);
        Service("SECONDLIB", source: source);
        if (defect == "signing")
        {
            var ca = _system.Certificates.CreateAuthority("POLICYCA", "Policy CA"); var signer = _system.Certificates.Issue("POLICYSIGN", "Policy signer", ca.Id, CertificatePurpose.ObjectSigning);
            _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("SECONDLIB", "SERVICE", ObjectType.ServiceProgram, signer.Id);
            var provider = _system.Objects.GetRequired("SECONDLIB", "SERVICE", ObjectType.ServiceProgram); provider.Source += " "; _system.Objects.Update(provider);
        }
        var before = Target(); Assert.True(Command("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(SECONDLIB) ACTGRP(RENAMED)").IsError); Unchanged(before);
    }
    [Theory]
    [InlineData("*IMMED")] [InlineData("*DEFER")]
    public void Service_dependency_relocation_preserves_activation_modes_and_public_exports(string mode)
    {
        Service(); Service("SECONDLIB", 10); Service("QGPL", name: "PARENT", source: Wrapper, dependencies: "(FIRSTLIB/SERVICE " + mode + ")");
        var before = RpgServiceImage.Deserialize(Target("PARENT", true).Source!);
        Ok(Command("UPDSRVPGM SRVPGM(PARENT) MODULE(*NONE) SRVPGMLIB(SECONDLIB)"));
        var after = RpgServiceImage.Deserialize(Target("PARENT", true).Source!); var dependency = Assert.Single(after.Services);
        Assert.Equal("SECONDLIB", dependency.Library); Assert.Equal(mode, dependency.Activation); Assert.Equal(before.Levels[0].Signature, after.Levels[0].Signature);
        Program(provider: "QGPL/PARENT", source: Main.Replace("NEXT", "FORWARD")); using var session = Session(); Count(session, 10);
    }
    [Fact]
    public void Library_list_connections_are_pinned_per_caller_and_share_the_actual_provider_storage()
    {
        Service(); Service("SECONDLIB", 10); Program(); Program("SECOND"); Program("DIRECT"); using var session = Session(); Libraries(session, "FIRSTLIB");
        Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)")); Ok(session.Execute("UPDPGM PGM(SECOND) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        Assert.Equal(4, Image().Version); Assert.Equal("*LIBL", Assert.Single(Image().Services).LookupLibrary); Count(session, 1);
        Libraries(session, "SECONDLIB"); Count(session, 2); Count(session, 10, "SECOND"); Count(session, 3, "DIRECT"); Count(session, 20, "SECOND");
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 10);
    }
    [Fact]
    public void Runtime_library_list_resolution_uses_each_jobs_list_without_the_original_bound_object()
    {
        Service(); Service("SECONDLIB", 10); Program(); using var binder = Session(); Libraries(binder, "FIRSTLIB");
        Ok(binder.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)")); _system.Objects.Delete("FIRSTLIB", "SERVICE", ObjectType.ServiceProgram);
        using var first = Session(); Libraries(first, "SECONDLIB"); Count(first, 10);
        using var missing = Session(); Libraries(missing, "FIRSTLIB"); Assert.True(missing.Execute("CALL QGPL/CALLER").IsError);
    }
    [Fact]
    public void Deferred_library_list_lookup_waits_for_the_first_procedure_call()
    {
        Service(); Service("SECONDLIB", 10);
        var source = Main.Replace("dcl-pr NEXT", "dcl-pi *n;\nRUN char(1);\nend-pi;\ndcl-pr NEXT").Replace("eval N=NEXT();", "if RUN='Y';\neval N=NEXT();\nelse;\neval N=99;\nendif;");
        Program(source: source, mode: "*DEFER"); using var session = Session(); Libraries(session, "FIRSTLIB"); Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        var unused = session.Execute("CALL PGM(QGPL/CALLER) PARM('N')"); Ok(unused); Assert.Equal("99", unused.Message?.Trim());
        Libraries(session, "SECONDLIB"); var used = session.Execute("CALL PGM(QGPL/CALLER) PARM('Y')"); Ok(used); Assert.Equal("10", used.Message?.Trim());
    }
    [Fact]
    public void Same_policy_rebinding_preserves_dynamic_lookup_and_rebinds_from_the_current_list()
    {
        Service(); Service("SECONDLIB", 10); Program(); using var session = Session(); Libraries(session, "FIRSTLIB"); Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        Libraries(session, "SECONDLIB"); Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE)")); Assert.Equal("SECONDLIB", Assert.Single(Image().Services).Library); Assert.Equal("*LIBL", Image().Services[0].LookupLibrary);
        Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(FIRSTLIB)")); Assert.Null(Assert.Single(Image().Services).LookupLibrary); Count(session, 1);
    }
    [Fact]
    public void Service_images_can_save_library_list_dependencies_and_inspect_the_runtime_library()
    {
        Service(); Service("SECONDLIB", 10); Service("QGPL", name: "PARENT", source: Wrapper, dependencies: "FIRSTLIB/SERVICE"); using var session = Session(); Libraries(session, "FIRSTLIB");
        Ok(session.Execute("UPDSRVPGM SRVPGM(PARENT) MODULE(*NONE) SRVPGMLIB(*LIBL)")); var image = RpgServiceImage.Deserialize(Target("PARENT", true).Source!);
        Assert.Equal(4, image.Version); Assert.Equal("*LIBL", Assert.Single(image.Restore().ServiceReferences).LookupLibrary);
        var display = session.Execute("DSPSRVPGM SRVPGM(PARENT)"); Ok(display); Assert.Contains("services: *LIBL/SERVICE", display.Message);
        Program(provider: "QGPL/PARENT", source: Main.Replace("NEXT", "FORWARD")); Libraries(session, "SECONDLIB"); Count(session, 10);
    }
    [Fact]
    public void Unauthorized_preferred_providers_do_not_fall_back_to_an_authorized_original()
    {
        Service(); Service("SECONDLIB", 10); Program(); _system.Security.Profiles.Create(new UserProfile { Name = "POLICYUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("POLICYUSER"), "PolicyUser45"); var target = Target(); target.Owner = "POLICYUSER"; _system.Objects.Update(target);
        foreach (var library in new[] { "QGPL", "FIRSTLIB", "SECONDLIB" }) _system.Security.Authority.Grant("QSYS", library, ObjectType.Library, "POLICYUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("SECONDLIB", "SERVICE", ObjectType.ServiceProgram, "POLICYUSER", AuthorityBit.None);
        using var session = Session(user: "POLICYUSER"); var before = Target(); Assert.True(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(SECONDLIB)").IsError); Unchanged(before);
    }
    [Fact]
    public void A_connected_library_list_import_rechecks_the_actual_providers_authority_after_the_list_changes()
    {
        Service(); Service("SECONDLIB", 10); Program(); using var binder = Session(); Libraries(binder, "FIRSTLIB");
        Ok(binder.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        _system.Security.Profiles.Create(new UserProfile { Name = "POLICYUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("POLICYUSER"), "PolicyUser45");
        foreach (var library in new[] { "QGPL", "FIRSTLIB", "SECONDLIB" }) _system.Security.Authority.Grant("QSYS", library, ObjectType.Library, "POLICYUSER", Authorities.AllBits);
        using var session = Session(user: "POLICYUSER"); Libraries(session, "FIRSTLIB"); Count(session, 1); Libraries(session, "SECONDLIB");
        _system.Security.Authority.Grant("FIRSTLIB", "SERVICE", ObjectType.ServiceProgram, "POLICYUSER", AuthorityBit.None);
        Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    [Theory]
    [InlineData("version")] [InlineData("library")] [InlineData("case")] [InlineData("missing")]
    public void Invalid_lookup_metadata_rejects_in_program_and_service_images(string defect)
    {
        Service(); Service("QGPL", name: "PARENT", source: Wrapper, dependencies: "FIRSTLIB/SERVICE"); Program(); using var session = Session(); Libraries(session, "FIRSTLIB");
        Ok(session.Execute("UPDPGM PGM(CALLER) MODULE(*NONE) SRVPGMLIB(*LIBL)")); Ok(session.Execute("UPDSRVPGM SRVPGM(PARENT) MODULE(*NONE) SRVPGMLIB(*LIBL)"));
        foreach (var target in new[] { Target(), Target("PARENT", true) })
        {
            var root = JsonNode.Parse(target.Source!)!;
            if (defect == "version") root["version"] = 3;
            else if (defect == "missing") root["services"]![0]!.AsObject().Remove("lookupLibrary");
            else root["services"]![0]!["lookupLibrary"] = defect == "case" ? "*libl" : "SECONDLIB";
            if (target.ObjectType == ObjectType.Program) Assert.Throws<InvalidDataException>(() => RpgBoundProgram.Deserialize(root.ToJsonString()));
            else Assert.Throws<InvalidDataException>(() => RpgServiceImage.Deserialize(root.ToJsonString()));
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Activation_metadata_publication_obeys_cancellation_and_concurrent_change_checks(bool concurrent)
    {
        Service(); Program(group: "ORIGINAL"); var expected = Target(); var store = new RpgObjectUpdateStore(_system.Connections, _system.ObjectSigning);
        if (concurrent)
        {
            var live = Target(); live.Description = "Concurrent edit"; _system.Objects.Update(live);
            Assert.Equal("IPC0130", Assert.Throws<CpfException>(() => store.Publish(expected, expected.Source!, expected.Attribute!, activationGroup: "RENAMED")).MessageId);
            Assert.Equal("Concurrent edit", Target().Description); Assert.Equal("ORIGINAL", Target().ExtendedAttributes!["ipc.program.activationGroup"]);
        }
        else { Assert.Throws<OperationCanceledException>(() => store.Publish(expected, expected.Source!, expected.Attribute!, new CancellationToken(true), "RENAMED")); Unchanged(expected); }
    }
    public void Dispose() => _system.Dispose();
}
