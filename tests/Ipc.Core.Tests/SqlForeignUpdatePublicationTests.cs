using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Theory]
    [InlineData(false, false, AuthorityBit.Read)]
    [InlineData(false, false, AuthorityBit.Delete)]
    [InlineData(false, true, AuthorityBit.Read)]
    [InlineData(false, true, AuthorityBit.Update)]
    [InlineData(true, false, AuthorityBit.Read)]
    [InlineData(true, false, AuthorityBit.Delete)]
    [InlineData(true, true, AuthorityBit.Read)]
    [InlineData(true, true, AuthorityBit.Update)]
    public void SQL_mutations_recheck_every_required_root_permission_before_publication(bool foreign, bool update, AuthorityBit revoked)
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        if (foreign)
        {
            Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/PARENT ON DELETE CASCADE)");
            if (!update) Run("INSERT INTO QGPL/CHILD VALUES(1)");
        }
        using var session = ForeignUser();
        var bits = AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Update | AuthorityBit.Delete;
        _system.Security.Authority.Grant("QGPL","PARENT",ObjectType.File,"QUSER",bits);
        var hook = foreign && !update ? "AFTER DELETE ON \"QGPL.CHILD.CHILD\"" : (update ? "AFTER UPDATE" : "AFTER DELETE") + " ON \"QGPL.PARENT.PARENT\"";
        Scalar("CREATE TRIGGER revoke_root " + hook + " BEGIN UPDATE sys_authorities SET bits=bits & " + ~(long)revoked + " WHERE name='PARENT' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER",session.Job.Key))
            Assert.Equal("SQL0551",Assert.Throws<CpfException>(() => Run(update ? "UPDATE QGPL/PARENT SET ID=ID+1 WHERE ID=1" : "DELETE FROM QGPL/PARENT WHERE ID=1")).MessageId);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
        if (foreign && !update) Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal((long)bits,Scalar("SELECT bits FROM sys_authorities WHERE name='PARENT' AND holder='QUSER'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constant_only_SQL_mutations_do_not_require_unneeded_root_READ(bool update)
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        using var session = ForeignUser();
        _system.Security.Authority.Grant("QGPL","PARENT",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate | (update ? AuthorityBit.Update : AuthorityBit.Delete));
        using (OperationIdentity.Enter("QUSER",session.Job.Key))
            Assert.Equal(1,Run(update ? "UPDATE QGPL/PARENT SET ID=2 WHERE 1=1" : "DELETE FROM QGPL/PARENT WHERE 1=1"));
        Assert.Equal(update ? 1L : 0L,Scalar("SELECT count(*) FROM \"QGPL.PARENT.PARENT\""));
    }
}
