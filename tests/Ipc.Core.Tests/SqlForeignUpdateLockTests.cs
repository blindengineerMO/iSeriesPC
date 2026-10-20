using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private void NativeForeignParent(string deleteRule = "NO ACTION", bool child = false)
    {
        var format = new RecordFormat { Name="PARENT", Fields=[
            new() { Name="ID", Type=FieldType.Binary, Length=4, Sequence=1 },
            new() { Name="CODE", Type=FieldType.Binary, Length=4 },
            new() { Name="EXTRA", Type=FieldType.Binary, Length=4 }] };
        format.AssignPositions();
        var definition = new FileDefinition { Name="PARENT", Attribute=Ipc.Db.Definitions.FileAttribute.Physical, MaximumMembers=1, Formats=[format],
            SqlTable=new([new("PK",true,["ID"]),new("UK",false,["CODE"])]) };
        _files.CreatePhysicalFile("QGPL","PARENT",definition,"");
        Run("INSERT INTO QGPL/PARENT VALUES(1,1,7)");
        Run("CREATE TABLE QGPL/CHILD(CODE INTEGER REFERENCES QGPL/PARENT(CODE) ON DELETE " + deleteRule + ")");
        if (child) Run("INSERT INTO QGPL/CHILD VALUES(1)");
        Run("CREATE INDEX QGPL/PARKEY ON QGPL/PARENT(ID)");
        Run("CREATE VIEW QGPL/PARVIEW AS SELECT ID,CODE,EXTRA FROM QGPL/PARENT");
    }

    private void ChangeForeignParent(string path, long code)
    {
        var values = new Dictionary<string,object?> { ["ID"]=1L,["CODE"]=code };
        switch (path)
        {
            case "SQL": Run("UPDATE QGPL/PARENT SET CODE=" + code + " WHERE ID=1"); break;
            case "VIEW": Run("UPDATE QGPL/PARVIEW SET CODE=" + code + " WHERE ID=1"); break;
            case "PHYSICAL": _files.Update("QGPL","PARENT","PARENT","PARENT",values); break;
            case "LOGICAL": _files.Update("QGPL","PARKEY","PARKEY","PARKEY",values); break;
            default:
                using (var cursor = _files.OpenUpdateCursor("QGPL","PARENT","PARENT"))
                { Assert.NotNull(cursor.Read()); cursor.Update(new Dictionary<string,object?> { ["CODE"]=code }); }
                break;
        }
    }

    private void DeleteForeignParent(string path)
    {
        switch (path)
        {
            case "SQL": Run("DELETE FROM QGPL/PARENT WHERE ID=1"); break;
            case "VIEW": Run("DELETE FROM QGPL/PARVIEW WHERE ID=1"); break;
            case "PHYSICAL": _files.Delete("QGPL","PARENT","PARENT","PARENT",Values(1)); break;
            case "LOGICAL": _files.Delete("QGPL","PARKEY","PARKEY","PARKEY",Values(1)); break;
            default:
                using (var cursor=_files.OpenUpdateCursor("QGPL","PARENT","PARENT")) { Assert.NotNull(cursor.Read()); cursor.Delete(); }
                break;
        }
    }

    [Theory]
    [InlineData("SQL","NO ACTION")]
    [InlineData("VIEW","NO ACTION")]
    [InlineData("PHYSICAL","NO ACTION")]
    [InlineData("LOGICAL","NO ACTION")]
    [InlineData("CURSOR","NO ACTION")]
    [InlineData("SQL","RESTRICT")]
    [InlineData("VIEW","RESTRICT")]
    [InlineData("PHYSICAL","RESTRICT")]
    [InlineData("LOGICAL","RESTRICT")]
    [InlineData("CURSOR","RESTRICT")]
    public void Restricting_parent_deletes_acquire_stable_dependent_reads(string path,string rule)
    {
        NativeForeignParent(rule,child:true); using var holder=NativeForeignSession(); using var writer=NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { child=_files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(()=>DeleteForeignParent(path)).MessageId);
            Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.CHILD.CHILD\""));
            child.Dispose(); Run("DELETE FROM QGPL/CHILD"); DeleteForeignParent(path);
        }
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.PARENT.PARENT\""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parent_key_swaps_allow_shared_dependent_reads_and_the_owners_retained_update_read(bool ownUpdateRead)
    {
        NativeForeignParent(child:true); Run("INSERT INTO QGPL/PARENT VALUES(2,2,8)"); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        using var holder=NativeForeignSession(); using var writer=NativeForeignSession(); IDisposable retained;
        using (OperationIdentity.Enter("QSECOFR",ownUpdateRead ? writer.Job.Key : holder.Job.Key))
        {
            if (ownUpdateRead)
            {
                var cursor=_files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(cursor.Read()); retained=cursor;
            }
            else retained=new JobLockStore(_system.Connections).Acquire(holder.Job.Key,new("QGPL","CHILD",ObjectType.File,"CHILD",1),JobLockMode.SharedRead,TimeSpan.Zero);
        }
        using (retained)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key)) Assert.Equal(2,Run("UPDATE QGPL/PARENT SET CODE=3-CODE"));
        Assert.Equal(2L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\" WHERE ID=1")); Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.CHILD.CHILD\" WHERE _rowid_=1"));
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL","CHILD",ObjectType.File)),item=>item.Job==writer.Job.Key);
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("VIEW")]
    [InlineData("PHYSICAL")]
    [InlineData("LOGICAL")]
    [InlineData("CURSOR")]
    public void Unrelated_parent_field_updates_do_not_take_cascading_child_write_locks(string path)
    {
        NativeForeignParent("CASCADE",child:true); using var holder=NativeForeignSession(); using var writer=NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { child=_files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            var values=new Dictionary<string,object?> { ["ID"]=1L,["EXTRA"]=9L };
            switch (path)
            {
                case "SQL": Run("UPDATE QGPL/PARENT SET EXTRA=9 WHERE ID=1"); break;
                case "VIEW": Run("UPDATE QGPL/PARVIEW SET EXTRA=9 WHERE ID=1"); break;
                case "PHYSICAL": _files.Update("QGPL","PARENT","PARENT","PARENT",values); break;
                case "LOGICAL": _files.Update("QGPL","PARKEY","PARKEY","PARKEY",values); break;
                default:
                    using (var cursor=_files.OpenUpdateCursor("QGPL","PARENT","PARENT")) { Assert.NotNull(cursor.Read()); cursor.Update(new Dictionary<string,object?> { ["EXTRA"]=9L }); }
                    break;
            }
        }
        Assert.Equal(9L,Scalar("SELECT EXTRA FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.CHILD.CHILD\""));
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("PHYSICAL")]
    public void Parent_update_validation_does_not_require_DELETE_or_dependent_data_authority(string path)
    {
        NativeForeignParent("CASCADE"); using var session=ForeignUser();
        _system.Security.Authority.Grant("QGPL","PARENT",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate|AuthorityBit.Read|AuthorityBit.Update);
        var child=_system.Objects.GetRequired("QGPL","CHILD",ObjectType.File); child.PublicAuthority=AuthorityBit.None; _system.Objects.Update(child);
        using (OperationIdentity.Enter("QUSER",session.Job.Key)) ChangeForeignParent(path,2);
        Assert.Equal(2L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\""));
    }

    [Fact]
    public void Parent_update_publication_rejects_dependent_member_rebinding_and_restores_all_rows()
    {
        NativeForeignParent(); using var session=NativeForeignSession();
        Scalar("CREATE TRIGGER change_dependent_member AFTER UPDATE ON \"QGPL.PARENT.PARENT\" BEGIN UPDATE sys_file_members SET mbr='CHANGED' WHERE name='CHILD'; END");
        using (OperationIdentity.Enter("QSECOFR",session.Job.Key))
            Assert.Equal("SQL0104",Assert.Throws<CpfException>(()=>ChangeForeignParent("SQL",2)).MessageId);
        Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal("CHILD",_files.FirstMember("QGPL","CHILD"));
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("VIEW")]
    [InlineData("PHYSICAL")]
    [InlineData("LOGICAL")]
    [InlineData("CURSOR")]
    public void Parent_updates_respect_empty_dependent_file_allocations(string path)
    {
        NativeForeignParent(); using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); IDisposable allocation;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) allocation = new JobLockStore(_system.Connections).Acquire(holder.Job.Key,new("QGPL","CHILD",ObjectType.File),JobLockMode.Exclusive,TimeSpan.Zero);
        using (allocation)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => ChangeForeignParent(path,2)).MessageId);
            Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\""));
        }
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key)) ChangeForeignParent(path,2);
        Assert.Equal(2L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\""));
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("VIEW")]
    [InlineData("PHYSICAL")]
    [InlineData("LOGICAL")]
    [InlineData("CURSOR")]
    public void Parent_updates_acquire_shared_locks_on_matching_dependent_records(string path)
    {
        NativeForeignParent(child:true); using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { child=_files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => ChangeForeignParent(path,2)).MessageId);
            Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\""));
            child.Dispose(); Run("DELETE FROM QGPL/CHILD"); ChangeForeignParent(path,2);
        }
        Assert.Equal(2L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\""));
    }

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public void Physical_keyed_deletes_protect_every_indirect_record_action(string rule)
    {
        NativeForeignParent(rule,child:true); using var holder=NativeForeignSession(); using var writer=NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { child=_files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => _files.Delete("QGPL","PARENT","PARENT","PARENT",Values(1))).MessageId);
            Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT CODE FROM \"QGPL.CHILD.CHILD\""));
            child.Dispose(); _files.Delete("QGPL","PARENT","PARENT","PARENT",Values(1));
        }
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.PARENT.PARENT\""));
        Assert.Equal(rule=="CASCADE" ? 0L : 1L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
}
