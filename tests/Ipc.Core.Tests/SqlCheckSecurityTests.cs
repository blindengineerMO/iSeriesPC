using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlCheckConstraintTests
{
    [Fact]
    public void Adding_a_check_requires_ALTER_but_does_not_grant_or_require_data_READ()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(1)");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "CheckAlterUser329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectAlter);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(ID>0)");
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(2)")).MessageId);
        }
        Assert.Equal(1L, Scalar("SELECT count(*) FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void Revoked_publication_authority_rolls_back_new_checks_and_the_schema_rewrite()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(1)");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "CheckAlterUser329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectAlter);
        Scalar("CREATE TRIGGER revoke_check AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='DATA' BEGIN UPDATE sys_authorities SET bits=0 WHERE name='DATA' AND holder='QUSER'; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD CHECK(ID>0)")).MessageId);
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks); Assert.Equal((long)AuthorityBit.ObjectAlter, Scalar("SELECT bits FROM sys_authorities WHERE name='DATA' AND holder='QUSER'"));
        Run("INSERT INTO QGPL/DATA VALUES(-1)");
    }
    [Fact]
    public async Task A_canceled_writer_wait_cannot_publish_a_check_or_change_existing_rows()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(1)");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false); using var cancel = new CancellationTokenSource();
        var work = Task.Run(() => Record.Exception(() => _files.ExecuteSql("ALTER TABLE QGPL/DATA ADD CHECK(ID>0)", Options(), cancel.Token)));
        try { await Task.Delay(150); cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancel.Cancel(); transaction.Rollback(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks);
    }
    [Fact]
    public void All_members_validate_before_an_added_check_is_published()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER)"); Run("INSERT INTO QGPL/DATA VALUES(1)");
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", 2); _files.AddMember("QGPL", "DATA", "OTHER");
        Scalar("INSERT INTO \"QGPL.DATA.OTHER\" VALUES(-1)");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(ID>0)")).MessageId);
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks);
        Scalar("DELETE FROM \"QGPL.DATA.OTHER\""); Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT POS CHECK(ID>0)");
        Assert.Equal(275, Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Scalar("INSERT INTO \"QGPL.DATA.OTHER\" VALUES(-1)")).SqliteExtendedErrorCode);
    }
}
