using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Services;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgServiceDependencyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Leaf = "**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;";
    private static string Wrapper(string export = "BUMP", string import = "NEXT") => $"**free\nctl-opt nomain;\ndcl-pr {import} int(10);\nend-pr;\ndcl-proc {export} export;\ndcl-pi *n int(10) end-pi;\nreturn {import}();\nend-proc;";
    private static string MainSource(string procedure = "BUMP") => $"**free\ndcl-pr {procedure} int(10);\nend-pr;\ndcl-s N int(10);\neval N={procedure}();\nsndpgmmsg %char(N);\nreturn;";
    public RpgServiceDependencyTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DependencyFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Service(string name, string source, string dependencies = "*NONE", string group = "*CALLER", string? binder = null)
    {
        var module = "M" + name; Save(module, source); Ok(Command($"CRTRPGMOD MODULE(QGPL/{module}) SRCFILE(QGPL/SOURCE)"));
        if (_system.Objects.Exists("QGPL", name, ObjectType.ServiceProgram)) _system.Objects.Delete("QGPL", name, ObjectType.ServiceProgram);
        if (binder is not null) Save("BINDER", binder);
        Ok(Command($"CRTSRVPGM SRVPGM(QGPL/{name}) MODULE(QGPL/{module}) BNDSRVPGM({dependencies}) ACTGRP({group}) " +
            (binder is null ? "EXPORT(*ALL)" : "SRCFILE(QGPL/SOURCE) SRCMBR(BINDER)")));
        _system.Objects.Delete("QGPL", module, ObjectType.Module); _files.RemoveMember("QGPL", "SOURCE", module);
    }
    private void Program(string name = "CALLER", string service = "PARENT", string procedure = "BUMP", string group = "CALLERS", string? source = null)
    {
        Save(name, source ?? MainSource(procedure)); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM(QGPL/{service}) ACTGRP({group})"));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, string name, int expected)
    { var result = session.Execute("CALL QGPL/" + name); Ok(result); Assert.Equal(expected.ToString(), result.Message?.Trim()); }
    private RpgServiceImage Image(string name) => RpgServiceImage.Deserialize(_system.Objects.GetRequired("QGPL", name, ObjectType.ServiceProgram).Source!);
    private static string Binder(string signature, string level = "*CURRENT") => $"STRPGMEXP PGMLVL({level}) SIGNATURE('{signature}')\nEXPORT SYMBOL(NEXT)\nENDPGMEXP";

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Service_dependencies_execute_transitively_from_saved_references(int ccsid)
    {
        Service("LEAF", Leaf); Service("MIDDLE", Wrapper(), "LEAF"); Service("TOP", Wrapper("TOPCALL", "BUMP"), "MIDDLE"); Program(service: "TOP", procedure: "TOPCALL");
        var image = Image("TOP"); Assert.Equal(2, image.Version); Assert.Equal("QGPL", Assert.Single(image.Services).Library); Assert.Equal("MIDDLE", image.Services[0].Name);
        Assert.Single(image.Modules); Assert.Single(image.Restore().ServiceReferences);
        var display = Command("DSPSRVPGM SRVPGM(TOP)"); Ok(display); Assert.Contains("services: QGPL/MIDDLE", display.Message);
        using var session = Session(ccsid); Count(session, "CALLER", 1); Count(session, "CALLER", 2);
    }
    [Fact]
    public void Caller_dependencies_follow_the_parent_services_named_group()
    {
        Service("LEAF", Leaf); Service("FIRST", Wrapper(), "LEAF", "SERVICES"); Service("SECOND", Wrapper(), "LEAF", "SERVICES");
        Program("ONE", "FIRST", group: "ONEGRP"); Program("TWO", "SECOND", group: "TWOGRP"); Program("DIRECT", "LEAF", "NEXT", "ONEGRP");
        using var session = Session(); Count(session, "ONE", 1); Count(session, "TWO", 2); Count(session, "DIRECT", 1);
        Ok(session.Execute("RCLACTGRP ACTGRP(ONEGRP)")); Count(session, "TWO", 3);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "ONE", 1);
    }
    [Fact]
    public void Dependency_storage_is_isolated_between_caller_groups_and_jobs()
    {
        Service("LEAF", Leaf); Service("PARENT", Wrapper(), "LEAF"); Program("ONE", group: "ONEGRP"); Program("TWO", group: "TWOGRP");
        using var one = Session(); using var two = Session(); Count(one, "ONE", 1); Count(one, "TWO", 1); Count(two, "ONE", 1); Count(one, "ONE", 2);
    }
    [Fact]
    public void Cyclic_dependencies_preflight_without_recursion_and_share_reentrant_state()
    {
        Service("FIRST", Leaf.Replace("NEXT", "AA"), group: "FIRSTGRP"); Service("SECOND", Wrapper("BB", "AA"), "FIRST", "SECONDGRP");
        var cyclic = Leaf.Replace("dcl-s COUNT", "dcl-pr BB int(10);\nend-pr;\ndcl-s COUNT").Replace("NEXT", "AA").Replace("return COUNT;", "if COUNT=1;\nreturn BB();\nendif;\nreturn COUNT;");
        Service("FIRST", cyclic, "SECOND", "FIRSTGRP"); Program(service: "FIRST", procedure: "AA"); using var session = Session(); Count(session, "CALLER", 2); Count(session, "CALLER", 3);
    }
    [Theory]
    [InlineData("missing")] [InlineData("signature")] [InlineData("interface")]
    public void Transitive_activation_failures_precede_main_side_effects(string defect)
    {
        Service("LEAF", Leaf, binder: Binder("V1")); Service("PARENT", Wrapper(), "LEAF");
        Save("MARK", "PGM\nCRTDTAARA DTAARA(QGPL/EFFECT) TYPE(*CHAR) LEN(1)\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/MARK) SRCFILE(QGPL/SOURCE)"));
        Program(source: MainSource().Replace("eval N=BUMP();", "call 'QGPL/MARK';\neval N=BUMP();"));
        if (defect == "missing") _system.Objects.Delete("QGPL", "LEAF", ObjectType.ServiceProgram);
        else Service("LEAF", defect == "interface" ? Leaf.Replace("int(10) end-pi", "char(4) end-pi").Replace("return COUNT;", "return 'BAD';") : Leaf, binder: Binder(defect == "signature" ? "V2" : "V1"));
        using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError); Assert.False(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
        Ok(session.Execute("CALL QGPL/MARK")); Assert.True(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Fact]
    public void Previous_signature_can_satisfy_a_saved_dependency()
    {
        Service("LEAF", Leaf, binder: Binder("V1")); Service("PARENT", Wrapper(), "LEAF"); Program();
        Service("LEAF", Leaf, binder: Binder("V2") + "\n" + Binder("V1", "*PRV")); using var session = Session(); Count(session, "CALLER", 1);
    }
    [Fact]
    public void Reclaiming_a_named_dependency_refreshes_parent_bindings_without_discarding_the_parent()
    {
        Service("LEAF", Leaf, group: "LEAVES"); Service("PARENT", Wrapper(), "LEAF", "PARENTS"); Program();
        using var session = Session(); Count(session, "CALLER", 1); Count(session, "CALLER", 2); Ok(session.Execute("RCLACTGRP ACTGRP(LEAVES)")); Count(session, "CALLER", 1);
    }
    [Fact]
    public void Transitive_authority_is_rechecked_without_losing_completed_updates()
    {
        Service("LEAF", Leaf); Service("PARENT", Wrapper(), "LEAF"); Program(); _system.Security.Profiles.Create(new UserProfile { Name = "DEPUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("DEPUSER"), "DependencyUser45"); _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "DEPUSER", Authorities.AllBits);
        using var session = Session(user: "DEPUSER"); Count(session, "CALLER", 1);
        _system.Security.Authority.Grant("QGPL", "LEAF", ObjectType.ServiceProgram, "DEPUSER", AuthorityBit.None); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
        _system.Security.Authority.Grant("QGPL", "LEAF", ObjectType.ServiceProgram, "DEPUSER", Authorities.UseBits); Count(session, "CALLER", 2);
    }
    [Fact]
    public void Unused_dependencies_are_excluded_from_saved_service_images()
    {
        Service("LEAF", Leaf); Service("PARENT", Leaf.Replace("NEXT", "BUMP"), "LEAF"); Assert.Equal(1, Image("PARENT").Version); Assert.Empty(Image("PARENT").Services);
        _system.Objects.Delete("QGPL", "LEAF", ObjectType.ServiceProgram); Program(); using var session = Session(); Count(session, "CALLER", 1);
    }
    [Theory]
    [InlineData("unknown")] [InlineData("null")] [InlineData("count")] [InlineData("exports")] [InlineData("identity")] [InlineData("version")] [InlineData("duplicate")] [InlineData("signature")]
    public void Malformed_or_incompatible_saved_dependencies_reject(string defect)
    {
        Service("LEAF", Leaf); Service("PARENT", Wrapper(), "LEAF"); Program();
        var descriptor = _system.Objects.GetRequired("QGPL", "PARENT", ObjectType.ServiceProgram); var root = JsonNode.Parse(descriptor.Source!)!.AsObject();
        switch (defect)
        {
            case "unknown": root["services"]![0]!["extra"] = true; break;
            case "null": root["services"]![0] = null; break;
            case "count": while (root["services"]!.AsArray().Count < 17) root["services"]!.AsArray().Add(root["services"]![0]!.DeepClone()); break;
            case "exports": while (root["services"]![0]!["exports"]!.AsArray().Count < 257) root["services"]![0]!["exports"]!.AsArray().Add("EXTRA"); break;
            case "identity": root["services"]![0]!["library"] = "qgpl"; break;
            case "version": root["version"] = 1; break;
            case "duplicate": root["services"]!.AsArray().Add(root["services"]![0]!.DeepClone()); break;
            case "signature": root["services"]![0]!["signature"] = new string('0', 32); break;
        }
        descriptor.Source = root.ToJsonString(); _system.Objects.Update(descriptor); using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    [Fact]
    public void Old_version_one_service_images_without_dependency_fields_still_restore()
    {
        Service("LEAF", Leaf); var node = JsonNode.Parse(_system.Objects.GetRequired("QGPL", "LEAF", ObjectType.ServiceProgram).Source!)!.AsObject(); node.Remove("services");
        Assert.Empty(RpgServiceImage.Deserialize(node.ToJsonString()).Restore().ServiceReferences);
    }
    [Fact]
    public void Excessive_dependency_depth_fails_before_procedure_execution()
    {
        Service("S064", Leaf.Replace("NEXT", "F064"));
        for (var i = 63; i >= 0; i--) Service("S" + i.ToString("D3"), Wrapper("F" + i.ToString("D3"), "F" + (i + 1).ToString("D3")), "S" + (i + 1).ToString("D3"));
        Program(service: "S000", procedure: "F000"); using var session = Session(); var result = session.Execute("CALL QGPL/CALLER"); Assert.True(result.IsError); Assert.Contains("64 levels", result.Message);
    }
    public void Dispose() => _system.Dispose();
}
