using Ipc.Console.Session;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Service_data_imports_share_nullable_record_bytes_and_flags_through_database_updates(int ccsid)
    {
        NullData();
        CompileModule("DSTORE", "**free\nctl-opt nomain alwnull(*usrctl);\ndcl-ds SHARED qualified extname('QGPL/NULLDATA':*all) export;\nend-ds;");
        Ok(new CommandService(_system).Execute("CRTSRVPGM SRVPGM(QGPL/DATASVC) MODULE(DSTORE) EXPORT(*ALL) ACTGRP(DATASTORE)"), "created");
        const string model = "dcl-ds COPY qualified extname('QGPL/NULLDATA':*all) import('SHARED');\nend-ds;\n";
        _files.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\nctl-opt alwnull(*usrctl);\ndcl-f NULLDATA keyed usage(*update);\n" + model + "chain 10 NULLDATA COPY;\nif COPY.NID=1 and %nullind(COPY.NTEXT);\neval COPY.NTEXT='data';\neval %nullind(COPY.NTEXT)=*off;\nupdate NULLROW COPY;\nsndpgmmsg 'DATA EXPORT RECORD';\nendif;\nreturn;", null);
        Ok(new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE) BNDSRVPGM((DATASVC *DEFER)) ACTGRP(DATAAPP)"), "created");
        _files.SaveSourceMember("QGPL", "SOURCE", "CHECK", "**free\nctl-opt alwnull(*usrctl);\n" + model + "if COPY.NID=1 and COPY.NTEXT='data' and not %nullind(COPY.NTEXT);\nsndpgmmsg 'DATA EXPORT SHARED';\nendif;\nreturn;", null);
        Ok(new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/CHECK) SRCFILE(QGPL/SOURCE) BNDSRVPGM(DATASVC) ACTGRP(DATACHECK)"), "created");
        _system.Objects.Delete("QGPL", "DSTORE", ObjectType.Module); _files.RemoveMember("QGPL", "SOURCE", "DSTORE");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "DATA EXPORT RECORD"); Ok(session.Execute("CALL QGPL/CHECK"), "DATA EXPORT SHARED");
        Assert.Equal("data", Assert.IsType<string>(_files.ReadAll("QGPL", "NULLDATA", "NULLDATA")[0]["NTEXT"]).TrimEnd());
    }
}
