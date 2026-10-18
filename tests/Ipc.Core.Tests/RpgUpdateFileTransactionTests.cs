using Ipc.Core.Messages;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Declared_update_files_reject_enlisted_service_workspaces_instead_of_silently_applying_noncommit_lock_rules()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key); Begin();
        var program = RpgCompiler.Compile("QGPL", "APP", "**free\ndcl-f FIRST keyed usage(*update) extdesc('QGPL/FIRST') extfile(*extdesc);\nread FIRST;\nseton LR;",
            diskResolver: request => RpgDiskLayouts.Describe(request, "QGPL", "FIRST", _files.GetDefinition("QGPL", "FIRST")!));
        var host = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" };
        Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program));
        _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Declared_noncommit_update_and_delete_emit_ordinary_job_attributed_journal_entries()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        var program = RpgCompiler.Compile("QGPL", "APP", "**free\ndcl-f FIRST keyed usage(*delete) extdesc('QGPL/FIRST') extfile(*extdesc);\n" +
            "chain 1 FIRST;\neval TEXT='declared';\nupdate REC;\nchain 1 FIRST;\ndelete REC;\nseton LR;",
            diskResolver: request => RpgDiskLayouts.Describe(request, "QGPL", "FIRST", _files.GetDefinition("QGPL", "FIRST")!));
        new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" }).Run(program);
        Assert.Equal(0, _files.RowCount("QGPL", "FIRST", "FIRST"));
        var entries = _system.Journals.Read(Journal, 3); Assert.Equal(new[] { "UP", "DL" }, entries.Select(e => e.Type));
        Assert.All(entries, entry => { Assert.Equal("R", entry.Code); Assert.Equal(_job.Key.ToString(), entry.Job); Assert.Null(entry.CommitCycle); });
    }

}
