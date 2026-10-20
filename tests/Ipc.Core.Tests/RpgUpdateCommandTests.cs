using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Core.Text;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgUpdateCommandTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string MainSource = "**free\ndcl-pr NEXT int(10);\nend-pr;\ndcl-s N int(10);\neval N=NEXT();\nsndpgmmsg %char(N);\nreturn;";
    private static string Counter(int step = 1) => $"**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+{step};\nreturn COUNT;\nend-proc;";
    private static string Constant(string name, int value) => $"**free\nctl-opt nomain;\ndcl-proc {name} export;\ndcl-pi *n int(10) end-pi;\nreturn {value};\nend-proc;";
    public RpgUpdateCommandTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "UpdateFixture45");
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
    private void Program(string options = "")
    { Module("MAIN", MainSource); Module("HELPER", Counter()); Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN HELPER) ACTGRP(UPDATES) " + options)); }
    private void Service(string options = "")
    { Module("HELPER", Counter()); Ok(Command("CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) EXPORT(*ALL) ACTGRP(SERVICES) " + options)); }
    private void ServiceCaller()
    { Save("CALLER", MainSource); Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) BNDSRVPGM(SERVICE) ACTGRP(UPDATES)")); }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, int count)
    { var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal(count.ToString(), result.Message?.Trim()); }
    private ObjectDescriptor Target(bool service = false) => _system.Objects.GetRequired("QGPL", service ? "SERVICE" : "CALLER", service ? ObjectType.ServiceProgram : ObjectType.Program);
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Updating_a_module_preserves_existing_activations_until_group_reclaim(int ccsid)
    {
        Program(); using var old = Session(ccsid); Count(old, 1); var original = Target(); Module("HELPER", Counter(10));
        Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(QGPL/HELPER)")); Count(old, 2);
        using var fresh = Session(ccsid); Count(fresh, 10); Ok(old.Execute("RCLACTGRP ACTGRP(UPDATES)")); Count(old, 10);
        var changed = Target(); Assert.Equal(original.Owner, changed.Owner); Assert.Equal(original.Created, changed.Created); Assert.Equal(original.PublicAuthority, changed.PublicAuthority); Assert.Equal(original.Description, changed.Description);
    }
    [Fact]
    public void Plain_CRTBNDRPG_programs_can_be_updated_without_original_source_or_module_objects()
    {
        Save("CALLER", "**free\nsndpgmmsg 'SINGLE';\nreturn;"); Ok(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) ACTGRP(UPDATES)"));
        using var session = Session(); var before = session.Execute("CALL QGPL/CALLER"); Ok(before); Assert.Equal("SINGLE", before.Message?.Trim());
        _files.RemoveMember("QGPL", "SOURCE", "CALLER"); Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE)")); Assert.Equal(RpgBoundProgram.Attribute, Target().Attribute);
        var after = session.Execute("CALL QGPL/CALLER"); Ok(after); Assert.Equal("SINGLE", after.Message?.Trim());
    }
    [Theory]
    [InlineData("*FIRST", "FIRSTLIB", "FIRST", 12)]
    [InlineData("*MODULE", "SECONDLIB", "SECOND", 21)]
    [InlineData("FIRSTLIB", "THIRDLIB", "FIRST", 12)]
    public void Replacement_selection_preserves_the_original_module_library(string policy, string replacementLibrary, string procedure, int expected)
    {
        foreach (var library in new[] { "FIRSTLIB", "SECONDLIB", "THIRDLIB" }) Ok(Command("CRTLIB LIB(" + library + ")"));
        Module("HELPER", Constant("FIRST", 1), "FIRSTLIB"); Module("HELPER", Constant("SECOND", 2), "SECONDLIB");
        Module("MAIN", "**free\ndcl-pr FIRST int(10);\nend-pr;\ndcl-pr SECOND int(10);\nend-pr;\ndcl-s N int(10);\neval N=FIRST()+SECOND();\nsndpgmmsg %char(N);\nreturn;");
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN FIRSTLIB/HELPER SECONDLIB/HELPER)"));
        Module("HELPER", Constant(procedure, procedure == "FIRST" ? 10 : 20), replacementLibrary);
        Ok(Command($"UPDPGM PGM(QGPL/CALLER) MODULE({replacementLibrary}/HELPER) RPLLIB({policy})"));
        var image = RpgBoundProgram.Deserialize(Target().Source!); Assert.Equal(["QGPL", "FIRSTLIB", "SECONDLIB"], image.Modules.Select(m => m.Library));
        using var session = Session(); Count(session, expected);
    }
    [Fact]
    public void Ambiguous_replacement_fails_without_modifying_the_program()
    {
        Ok(Command("CRTLIB LIB(OTHER)")); Module("HELPER", Constant("FIRST", 1)); Module("HELPER", Constant("SECOND", 2), "OTHER"); Module("MAIN", "**free\nreturn;");
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN QGPL/HELPER OTHER/HELPER)")); var before = Target();
        Assert.True(Command("UPDPGM PGM(QGPL/CALLER) MODULE(QGPL/HELPER)").IsError); Assert.Equal(before.Source, Target().Source); Assert.Equal(before.Changed, Target().Changed);
    }
    [Theory]
    [InlineData("*ALL")] [InlineData("HELP*")]
    public void Generic_module_updates_select_existing_bound_names(string pattern)
    {
        Program(); Ok(Command("CRTLIB LIB(UPDLIB)")); Module("HELPER", Counter(10), "UPDLIB");
        Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(UPDLIB/" + pattern + ")")); using var session = Session(); Count(session, 10);
        Assert.Equal("QGPL", RpgBoundProgram.Deserialize(Target().Source!).Modules[1].Library);
    }
    [Fact]
    public void Incompatible_or_missing_replacements_leave_the_original_payload_and_timestamp_intact()
    {
        Program(); var before = Target(); Module("HELPER", Counter().Replace("int(10) end-pi", "char(4) end-pi").Replace("return COUNT;", "return 'BAD';"));
        Assert.True(Command("UPDPGM PGM(QGPL/CALLER) MODULE(HELPER)").IsError); Assert.Equal(before.Source, Target().Source); Assert.Equal(before.Changed, Target().Changed);
        Assert.True(Command("UPDPGM PGM(QGPL/CALLER) MODULE(MISSING)").IsError); using var session = Session(); Count(session, 1);
    }
    [Fact]
    public void Export_current_keeps_signatures_and_hides_new_exports_until_explicitly_requested()
    {
        Service(); ServiceCaller(); using var old = Session(); Count(old, 1); var original = RpgServiceImage.Deserialize(Target(true).Source!);
        Module("HELPER", Counter(10) + "\ndcl-proc EXTRA export;\ndcl-pi *n int(10) end-pi;\nreturn 99;\nend-proc;");
        Ok(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER)")); var image = RpgServiceImage.Deserialize(Target(true).Source!);
        Assert.Equal(original.Levels[0].Signature, image.Levels[0].Signature); Assert.Equal(["NEXT"], image.Levels[0].Exports); Assert.NotNull(image.BinderSource);
        using var fresh = Session(); Count(fresh, 10); Count(old, 2); Ok(old.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(old, 10);
    }
    [Fact]
    public void Rebinding_with_module_none_updates_service_signatures_without_recompiling_callers()
    {
        Service(); ServiceCaller(); Module("HELPER", Counter(10) + "\ndcl-proc EXTRA export;\ndcl-pi *n int(10) end-pi;\nreturn 99;\nend-proc;");
        Ok(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) EXPORT(*ALL)"));
        using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
        _files.RemoveMember("QGPL", "SOURCE", "CALLER"); Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE)")); Count(session, 10);
    }
    [Fact]
    public void Binder_source_updates_can_preserve_previous_signatures_for_existing_clients()
    {
        Module("HELPER", Counter()); Save("BINDER", "STRPGMEXP SIGNATURE('V1')\nEXPORT SYMBOL(NEXT)\nENDPGMEXP");
        Ok(Command("CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) SRCFILE(QGPL/SOURCE) SRCMBR(BINDER)")); ServiceCaller();
        Module("HELPER", Counter(10)); Save("BINDER", "STRPGMEXP SIGNATURE('V2')\nEXPORT SYMBOL(NEXT)\nENDPGMEXP\nSTRPGMEXP PGMLVL(*PRV) SIGNATURE('V1')\nEXPORT SYMBOL(NEXT)\nENDPGMEXP");
        Ok(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER) EXPORT(*SRCFILE) SRCFILE(QGPL/SOURCE) SRCMBR(BINDER)"));
        _files.RemoveMember("QGPL", "SOURCE", "BINDER"); using var session = Session(); Count(session, 10);
    }
    [Fact]
    public void Removing_a_current_export_rejects_the_whole_service_update()
    {
        Service(); var before = Target(true); Module("HELPER", Constant("OTHER", 7));
        Assert.True(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(HELPER)").IsError); Assert.Equal(before.Source, Target(true).Source); Assert.Equal(before.Changed, Target(true).Changed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Allow_update_no_rejects_updates_even_for_the_owner(bool service)
    {
        if (service) Service("ALWUPD(*NO)"); else Program("ALWUPD(*NO)");
        Assert.True(Command(service ? "UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(*NONE)" : "UPDPGM PGM(QGPL/CALLER) MODULE(*NONE)").IsError);
    }
    [Fact]
    public void Object_management_grants_alone_do_not_authorize_another_owners_program_update()
    {
        Program(); _system.Security.Profiles.Create(new UserProfile { Name = "UPDUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("UPDUSER"), "UpdateUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "UPDUSER", Authorities.AllBits); _system.Security.Authority.Grant("QGPL", "CALLER", ObjectType.Program, "UPDUSER", Authorities.AllBits);
        using var session = Session(user: "UPDUSER"); var before = Target(); Assert.True(session.Execute("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE)").IsError); Assert.Equal(before.Source, Target().Source);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Successful_updates_remove_stale_signatures_and_failures_keep_them(bool service)
    {
        if (service) Service(); else Program();
        var name = service ? "SERVICE" : "CALLER"; var type = service ? ObjectType.ServiceProgram : ObjectType.Program;
        var ca = _system.Certificates.CreateAuthority("UPDCA", "Update CA"); var signer = _system.Certificates.Issue("UPDSIGN", "Update signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", name, type, signer.Id); var original = Target(service);
        Assert.True(Command($"{(service ? "UPDSRVPGM SRVPGM" : "UPDPGM PGM")}(QGPL/{name}) MODULE(MISSING)").IsError); Assert.Equal(original.Signature, Target(service).Signature);
        Ok(Command($"{(service ? "UPDSRVPGM SRVPGM" : "UPDPGM PGM")}(QGPL/{name}) MODULE(*NONE)")); Assert.Null(Target(service).Signature);
    }
    [Fact]
    public void Publishing_a_stale_prepared_update_does_not_overwrite_concurrent_changes()
    {
        Program(); var expected = Target(); var current = Target(); current.Description = "Concurrent edit"; _system.Objects.Update(current);
        var store = new RpgObjectUpdateStore(_system.Connections, _system.ObjectSigning); Assert.Equal("IPC0130", Assert.Throws<CpfException>(() => store.Publish(expected, expected.Source!, expected.Attribute!)).MessageId);
        Assert.Equal("Concurrent edit", Target().Description); Assert.Equal(current.Source, Target().Source);
    }
    [Fact]
    public void Cancellation_before_publication_preserves_the_complete_original_object()
    {
        Program(); var before = Target(); var store = new RpgObjectUpdateStore(_system.Connections, _system.ObjectSigning);
        Assert.Throws<OperationCanceledException>(() => store.Publish(before, before.Source!, before.Attribute!, new CancellationToken(true))); Assert.Equal(before.Source, Target().Source); Assert.Equal(before.Changed, Target().Changed);
    }
    [Theory]
    [InlineData("UPDPGM")] [InlineData("UPDSRVPGM")]
    [InlineData("UPDPGM PGM(QGPL/CALLER)")]
    [InlineData("UPDSRVPGM SRVPGM(QGPL/SERVICE)")]
    public void Update_commands_require_targets_and_explicit_module_operands(string command) => Assert.True(Command(command).IsError);
    [Theory]
    [InlineData("ACTGRP(*CALLER)")] [InlineData("SRVPGMLIB(*LIBL)")]
    [InlineData("RPLLIB(*INVALID)")] [InlineData("OPTION(*UNRSLVREF)")]
    public void Unsupported_options_do_not_change_the_program(string option)
    {
        Program(); var before = Target(); Assert.True(Command("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE) " + option).IsError); Assert.Equal(before.Source, Target().Source);
    }
    [Fact]
    public void Owner_group_members_can_update_without_all_object_special_authority()
    {
        Program(); _system.Security.Profiles.Create(new UserProfile { Name = "UPDGROUP" }); _system.Security.Profiles.Create(new UserProfile { Name = "UPDUSER", GroupProfile = "UPDGROUP" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("UPDUSER"), "UpdateUser45"); var descriptor = Target(); descriptor.Owner = "UPDGROUP"; _system.Objects.Update(descriptor);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "UPDUSER", Authorities.AllBits);
        using var session = Session(user: "UPDUSER"); Ok(session.Execute("UPDPGM PGM(QGPL/CALLER) MODULE(*NONE)")); Count(session, 1);
    }
    [Fact]
    public void Retained_program_selection_precedes_binding_the_updated_entry_interface()
    {
        var source = "**free\ndcl-pi *n;\nP char(4);\nend-pi;\nsndpgmmsg P;\nreturn;";
        Module("MAIN", source); Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN) ACTGRP(UPDATES)"));
        using var old = Session(); var buffer = new ProgramBuffer(CodePage.FromCcsid(37).GetBytes("ABCD"), 37);
        var value = new ProgramArgument(() => buffer, _ => { }, v => v, "*CHAR", 4, 0, () => buffer);
        var initial = old.ExecuteProgram("QGPL/CALLER", [value]); Ok(initial); Assert.Equal("ABCD", initial.Message?.Trim());
        Module("MAIN", source.Replace("char(4)", "char(8)")); Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(MAIN)"));
        var retained = old.ExecuteProgram("QGPL/CALLER", [value]); Ok(retained); Assert.Equal("ABCD", retained.Message?.Trim());
        using var fresh = Session(); Assert.True(fresh.ExecuteProgram("QGPL/CALLER", [value]).IsError);
        Ok(old.Execute("RCLACTGRP ACTGRP(UPDATES)")); Assert.True(old.ExecuteProgram("QGPL/CALLER", [value]).IsError);
    }
    [Fact]
    public void Newly_introduced_imports_can_be_resolved_by_update_directories()
    {
        Program(); Module("HELPER", "**free\nctl-opt nomain;\ndcl-pr INNER int(10);\nend-pr;\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\nreturn INNER()+1;\nend-proc;");
        Module("PROVIDER", Constant("INNER", 7)); var directories = new BindingDirectoryStore(_system.Connections); directories.Create("QGPL", "PROVIDERS"); directories.Add("QGPL", "PROVIDERS", [new("QGPL", "PROVIDER", ObjectType.Module)]);
        Ok(Command("UPDPGM PGM(QGPL/CALLER) MODULE(HELPER) BNDDIR(PROVIDERS)")); using var session = Session(); Count(session, 8);
    }
    [Fact]
    public void Modified_replacement_signatures_are_checked_before_any_update_is_published()
    {
        Program(); var before = Target(); var ca = _system.Certificates.CreateAuthority("MODCA", "Module CA"); var signer = _system.Certificates.Issue("MODSIGN", "Module signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "HELPER", ObjectType.Module, signer.Id);
        var module = _system.Objects.GetRequired("QGPL", "HELPER", ObjectType.Module); module.Source += " "; _system.Objects.Update(module);
        var result = Command("UPDPGM PGM(QGPL/CALLER) MODULE(HELPER)"); Assert.True(result.IsError); Assert.Contains("integrity", result.Message); Assert.Equal(before.Source, Target().Source);
    }
    public void Dispose() => _system.Dispose();
}
