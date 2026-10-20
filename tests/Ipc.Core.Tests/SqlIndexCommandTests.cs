using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed partial class SqlIndexTests
{
    [Fact]
    public void RUNSQL_and_RUNSQLSTM_share_index_execution_and_script_syntax_checking()
    {
        _files.CreateSourceFile("QGPL", "QSQLSRC", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "QSQLSRC", "INDEXES", "CREATE INDEX QGPL/IDX ON QGPL/DATA(ID DESC);", null);
        var syntax = _session.Execute("RUNSQLSTM QGPL/QSQLSRC INDEXES COMMIT(*NONE) OPTION(*NOLIST) PROCESS(*SYN)"); Assert.False(syntax.IsError, syntax.Message); Assert.False(_files.FileExists("QGPL", "IDX"));
        var executed = _session.Execute("RUNSQLSTM QGPL/QSQLSRC INDEXES COMMIT(*NONE) OPTION(*NOLIST)"); Assert.False(executed.IsError, executed.Message);
        Assert.Equal(new long[] { 3, 2, 1 }, Keyed().Select(row => (long)row["ID"]!));
        var dropped = _session.Execute("RUNSQL SQL('DROP INDEX QGPL/IDX') COMMIT(*NONE)"); Assert.False(dropped.IsError, dropped.Message); Assert.False(_files.FileExists("QGPL", "IDX"));
    }
    [Fact]
    public void Saved_query_DDL_executes_index_creation_through_the_shared_SQL_engine()
    {
        _files.CreateSourceFile("QGPL", "QQMQRYSRC", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "QQMQRYSRC", "INDEXES", "CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(&KEY)", null);
        var created = _session.Execute("CRTQMQRY QGPL/INDEXES"); Assert.False(created.IsError, created.Message);
        var executed = _session.Execute("STRQMQRY QGPL/INDEXES SETVAR((KEY ID))"); Assert.False(executed.IsError, executed.Message);
        Assert.True(_files.GetDefinition("QGPL", "IDX")!.SqlIndex);
    }
}

public sealed partial class InteractiveSqlTests
{
    [Fact]
    public void Interactive_SQL_executes_and_validates_index_DDL()
    {
        Seed(); Command("STRSQL PROCESS(*VLD)"); Command("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID DESC)");
        Assert.Contains("validated", Screen); Assert.False(_files.FileExists("QGPL", "IDX")); Exit("2");
        Command("STRSQL"); Command("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID DESC)"); Assert.Contains("SQL statement completed", Screen);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, _files.ReadKeyed("QGPL", "IDX", "IDX").Select(row => (long)row["ID"]!));
        Command("DROP INDEX QGPL/IDX"); Assert.Contains("SQL statement completed", Screen); Assert.False(_files.FileExists("QGPL", "IDX")); Exit("2");
    }
}
