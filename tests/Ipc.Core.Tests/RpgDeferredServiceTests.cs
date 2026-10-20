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

public sealed class RpgDeferredServiceTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Counter = "**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;";
    private const string Main = "**free\ndcl-pi *n;\nRUN char(1);\nend-pi;\ndcl-pr NEXT int(10);\nend-pr;\ndcl-s N int(10);\nif RUN='Y';\neval N=NEXT();\nsndpgmmsg %char(N);\nelse;\nsndpgmmsg 'READY';\nendif;\nreturn;";
    private const string Wrapper = "**free\nctl-opt nomain;\ndcl-pr NEXT int(10);\nend-pr;\ndcl-proc FORWARD export;\ndcl-pi *n int(10) end-pi;\nreturn NEXT();\nend-proc;";
    public RpgDeferredServiceTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DeferredFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Module(string name, string source)
    {
        Save(name, source); if (_system.Objects.Exists("QGPL", name, ObjectType.Module)) _system.Objects.Delete("QGPL", name, ObjectType.Module);
        Ok(Command($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Service(string name = "SERVICE", string source = Counter, string dependencies = "*NONE", string group = "*CALLER")
    {
        Module("M" + name, source);
        if (_system.Objects.Exists("QGPL", name, ObjectType.ServiceProgram)) _system.Objects.Delete("QGPL", name, ObjectType.ServiceProgram);
        Ok(Command($"CRTSRVPGM SRVPGM(QGPL/{name}) MODULE(M{name}) EXPORT(*ALL) BNDSRVPGM({dependencies}) ACTGRP({group})"));
    }
    private void Program(string name = "CALLER", string source = Main, string services = "(SERVICE *DEFER)", string group = "CALLERS", string directories = "*NONE")
    {
        Save(name, source); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM({services}) BNDDIR({directories}) ACTGRP({group})"));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static CommandResult Call(ExecutionSession session, string run = "Y", string name = "CALLER") => session.Execute($"CALL PGM(QGPL/{name}) PARM('{run}')");
    private static void Count(ExecutionSession session, int expected, string name = "CALLER")
    { var result = Call(session, name: name); Ok(result); Assert.Equal(expected.ToString(), result.Message?.Trim()); }
    private RpgBoundProgram Image(string name = "CALLER") => RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", name, ObjectType.Program).Source!);
    private void Marker()
    {
        Save("MARK", "PGM\nCRTDTAARA DTAARA(QGPL/EFFECT) TYPE(*CHAR) LEN(1)\nENDPGM");
        Ok(Command("CRTCLPGM PGM(QGPL/MARK) SRCFILE(QGPL/SOURCE)"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Deferred_calls_share_storage_and_reclaim_in_their_own_activation_group(int ccsid)
    {
        Service(group: "SERVICES"); Program(); Program("SECOND", group: "OTHERS");
        Assert.Equal(3, Image().Version); Assert.Equal("*DEFER", Assert.Single(Image().Services).Activation);
        using var session = Session(ccsid); Ok(Call(session, "N")); Count(session, 1); Count(session, 2, "SECOND");
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 3);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, 1, "SECOND");
    }
    [Theory]
    [InlineData("missing")] [InlineData("signature")] [InlineData("interface")] [InlineData("authority")]
    [InlineData("tamper")] [InlineData("trust")]
    public void Unused_deferred_imports_do_not_lookup_or_check_the_provider_until_first_call(string defect)
    {
        Service(); Marker(); Program(source: Main.Replace("if RUN='Y';", "call 'QGPL/MARK';\nif RUN='Y';"));
        var user = "QSECOFR";
        if (defect == "missing") _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        if (defect == "signature") Service(source: Counter.Replace("NEXT", "OTHER"));
        if (defect == "interface") Service(source: Counter.Replace("int(10) end-pi", "char(4) end-pi").Replace("return COUNT;", "return 'BAD';"));
        if (defect == "authority")
        {
            user = "DEFUSER"; _system.Security.Profiles.Create(new UserProfile { Name = user });
            _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(user), "DeferredUser45");
            _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, user, Authorities.AllBits);
            _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, user, AuthorityBit.None);
        }
        if (defect is "tamper" or "trust")
        {
            var ca = _system.Certificates.CreateAuthority("DEFCA", "Deferred CA");
            var signer = _system.Certificates.Issue("DEFSIGN", "Deferred signer", ca.Id, CertificatePurpose.ObjectSigning);
            _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
            if (defect == "trust") _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
            else { var descriptor = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram); descriptor.Source += " "; _system.Objects.Update(descriptor); }
        }
        using var session = Session(user: user); var unused = Call(session, "N"); Ok(unused); Assert.Equal("READY", unused.Message?.Trim());
        Assert.True(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea)); _system.Objects.Delete("QGPL", "EFFECT", ObjectType.DataArea);
        Assert.True(Call(session).IsError); Assert.True(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Fact]
    public void Immediate_import_failures_still_precede_caller_effects()
    {
        Service(); Marker(); Program(source: Main.Replace("if RUN='Y';", "call 'QGPL/MARK';\nif RUN='Y';"), services: "(SERVICE *IMMED)");
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); using var session = Session();
        Assert.True(Call(session, "N").IsError); Assert.False(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Theory]
    [InlineData("*IMMED")] [InlineData("*DEFER")]
    public void A_deferred_parent_checks_immediate_children_before_its_body_and_defers_other_children(string activation)
    {
        Service("LEAF"); Marker(); Service("PARENT", Wrapper.Replace("return NEXT();", "call 'QGPL/MARK';\nreturn NEXT();"), $"(LEAF {activation})", "PARENTS");
        Program(source: Main.Replace("NEXT", "FORWARD"), services: "(PARENT *DEFER)");
        _system.Objects.Delete("QGPL", "LEAF", ObjectType.ServiceProgram); using var session = Session(); Ok(Call(session, "N"));
        Assert.True(Call(session).IsError); Assert.Equal(activation == "*DEFER", _system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Theory]
    [InlineData("*IMMED")] [InlineData("*DEFER")]
    public void Deferred_caller_children_belong_to_the_parent_group(string activation)
    {
        Service("LEAF"); Service("PARENT", Wrapper, $"(LEAF {activation})", "PARENTS");
        Program(source: Main.Replace("NEXT", "FORWARD"), services: "(PARENT *DEFER)"); Program("DIRECT", services: "(LEAF *DEFER)");
        using var session = Session(); Count(session, 1); Count(session, 1, "DIRECT"); Count(session, 2);
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, 3);
        Ok(session.Execute("RCLACTGRP ACTGRP(PARENTS)")); Count(session, 1);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Directory_activation_modes_survive_capture_and_directory_deletion(bool sourceControl)
    {
        Service(); var directory = new BindingDirectoryStore(_system.Connections); directory.Create("QGPL", "SERVICES");
        directory.Add("QGPL", "SERVICES", [new("QGPL", "SERVICE", ObjectType.ServiceProgram, "*DEFER")]);
        var display = Command("DSPBNDDIR BNDDIR(SERVICES)"); Ok(display); Assert.Contains("*DEFER", Assert.Single(display.Listing!));
        Program(source: sourceControl ? Main.Replace("**free", "**free\nctl-opt bnddir('QGPL/SERVICES');") : Main, services: "*NONE", directories: sourceControl ? "*NONE" : "SERVICES");
        Assert.Equal("*DEFER", Assert.Single(Image().Services).Activation); _system.Objects.Delete("QGPL", "SERVICES", ObjectType.BindingDirectory);
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); using var session = Session(); Ok(Call(session, "N")); Assert.True(Call(session).IsError);
    }
    [Theory]
    [InlineData("CRTPGM")] [InlineData("CRTBNDRPG")] [InlineData("CRTSRVPGM")]
    public void Creation_commands_accept_flat_and_parenthesized_activation_tuples(string command)
    {
        Service(); Module("MAIN", command == "CRTSRVPGM" ? Wrapper : Main); Save("CALLER", Main);
        var prefix = command == "CRTSRVPGM" ? "CRTSRVPGM SRVPGM(QGPL/PARENT) MODULE(MAIN) EXPORT(*ALL)" : command == "CRTPGM" ? "CRTPGM PGM(QGPL/CALLER) MODULE(MAIN)" : "CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE)";
        Ok(Command(prefix + " BNDSRVPGM(SERVICE *DEFER)"));
        if (command == "CRTSRVPGM") Assert.Equal("*DEFER", Assert.Single(RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", "PARENT", ObjectType.ServiceProgram).Source!).Services).Activation);
        else Assert.Equal("*DEFER", Assert.Single(Image().Services).Activation);
    }
    [Theory]
    [InlineData("(SERVICE *SAME)")] [InlineData("(SERVICE *BAD)")] [InlineData("(SERVICE *DEFER EXTRA)")] [InlineData("(SERVICE *DEFER) (QGPL/SERVICE *IMMED)")]
    public void Invalid_creation_modes_or_duplicate_providers_reject_atomically(string services)
    {
        Service(); Save("CALLER", Main); Assert.True(Command("CRTBNDRPG PGM(QGPL/CALLER) SRCFILE(QGPL/SOURCE) BNDSRVPGM(" + services + ")").IsError);
        Assert.False(_system.Objects.Exists("QGPL", "CALLER", ObjectType.Program));
    }
    [Theory]
    [InlineData("omitted", "*DEFER")] [InlineData("SERVICE", "*DEFER")] [InlineData("(SERVICE *SAME)", "*DEFER")]
    [InlineData("(SERVICE *IMMED)", "*IMMED")] [InlineData("(SERVICE *DEFER)", "*DEFER")]
    public void Program_updates_preserve_or_explicitly_change_existing_activation(string option, string expected)
    {
        Service(); Program(); Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE)" + (option == "omitted" ? "" : " BNDSRVPGM(" + option + ")")));
        Assert.Equal(expected, Assert.Single(Image().Services).Activation); Assert.Equal(expected == "*DEFER" ? 3 : 2, Image().Version);
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); using var session = Session(); Assert.Equal(expected == "*IMMED", Call(session, "N").IsError);
    }
    [Theory]
    [InlineData("*SAME", "*DEFER")] [InlineData("*IMMED", "*IMMED")] [InlineData("*DEFER", "*DEFER")]
    public void Service_updates_preserve_or_change_dependency_activation(string mode, string expected)
    {
        Service("LEAF"); Service("PARENT", Wrapper, "(LEAF *DEFER)");
        Ok(Command($"UPDSRVPGM SRVPGM(PARENT) MODULE(*NONE) BNDSRVPGM((LEAF {mode}))"));
        var image = RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", "PARENT", ObjectType.ServiceProgram).Source!);
        Assert.Equal(expected, Assert.Single(image.Services).Activation); Assert.Single(image.Restore().ServiceReferences);
    }
    [Theory]
    [InlineData("null")] [InlineData("unknown")] [InlineData("case")] [InlineData("same")] [InlineData("version")]
    public void Invalid_saved_activation_modes_are_rejected_for_programs_and_services(string defect)
    {
        Service("LEAF"); Service("SERVICE", Wrapper, "(LEAF *DEFER)"); Program(source: Main.Replace("NEXT", "FORWARD"));
        foreach (var descriptor in new[] { _system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program), _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram) })
        {
            var root = JsonNode.Parse(descriptor.Source!)!; var reference = root["services"]![0]!;
            if (defect == "version") root["version"] = 2;
            else reference["activation"] = defect switch { "null" => null, "case" => "*defer", "same" => "*SAME", _ => "*OTHER" };
            if (descriptor.ObjectType == ObjectType.Program) Assert.Throws<InvalidDataException>(() => RpgBoundProgram.Deserialize(root.ToJsonString()));
            else Assert.Throws<InvalidDataException>(() => RpgServiceImage.Deserialize(root.ToJsonString()));
        }
    }
    [Fact]
    public void Immediate_only_images_keep_the_version_two_shape_and_restore_without_activation_properties()
    {
        Service("LEAF"); Service("SERVICE", Wrapper, "LEAF"); Program(source: Main.Replace("NEXT", "FORWARD"), services: "SERVICE");
        Assert.Equal(2, Image().Version); Assert.DoesNotContain("\"activation\"", Image().Serialize());
        var image = RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram).Source!);
        Assert.Equal(2, image.Version); Assert.DoesNotContain("\"activation\"", image.Serialize());
        using var session = Session(); Count(session, 1);
    }
    [Fact]
    public void Deferred_first_use_picks_the_current_provider_then_retains_it_until_reclaim()
    {
        Service(group: "SERVICES"); Program(); using var session = Session(); Ok(Call(session, "N"));
        Module("MSERVICE", Counter.Replace("COUNT+1", "COUNT+10")); Ok(Command("UPDSRVPGM SRVPGM(SERVICE) MODULE(MSERVICE)")); Count(session, 10);
        Module("MSERVICE", Counter.Replace("COUNT+1", "COUNT+100")); Ok(Command("UPDSRVPGM SRVPGM(SERVICE) MODULE(MSERVICE)")); Count(session, 20);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, 100);
    }
    [Fact]
    public void A_deferred_import_reuses_a_service_already_activated_by_an_immediate_path()
    {
        Service("LEAF", group: "LEAVES"); Service("PARENT", Wrapper, "LEAF");
        Program(source: Main.Replace("NEXT", "FORWARD"), services: "PARENT"); Program("DIRECT", services: "(LEAF *DEFER)");
        using var session = Session(); Count(session, 1); Count(session, 2, "DIRECT"); Count(session, 3);
    }
    [Fact]
    public void Deferred_cycles_preserve_reentrant_storage_without_recursive_activation()
    {
        Service("FIRST", Counter.Replace("NEXT", "AA"), group: "FIRSTGRP");
        Service("SECOND", Wrapper.Replace("NEXT", "AA").Replace("FORWARD", "BB"), "(FIRST *DEFER)", "SECONDGRP");
        var cyclic = Counter.Replace("dcl-s COUNT", "dcl-pr BB int(10);\nend-pr;\ndcl-s COUNT").Replace("NEXT", "AA").Replace("return COUNT;", "if COUNT=1;\nreturn BB();\nendif;\nreturn COUNT;");
        Service("FIRST", cyclic, "(SECOND *DEFER)", "FIRSTGRP"); Program(source: Main.Replace("NEXT", "AA"), services: "(FIRST *DEFER)");
        using var session = Session(); Count(session, 2); Count(session, 3);
    }
    [Fact]
    public void Rebinding_activation_policy_does_not_change_an_existing_program_activation()
    {
        Service(); Program(); using var old = Session(); Ok(Call(old, "N"));
        Ok(Command("UPDPGM PGM(CALLER) MODULE(*NONE) BNDSRVPGM((SERVICE *IMMED))"));
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); Ok(Call(old, "N"));
        using var fresh = Session(); Assert.True(Call(fresh, "N").IsError);
        Ok(old.Execute("RCLACTGRP ACTGRP(CALLERS)")); Assert.True(Call(old, "N").IsError);
    }
    public void Dispose() => _system.Dispose();
}
