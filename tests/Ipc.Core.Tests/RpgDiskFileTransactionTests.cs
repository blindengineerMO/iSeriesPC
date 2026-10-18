using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Declared_disk_input_keeps_commitment_checkpoints_when_a_retained_cursor_rebinds_cancellation()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        Sql("INSERT INTO \"QGPL.FIRST.FIRST\"(ID,TEXT) VALUES(2,'second'),(3,'third')"); Begin();
        var program = RpgCompiler.Compile("QGPL", "APP", "**free\ndcl-f FIRST extdesc('QGPL/FIRST') extfile(*extdesc);\nread FIRST;\nreturn;",
            diskResolver: request => RpgDiskLayouts.Describe(request, "QGPL", "FIRST", _files.GetDefinition("QGPL", "FIRST")!));
        using var original = new CancellationTokenSource();
        RpgHost Host(CancellationToken cancellation = default) => new() { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL",
            RetainFiles = true, CancellationToken = cancellation };
        var context = new RpgInterpreter(Host(original.Token)).Run(program); Assert.Equal(1L, context.ReadValue("ID"));
        _system.RecordTransactions.Commit(_job.Key); original.Cancel();
        try
        {
            new RpgInterpreter(Host()).Run(program, existingContext: context); Assert.Equal(2L, context.ReadValue("ID"));
            _system.RecordTransactions.Rollback(_job.Key);
            new RpgInterpreter(Host()).Run(program, existingContext: context); Assert.Equal(2L, context.ReadValue("ID"));
        }
        finally { context.CloseDiskFiles(); }
        _system.RecordTransactions.End(_job.Key);
    }
}
