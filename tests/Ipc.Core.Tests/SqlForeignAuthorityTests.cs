using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private ExecutionSession ForeignUser()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"),"ForeignReferenceUser329");
        _system.Security.Authority.Grant("QSYS","QGPL",ObjectType.Library,"QUSER",Authorities.UseBits | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL","PARENT",ObjectType.File,"QUSER",AuthorityBit.ObjectReference);
        var parent = _system.Objects.GetRequired("QGPL","PARENT",ObjectType.File); parent.PublicAuthority = AuthorityBit.None; _system.Objects.Update(parent);
        return new(_system,_system.Jobs.CreateInteractive("QUSER"),CancellationToken.None);
    }
    [Fact]
    public void REFERENCES_authority_allows_definition_and_child_writes_without_parent_READ()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        using var session = ForeignUser(); using var identity = OperationIdentity.Enter("QUSER",session.Job.Key);
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER)"); Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT");
        Assert.Equal(1,Run("INSERT INTO QGPL/CHILD VALUES(1)"));
        Assert.Equal("SQL0551",Assert.Throws<CpfException>(() => _files.OpenSqlQuery("SELECT ID FROM QGPL/PARENT",Options)).MessageId);
    }
    [Fact]
    public void Revoked_parent_REFERENCES_during_ALTER_publication_rolls_back_metadata_rows_and_authority()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        using var session = ForeignUser();
        using (OperationIdentity.Enter("QUSER",session.Job.Key)) { Run("CREATE TABLE QGPL/CHILD(ID INTEGER)"); Run("INSERT INTO QGPL/CHILD VALUES(1)"); }
        var before = _files.GetDefinition("QGPL","CHILD")!.ToJson();
        Scalar("CREATE TRIGGER revoke_foreign_reference AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='CHILD' BEGIN UPDATE sys_authorities SET bits=0 WHERE name='PARENT' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER",session.Job.Key))
            Assert.Equal("SQL0551",Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT")).MessageId);
        Assert.Equal(before,_files.GetDefinition("QGPL","CHILD")!.ToJson()); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal((long)AuthorityBit.ObjectReference,Scalar("SELECT bits FROM sys_authorities WHERE name='PARENT' AND holder='QUSER'"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)");
    }
    [Fact]
    public void Parent_member_rebinding_by_a_catalog_hook_cannot_publish_a_different_relationship()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("CREATE TABLE QGPL/CHILD(ID INTEGER)");
        var before = _files.GetDefinition("QGPL","CHILD")!.ToJson();
        Scalar("CREATE TRIGGER change_foreign_member AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='CHILD' BEGIN UPDATE sys_file_members SET mbr='CHANGED' WHERE name='PARENT'; END");
        Assert.Equal("SQL0104",Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT")).MessageId);
        Assert.Equal(before,_files.GetDefinition("QGPL","CHILD")!.ToJson()); Assert.Equal("PARENT",_files.FirstMember("QGPL","PARENT"));
        Run("INSERT INTO QGPL/CHILD VALUES(1)");
    }
}
