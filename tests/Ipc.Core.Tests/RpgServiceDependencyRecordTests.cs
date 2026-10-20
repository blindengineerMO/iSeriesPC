using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, "*IMMED")] [InlineData(1208, "*IMMED")]
    [InlineData(37, "*DEFER")] [InlineData(1208, "*DEFER")]
    public void Directory_selected_dependency_chains_preserve_nullable_structure_aliases_after_description_deletion(int ccsid, string activation)
    {
        _files.DeleteFile("QGPL", "DATA"); Data("DATA", nullable: true);
        const string model = "ctl-opt alwnull(*usrctl);\ndcl-ds MODEL qualified extname('QGPL/DATA':*all) template end-ds;\n";
        const string parameter = "P likeds(MODEL) options(*nullind);\n";
        CompileModule("DLEAF", "**free\nctl-opt nomain;\n" + model + "dcl-proc FILL export;\ndcl-pi *n;\n" + parameter + "end-pi;\neval P.ID=77;\nevalr P.TEXT='dep';\neval %nullind(P.TEXT)=*on;\nend-proc;");
        Ok(new CommandService(_system).Execute("CRTSRVPGM SRVPGM(QGPL/LEAF) MODULE(DLEAF) EXPORT(*ALL) ACTGRP(LEAVES)"), "created");
        var directories = new BindingDirectoryStore(_system.Connections); directories.Create("QGPL", "LEAFDIR"); directories.Add("QGPL", "LEAFDIR", [new("QGPL", "LEAF", ObjectType.ServiceProgram, activation)]);
        CompileModule("DPARENT", "**free\nctl-opt nomain;\n" + model + "dcl-pr FILL;\n" + parameter + "end-pr;\ndcl-proc FORWARD export;\ndcl-pi *n;\n" + parameter + "end-pi;\ncallp FILL(P);\nend-proc;");
        Ok(new CommandService(_system).Execute("CRTSRVPGM SRVPGM(QGPL/PARENT) MODULE(DPARENT) BNDDIR(LEAFDIR) EXPORT(*ALL) ACTGRP(PARENTS)"), "created");
        directories.Create("QGPL", "PARENTDIR"); directories.Add("QGPL", "PARENTDIR", [new("QGPL", "PARENT", ObjectType.ServiceProgram, activation)]);
        _files.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\n" + model + "dcl-pr FORWARD;\n" + parameter + "end-pr;\ndcl-ds BUFFER likeds(MODEL) inz;\ncallp FORWARD(BUFFER);\nif BUFFER.ID=77 and BUFFER.TEXT='         dep' and %nullind(BUFFER.TEXT);\nsndpgmmsg 'DEPENDENCY NULL STRUCTURE';\nendif;\nreturn;", null);
        Ok(new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE) BNDDIR(PARENTDIR) ACTGRP(CALLERS)"), "created");
        foreach (var name in new[] { "DLEAF", "DPARENT" }) { _system.Objects.Delete("QGPL", name, ObjectType.Module); _files.RemoveMember("QGPL", "SOURCE", name); }
        _system.Objects.Delete("QGPL", "LEAFDIR", ObjectType.BindingDirectory); _system.Objects.Delete("QGPL", "PARENTDIR", ObjectType.BindingDirectory); _files.DeleteFile("QGPL", "DATA");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "DEPENDENCY NULL STRUCTURE");
    }
    [Theory]
    [InlineData(37, "*IMMED")] [InlineData(1208, "*IMMED")]
    [InlineData(37, "*DEFER")] [InlineData(1208, "*DEFER")]
    public void Transitive_service_file_cursors_belong_to_the_leaf_activation(int ccsid, string activation)
    {
        CreateReaderService("LEAVES");
        CompileModule("DPARENT", "**free\nctl-opt nomain;\ndcl-pr NEXTROW int(20);\nend-pr;\ndcl-proc FORWARD export;\ndcl-pi *n int(20) end-pi;\nreturn NEXTROW();\nend-proc;");
        Ok(new CommandService(_system).Execute($"CRTSRVPGM SRVPGM(QGPL/PARENT) MODULE(DPARENT) BNDSRVPGM((READERS {activation})) EXPORT(*ALL) ACTGRP(PARENTS)"), "created");
        _files.SaveSourceMember("QGPL", "SOURCE", "APP", ModuleReaderMain.Replace("NEXTROW", "FORWARD"), null);
        Ok(new CommandService(_system).Execute($"CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE) BNDSRVPGM((PARENT {activation})) ACTGRP(CALLERS)"), "created");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "101"); Ok(session.Execute("RCLACTGRP ACTGRP(PARENTS)"), "");
        Ok(session.Execute("CALL QGPL/APP"), "102"); Ok(session.Execute("RCLACTGRP ACTGRP(LEAVES)"), ""); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
}
