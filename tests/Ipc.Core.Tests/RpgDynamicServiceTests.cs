using System.Text.Json.Nodes;
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
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgDynamicServiceTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Counter = "**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc BUMP export;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;";
    private const string Main = "**free\ndcl-pr BUMP int(10);\nend-pr;\ndcl-s N int(10);\neval N=BUMP();\nsndpgmmsg %char(N);\nreturn;";
    public RpgDynamicServiceTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DynamicFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Module(string name, string source)
    {
        Save(name, source); Ok(Command($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)"));
    }
    private void Service(string name = "SERVICE", string source = Counter, string group = "*CALLER", string? binder = null)
    {
        Module("HELPER", source);
        if (_system.Objects.Exists("QGPL", name, ObjectType.ServiceProgram)) _system.Objects.Delete("QGPL", name, ObjectType.ServiceProgram);
        if (binder is not null) Save("BINDER", binder);
        Ok(Command($"CRTSRVPGM SRVPGM(QGPL/{name}) MODULE(QGPL/HELPER) ACTGRP({group}) " + (binder is null ? "EXPORT(*ALL)" : "SRCFILE(QGPL/SOURCE) SRCMBR(BINDER)")));
        _system.Objects.Delete("QGPL", "HELPER", ObjectType.Module);
    }
    private void Program(string name = "CALLER", string source = Main, string group = "CALLERS", string services = "QGPL/SERVICE", bool boundSource = false)
    {
        Save(name, source);
        if (boundSource) Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM({services}) ACTGRP({group})"));
        else
        {
            Module(name, source);
            Ok(Command($"CRTPGM PGM(QGPL/{name}) MODULE(QGPL/{name}) BNDSRVPGM({services}) ACTGRP({group})"));
        }
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, string program, int expected)
    {
        var result = session.Execute("CALL QGPL/" + program); Ok(result); Assert.Equal(expected.ToString(), result.Message?.Trim());
    }
    private static string Binder(string signature, string symbols, string level = "*CURRENT") => $"STRPGMEXP PGMLVL({level}) SIGNATURE('{signature}')\n" + string.Join('\n', symbols.Split(' ').Select(s => "EXPORT SYMBOL(" + s + ")")) + "\nENDPGMEXP";

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Distinct_programs_share_one_service_activation_in_the_same_group(int ccsid, bool boundSource)
    {
        Service(); Program("FIRST", boundSource: boundSource); Program("SECOND", boundSource: boundSource);
        using var session = Session(ccsid); Count(session, "FIRST", 1); Count(session, "SECOND", 2); Count(session, "FIRST", 3);
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)")); Count(session, "SECOND", 1);
    }
    [Fact]
    public void Caller_service_storage_is_isolated_by_group_and_job()
    {
        Service(); Program("FIRST", group: "FIRSTGRP"); Program("SECOND", group: "SECONDGRP"); using var first = Session(); using var second = Session();
        Count(first, "FIRST", 1); Count(first, "SECOND", 1); Count(first, "FIRST", 2); Count(second, "FIRST", 1);
    }
    [Fact]
    public void Named_service_storage_is_shared_across_caller_groups_and_survives_caller_reclaim()
    {
        Service(group: "SERVICES"); Program("FIRST", group: "FIRSTGRP"); Program("SECOND", group: "SECONDGRP"); using var session = Session();
        Count(session, "FIRST", 1); Count(session, "SECOND", 2); Ok(session.Execute("RCLACTGRP ACTGRP(FIRSTGRP)")); Count(session, "FIRST", 3);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "SECOND", 1); Count(session, "FIRST", 2);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Entry_LR_and_program_failure_leave_shared_service_globals_alive(bool failure)
    {
        Service(); Program("FIRST", Main.Replace("return;", failure ? "eval N=1/0;" : "seton LR;")); Program("SECOND"); using var session = Session();
        var result = session.Execute("CALL QGPL/FIRST"); Assert.Equal(failure, result.IsError); Count(session, "SECOND", 2);
    }
    [Fact]
    public void New_caller_groups_destroy_their_caller_services_at_return()
    {
        Service(); Program(group: "*NEW"); using var session = Session(); Count(session, "CALLER", 1); Count(session, "CALLER", 1);
    }
    [Fact]
    public void Trusted_calls_without_a_job_share_within_a_call_and_release_after_it()
    {
        Service(); Program(source: Main.Replace("eval N=BUMP();", "eval N=BUMP()+BUMP();"));
        var command = new CommandService(_system); var first = command.Execute("CALL QGPL/CALLER"); Ok(first); Assert.Equal("3", first.Message?.Trim());
        var second = command.Execute("CALL QGPL/CALLER"); Ok(second); Assert.Equal("3", second.Message?.Trim());
    }
    [Fact]
    public void Programs_capture_modules_but_retain_only_dynamic_service_references()
    {
        Service(); Program(); var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!);
        Assert.Equal(2, image.Version); Assert.Single(image.Modules); Assert.Single(image.Services); Assert.DoesNotContain("COUNT", image.Modules[0].Source);
        _files.RemoveMember("QGPL", "SOURCE", "CALLER"); _files.RemoveMember("QGPL", "SOURCE", "HELPER"); _system.Objects.Delete("QGPL", "CALLER", ObjectType.Module);
        using var session = Session(); Count(session, "CALLER", 1);
    }
    [Theory]
    [InlineData("missing")] [InlineData("signature")] [InlineData("interface")]
    public void Activation_checks_services_before_main_side_effects(string defect)
    {
        Service(binder: Binder("V1", "BUMP"));
        Save("MARK", "PGM\nCRTDTAARA DTAARA(QGPL/EFFECT) TYPE(*CHAR) LEN(1)\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/MARK) SRCFILE(QGPL/SOURCE)"));
        Program(source: Main.Replace("eval N=BUMP();", "call 'QGPL/MARK';\neval N=BUMP();"));
        if (defect == "missing") _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        else Service(source: defect == "interface" ? Counter.Replace("int(10) end-pi", "char(4) end-pi").Replace("return COUNT;", "return 'BAD';") : Counter, binder: Binder(defect == "signature" ? "V2" : "V1", "BUMP"));
        using var session = Session(); var result = session.Execute("CALL QGPL/CALLER"); Assert.True(result.IsError, result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
        Ok(session.Execute("CALL QGPL/MARK")); Assert.True(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Fact]
    public void Matching_previous_signature_accepts_an_append_only_service_upgrade()
    {
        Service(binder: Binder("V1", "BUMP")); Program();
        var extended = Counter + "\ndcl-proc NEXT export;\ndcl-pi *n int(10) end-pi;\nreturn 99;\nend-proc;";
        Service(source: extended, binder: Binder("V2", "BUMP NEXT") + "\n" + Binder("V1", "BUMP", "*PRV"));
        using var session = Session(); Count(session, "CALLER", 1);
    }
    [Fact]
    public void Activated_code_and_policy_are_pinned_until_its_service_group_is_reclaimed()
    {
        Service(group: "SERVICES", binder: Binder("V1", "BUMP")); Program(); using var session = Session(); Count(session, "CALLER", 1);
        Service(source: Counter.Replace("COUNT=COUNT+1", "COUNT=COUNT+10"), group: "OTHER", binder: Binder("V1", "BUMP"));
        Count(session, "CALLER", 2); Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "CALLER", 10);
    }
    [Fact]
    public void A_new_signature_cannot_use_an_older_retained_service_activation()
    {
        Service(group: "SERVICES", binder: Binder("V1", "BUMP")); Program("FIRST"); using var session = Session(); Count(session, "FIRST", 1);
        Service(group: "SERVICES", binder: Binder("V2", "BUMP")); Program("SECOND");
        var result = session.Execute("CALL QGPL/SECOND"); Assert.True(result.IsError); Assert.Contains("signature", result.Message);
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "SECOND", 1);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_interface_mismatch_rejects_binding_before_program_creation(bool boundSource)
    {
        Service(); var wrong = Main.Replace("dcl-pr BUMP int(10)", "dcl-pr BUMP char(4)").Replace("dcl-s N int(10)", "dcl-s N char(4)");
        if (boundSource) Save("WRONG", wrong); else Module("WRONG", wrong);
        var result = Command(boundSource ? "CRTBNDRPG PGM(QGPL/WRONG) SRCFILE(QGPL/SOURCE) BNDSRVPGM(SERVICE)" : "CRTPGM PGM(QGPL/WRONG) MODULE(WRONG) BNDSRVPGM(SERVICE)");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "WRONG", ObjectType.Program));
    }
    [Fact]
    public void Static_module_exports_take_precedence_over_services_and_unused_services_are_not_captured()
    {
        Service(); Module("MAIN", Main); Module("LOCAL", Counter.Replace("return COUNT;", "return 77;"));
        Ok(Command("CRTPGM PGM(QGPL/CALLER) MODULE(MAIN LOCAL) BNDSRVPGM(SERVICE)"));
        var image = RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!); Assert.Empty(image.Services); Assert.Equal(1, image.Version);
        _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram); var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("77", result.Message?.Trim());
    }
    [Fact]
    public void Explicit_service_list_order_selects_the_first_matching_export()
    {
        Service("FIRST", Counter.Replace("return COUNT;", "return 11;")); Service("SECOND", Counter.Replace("return COUNT;", "return 22;"));
        Program(services: "SECOND FIRST"); using var session = Session(); Count(session, "CALLER", 22);
        Assert.Equal("SECOND", Assert.Single(RpgBoundProgram.Deserialize(_system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program).Source!).Services).Name);
    }
    [Theory]
    [InlineData("SERVICE SERVICE")] [InlineData("*LIBL/SERVICE QGPL/SERVICE")]
    public void Duplicate_resolved_service_names_reject_binding(string services)
    {
        Service(); Module("MAIN", Main); Assert.True(Command("CRTPGM PGM(QGPL/BAD) MODULE(MAIN) BNDSRVPGM(" + services + ")").IsError);
        Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.Program));
    }
    [Theory]
    [InlineData("count")] [InlineData("exports")] [InlineData("null")] [InlineData("unknown")] [InlineData("duplicate")]
    [InlineData("case")] [InlineData("identity")] [InlineData("ordinal")] [InlineData("signature")] [InlineData("version")]
    public void Invalid_saved_dynamic_references_reject_before_main_execution(string defect)
    {
        Service(); Program(); var descriptor = _system.Objects.GetRequired("QGPL", "CALLER", ObjectType.Program); var root = JsonNode.Parse(descriptor.Source!)!.AsObject();
        switch (defect)
        {
            case "count": while (root["services"]!.AsArray().Count < 17) root["services"]!.AsArray().Add(root["services"]![0]!.DeepClone()); break;
            case "exports": while (root["services"]![0]!["exports"]!.AsArray().Count < 257) root["services"]![0]!["exports"]!.AsArray().Add("EXTRA"); break;
            case "null": root["services"]![0] = null; break;
            case "unknown": root["services"]![0]!["extra"] = true; break;
            case "case": root["services"]![0]!["Library"] = "QGPL"; break;
            case "identity": root["services"]![0]!["library"] = "qgpl"; break;
            case "ordinal": root["services"]![0]!["exports"]!.AsArray().Insert(0, "EXTRA"); break;
            case "signature": root["services"]![0]!["signature"] = new string('0', 32); break;
            case "version": root["version"] = 1; break;
        }
        descriptor.Source = defect == "duplicate" ? root.ToJsonString().Replace("\"library\":\"QGPL\"", "\"library\":\"QGPL\",\"library\":\"QGPL\"", StringComparison.Ordinal) : root.ToJsonString();
        _system.Objects.Update(descriptor); using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    [Fact]
    public void Live_service_authority_is_rechecked_without_discarding_service_state()
    {
        Service(); Program(); _system.Security.Profiles.Create(new UserProfile { Name = "SRVUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SRVUSER"), "ServiceUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SRVUSER", Authorities.AllBits);
        using var session = Session(user: "SRVUSER"); Count(session, "CALLER", 1);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "SRVUSER", AuthorityBit.None);
        Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "SRVUSER", Authorities.UseBits); Count(session, "CALLER", 2);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Live_service_signing_and_retained_snapshot_trust_are_rechecked(bool trust)
    {
        Service(); Program(); var ca = _system.Certificates.CreateAuthority("DYNCA", "Dynamic signing CA"); var signer = _system.Certificates.Issue("DYNSIGN", "Dynamic signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
        using var session = Session(); Count(session, "CALLER", 1);
        if (trust) _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        else { var changed = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram); changed.Source += " "; _system.Objects.Update(changed); }
        Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    [Fact]
    public void Binding_requires_service_use_authority()
    {
        Service(); Module("MAIN", Main); _system.Security.Profiles.Create(new UserProfile { Name = "SRVUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SRVUSER"), "ServiceUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SRVUSER", Authorities.AllBits); _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "SRVUSER", AuthorityBit.None);
        using var session = Session(user: "SRVUSER"); Assert.True(session.Execute("CRTPGM PGM(QGPL/DENIED) MODULE(QGPL/MAIN) BNDSRVPGM(QGPL/SERVICE)").IsError);
        Assert.False(_system.Objects.Exists("QGPL", "DENIED", ObjectType.Program));
    }
    [Theory]
    [InlineData("*NONE")] [InlineData("'*NONE'")]
    public void None_service_option_preserves_static_program_behavior(string services)
    {
        Program(source: "**free\nsndpgmmsg 'STATIC';\nreturn;", services: services, boundSource: true); var result = Command("CALL QGPL/CALLER"); Ok(result); Assert.Equal("STATIC", result.Message?.Trim());
    }
    [Fact]
    public void Service_module_imports_use_their_own_module_globals()
    {
        Module("TOP", "**free\nctl-opt nomain;\ndcl-pr INNER int(10);\nend-pr;\ndcl-proc BUMP export;\ndcl-pi *n int(10) end-pi;\nreturn INNER();\nend-proc;");
        Module("BASE", Counter.Replace("BUMP", "INNER"));
        Ok(Command("CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(TOP BASE) EXPORT(*ALL)")); Program(); using var session = Session(); Count(session, "CALLER", 1); Count(session, "CALLER", 2);
    }
    [Fact]
    public void Reentrant_service_calls_through_a_dynamic_program_share_the_active_service_state()
    {
        Service(group: "SERVICES", source: Counter.Replace("return COUNT;", "if COUNT=1;\ncall 'QGPL/NESTED';\nendif;\nreturn COUNT;"));
        Program("FIRST"); Program("NESTED"); using var session = Session(); var result = session.Execute("CALL QGPL/FIRST"); Ok(result);
        Assert.Equal(new[] { "2", "2" }, result.Message!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); Count(session, "FIRST", 3);
    }
    [Fact]
    public void Service_scope_begins_after_arguments_are_evaluated_in_the_callers_group()
    {
        Service("MARKS", Counter.Replace("BUMP", "MARK"));
        Service(group: "SERVICES", source: "**free\nctl-opt nomain;\ndcl-proc BUMP export;\ndcl-pi *n int(10);\nP int(10) value;\nend-pi;\nreturn P;\nend-proc;");
        var source = "**free\ndcl-pr BUMP int(10);\nP int(10) value;\nend-pr;\ndcl-pr MARK int(10);\nend-pr;\ndcl-s N int(10);\neval N=BUMP(MARK());\nsndpgmmsg %char(N);\nreturn;";
        Program("FIRST", source, services: "SERVICE MARKS"); Program("SECOND", Main.Replace("BUMP", "MARK"), services: "MARKS");
        using var session = Session(); Count(session, "FIRST", 1); Count(session, "SECOND", 2);
    }
    [Fact]
    public void Service_procedure_errors_keep_completed_global_updates_and_release_the_call_scope()
    {
        Service(source: Counter.Replace("return COUNT;", "if COUNT=1;\neval COUNT=COUNT/0;\nendif;\nreturn COUNT;"));
        Program("FIRST"); Program("SECOND"); using var session = Session(); Assert.True(session.Execute("CALL QGPL/FIRST").IsError); Count(session, "SECOND", 2);
        Ok(session.Execute("RCLACTGRP ACTGRP(CALLERS)"));
    }
    [Fact]
    public void Native_service_recursion_preserves_VALUE_frames_and_returns()
    {
        const string source = "**free\nctl-opt nomain;\ndcl-proc FACT export;\ndcl-pi *n int(10);\nN int(10) value;\nend-pi;\nif N<=1;\nreturn 1;\nendif;\nreturn N*FACT(N-1);\nend-proc;";
        Service(source: source); Program(source: "**free\ndcl-pr FACT int(10);\nN int(10) value;\nend-pr;\ndcl-s RESULT int(10);\neval RESULT=FACT(5);\nsndpgmmsg %char(RESULT);\nreturn;");
        using var session = Session(); Count(session, "CALLER", 120);
    }
    [Fact]
    public void A_service_group_cannot_be_reclaimed_while_its_procedure_is_on_the_stack()
    {
        Save("RECLAIM", "PGM\nRCLACTGRP ACTGRP(SERVICES)\nMONMSG MSGID(IPC0124) EXEC(SNDPGMMSG MSG('ACTIVE SERVICE'))\nENDPGM");
        Ok(Command("CRTCLPGM PGM(QGPL/RECLAIM) SRCFILE(QGPL/SOURCE) ACTGRP(TOOLS)"));
        Service(group: "SERVICES", source: Counter.Replace("return COUNT;", "call 'QGPL/RECLAIM';\nreturn COUNT;")); Program(); using var session = Session();
        var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Contains("ACTIVE SERVICE", result.Message); Assert.EndsWith("1", result.Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)"));
    }
    [Fact]
    public void Runtime_interface_validation_precedes_argument_effects_and_service_entry()
    {
        static RpgProgram Compile(string name, string source) => RpgCompiler.CompileModule("QGPL", name, new SourceDocument("QGPL/SOURCE(" + name + ")", "**free\n" + source));
        var main = Compile("MAIN", "dcl-pr BUMP int(10);\nP int(10) value;\nend-pr;\ndcl-s SIDE int(10);\ndcl-s N int(10);\neval N=BUMP(EFFECT());\nreturn;\ndcl-proc EFFECT;\ndcl-pi *n int(10) end-pi;\neval SIDE=SIDE+1;\nreturn SIDE;\nend-proc;");
        var wrong = Compile("WRONG", "ctl-opt nomain;\ndcl-proc BUMP export;\ndcl-pi *n char(4);\nP int(10) value;\nend-pi;\nreturn 'BAD';\nend-proc;");
        var level = RpgCompiler.GeneratedExports(["BUMP"]); var definition = new RpgServiceDefinition([wrong], [level], RpgCompiler.ModuleExports([wrong]));
        var reference = definition.Reference("QGPL", "SERVICE"); RpgCompiler.BindModules([main], main, services: [reference]);
        using var runtime = new RpgServiceRuntime(definition); var entered = 0;
        var host = new RpgHost { ServiceProcedure = (_, h) => new(definition.Procedures["BUMP"], () => { entered++; return null; }, () => runtime.ModuleContext(definition.Procedures["BUMP"], h)) };
        var context = new RpgRuntimeContext(main, host); Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(main, existingContext: context));
        Assert.Equal(0L, context.ReadValue("SIDE")); Assert.Equal(0, entered);
    }
    [Fact]
    public void Too_many_service_names_reject_before_loading_or_program_creation()
    {
        Module("MAIN", Main); var result = Command("CRTPGM PGM(QGPL/BAD) MODULE(MAIN) BNDSRVPGM(" + string.Join(' ', Enumerable.Range(1, 17).Select(i => "SERVICE" + i)) + ")");
        Assert.True(result.IsError); Assert.Contains("16", result.Message); Assert.False(_system.Objects.Exists("QGPL", "BAD", ObjectType.Program));
    }
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<RpgRuntimeContext> ServiceContextReference(RpgServiceRuntime runtime, RpgSubprocedure procedure)
        => new(runtime.ModuleContext(procedure, new RpgHost()));
    [Fact]
    public void Disposed_service_runtime_releases_its_module_storage()
    {
        var module = RpgCompiler.CompileModule("QGPL", "COUNTER", new SourceDocument("QGPL/SOURCE(COUNTER)", Counter));
        var exports = RpgCompiler.ModuleExports([module]);
        using var runtime = new RpgServiceRuntime(new([module], [RpgCompiler.GeneratedExports(["BUMP"])], exports));
        var context = ServiceContextReference(runtime, exports["BUMP"]); runtime.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.False(context.TryGetTarget(out _));
    }
    public void Dispose() => _system.Dispose();
}
