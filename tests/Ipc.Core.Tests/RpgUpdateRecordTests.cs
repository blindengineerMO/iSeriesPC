using Ipc.Console.Session;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Named_program_group_updates_keep_each_groups_saved_file_cursor(int ccsid)
    {
        CompileModule("MMAIN", ModuleReaderMain); CompileModule("MHELP", ModuleReaderHelper); BindModuleProgram();
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "101");
        Ok(new CommandService(_system).Execute("UPDPGM PGM(QGPL/APP) MODULE(*NONE) ACTGRP(NEWFILES)"), "completed");
        Ok(session.Execute("CALL QGPL/APP"), "101");
        Ok(new CommandService(_system).Execute("UPDPGM PGM(QGPL/APP) MODULE(*NONE) ACTGRP(MODFILES)"), "completed");
        Ok(session.Execute("CALL QGPL/APP"), "102"); Ok(session.Execute("RCLACTGRP ACTGRP(MODFILES)"), ""); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Named_service_group_updates_keep_old_cursors_and_connect_new_callers_to_the_new_group(int ccsid)
    {
        CreateReaderService("OLDGROUP"); BindReaderServiceProgram(); BindReaderServiceProgram("SECOND", "OTHER");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/FIRST"), "101");
        Ok(new CommandService(_system).Execute("UPDSRVPGM SRVPGM(QGPL/READERS) MODULE(*NONE) ACTGRP(NEWGROUP)"), "completed");
        Ok(session.Execute("CALL QGPL/FIRST"), "102"); Ok(session.Execute("CALL QGPL/SECOND"), "101");
        Ok(session.Execute("RCLACTGRP ACTGRP(OLDGROUP)"), ""); Ok(session.Execute("CALL QGPL/FIRST"), "102");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Program_module_updates_keep_existing_cursors_until_activation_reclaim(int ccsid)
    {
        CompileModule("MMAIN", ModuleReaderMain); CompileModule("MHELP", ModuleReaderHelper); BindModuleProgram();
        using var old = Session(ccsid); Ok(old.Execute("CALL QGPL/APP"), "101");
        _system.Objects.Delete("QGPL", "MHELP", ObjectType.Module);
        var saved = _files.ReadSourceMember("QGPL", "SOURCE", "MHELP"); _files.SaveSourceMember("QGPL", "SOURCE", "MHELP", ModuleReaderHelper.Replace("return BUFFER.ID;", "return BUFFER.ID+1000;"), saved.Revision);
        Ok(new CommandService(_system).Execute("CRTRPGMOD MODULE(QGPL/MHELP) SRCFILE(QGPL/SOURCE)"), "created");
        Ok(new CommandService(_system).Execute("UPDPGM PGM(QGPL/APP) MODULE(MHELP)"), "completed");
        Ok(old.Execute("CALL QGPL/APP"), "102"); using var fresh = Session(ccsid); Ok(fresh.Execute("CALL QGPL/APP"), "1101");
        Ok(old.Execute("RCLACTGRP ACTGRP(MODFILES)"), ""); Ok(old.Execute("CALL QGPL/APP"), "1101");
    }
}
