using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class RpgDataPointerActivationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private const string Counter = "**free\nctl-opt nomain;\ndcl-s COUNT int(10) inz(10);\ndcl-proc GET export;\ndcl-pi *n pointer end-pi;\nreturn %addr(COUNT);\nend-proc;";
    private const string Main = "**free\ndcl-pi *n;\nRESET char(1);\nend-pi;\ndcl-pr GET pointer end-pr;\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\nif RESET='Y';\nclear P;\nendif;\nif P=*null;\neval P=GET();\nendif;\nmonitor;\neval VIEW=VIEW+1;\nsndpgmmsg %char(VIEW);\non-error 222;\nsndpgmmsg 'EXPIRED';\nendmon;\nreturn;";
    public RpgDataPointerActivationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "BasingFixture45");
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
    private void Service()
    { Module(Counter); Ok(Command("CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(MOD) EXPORT(*ALL) ACTGRP(STORAGE)")); }
    private void Program(string name = "CALLER", string source = Main, string services = "SERVICE", string group = "CALLERS")
    {
        Save(name, source); Ok(Command($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE) BNDSRVPGM({services}) ACTGRP({group})"));
    }
    private ExecutionSession Session(int ccsid = 37, string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, ccsid: ccsid), CancellationToken.None);
    private static void Count(ExecutionSession session, string expected, string reset = "N", string program = "CALLER")
    { var result = session.Execute($"CALL QGPL/{program} PARM('{reset}')"); Ok(result); Assert.Equal(expected, result.Message?.Trim()); }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Service_addresses_alias_private_data_and_expire_on_reclaim_without_rebinding(int ccsid)
    {
        Service(); Program(); using var session = Session(ccsid); Count(session, "11"); Count(session, "12");
        Ok(session.Execute("RCLACTGRP ACTGRP(STORAGE)")); Count(session, "EXPIRED"); Count(session, "11", "Y");
    }
    [Fact]
    public void Service_addresses_are_isolated_between_jobs()
    {
        Service(); Program(); using var first = Session(); using var second = Session();
        Count(first, "11"); Count(second, "11"); Count(first, "12");
    }
    [Fact]
    public void Service_replacement_keeps_retained_address_storage_until_reclaim()
    {
        Service(); Program(); using var session = Session(); Count(session, "11");
        Module(Counter.Replace("inz(10)", "inz(100)")); Ok(Command("UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(MOD)")); Count(session, "12");
        Ok(session.Execute("RCLACTGRP ACTGRP(STORAGE)")); Count(session, "EXPIRED"); Count(session, "101", "Y");
    }
    [Theory]
    [InlineData("authority", false)] [InlineData("trust", false)] [InlineData("tamper", false)]
    [InlineData("authority", true)] [InlineData("trust", true)] [InlineData("tamper", true)]
    public void Retained_data_addresses_recheck_live_origin_security(string defect, bool deferred)
    {
        Service(); Program(services: deferred ? "(SERVICE *DEFER)" : "SERVICE"); var user = "QSECOFR";
        if (defect == "authority")
        {
            user = "PTRUSER"; _system.Security.Profiles.Create(new UserProfile { Name = user }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(user), "PointerUser45");
            _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, user, Authorities.AllBits);
        }
        var ca = _system.Certificates.CreateAuthority("PTRCA", "Pointer CA"); var signer = _system.Certificates.Issue("PTRSIGN", "Pointer signer", ca.Id, CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); _system.ObjectSigning.Sign("QGPL", "SERVICE", ObjectType.ServiceProgram, signer.Id);
        using var session = Session(user: user); Count(session, "11");
        if (defect == "authority") _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, user, AuthorityBit.None);
        else if (defect == "trust") _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        else { var descriptor = _system.Objects.GetRequired("QGPL", "SERVICE", ObjectType.ServiceProgram); descriptor.Source += " "; _system.Objects.Update(descriptor); }
        Assert.True(session.Execute("CALL QGPL/CALLER PARM('N')").IsError);
        if (defect == "authority") { _system.Security.Authority.Grant("QGPL", "SERVICE", ObjectType.ServiceProgram, user, Authorities.UseBits); Count(session, "12"); }
        if (defect == "trust") { _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true); Count(session, "12"); }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void Program_storage_and_heap_addresses_survive_LR_or_failure_until_reclaim(bool fail, bool heap)
    {
        Program("HOLDER", "**free\ndcl-pi *n;\nPTR pointer const;\nMODE char(1) const;\nend-pi;\ndcl-s SAVED pointer;\ndcl-s VIEW int(10) based(SAVED);\nif MODE='S';\neval SAVED=PTR;\nelse;\neval VIEW=VIEW+1;\nsndpgmmsg %char(VIEW);\nendif;\nreturn;", "*NONE", "HOLDERS");
        var prototype = "dcl-pr HOLDER extpgm('QGPL/HOLDER');\nPTR pointer const;\nMODE char(1) const;\nend-pr;\n";
        var storage = heap ? "dcl-s COUNT int(10) based(P);\neval P=%alloc(4);\neval COUNT=100;\n" : "dcl-s COUNT int(10);\neval COUNT=100;\neval P=%addr(COUNT);\n";
        Program("ORIGIN", "**free\n" + prototype + "dcl-s P pointer;\n" + storage + "callp HOLDER(P:'S');\n" + (fail ? "eval COUNT=1/0;\n" : "seton LR;\n") + "return;", "*NONE", "ORIGINS");
        Program(source: "**free\n" + prototype + "callp HOLDER(*null:'R');\nreturn;", services: "*NONE"); using var session = Session();
        Assert.Equal(fail, session.Execute("CALL QGPL/ORIGIN").IsError);
        var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal(heap ? "101" : "1", result.Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(ORIGINS)")); Assert.True(session.Execute("CALL QGPL/CALLER").IsError);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CL_procedures_borrow_based_native_storage_and_keep_reference_writes(int ccsid)
    {
        Save("UPDATE", "PGM PARM(&N)\nDCL &N *INT LEN(4)\nCHGVAR &N VALUE(77)\nENDPGM"); Ok(Command("CRTCLPGM PGM(QGPL/UPDATE) SRCFILE(QGPL/SOURCE)"));
        Program(source: "**free\ndcl-pr UPDATE extpgm('QGPL/UPDATE');\nN int(10);\nend-pr;\ndcl-s P pointer;\ndcl-s VIEW int(10) based(P);\neval P=%alloc(4);\neval VIEW=1;\ncallp UPDATE(VIEW);\nsndpgmmsg %char(VIEW);\ndealloc(n) P;\nreturn;", services: "*NONE");
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("77", result.Message?.Trim());
    }
    [Fact]
    public void A_callee_taking_a_parameter_address_keeps_the_original_owners_activation_after_error()
    {
        Program("GETTER", "**free\ndcl-pi *n;\nARG int(10);\nOUT pointer;\nend-pi;\neval OUT=%addr(ARG);\nreturn;", "*NONE", "GETTERS");
        Program("HOLDER", "**free\ndcl-pi *n;\nPTR pointer const;\nMODE char(1) const;\nend-pi;\ndcl-s SAVED pointer;\ndcl-s VIEW int(10) based(SAVED);\nif MODE='S';\neval SAVED=PTR;\nelse;\neval VIEW=VIEW+1;\nsndpgmmsg %char(VIEW);\nendif;\nreturn;", "*NONE", "HOLDERS");
        var holder = "dcl-pr HOLDER extpgm('QGPL/HOLDER');\nPTR pointer const;\nMODE char(1) const;\nend-pr;\n";
        Program("ORIGIN", "**free\n" + holder + "dcl-pr GETTER extpgm('QGPL/GETTER');\nARG int(10);\nOUT pointer;\nend-pr;\ndcl-s N int(10);\ndcl-s P pointer;\ncallp GETTER(N:P);\ncallp HOLDER(P:'S');\neval N=1/0;\nreturn;", "*NONE", "ORIGINS");
        Program(source: "**free\n" + holder + "callp HOLDER(*null:'R');\nreturn;", services: "*NONE"); using var session = Session(); Assert.True(session.Execute("CALL QGPL/ORIGIN").IsError);
        var result = session.Execute("CALL QGPL/CALLER"); Ok(result); Assert.Equal("1", result.Message?.Trim());
    }
    public void Dispose() => _system.Dispose();
}
