using Ipc.Db.Dds;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiled_compound_DELETE_uses_logical_keys_and_shares_commit_and_rollback(bool kds)
    {
        AddDuplicateRecord();
        var dds = string.Join('\n', LogicalFileTests.Line('R', "CURREC", "PFILE(QGPL/FIRST)"), LogicalFileTests.Line('K', "ID"), LogicalFileTests.Line('K', "TEXT"));
        var definition = new LogicalDdsCompiler(_ => (First, _files.GetDefinition("QGPL", "FIRST")!)).Compile("CFIRST", dds);
        _files.CreateLogicalFile("QGPL", "CFIRST", definition, dds); Begin();
        using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var source = kds ? RpgFileKeyCompilerTests.DsSource("eval KAMT=1;\neval KLABEL='original';\ndelete %KDS(KEYDS) CFIRST;") : "**free\ndelete (1:'original') CFIRST;\nreturn;";
        void Run() => new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" })
            .Run(RpgCompiler.Compile("QGPL", "CKEYDEL", source));
        Run(); Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.Equal(2L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Run(); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("duplicate", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(1L, Sql("SELECT record_number FROM sys_journal_entries WHERE entry_type='DL' ORDER BY sequence DESC LIMIT 1"));
    }
}
