using Ipc.Core.Messages;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void SQLite_RPG_cursors_share_commit_and_rollback_positions_with_the_job_definition()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "FIRST", "FIRST");
        Assert.True(cursor.ReadNext(out var first, out _)); Assert.Equal(1L, first!["ID"]);
        Change("SECOND", "committed"); _system.RecordTransactions.Commit(_job.Key);
        Assert.True(cursor.ReadNext(out var second, out _)); Assert.Equal(2L, second!["ID"]);
        Change("SECOND", "discarded"); _system.RecordTransactions.Rollback(_job.Key);
        Assert.Null(cursor.Current); Assert.True(cursor.ReadNext(out var restored, out _)); Assert.Equal(2L, restored!["ID"]);
        Assert.Equal("committed", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void RPG_limit_positions_are_saved_before_the_target_record_at_commit()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "FIRST", "FIRST");
        Assert.True(cursor.SetLowerBound(new Dictionary<string, object?> { ["ID"] = 3L })); _system.RecordTransactions.Commit(_job.Key);
        Assert.True(cursor.ReadNext(out var third, out _)); Assert.Equal(3L, third!["ID"]);
        Assert.True(cursor.ReadNext(out var fourth, out _)); Assert.Equal(4L, fourth!["ID"]);
        _system.RecordTransactions.Rollback(_job.Key); Assert.True(cursor.ReadNext(out var again, out _)); Assert.Equal(3L, again!["ID"]);
    }
    [Fact]
    public void RPG_current_record_is_invalidated_by_rollback_until_another_input_operation()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "FIRST", "FIRST");
        Assert.True(cursor.Chain(new Dictionary<string, object?> { ["ID"] = 2L })); _system.RecordTransactions.Commit(_job.Key);
        Assert.True(cursor.Chain(new Dictionary<string, object?> { ["ID"] = 4L })); _system.RecordTransactions.Rollback(_job.Key);
        cursor.Reload(); Assert.Null(cursor.Current); Assert.True(cursor.ReadNext(out var next, out _)); Assert.Equal(3L, next!["ID"]);
    }
    [Fact]
    public void A_compiled_RPG_program_resumes_at_the_restored_boundary_after_a_called_program_rolls_back()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var source = "**free\ndcl-s ID int(20);\nread FIRST;\ncall 'CHECKP';\nread FIRST;\ncall 'RESTORE';\nread FIRST;\nreturn;\n";
        var program = RpgCompiler.Compile("CURTEST", "QGPL", source);
        var host = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", ProgramCaller = (_, name, _) => {
            if (name == "CHECKP") _system.RecordTransactions.Commit(_job.Key);
            else if (name == "RESTORE") _system.RecordTransactions.Rollback(_job.Key);
            else throw new InvalidOperationException(name);
            return new RpgExternalCallResult { Success = true };
        } };
        var result = new RpgInterpreter(host).Run(program); Assert.Equal(2m, RpgValues.ToDecimal(result.ReadValue("ID")));
        _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void RPG_disk_EOF_and_FOUND_functions_track_bounded_input_operations()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var source = "**free\ndcl-s ID int(20);\ndcl-s ended ind;\ndcl-s namedend ind;\ndcl-s found ind;\nread FIRST;\nread FIRST;\neval ended = %eof();\nchain 1 FIRST;\neval namedend = %eof(FIRST);\neval found = %found(FIRST);\nreturn;\n";
        var result = new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" })
            .Run(RpgCompiler.Compile("CURSTS", "QGPL", source));
        Assert.True(RpgValues.ToBool(result.ReadValue("ended"))); Assert.False(RpgValues.ToBool(result.ReadValue("namedend"))); Assert.True(RpgValues.ToBool(result.ReadValue("found")));
    }
}
