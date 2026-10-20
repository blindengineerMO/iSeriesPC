using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Session;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlCheckConstraintTests
{
    private static Dictionary<string, object?> Value(int value) => new() { ["ID"] = value };
    [Fact]
    public void Physical_logical_and_retained_cursor_writes_cannot_bypass_base_checks()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))");
        var source = LogicalFileTests.Line('R', "DATAREC", "PFILE(QGPL/DATA)") + "\n" + LogicalFileTests.Line('K', "ID");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("LOGICAL", source);
        _files.CreateLogicalFile("QGPL", "LOGICAL", definition, source);
        Assert.Equal(275, Assert.Throws<SqliteException>(() => _files.Insert("QGPL", "DATA", "DATA", "DATA", Value(-1))).SqliteExtendedErrorCode);
        Assert.Equal(275, Assert.Throws<SqliteException>(() => _files.Insert("QGPL", "LOGICAL", "LOGICAL", "DATAREC", Value(-1))).SqliteExtendedErrorCode);
        _files.Insert("QGPL", "DATA", "DATA", "DATA", Value(1));
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA"); Assert.NotNull(cursor.Read());
        Assert.Equal(275, Assert.Throws<SqliteException>(() => cursor.Update(Value(-1))).SqliteExtendedErrorCode);
        Assert.Equal(1L, Scalar("SELECT ID FROM \"QGPL.DATA.DATA\""));
    }
    [Fact]
    public void Private_record_workspaces_and_commit_publication_enforce_the_same_checks()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CheckUnitAdmin329");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        _system.Journals.CreateReceiver(new("QGPL", "CKRCV")); _system.Journals.Create(new("QGPL", "CKJRN"), new("QGPL", "CKRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "CKJRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        _system.RecordTransactions.Begin(session.Job.Key); _system.RecordTransactions.Enlist(session.Job.Key, new("QGPL", "DATA"));
        try
        {
            _files.Insert("QGPL", "DATA", "DATA", "DATA", Value(1));
            Assert.Equal(275, Assert.Throws<SqliteException>(() => _files.Insert("QGPL", "DATA", "DATA", "DATA", Value(-1))).SqliteExtendedErrorCode);
            Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
            _system.RecordTransactions.Commit(session.Job.Key);
        }
        finally { _system.RecordTransactions.End(session.Job.Key); }
        Assert.Equal(1L, Scalar("SELECT ID FROM \"QGPL.DATA.DATA\"")); Assert.Single(_system.Journals.Read(new("QGPL", "CKJRN")), entry => entry.Type == "PT");
    }
    [Fact]
    public void Copied_and_renamed_objects_keep_enforcement_and_rebind_constraint_names_for_later_ALTER()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(DATA.ID>0))"); Run("INSERT INTO QGPL/DATA VALUES(1)");
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Assert.NotEqual(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks![0].Name, _files.GetDefinition("QGPL", "COPIED")!.SqlTable!.Checks![0].Name);
        Run("ALTER TABLE QGPL/COPIED ADD FLAG INTEGER");
        _system.ObjectOperations.Relocate(new("QGPL", "COPIED"), ObjectType.File, new("QGPL", "MOVED"));
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/MOVED(ID) VALUES(-1)")).MessageId);
        Run("ALTER TABLE QGPL/MOVED ALTER ID SET DATA TYPE BIGINT"); Run("INSERT INTO QGPL/MOVED(ID) VALUES(2)");
    }
    [Fact]
    public void Dropped_column_checks_follow_the_native_all_columns_and_partial_RESTRICT_rules()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER,B INTEGER,CONSTRAINT POS CHECK(A>0),CONSTRAINT PAIR CHECK(A<B))"); Run("INSERT INTO QGPL/DATA VALUES(1,2)");
        Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA DROP A RESTRICT")).MessageId);
        Run("ALTER TABLE QGPL/DATA DROP A CASCADE");
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks); Run("INSERT INTO QGPL/DATA VALUES(-1)");
        Run("CREATE TABLE QGPL/SINGLE(A INTEGER,B INTEGER,CHECK(A>0))"); Run("ALTER TABLE QGPL/SINGLE DROP A RESTRICT");
        Assert.Null(_files.GetDefinition("QGPL", "SINGLE")!.SqlTable!.Checks);
    }
    [Fact]
    public void Anonymous_names_and_schema_name_collisions_include_every_constraint_kind()
    {
        Run("CREATE TABLE QGPL/DATA(A INTEGER,CHECK(A>0),CONSTRAINT BOUND CHECK(A<10))");
        var checks = _files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks!; Assert.All(checks, check => Assert.True(ObjectName.IsValid(check.Name!)));
        Assert.Equal("SQL0601", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL/OTHER(A INTEGER,CONSTRAINT BOUND UNIQUE(A))")).MessageId);
        Assert.Equal("SQL0601", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL/OTHER(A INTEGER,CONSTRAINT BOUND CHECK(A>0))")).MessageId);
    }
    [Fact]
    public void View_writes_keep_base_checks_distinct_from_view_CHECK_OPTION()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CHECK(ID>0))"); Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA WHERE ID<10 WITH CHECK OPTION");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(-1)")).MessageId);
        Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(10)")).MessageId);
        Run("INSERT INTO QGPL/REPORT VALUES(1)");
    }
}
