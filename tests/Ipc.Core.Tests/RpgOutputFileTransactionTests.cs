using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Declared_output_rejects_an_enlisted_resource_at_open()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key); Begin();
        Assert.Throws<CpfException>(() => _files.OpenRecordWriter("QGPL", "FIRST", "FIRST"));
        _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Retained_output_rejects_late_enlistment_before_adding_data()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var writer = _files.OpenRecordWriter("QGPL", "FIRST", "FIRST"); Begin();
        Assert.Throws<CpfException>(() => writer.Write(new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "denied" }));
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Rollback(_job.Key); _system.RecordTransactions.End(_job.Key); Assert.Equal(1, _files.RowCount("QGPL", "FIRST", "FIRST"));
    }
    [Fact]
    public void Declared_noncommit_WRITE_captures_an_ordinary_job_attributed_journal_entry()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        var program = RpgCompiler.Compile("QGPL", "APP", "**free\ndcl-f FIRST usage(*output);\neval ID=2;\neval TEXT='output';\nwrite REC;\nseton LR;",
            diskResolver: request => RpgDiskLayouts.Describe(request, "QGPL", "FIRST", _files.GetDefinition("QGPL", "FIRST")!));
        new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" }).Run(program);
        Assert.Equal(2, _files.RowCount("QGPL", "FIRST", "FIRST"));
        var entry = Assert.Single(_system.Journals.Read(Journal, 3)); Assert.Equal("PT", entry.Type); Assert.Equal("R", entry.Code);
        Assert.Equal(_job.Key.ToString(), entry.Job); Assert.Null(entry.CommitCycle);
    }
    [Fact]
    public void Disposing_the_factory_closes_output_handles_and_releases_their_allocations()
    {
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var factory = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(factory, _system.Objects);
        var writer = files.OpenRecordWriter("QGPL", "FIRST", "FIRST"); factory.Dispose();
        Assert.Throws<ObjectDisposedException>(writer.Validate); writer.Dispose();
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)), l => l.Lifetime == "OutputOpen");
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Output_in_a_write_operation_stops_on_call_cancellation_or_factory_shutdown(bool shutdown, bool relative)
    {
        if(relative)_files.InitializeMember("QGPL","FIRST",totalRecords:2,deleted:true);
        using var owner = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var factory = new Ipc.Services.Sqlite.SqliteConnectionFactory(_directory); var files = new SqliteFileStore(factory, _system.Objects);
        using var cancellation = new CancellationTokenSource(); using var writer = files.OpenRecordWriter("QGPL", "FIRST", "FIRST", cancellation.Token);
        using var blocker = _system.Connections.Open(); using var transaction = blocker.BeginTransaction(deferred: false);
        var waiting = Task.Run(() =>
        {
            var error = Record.Exception(() => {
                var values=new Dictionary<string,object?>{["ID"]=2L,["TEXT"]="cancelled"};
                if(relative)writer.WriteRelative(2,values);else writer.Write(values);
            });
            // Shutdown can win before a connection opens, or cancel a live
            // SQLite writer wait. Both stop the operation without publishing.
            if (shutdown) Assert.True(error is OperationCanceledException or ObjectDisposedException, error?.ToString() ?? "Output unexpectedly succeeded.");
            else Assert.IsAssignableFrom<OperationCanceledException>(error);
        });
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)).Any(l => l.Lifetime == "Command"))
            { Assert.True(DateTime.UtcNow < deadline, "Output must enter its write operation."); await Task.Delay(20); }
            Assert.False(waiting.IsCompleted);
            if (shutdown) factory.Dispose(); else cancellation.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cancellation.Cancel(); await waiting.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)), l => l.Lifetime == "Command");
        transaction.Rollback(); Assert.Equal(1, _files.RowCount("QGPL", "FIRST", "FIRST"));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Output_rechecks_authority_and_job_state_after_waiting_for_the_writer(bool logical, bool cancelJob)
    {
        _system.Security.Profiles.Create(new() { Name = "ADDUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ADDUSER"), "WaitingOutputAccess42");
        var job = _system.Jobs.CreateInteractive("ADDUSER"); var target = logical ? "VIEW" : "FIRST";
        if (logical)
        {
            var source = LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/FIRST)");
            var definition = new Ipc.Db.Dds.LogicalDdsCompiler(_ => (First, _files.GetDefinition("QGPL", "FIRST")!)).Compile("VIEW", source);
            _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
            _system.Security.Authority.Grant("QGPL", "VIEW", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        }
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var cancellation = new CancellationTokenSource(); DatabaseRecordWriter writer;
        using (OperationIdentity.Enter("ADDUSER", job.Key)) writer = _files.OpenRecordWriter("QGPL", target, target, cancellation.Token);
        using var output = writer;
        using var blocker = _system.Connections.Open(); using var transaction = blocker.BeginTransaction(deferred: false);
        using (var change = blocker.CreateCommand())
        {
            change.Transaction = transaction;
            change.CommandText = cancelJob ? "UPDATE sys_jobs SET cancel_requested=1 WHERE number=$job" :
                "UPDATE sys_authorities SET bits=1 WHERE lib='QGPL' AND name='FIRST' AND type='*FILE' AND holder='ADDUSER'";
            if (cancelJob) change.Parameters.AddWithValue("$job", job.Key.Number);
            Assert.Equal(1, change.ExecuteNonQuery());
        }
        var waiting = Task.Run(() =>
        {
            using var owner = OperationIdentity.Enter("ADDUSER", job.Key);
            return Record.Exception(() => writer.Write(new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "denied" }));
        });
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!new JobLockStore(_system.Connections).Inspect(new("QGPL", target, ObjectType.File)).Any(l => l.Lifetime == "Command" && l.Mode == JobLockMode.SharedUpdate))
            { Assert.True(DateTime.UtcNow < deadline, "Output must reach its write operation before the blocked change is committed."); await Task.Delay(20); }
            Assert.False(waiting.IsCompleted); transaction.Commit();
            var error = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelJob) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.Equal("CPF9802", Assert.IsType<CpfException>(error).MessageId);
        }
        finally { cancellation.Cancel(); await waiting.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(1, _files.RowCount("QGPL", "FIRST", "FIRST"));
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)), l => l.Lifetime == "Command");
    }
}
