using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgProcedurePointerActivationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Counter = "**free\nctl-opt nomain;\ndcl-s COUNT int(10);\ndcl-proc GET export;\ndcl-pi *n pointer(*proc) end-pi;\nreturn %paddr(NEXT);\nend-proc;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;";
    private const string Main = "**free\ndcl-pi *n;\nRESET char(1);\nend-pi;\ndcl-pr GET pointer(*proc) end-pr;\ndcl-s FP pointer(*proc);\ndcl-pr RUN int(10) extproc(FP) end-pr;\ndcl-s N int(10);\nif RESET='Y';\nclear FP;\nendif;\nif FP=*null;\neval FP=GET();\nendif;\nmonitor;\neval N=RUN();\nsndpgmmsg %char(N);\non-error 202;\nsndpgmmsg 'EXPIRED';\nendmon;\nreturn;";
    public RpgProcedurePointerActivationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PointerFixture45");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Save(string name, string source) => _files.SaveSourceMember("QGPL", "SOURCE", name, source, _files.ReadSourceMember("QGPL", "SOURCE", name).Revision);
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Module(string source)
    {
        Save("MOD", source); if (_system.Objects.Exists("QGPL", "MOD", ObjectType.Module)) _system.Objects.Delete("QGPL", "MOD", ObjectType.Module);
        Ok(Command("CRTRPGMOD MODULE(QGPL/MOD) SRCFILE(QGPL/SOURCE)"));
    }
    private void Service(string source = Counter, string group = "SERVICES")
    {
        Module(source); Ok(Command($"CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(MOD) EXPORT(*ALL) ACTGRP({group})"));
    }
    private void Program(string name = "CALLER", string source = Main, string services = "SERVICE", string group = "CALLERS")
    {
        Save(name, source); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM({services}) ACTGRP({group})"));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, string expected, string reset = "N", string program = "CALLER")
    { var result = session.Execute($"CALL QGPL/{program} PARM('{reset}')"); Ok(result); Assert.Equal(expected, result.Message?.Trim()); }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Private_service_callbacks_retain_storage_until_reclaim_and_do_not_rebind_stale_addresses(int ccsid)
    {
        Service(); Program(); using var session = Session(ccsid); Count(session, "1"); Count(session, "2");
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "EXPIRED"); Count(session, "1", "Y");
    }
    [Fact]
    public void Service_callbacks_are_isolated_by_job()
    {
        Service(); Program(); using var first = Session(); using var second = Session();
        Count(first, "1"); Count(second, "1"); Count(first, "2");
    }
    [Theory]
    [InlineData("delete")] [InlineData("trust")] [InlineData("tamper")]
    public void Retained_private_service_pointers_recheck_live_origin_and_captured_trust(string defect)
    {
        Service(); Program();
        var ca = _system.Certificates.CreateAuthority("PTRCA", "Pointer CA");
        var signer = _system.Certificates.Issue("PTRSIGN", "Pointer signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
        using var session = Session(); Count(session, "1");
        if (defect == "delete") _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        else if (defect == "trust") _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        else { var descriptor = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram); descriptor.Source += " "; _system.Objects.Update(descriptor); }
        Assert.True(session.Execute("CALL QGPL/CALLER PARM('N')").IsError);
    }
    [Fact]
    public void Updating_a_service_preserves_old_callback_code_until_reclaim()
    {
        Service(); Program(); using var session = Session(); Count(session, "1");
        Module(Counter.Replace("COUNT+1", "COUNT+10")); Ok(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(MOD)")); Count(session, "2");
        Ok(session.Execute("RCLACTGRP ACTGRP(SERVICES)")); Count(session, "EXPIRED"); Count(session, "10", "Y");
    }
    [Theory]
    [InlineData("missing")] [InlineData("trust")]
    public void Paddr_imports_activate_deferred_services_before_main_effects(string defect)
    {
        Service(); Program(source: "**free\ndcl-s FP pointer(*proc);\ncall 'QGPL/MARK';\nif 1=0;\neval FP=%paddr('GET');\nendif;\nreturn;", services: "(SERVICE *DEFER)");
        Save("MARK", "PGM\nCRTDTAARA DTAARA(QGPL/EFFECT) TYPE(*CHAR) LEN(1)\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/MARK) SRCFILE(QGPL/SOURCE)"));
        if (defect == "missing") _system.Objects.Delete("QGPL", "SERVICE", ObjectType.ServiceProgram);
        else
        {
            var ca = _system.Certificates.CreateAuthority("PTRCA", "Pointer CA"); var signer = _system.Certificates.Issue("PTRSIGN", "Pointer signer", ca.Id, CertificatePurpose.ObjectSigning);
            _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
            _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        }
        using var session = Session(); Assert.True(session.Execute("CALL QGPL/CALLER").IsError); Assert.False(_system.Objects.Exists("QGPL", "EFFECT", ObjectType.DataArea));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Callbacks_reenter_the_owning_group_when_the_receiver_runs_elsewhere(bool service)
    {
        Program("CHILD", "**free\ndcl-s COUNT int(10);\neval COUNT=COUNT+1;\nsndpgmmsg %char(COUNT);\nreturn;", "*NONE", "*CALLER");
        Program("CALLEE", "**free\ndcl-pi *n;\nFP pointer(*proc) const;\nend-pi;\ndcl-pr RUN int(10) extproc(FP) end-pr;\ndcl-s N int(10);\neval N=RUN();\nreturn;", "*NONE", "OTHERS");
        var callback = "dcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\ncall 'QGPL/CHILD';\nreturn 1;\nend-proc;";
        var main = "**free\ndcl-s FP pointer(*proc);\ndcl-pr CALLEE extpgm('QGPL/CALLEE');\nP pointer(*proc) const;\nend-pr;\neval FP=%paddr(NEXT);\ncallp CALLEE(FP);\ncall 'QGPL/CHILD';\nreturn;\n";
        if (service)
        {
            Service("**free\nctl-opt nomain;\n" + callback.Replace("NEXT;", "NEXT export;"), "*CALLER");
            main = main.Replace("dcl-s FP", "dcl-pr NEXT int(10) end-pr;\ndcl-s FP");
        }
        else main += callback;
        Program(source: main, services: service ? "SERVICE" : "*NONE"); using var session = Session();
        var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("1\n2", result.Message?.Trim());
    }
    [Fact]
    public void Retained_service_callbacks_recheck_live_authority()
    {
        Service(); Program(); _system.Security.Profiles.Create(new UserProfile { Name = "PTRUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("PTRUSER"), "PointerUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "PTRUSER", Authorities.AllBits);
        using var session = Session(user: "PTRUSER"); Count(session, "1");
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "PTRUSER", AuthorityBit.None);
        Assert.True(session.Execute("CALL QGPL/CALLER PARM('N')").IsError);
        _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, "PTRUSER", Authorities.UseBits); Count(session, "2");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Local_callbacks_keep_the_activation_after_LR_or_an_abnormal_return(bool fail)
    {
        Program("HOLDER", "**free\ndcl-pi *n;\nFP pointer(*proc) const;\nMODE char(1) const;\nend-pi;\ndcl-s SAVED pointer(*proc);\ndcl-pr RUN int(10) extproc(SAVED) end-pr;\ndcl-s N int(10);\nif MODE='S';\neval SAVED=FP;\nelse;\neval N=RUN();\nsndpgmmsg %char(N);\nendif;\nreturn;", "*NONE", "HOLDERS");
        var prototype = "dcl-pr HOLDER extpgm('QGPL/HOLDER');\nFP pointer(*proc) const;\nMODE char(1) const;\nend-pr;\n";
        Program("ORIGIN", "**free\n" + prototype + "dcl-s FP pointer(*proc);\ndcl-s COUNT int(10);\neval COUNT=100;\neval FP=%paddr(NEXT);\ncallp HOLDER(FP:'S');\n" + (fail ? "eval COUNT=1/0;\n" : "seton LR;\n") + "return;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;", "*NONE", "ORIGINS");
        Program(source: "**free\n" + prototype + "callp HOLDER(*null:'R');\nreturn;", services: "*NONE");
        using var session = Session(); Assert.Equal(fail, session.Execute("CALL QGPL/ORIGIN").IsError);
        var first = session.Execute("CALL QGPL/CALLER"); Ok(first); Assert.Equal("1", first.Message?.Trim());
        var second = session.Execute("CALL QGPL/CALLER"); Ok(second); Assert.Equal("2", second.Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(ORIGINS)")); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    public void Dispose() => _system.Dispose();
}
