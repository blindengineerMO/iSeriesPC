using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryOutputTests
{
    private ExecutionSession User()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "QueryOutputReader723");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits);
        return new(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
    }
    [Fact]
    public void Output_creation_requires_library_ADD_and_the_executing_user_owns_the_private_file()
    {
        Query("SELECT ID FROM QGPL/DATA"); using var user = User();
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits);
        Assert.Equal("CPF9802", user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)").MessageId); Assert.False(_files.FileExists("QGPL", "OUT"));
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        Ok(user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)"));
        var descriptor = _system.Objects.GetRequired("QGPL", "OUT", ObjectType.File); Assert.Equal("QUSER", descriptor.Owner); Assert.Equal(AuthorityBit.None, descriptor.PublicAuthority);
        _system.Security.Profiles.Create(new UserProfile { Name = "OTHER" });
        using (OperationIdentity.Enter("OTHER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "OUT", "OUT")).MessageId);
    }
    [Fact]
    public void Existing_output_requires_CHANGE_and_OBJALTER_even_for_append()
    {
        Save(); Query("SELECT ID,NAME FROM QGPL/DATA"); using var user = User();
        _system.Security.Authority.Grant("QGPL", "OUT", ObjectType.File, "QUSER", Authorities.ChangeBits);
        Assert.Equal("CPF9802", user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(*FIRST *ADD)").MessageId); Assert.Equal(3, Rows().Count);
        _system.Security.Authority.Grant("QGPL", "OUT", ObjectType.File, "QUSER", Authorities.ChangeBits | AuthorityBit.ObjectAlter);
        Ok(user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(*FIRST *ADD)")); Assert.Equal(6, Rows().Count);
    }
    [Fact]
    public void Replacement_honors_CLRPFM_command_authority_without_preventing_append()
    {
        Save(); Query("SELECT ID,NAME FROM QGPL/DATA"); using var user = User();
        _system.Security.Authority.Grant("QGPL", "OUT", ObjectType.File, "QUSER", Authorities.ChangeBits | AuthorityBit.ObjectAlter);
        _system.Security.Authority.Grant("QSYS", "CLRPFM", ObjectType.Command, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)").MessageId); Assert.Equal(3, Rows().Count);
        Ok(user.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT) OUTMBR(*FIRST *ADD)")); Assert.Equal(6, Rows().Count);
    }
    [Fact]
    public void Revoked_source_access_between_rows_stops_publication_without_undoing_the_prefix()
    {
        using var user = User();
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        using var owner = OperationIdentity.Enter("QUSER", user.Job.Key);
        var error = Assert.Throws<CpfException>(() => _files.WriteSqlQueryFile("SELECT ID FROM QGPL/DATA ORDER BY ID", Options, new("QGPL", "OUT"), validateQuery: () => {
            if (_system.Objects.Exists("QGPL", "OUT", ObjectType.File) && _files.RowCount("QGPL", "OUT", "OUT") == 1)
            { using var admin = OperationIdentity.Enter("QSECOFR"); _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.None); }
        }));
        Assert.Equal("SQL0551", error.MessageId); Assert.Contains("1 rows were written", error.Message); Assert.Single(Rows());
    }
    [Fact]
    public void Canceled_job_updates_in_an_insert_trigger_are_checked_on_the_publisher_connection()
    {
        Save("SELECT ID FROM QGPL/DATA WHERE 1=0");
        Scalar("CREATE TRIGGER cancel_output BEFORE INSERT ON \"QGPL.OUT.OUT\" WHEN NEW.ID=2 BEGIN UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + _session.Job.Key.Number + "; END");
        Assert.ThrowsAny<OperationCanceledException>(() => Save("SELECT ID FROM QGPL/DATA ORDER BY ID", replace: false));
        Assert.Equal(1L, Assert.Single(Rows())["ID"]); Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
    }
    [Fact]
    public void Query_output_uses_normal_member_full_recovery_without_replaying_published_rows()
    {
        Save("SELECT ID FROM QGPL/DATA WHERE 1=0"); _files.ChangePhysicalMemberLimit("QGPL", "OUT", null, memberSize: new(InitialRecords: 1, IncrementRecords: 1, MaximumIncrements: 1, Unlimited: false));
        new Ipc.Services.Messages.SystemReplyList(_system.Connections).Put(new(1, "CPA5305", Reply: "I"));
        new Ipc.Services.Messages.InquiryReplyPolicy(_system.Connections).Set(_session.Job.Key, "*SYSRPYL");
        Assert.Equal(3, Save("SELECT ID FROM QGPL/DATA ORDER BY ID", replace: false).Rows);
        Assert.Equal(new long[] { 1, 2, 3 }, Rows().Select(row => (long)row["ID"]!));
        Assert.Equal(3L, Scalar("SELECT capacity FROM sys_member_capacity"));
    }
    [Fact]
    public void A_failed_output_create_rolls_back_catalog_member_and_slot_metadata_together()
    {
        Scalar("CREATE TRIGGER fail_output BEFORE INSERT ON sys_file_members WHEN NEW.name='OUT' BEGIN SELECT RAISE(ABORT,'member unavailable'); END");
        Assert.Equal("SQL0901", Assert.Throws<CpfException>(() => Save()).MessageId);
        Assert.False(_files.FileExists("QGPL", "OUT")); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_defs WHERE name='OUT'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_record_slots WHERE name='OUT'"));
    }
    [Fact]
    public void A_profile_disabled_by_creation_hooks_is_checked_without_a_schema_self_lock()
    {
        Scalar("CREATE TRIGGER disable_output_creator AFTER INSERT ON sys_file_members WHEN NEW.name='OUT' BEGIN UPDATE sys_profiles SET status='Disabled' WHERE name='QSECOFR'; END");
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Save()).MessageId);
        Assert.False(_files.FileExists("QGPL", "OUT")); Assert.Equal("Enabled", Scalar("SELECT status FROM sys_profiles WHERE name='QSECOFR'"));
    }
    [Fact]
    public void Cancellation_from_a_clear_trigger_rolls_back_the_clear_before_any_output_rows_are_written()
    {
        Save(); Scalar("CREATE TRIGGER cancel_clear BEFORE DELETE ON \"QGPL.OUT.OUT\" BEGIN UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + _session.Job.Key.Number + "; END");
        Assert.ThrowsAny<OperationCanceledException>(() => Save()); Assert.Equal(3, Rows().Count);
        Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
    }
    [Fact]
    public void QTEMP_output_remains_private_and_is_removed_with_the_creating_job()
    {
        Query("SELECT ID,NAME FROM QGPL/DATA"); Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QTEMP/OUT)"));
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key)) Assert.Equal(3, _files.ReadAll("QTEMP", "OUT", "OUT").Count);
        using var other = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using (OperationIdentity.Enter("QSECOFR", other.Job.Key)) Assert.False(_files.FileExists("QTEMP", "OUT"));
        var backing = (string)Scalar("SELECT lib FROM sys_objects WHERE name='OUT' AND type='*FILE'")!;
        _session.Dispose(); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_objects WHERE lib='" + backing + "' AND name='OUT'"));
    }
    [Fact]
    public void The_row_allocation_budget_is_bounded_past_8192_output_rows()
    {
        _files.ExecuteSql("INSERT INTO QGPL/DATA(ID) VALUES " + string.Join(',', Enumerable.Range(4, 8200).Select(index => "(" + index + ")")), Options);
        Assert.Equal(8203, Save("SELECT ID FROM QGPL/DATA ORDER BY ID").Rows); Assert.Equal(8203, _files.RowCount("QGPL", "OUT", "OUT"));
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "OUT", ObjectType.File)), item => item.Job == _session.Job.Key);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_or_factory_end_interrupts_query_spooling_and_joins_output_cleanup(bool shutdown)
    {
        // Keep the cancellation controller runnable while xUnit schedules other synchronous tests.
        var failure = await Task.Factory.StartNew(() => QueryShutdownWorker(shutdown),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Assert.True(failure is OperationCanceledException, failure?.ToString() ?? "Query unexpectedly finished before shutdown.");
    }
    private Exception? QueryShutdownWorker(bool shutdown)
    {
        var sql = "SELECT COUNT(*) FROM " + string.Join(',', Enumerable.Range(0, 17).Select(index => "QGPL/DATA A" + index));
        var work = Task.Factory.StartNew(() => { using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key); return Record.Exception(() =>
            _files.WriteSqlQueryFile(sql, Options, new("QGPL", "OUT"))); },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        var locks = new JobLockStore(_system.Connections); var deadline = DateTime.UtcNow.AddSeconds(10);
        try
        {
            while (!locks.Inspect(new("QGPL", "DATA", ObjectType.File)).Any(item => item.Lifetime == "Query"))
            {
                Assert.False(work.IsCompleted, "Query ended before its allocation was observed: " + (work.IsCompleted ? work.GetAwaiter().GetResult()?.ToString() ?? "completed normally" : "running"));
                Assert.True(DateTime.UtcNow < deadline, "Query did not acquire its allocation within ten seconds."); Thread.Sleep(10);
            }
            if (shutdown) { _session.Dispose(); _system.Connections.Dispose(); } else _system.Jobs.Complete(_session.Job.Key);
            return work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally
        {
            if (!work.IsCompleted) _system.Jobs.Complete(_session.Job.Key);
            work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }
}
