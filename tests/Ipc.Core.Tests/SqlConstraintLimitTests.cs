using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private static string ForeignBatch(string table, string parent, int count) =>
        "CREATE TABLE " + table + "(ID INTEGER," + string.Join(',', Enumerable.Range(0, count)
            .Select(index => "CONSTRAINT F" + index + " FOREIGN KEY(ID) REFERENCES " + parent)) + ")";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CREATE_rejects_5001_checks_even_without_any_foreign_keys(bool validateOnly)
    {
        var sql = "CREATE TABLE QGPL/DATA(ID INTEGER," + string.Join(',', Enumerable.Range(0, 5001)
            .Select(index => "CONSTRAINT C" + index + " CHECK(ID IS NULL)")) + ")";
        var error = Assert.Throws<CpfException>(() => {
            if (validateOnly) _files.ValidateSql(sql, Options); else Run(sql);
        });
        Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
        Assert.False(_files.FileExists("QGPL", "DATA"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.DATA.DATA'"));
    }

    [Fact]
    public void Incoming_constraints_share_the_parent_limit_with_its_own_keys_and_new_constraints()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER CONSTRAINT PK PRIMARY KEY,EXTRA INTEGER)");
        var batch = ForeignBatch("QGPL/CHILD", "QGPL/PARENT", 4999);
        _files.ValidateSql(batch, Options); Run(batch);
        Assert.Equal(4999, _files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys!.Count);
        Run("CREATE TABLE QGPL/OTHER(ID INTEGER)");
        foreach (var sql in new[] {
            "CREATE TABLE QGPL/EXCESS(ID INTEGER REFERENCES QGPL/PARENT)",
            "ALTER TABLE QGPL/OTHER ADD CONSTRAINT TOO_MANY FOREIGN KEY(ID) REFERENCES QGPL/PARENT",
            "ALTER TABLE QGPL/PARENT ADD CONSTRAINT CEXTRA CHECK(EXTRA>=0)",
            "ALTER TABLE QGPL/PARENT ADD CONSTRAINT UEXTRA UNIQUE(EXTRA)"
        })
        {
            foreach (var validateOnly in new[] { true, false })
            {
                var error = Assert.Throws<CpfException>(() => {
                    if (validateOnly) _files.ValidateSql(sql, Options); else Run(sql);
                });
                Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
            }
        }
        Assert.False(_files.FileExists("QGPL", "EXCESS"));
        Assert.Null(_files.GetDefinition("QGPL", "OTHER")!.SqlTable!.ForeignKeys);
        Assert.Null(_files.GetDefinition("QGPL", "PARENT")!.SqlTable!.Checks);
        Assert.Single(_files.GetDefinition("QGPL", "PARENT")!.SqlTable!.Constraints);

        // Copying a dependent consumes capacity on its existing parent as well.
        Assert.Equal("SQL0642", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
            new("QGPL", "CHILD"), ObjectType.File, new("QGPL", "COPIED"), copy: true)).MessageId);
        Assert.False(_files.FileExists("QGPL", "COPIED"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.COPIED.CHILD'"));

        // Replacing a definition must not count its old references a second time.
        Run("ALTER TABLE QGPL/CHILD ADD EXTRA INTEGER");
        Run("ALTER TABLE QGPL/CHILD DROP FOREIGN KEY F0");
        Run("ALTER TABLE QGPL/OTHER ADD CONSTRAINT LAST_REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT");
        Assert.Single(_files.GetDefinition("QGPL", "OTHER")!.SqlTable!.ForeignKeys!);
        Run("DROP TABLE QGPL/CHILD");
        Run("ALTER TABLE QGPL/PARENT ADD CONSTRAINT CEXTRA CHECK(EXTRA>=0)");
        Run("CREATE TABLE QGPL/EXCESS(ID INTEGER REFERENCES QGPL/PARENT)");
        Assert.Single(_files.GetDefinition("QGPL", "PARENT")!.SqlTable!.Checks!);
        _system.ObjectOperations.Relocate(new("QGPL", "OTHER"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Assert.Single(_files.GetDefinition("QGPL", "COPIED")!.SqlTable!.ForeignKeys!);
    }

    [Fact]
    public void Self_references_count_once_and_copies_use_an_independent_constraint_budget()
    {
        var sql = ForeignBatch("QGPL/TREE", "QGPL/TREE", 4999)
            .Replace("ID INTEGER,", "ID INTEGER CONSTRAINT PK PRIMARY KEY,", StringComparison.Ordinal);
        _files.ValidateSql(sql, Options); Run(sql);
        _system.ObjectOperations.Relocate(new("QGPL", "TREE"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Assert.Equal(4999, _files.GetDefinition("QGPL", "COPIED")!.SqlTable!.ForeignKeys!.Count);
        var error = Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/TREE ADD CONSTRAINT EXCESS CHECK(ID IS NULL)"));
        Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
        Assert.Null(_files.GetDefinition("QGPL", "TREE")!.SqlTable!.Checks);
    }

    [Fact]
    public void Cross_library_copy_counts_the_destination_parent_and_can_retry_after_capacity_is_released()
    {
        _system.Libraries.CreateLibrary("DEST");
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)");
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        Run(ForeignBatch("DEST/BUSY", "DEST/PARENT", 4999));
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER CONSTRAINT REF REFERENCES QGPL/PARENT)");
        var error = Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
            new("QGPL", "CHILD"), ObjectType.File, new("DEST", "COPIED"), copy: true));
        Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
        Assert.Contains("DEST/PARENT", error.Message);
        Assert.False(_files.FileExists("DEST", "COPIED"));
        Assert.Equal("QGPL", Assert.Single(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys!).Parent.Schema);
        Run("ALTER TABLE DEST/BUSY DROP FOREIGN KEY F0");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "COPIED"), copy: true);
        Assert.Equal("DEST", Assert.Single(_files.GetDefinition("DEST", "COPIED")!.SqlTable!.ForeignKeys!).Parent.Schema);
        Run("INSERT INTO DEST/PARENT VALUES(2)"); Run("INSERT INTO DEST/COPIED VALUES(2)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
    }

    [Fact]
    public async Task Competing_creates_cannot_both_consume_the_last_parent_constraint_slot()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)");
        Run(ForeignBatch("QGPL/BUSY", "QGPL/PARENT", 4998));
        using var ready = new Barrier(2);
        var tasks = new[] { "FIRST", "SECOND" }.Select(name => Task.Factory.StartNew(() => {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(10)));
            try { Run("CREATE TABLE QGPL/" + name + "(ID INTEGER REFERENCES QGPL/PARENT)"); return (Name: name, Error: (CpfException?)null); }
            catch (CpfException error) { return (Name: name, Error: (CpfException?)error); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, result => result.Error is null);
        var loser = Assert.Single(results, result => result.Error is not null);
        Assert.Equal("SQL0642", loser.Error!.MessageId); Assert.Equal("54021", loser.Error.SqlState);
        Assert.True(_files.FileExists("QGPL", winner.Name)); Assert.False(_files.FileExists("QGPL", loser.Name));
        Run("DROP TABLE QGPL/" + winner.Name);
        Run("CREATE TABLE QGPL/" + loser.Name + "(ID INTEGER REFERENCES QGPL/PARENT)");
        Assert.True(_files.FileExists("QGPL", loser.Name));
    }

    [Fact]
    public void Exactly_5000_checks_enforce_rows_and_an_atomic_drop_and_add_reuses_capacity()
    {
        var sql = "CREATE TABLE QGPL/DATA(ID INTEGER," + string.Join(',', Enumerable.Range(0, 5000)
            .Select(index => "CONSTRAINT C" + index + " CHECK(ID IS NULL)")) + ")";
        _files.ValidateSql(sql, Options); Run(sql); Run("INSERT INTO QGPL/DATA VALUES(NULL)");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1)")).MessageId);
        Run("ALTER TABLE QGPL/DATA DROP CHECK C0 ADD CONSTRAINT NEWKEY UNIQUE(ID)");
        Assert.Equal(4999, _files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks!.Count);
        Assert.Single(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Constraints);
        Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
        var error = Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT EXCESS CHECK(ID IS NULL)"));
        Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
    }

    [Fact]
    public void Cascaded_incoming_drops_release_capacity_inside_the_same_ALTER_statement()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY," + string.Join(',', Enumerable.Range(0, 4998)
            .Select(index => "CONSTRAINT C" + index + " CHECK(ID IS NOT NULL)")) + ")");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/PARENT)");
        Run("INSERT INTO QGPL/PARENT VALUES(1)"); Run("INSERT INTO QGPL/CHILD VALUES(1)");
        const string sql = "ALTER TABLE QGPL/PARENT DROP PRIMARY KEY CASCADE " +
            "ADD CONSTRAINT NEW1 CHECK(ID IS NOT NULL) ADD CONSTRAINT NEW2 CHECK(ID IS NOT NULL)";
        _files.ValidateSql(sql, Options);
        Assert.Single(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys!);
        Run(sql);
        var parent = _files.GetDefinition("QGPL", "PARENT")!;
        Assert.Empty(parent.SqlTable!.Constraints); Assert.Equal(5000, parent.SqlTable.Checks!.Count);
        Assert.Null(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys);
        Assert.Equal(1, _files.RowCount("QGPL", "PARENT", "PARENT"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)");
        Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }

    [Theory]
    [InlineData("CREATE")]
    [InlineData("ALTER")]
    [InlineData("COPY")]
    public void Publication_rechecks_limits_after_catalog_hooks_and_rolls_back_all_changes(string operation)
    {
        Run("CREATE TABLE QGPL/BUDGET(ID INTEGER)");
        Run("CREATE TABLE QGPL/SOURCE(ID INTEGER)");
        var before = _files.GetDefinition("QGPL", "BUDGET")!.ToJson();
        var overflow = FileDefinition.FromJson(before);
        overflow.SqlTable = overflow.SqlTable! with { Checks = Enumerable.Range(0, 5001)
            .Select(index => new SqlCheckConstraint("C" + index, "ID IS NULL")).ToArray() };
        var definition = overflow.ToJson().Replace("'", "''", StringComparison.Ordinal);
        var trigger = operation == "ALTER" ? "AFTER UPDATE" : "AFTER INSERT";
        var target = operation == "ALTER" ? "SOURCE" : "DATA";
        Scalar("CREATE TRIGGER exceed_constraint_budget " + trigger + " ON sys_file_defs WHEN NEW.name='" + target +
            "' BEGIN UPDATE sys_file_defs SET def='" + definition + "' WHERE lib='QGPL' AND name='BUDGET'; END");
        var error = Assert.Throws<CpfException>(() => {
            if (operation == "COPY") _system.ObjectOperations.Relocate(new("QGPL", "SOURCE"), ObjectType.File, new("QGPL", "DATA"), copy: true);
            else Run(operation == "CREATE" ? "CREATE TABLE QGPL/DATA(ID INTEGER)" : "ALTER TABLE QGPL/SOURCE ADD EXTRA INTEGER");
        });
        Assert.Equal("SQL0642", error.MessageId); Assert.Equal("54021", error.SqlState);
        Assert.Equal(before, _files.GetDefinition("QGPL", "BUDGET")!.ToJson());
        Assert.Null(_files.GetDefinition("QGPL", "SOURCE")!.PrimaryFormat.Find("EXTRA"));
        Assert.False(_files.FileExists("QGPL", "DATA"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_alter_*' OR name='QGPL.DATA.SOURCE' OR name='QGPL.DATA.DATA'"));
    }
}
