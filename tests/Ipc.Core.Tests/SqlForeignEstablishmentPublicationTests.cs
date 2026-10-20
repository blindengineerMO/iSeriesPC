using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private void DefinedCopy(bool rows = true, bool disabled = false)
    {
        StatePair(); if (!rows) Run("DELETE FROM QGPL/CHILD"); if (disabled) State("CHILD", false);
        _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "CHILD"), copy: true);
    }

    [Fact]
    public void Automatic_establishment_preserves_disabled_state_and_requires_later_verification()
    {
        DefinedCopy(disabled: true); Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.Disabled); Assert.True(foreign.CheckPending);
        Run("INSERT INTO DEST/CHILD VALUES(2)");
        Assert.Equal(2, _files.RowCount("DEST", "CHILD", "CHILD"));
        Run("INSERT INTO DEST/PARENT VALUES(1),(2)");
        Assert.Null(Assert.Single(_files.ChangeConstraints("DEST", "CHILD", ["*CHKPND"], enabled: true)).Error);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(3)")).MessageId);
    }

    [Theory]
    [InlineData("ID BIGINT PRIMARY KEY", "ALTER ID SET DATA TYPE INTEGER")]
    [InlineData("OTHER INTEGER PRIMARY KEY", "ADD ID INTEGER ADD UNIQUE(ID)")]
    public void Incompatible_parent_creation_preserves_the_definition_until_a_matching_ALTER(string columns, string alteration)
    {
        DefinedCopy(rows: false); Run("CREATE TABLE DEST/PARENT(" + columns + ")");
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("ALTER TABLE DEST/PARENT " + alteration);
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Zero_member_files_establish_in_either_member_creation_order(bool parentFirst)
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST");
        var parent = FileDefinition.FromJson(_files.GetDefinition("QGPL", "PARENT")!.ToJson());
        _files.CreatePhysicalFile("DEST", "PARENT", parent, "", member: "*NONE");
        var child = FileDefinition.FromJson(_files.GetDefinition("QGPL", "CHILD")!.ToJson());
        child.SqlTable = child.SqlTable! with { ForeignKeys = child.SqlTable!.ForeignKeys!.Select(item => item with { Parent = new("DEST", "PARENT"), ParentMember = null, Defined = true }).ToArray() };
        _files.CreatePhysicalFile("DEST", "CHILD", child, "", member: "*NONE");
        _files.AddMember("DEST", parentFirst ? "PARENT" : "CHILD", "FIRST");
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        _files.AddMember("DEST", parentFirst ? "CHILD" : "PARENT", "SECOND");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.Equal(parentFirst ? "FIRST" : "SECOND", foreign.ParentMember);
        Run("INSERT INTO DEST/PARENT VALUES(1)"); Run("INSERT INTO DEST/CHILD VALUES(1)");
    }

    [Fact]
    public void Self_references_bind_the_added_member_instead_of_the_default_file_name()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER REFERENCES QGPL/TREE)"); _system.Libraries.CreateLibrary("DEST");
        var definition = FileDefinition.FromJson(_files.GetDefinition("QGPL", "TREE")!.ToJson());
        definition.SqlTable = definition.SqlTable! with { ForeignKeys = definition.SqlTable!.ForeignKeys!.Select(item => item with { Parent = new("DEST", "TREE"), ParentMember = null, Defined = true }).ToArray() };
        _files.CreatePhysicalFile("DEST", "TREE", definition, "", member: "*NONE"); _files.AddMember("DEST", "TREE", "ACTUAL");
        var foreign = _files.GetDefinition("DEST", "TREE")!.SqlTable!.ForeignKeys![0]; Assert.False(foreign.Defined); Assert.Equal("ACTUAL", foreign.ParentMember);
        Run("INSERT INTO DEST/TREE VALUES(1,NULL),(2,1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/TREE VALUES(3,4)")).MessageId);
    }

    [Fact]
    public void Parent_creation_delegates_establishment_without_child_data_or_management_rights()
    {
        DefinedCopy(rows: false);
        var child = _system.Objects.GetRequired("DEST", "CHILD", ObjectType.File); child.PublicAuthority = AuthorityBit.None; _system.Objects.Update(child);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "AutoParentPassword329");
        _system.Security.Authority.Grant("QSYS", "DEST", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("DEST", "CHILD", "CHILD")).MessageId);
        }
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Fact]
    public void Parent_creation_rechecks_library_ADD_after_delegated_child_hooks()
    {
        DefinedCopy(rows: false);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "AutoRevokePassword329");
        var rights = AuthorityBit.ObjectOperate | AuthorityBit.Add;
        _system.Security.Authority.Grant("QSYS", "DEST", ObjectType.Library, "QUSER", rights);
        var child = _system.Objects.GetRequired("DEST", "CHILD", ObjectType.File); child.PublicAuthority = AuthorityBit.None; _system.Objects.Update(child);
        Scalar("CREATE TRIGGER revoke_auto_creator AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN UPDATE sys_authorities SET bits=" +
            (int)AuthorityBit.ObjectOperate + " WHERE lib='QSYS' AND name='DEST' AND type='*LIB' AND holder='QUSER'; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)")).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal((long)rights, Scalar("SELECT bits FROM sys_authorities WHERE lib='QSYS' AND name='DEST' AND holder='QUSER'"));
    }

    [Fact]
    public void Retained_child_queries_block_parent_creation_without_leaving_the_parent_or_new_state()
    {
        DefinedCopy(rows: false); using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var query = _files.OpenSqlQuery("SELECT ID FROM DEST/CHILD", Options))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)")).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData("UPDATE sys_file_members SET mbr='CHANGED' WHERE lib='DEST' AND name='PARENT'")]
    [InlineData("UPDATE sys_record_slots SET id='changed-slot' WHERE lib='DEST' AND name='CHILD'")]
    [InlineData("UPDATE sys_objects SET owner='QUSER' WHERE lib='DEST' AND name='CHILD' AND type='*FILE'")]
    [InlineData("INSERT INTO \"DEST.CHILD.CHILD\" VALUES(2)")]
    public void Automatic_establishment_rolls_back_hook_changes_to_members_owner_or_data(string mutation)
    {
        DefinedCopy(rows: false); var before = _files.GetDefinition("DEST", "CHILD")!.ToJson();
        Scalar("CREATE TRIGGER change_auto_binding AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN " + mutation + "; END");
        Assert.Throws<CpfException>(() => Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)"));
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.Equal(before, _files.GetDefinition("DEST", "CHILD")!.ToJson());
        Assert.Equal(0, _files.RowCount("DEST", "CHILD", "CHILD"));
        Assert.Equal("QSECOFR", _system.Objects.GetRequired("DEST", "CHILD", ObjectType.File).Owner);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_alter_*'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_enable_cannot_publish_data_inserted_after_initial_verification(bool foreign)
    {
        if (foreign) { StatePair(); State("CHILD", false); }
        else { Run("CREATE TABLE QGPL/CHILD(ID INTEGER,CONSTRAINT REF CHECK(ID>0))"); State("CHILD", false); }
        var before = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        Scalar("CREATE TRIGGER change_after_verification AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='QGPL' AND NEW.name='CHILD' BEGIN INSERT INTO \"QGPL.CHILD.CHILD\" VALUES(" + (foreign ? "2" : "-1") + "); END");
        var result = State("CHILD", true); Assert.NotNull(result.Error); Assert.False(result.Changed);
        Assert.Equal(before, _files.GetDefinition("QGPL", "CHILD")!.ToJson());
        Assert.Equal(foreign ? 1 : 0, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }
}
