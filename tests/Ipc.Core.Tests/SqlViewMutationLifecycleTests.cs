using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewMutationTests
{
    [Theory]
    [InlineData("INSERT INTO QGPL/REPORT VALUES(4,10)")]
    [InlineData("UPDATE QGPL/REPORT SET AMOUNT=40")]
    [InlineData("DELETE FROM QGPL/REPORT")]
    public async Task A_canceled_writer_wait_publishes_no_view_rows_or_metadata(string sql)
        => await Task.Factory.StartNew(() => CheckCanceledViewWriter(sql),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private void CheckCanceledViewWriter(string sql)
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false); using var cancel = new CancellationTokenSource();
        var work = Task.Factory.StartNew(() => Record.Exception(() => _files.ExecuteSql(sql, Options, cancel.Token)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try { Thread.Sleep(150); cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()); }
        finally { cancel.Cancel(); transaction.Rollback(); work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public async Task Job_completion_cancels_and_joins_a_view_writer_before_waiting_for_the_catalog()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        var work = Task.Run(() => Record.Exception(() => Run("UPDATE QGPL/REPORT SET AMOUNT=40")));
        Task? ending = null;
        try
        {
            await Task.Delay(150); ending = Task.Run(() => _system.Jobs.Complete(_session.Job.Key, JobCompletion.Abnormal));
            Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            transaction.Rollback(); await work.WaitAsync(TimeSpan.FromSeconds(10));
            if (ending is not null) await ending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        identity.Dispose();
        Assert.Equal("60.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
    [Fact]
    public void Current_job_cancellation_in_a_record_hook_rolls_back_the_statement_and_job_marker()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        Scalar("CREATE TRIGGER cancel_writer AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + _session.Job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key)) Assert.ThrowsAny<OperationCanceledException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=40"));
        Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
        Assert.Equal("60.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
    [Fact]
    public void Enlisted_view_writes_reject_without_affecting_pending_record_data()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        _system.Journals.CreateReceiver(new("QGPL", "VWRCV")); _system.Journals.Create(new("QGPL", "VWJRN"), new("QGPL", "VWRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "VWJRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        _system.RecordTransactions.Begin(_session.Job.Key); _system.RecordTransactions.Enlist(_session.Job.Key, new("QGPL", "DATA"));
        try
        {
            using (var scope = _system.RecordTransactions.Open(_session.Job.Key)) { scope.Execute("UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='9.50' WHERE ID=1"); scope.Complete(); }
            foreach (var sql in new[] { "INSERT INTO QGPL/REPORT VALUES(4,1)", "UPDATE QGPL/REPORT SET AMOUNT=40", "DELETE FROM QGPL/REPORT" })
                Assert.Equal("IPC0211", Assert.Throws<CpfException>(() => Run(sql)).MessageId);
            Assert.Equal("9.50", Query("SELECT AMOUNT FROM QGPL/REPORT WHERE ID=1")[0][0]!.ToString());
        }
        finally { _system.RecordTransactions.Rollback(_session.Job.Key); _system.RecordTransactions.End(_session.Job.Key); }
    }
    [Fact]
    public void A_retained_base_record_allocation_blocks_a_view_update_without_partial_changes()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        using var cursor = _files.OpenUpdateCursor("QGPL", "DATA", "DATA"); Assert.NotNull(cursor.Read());
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=40")).MessageId);
        Assert.Equal("60.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
}
