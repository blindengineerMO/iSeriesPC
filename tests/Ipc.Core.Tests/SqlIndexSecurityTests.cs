using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_index_allocation_blocks_both_DROP_INDEX_and_cascading_DROP_TABLE(bool table)
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        var locks = new JobLockStore(_system.Connections);
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key))
        using (locks.Acquire(_session.Job.Key, new("QGPL", "IDX", ObjectType.File), JobLockMode.SharedRead, TimeSpan.Zero))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run(table ? "DROP TABLE QGPL/DATA" : "DROP INDEX QGPL/IDX")).MessageId);
        Assert.True(_files.FileExists("QGPL", "IDX")); Assert.Equal(3, Keyed().Count);
    }
    [Fact]
    public void A_failure_after_dependent_deletion_rolls_back_the_entire_DROP_TABLE_cascade()
    {
        Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        Scalar("CREATE TRIGGER fail_table_drop BEFORE DELETE ON sys_file_defs WHEN OLD.name='DATA' BEGIN SELECT RAISE(ABORT,'base unavailable'); END");
        Assert.Equal("SQL0901", Assert.Throws<CpfException>(() => Run("DROP TABLE QGPL/DATA")).MessageId);
        Assert.True(_files.FileExists("QGPL", "IDX")); Assert.Equal(3, Keyed().Count);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,'again',0)")).MessageId);
    }
    private void IndexCreator(AuthorityBit rights = AuthorityBit.ObjectAlter)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "SqlIndexCreator834");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", rights);
    }
    [Fact]
    public void INDEX_privilege_allows_creation_without_READ_and_does_not_grant_base_read_access()
    {
        IndexCreator();
        using (OperationIdentity.Enter("QUSER"))
        {
            _files.ValidateSql("CREATE INDEX IDX ON DATA(ID)", Options); Run("CREATE INDEX IDX ON DATA(ID)");
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Keyed()).MessageId);
        }
        Assert.Equal("QUSER", _system.Objects.GetRequired("QGPL", "IDX", ObjectType.File).Owner);
    }
    [Fact]
    public void Creation_requires_INDEX_privilege_and_CRTLF_command_authority()
    {
        IndexCreator(Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE INDEX IDX ON DATA(ID)")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectAlter);
        _system.Security.Authority.Grant("QSYS", "CRTLF", ObjectType.Command, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE INDEX IDX ON DATA(ID)")).MessageId);
        Assert.False(_files.FileExists("QGPL", "IDX"));
    }
    [Fact]
    public void A_target_schema_dictionary_requires_CHANGE_authority()
    {
        IndexCreator(); _system.Objects.Create(new() { Key = new("QGPL", "DICT"), ObjectType = ObjectType.DataDictionary, Owner = "QSECOFR", PublicAuthority = AuthorityBit.None });
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE INDEX IDX ON DATA(ID)")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DICT", ObjectType.DataDictionary, "QUSER", Authorities.ChangeBits);
        using (OperationIdentity.Enter("QUSER")) Run("CREATE INDEX IDX ON DATA(ID)");
    }
    [Fact]
    public void DROP_TABLE_preflights_every_index_authority_before_deleting_any_object()
    {
        Run("CREATE INDEX QGPL/FIRST ON QGPL/DATA(ID)"); Run("CREATE UNIQUE INDEX QGPL/SECOND ON QGPL/DATA(ID)");
        IndexCreator(AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        using (OperationIdentity.Enter("QUSER"))
        {
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => _files.ValidateSql("DROP TABLE DATA", Options)).MessageId);
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("DROP TABLE DATA")).MessageId);
        }
        Assert.True(_files.FileExists("QGPL", "FIRST")); Assert.True(_files.FileExists("QGPL", "SECOND")); Assert.True(_files.FileExists("QGPL", "DATA"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,'again',0)")).MessageId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publisher_hooks_that_disable_credentials_roll_back_creation_and_drop_without_a_schema_self_lock(bool drop)
    {
        if (drop) Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        Scalar("CREATE TRIGGER disable_index_creator AFTER " + (drop ? "DELETE" : "INSERT") + " ON sys_file_defs WHEN " + (drop ? "OLD" : "NEW") + ".name='IDX' BEGIN UPDATE sys_profiles SET status='Disabled' WHERE name='QSECOFR'; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run(drop ? "DROP INDEX QGPL/IDX" : "CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)")).MessageId);
        Assert.Equal(drop, _files.FileExists("QGPL", "IDX")); Assert.Equal("Enabled", Scalar("SELECT status FROM sys_profiles WHERE name='QSECOFR'"));
        Assert.Equal(drop ? 1L : 0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name LIKE 'ipc_unique_%'"));
    }
    [Fact]
    public void Cancellation_in_a_catalog_hook_rolls_back_index_and_job_changes_together()
    {
        Scalar("CREATE TRIGGER cancel_index_creator AFTER INSERT ON sys_file_defs WHEN NEW.name='IDX' BEGIN UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + _session.Job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key))
            Assert.ThrowsAny<OperationCanceledException>(() => Run("CREATE UNIQUE INDEX QGPL/IDX ON QGPL/DATA(ID)"));
        Assert.False(_files.FileExists("QGPL", "IDX")); Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_for_a_writer_is_cancellable_and_keeps_the_existing_catalog(bool drop)
    {
        if (drop) Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var cancel = new CancellationTokenSource();
        var work = Task.Run(() => Record.Exception(() => _files.ExecuteSql(drop ? "DROP INDEX QGPL/IDX" : "CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)", Options, cancel.Token)));
        try { await Task.Delay(150); cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancel.Cancel(); transaction.Rollback(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(drop, _files.FileExists("QGPL", "IDX"));
    }
    [Fact]
    public void Index_DDL_rejects_an_enlisted_base_and_preserves_the_record_unit()
    {
        _system.Journals.CreateReceiver(new("QGPL", "IDXRCV")); _system.Journals.Create(new("QGPL", "IDXJRN"), new("QGPL", "IDXRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "IDXJRN"), "*BOTH");
        Run("CREATE INDEX QGPL/IDX ON QGPL/DATA(ID)");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        _system.RecordTransactions.Begin(_session.Job.Key); _system.RecordTransactions.Enlist(_session.Job.Key, new("QGPL", "DATA"));
        try
        {
            Assert.Throws<CpfException>(() => Run("CREATE INDEX QGPL/SECOND ON QGPL/DATA(ID)"));
            Assert.Throws<CpfException>(() => Run("DROP INDEX QGPL/IDX"));
        }
        finally { _system.RecordTransactions.Rollback(_session.Job.Key); _system.RecordTransactions.End(_session.Job.Key); }
        Assert.True(_files.FileExists("QGPL", "IDX")); Assert.False(_files.FileExists("QGPL", "SECOND"));
    }
}
