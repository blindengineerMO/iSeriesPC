using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Session;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private void CreateReaderService(string group = "*CALLER")
    {
        CompileModule("MHELP", ModuleReaderHelper);
        Ok(new CommandService(_system).Execute($"CRTSRVPGM SRVPGM(QGPL/READERS) MODULE(QGPL/MHELP) EXPORT(*ALL) ACTGRP({group})"), "created");
        _files.RemoveMember("QGPL", "SOURCE", "MHELP"); _system.Objects.Delete("QGPL", "MHELP", ObjectType.Module);
    }
    private void BindReaderServiceProgram(string name = "FIRST", string group = "READGRP", string? source = null)
    {
        CompileModule(name, source ?? ModuleReaderMain);
        Ok(new CommandService(_system).Execute($"CRTPGM PGM(QGPL/{name}) MODULE(QGPL/{name}) BNDSRVPGM(QGPL/READERS) ACTGRP({group})"), "created");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Dynamic_service_global_file_cursor_is_shared_between_programs_and_closed_on_reclaim(int ccsid)
    {
        CreateReaderService(); BindReaderServiceProgram(); BindReaderServiceProgram("SECOND"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/FIRST"), "101"); Ok(session.Execute("CALL QGPL/SECOND"), "102");
        Ok(session.Execute("RCLACTGRP ACTGRP(READGRP)"), ""); Ok(session.Execute("CALL QGPL/FIRST"), "101");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Dynamic_named_service_retains_its_cursor_across_caller_LR_and_caller_reclaim(int ccsid)
    {
        CreateReaderService("READERS"); BindReaderServiceProgram(source: ModuleReaderMain.Replace("return;", "seton LR;"));
        BindReaderServiceProgram("SECOND", "OTHER"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/FIRST"), "101"); Ok(session.Execute("RCLACTGRP ACTGRP(READGRP)"), "");
        Ok(session.Execute("CALL QGPL/SECOND"), "102"); Ok(session.Execute("RCLACTGRP ACTGRP(READERS)"), ""); Ok(session.Execute("CALL QGPL/SECOND"), "101");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Dynamic_services_borrow_LIKEFILE_parameters_without_owning_the_callers_cursor(int ccsid)
    {
        const string model = "dcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\n";
        CompileModule("MHELP", "**free\nctl-opt nomain;\n" + model + "dcl-proc LOAD export;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        Ok(new CommandService(_system).Execute("CRTSRVPGM SRVPGM(QGPL/READERS) MODULE(MHELP) EXPORT(*ALL)"), "created");
        CompileModule("MMAIN", "**free\n" + model + "dcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD int(20);\nF likefile(MODEL);\nend-pr;\ndcl-s A int(20);\ndcl-s B int(20);\neval A=LOAD(COPY);\neval B=LOAD(COPY);\nif A=101 and B=102 and %open(COPY);\nsndpgmmsg 'SERVICE FILE PARAMETER';\nendif;\nreturn;");
        Ok(new CommandService(_system).Execute("CRTPGM PGM(QGPL/APP) MODULE(MMAIN) BNDSRVPGM(READERS) ACTGRP(READGRP)"), "created");
        RemoveModuleSources(); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SERVICE FILE PARAMETER");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Dynamic_services_restore_nullable_structure_interfaces_without_live_external_descriptions(int ccsid)
    {
        _files.DeleteFile("QGPL", "DATA"); Data("DATA", nullable: true);
        const string model = "ctl-opt alwnull(*usrctl);\ndcl-ds MODEL qualified extname('QGPL/DATA':*all) template end-ds;\n";
        CompileModule("MMAIN", "**free\n" + model + "dcl-pr FILL;\nP likeds(MODEL) options(*nullind);\nend-pr;\ndcl-ds BUFFER likeds(MODEL) inz;\ncallp FILL(BUFFER);\nif BUFFER.ID=77 and BUFFER.TEXT='service' and %nullind(BUFFER.TEXT);\nsndpgmmsg 'SERVICE NULL STRUCTURE';\nendif;\nreturn;");
        CompileModule("MHELP", "**free\nctl-opt nomain;\n" + model + "dcl-proc FILL export;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nend-pi;\neval P.ID=77;\neval P.TEXT='service';\neval %nullind(P.TEXT)=*on;\nend-proc;");
        Ok(new CommandService(_system).Execute("CRTSRVPGM SRVPGM(QGPL/READERS) MODULE(MHELP) EXPORT(*ALL)"), "created");
        Ok(new CommandService(_system).Execute("CRTPGM PGM(QGPL/APP) MODULE(MMAIN) BNDSRVPGM(READERS) ACTGRP(READGRP)"), "created");
        RemoveModuleSources(); _files.DeleteFile("QGPL", "DATA"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SERVICE NULL STRUCTURE");
    }
    [Fact]
    public void Dynamic_service_owned_file_locks_are_released_when_the_job_ends()
    {
        CreateReaderService("READERS"); BindReaderServiceProgram(); var session = Session();
        try
        {
            Ok(session.Execute("CALL QGPL/FIRST"), "101");
            using var identity = OperationIdentity.Enter("QSECOFR");
            Assert.NotEmpty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
        }
        finally { session.Dispose(); }
        using var endedIdentity = OperationIdentity.Enter("QSECOFR");
        Assert.Empty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }
    [Fact]
    public void Dynamic_service_file_authority_is_live_and_service_owner_adds_no_adopted_authority()
    {
        CreateReaderService(); BindReaderServiceProgram(); _system.Security.Profiles.Create(new UserProfile { Name = "SRVREADER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SRVREADER"), "SrvReader45");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SRVREADER", Authorities.UseBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("SRVREADER"), CancellationToken.None);
        Ok(session.Execute("CALL QGPL/FIRST"), "101"); _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SRVREADER", AuthorityBit.None);
        Assert.True(session.Execute("CALL QGPL/FIRST").IsError);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SRVREADER", Authorities.UseBits);
        Ok(session.Execute("CALL QGPL/FIRST"), "102");
    }
}
