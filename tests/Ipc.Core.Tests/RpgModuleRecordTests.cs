using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private void CompileModule(string name, string source)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", name, source, null);
        Ok(new CommandService(_system).Execute($"CRTRPGMOD MODULE(QGPL/{name}) SRCFILE(QGPL/SOURCE)"), "created");
    }
    private void BindModuleProgram() => Ok(new CommandService(_system).Execute("CRTPGM PGM(QGPL/APP) MODULE(QGPL/MMAIN QGPL/MHELP) ACTGRP(MODFILES)"), "created");
    private void RemoveModuleSources()
    {
        foreach (var name in new[] { "MMAIN", "MHELP" }) { _files.RemoveMember("QGPL", "SOURCE", name); _system.Objects.Delete("QGPL", name, ObjectType.Module); }
    }
    private const string ModuleReaderMain = "**free\ndcl-pr NEXTROW int(20);\nend-pr;\ndcl-s N int(20);\neval N=NEXTROW();\nsndpgmmsg %char(N);\nreturn;";
    private const string ModuleReaderHelper = "**free\nctl-opt nomain;\ndcl-f DATA qualified;\ndcl-proc NEXTROW export;\ndcl-pi *n int(20) end-pi;\ndcl-ds BUFFER likerec(DATA.ROW);\nread DATA BUFFER;\nreturn BUFFER.ID;\nend-proc;";
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Bound_modules_retain_global_file_cursors_and_reclaim_them_with_the_program(int ccsid)
    {
        CompileModule("MMAIN", ModuleReaderMain); CompileModule("MHELP", ModuleReaderHelper); BindModuleProgram(); RemoveModuleSources();
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "101"); Ok(session.Execute("CALL QGPL/APP"), "102");
        Ok(session.Execute("RCLACTGRP ACTGRP(MODFILES)"), ""); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Bound_modules_borrow_the_entry_modules_file_cursor_through_LIKEFILE(int ccsid)
    {
        const string model = "dcl-f MODEL qualified template block(*no) extdesc('QGPL/DATA');\n";
        CompileModule("MMAIN", "**free\n" + model + "dcl-f COPY likefile(MODEL) extfile('QGPL/DATA');\ndcl-pr LOAD int(20);\nF likefile(MODEL);\nend-pr;\ndcl-s A int(20);\ndcl-s B int(20);\neval A=LOAD(COPY);\neval B=LOAD(COPY);\nif A=101 and B=102 and %open(COPY);\nsndpgmmsg 'BOUND FILE PARAMETER';\nendif;\nreturn;");
        CompileModule("MHELP", "**free\nctl-opt nomain;\n" + model + "dcl-proc LOAD export;\ndcl-pi *n int(20);\nF likefile(MODEL);\nend-pi;\ndcl-ds BUFFER likerec(F.ROW);\nread F BUFFER;\nreturn BUFFER.ID;\nend-proc;");
        BindModuleProgram(); RemoveModuleSources(); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "BOUND FILE PARAMETER");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Bound_modules_reconstruct_structure_ABIs_after_external_descriptions_are_deleted(int ccsid)
    {
        const string model = "dcl-ds MODEL qualified extname('QGPL/DATA':*all) template end-ds;\n";
        CompileModule("MMAIN", "**free\n" + model + "dcl-pr FILL;\nP likeds(MODEL);\nend-pr;\ndcl-ds BUFFER likeds(MODEL) inz;\ncallp FILL(BUFFER);\nif BUFFER.ID=77 and BUFFER.TEXT='bound';\nsndpgmmsg 'BOUND STRUCTURE ABI';\nendif;\nreturn;");
        CompileModule("MHELP", "**free\nctl-opt nomain;\n" + model + "dcl-proc FILL export;\ndcl-pi *n;\nP likeds(MODEL);\nend-pi;\neval P.ID=77;\neval P.TEXT='bound';\nend-proc;");
        BindModuleProgram(); RemoveModuleSources(); _files.DeleteFile("QGPL", "DATA");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "BOUND STRUCTURE ABI");
    }
    [Fact]
    public void Bound_modules_recheck_live_file_authority_and_discard_a_failed_activation()
    {
        CompileModule("MMAIN", ModuleReaderMain); CompileModule("MHELP", ModuleReaderHelper); BindModuleProgram();
        _system.Security.Profiles.Create(new UserProfile { Name = "MODREADER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("MODREADER"), "ModReader45");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "MODREADER", Authorities.UseBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("MODREADER"), CancellationToken.None);
        Ok(session.Execute("CALL QGPL/APP"), "101"); _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "MODREADER", AuthorityBit.None);
        Assert.True(session.Execute("CALL QGPL/APP").IsError);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "MODREADER", Authorities.UseBits); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
}
