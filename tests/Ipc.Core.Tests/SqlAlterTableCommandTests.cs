using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests
{
    [Fact]
    public void Scripts_syntax_validation_and_saved_queries_share_atomic_table_alteration()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "AlterCommandAdmin329");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "ALTERS", "ALTER TABLE QGPL/DATA ADD FLAG INTEGER NOT NULL DEFAULT 5;\nALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE VARCHAR(8);", null);
        Ok("RUNSQLSTM QGPL/SOURCE ALTERS COMMIT(*NONE) OPTION(*NOLIST) PROCESS(*SYN)"); Assert.Null(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("FLAG"));
        Ok("RUNSQLSTM QGPL/SOURCE ALTERS COMMIT(*NONE) OPTION(*NOLIST)"); Assert.Equal(5L, Query("SELECT FLAG FROM QGPL/DATA WHERE ID=1")[0][0]);
        _files.SaveSourceMember("QGPL", "SOURCE", "QUERY", "ALTER TABLE QGPL/DATA ALTER FLAG SET DEFAULT 7", null);
        Ok("CRTQMQRY QGPL/QUERY SRCFILE(QGPL/SOURCE)"); Ok("STRQMQRY QGPL/QUERY");
        Ok("RUNSQL SQL('INSERT INTO QGPL/DATA(ID) VALUES(3)') COMMIT(*NONE)"); Assert.Equal(7L, Query("SELECT FLAG FROM QGPL/DATA WHERE ID=3")[0][0]);
        void Ok(string command) { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
    }
}
