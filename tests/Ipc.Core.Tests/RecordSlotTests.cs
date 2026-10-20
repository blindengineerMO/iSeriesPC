using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    private object? SlotValue(string sql)
    {
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = sql; return command.ExecuteScalar();
    }
    private long Extent(string file = "NAV", string member = "NAV") => (long)SlotValue(
        "SELECT high_water FROM sys_record_slots WHERE lib='QGPL' AND name='" + file + "' AND mbr='" + member + "'")!;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_addition_rolls_back_allocated_numbers_and_record_versions(bool replaceCopy)
    {
        Sql("CREATE TRIGGER reject_slot BEFORE INSERT ON \"QGPL.NAV.NAV\" BEGIN SELECT RAISE(ABORT,'rejected'); END");
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(4));
        if (replaceCopy) Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.CopyRecords(new("QGPL", "NAV"), "NAV", new("QGPL", "NAV"), "NAV", replace: true));
        else Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(9, 3, "C"));
        Assert.Equal(8, Extent()); Assert.Equal(8, _files.RowCount("QGPL", "NAV", "NAV"));
        cursor.Update(TextChange("still valid")); Assert.Equal("still valid", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Logical_addition_uses_the_source_members_persisted_extent()
    {
        var source = LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)");
        var definition = new Ipc.Db.Dds.LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("SLOTS", source);
        _files.CreateLogicalFile("QGPL", "SLOTS", definition, source, ["NAV"]);
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid>=7");
        _files.Insert("QGPL", "SLOTS", "SLOTS", "VREC", new Dictionary<string, object?> { ["ID"] = 9L });
        Assert.Equal(9, Extent()); using var cursor = _files.OpenSequentialCursor("QGPL", "SLOTS", "SLOTS", keyed: false);
        Assert.True(cursor.ChainRecord(9)); Assert.Equal(9, Id(cursor.Current));
    }
    [Fact]
    public void Native_addition_does_not_reuse_a_deleted_last_record_by_default()
    {
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid>=7"); Assert.Equal(8, Extent());
        Add(9, 3, "C"); using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false);
        Assert.False(cursor.ChainRecord(7)); Assert.False(cursor.ChainRecord(8));
        Assert.True(cursor.ChainRecord(9)); Assert.Equal(9, Id(cursor.Current)); Assert.Equal(9, Extent());
    }
    [Fact]
    public void Reuse_enabled_addition_fills_the_first_deleted_slot_before_extending()
    {
        var definition = _files.GetDefinition("QGPL", "NAV")!; definition.ReuseDeletedRecords = true;
        _files.CreatePhysicalFile("QGPL", "REUSE", definition, "");
        void Insert(long value) => _files.Insert("QGPL", "REUSE", "REUSE", "REC", new Dictionary<string, object?> { ["ID"] = value });
        Insert(1); Insert(2); Insert(3); Sql("DELETE FROM \"QGPL.REUSE.REUSE\" WHERE rowid<>2");
        Insert(4); Insert(5); Insert(6);
        using var cursor = _files.OpenSequentialCursor("QGPL", "REUSE", "REUSE", keyed: false);
        Assert.True(cursor.ChainRecord(1)); Assert.Equal(4, Id(cursor.Current));
        Assert.True(cursor.ChainRecord(3)); Assert.Equal(5, Id(cursor.Current));
        Assert.True(cursor.ChainRecord(4)); Assert.Equal(6, Id(cursor.Current)); Assert.Equal(4, Extent("REUSE", "REUSE"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_a_record_with_identical_values_invalidates_the_old_cursor(bool delete)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(4));
        Sql("CREATE TABLE saved_slot AS SELECT rowid AS rrn,* FROM \"QGPL.NAV.NAV\" WHERE rowid=4; DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=4; INSERT INTO \"QGPL.NAV.NAV\"(rowid,ID,AMOUNT,LABEL,TEXT) SELECT * FROM saved_slot; DROP TABLE saved_slot");
        var error = Assert.Throws<CpfException>(() => { if (delete) cursor.Delete(); else cursor.Update(TextChange("stale")); });
        Assert.Equal("CPF5026", error.MessageId); Assert.Equal("row 4", Stored(4)["TEXT"]);
        Assert.True(cursor.ChainRecord(4)); cursor.Update(TextChange("fresh")); Assert.Equal("fresh", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Rolled_back_record_changes_preserve_the_cursor_incarnation_and_extent()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(4));
        Sql("BEGIN; UPDATE \"QGPL.NAV.NAV\" SET TEXT='temporary' WHERE rowid=4; UPDATE \"QGPL.NAV.NAV\" SET rowid=99 WHERE rowid=8; ROLLBACK");
        Assert.Equal(8, Extent()); cursor.Update(TextChange("still current")); Assert.Equal("still current", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Clear_member_resets_allocation_and_does_not_revive_a_stale_cursor()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(1));
        _files.ClearMember("QGPL", "NAV", "NAV"); Assert.Equal(0, Extent()); Add(1, 1, "A");
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Delete()).MessageId);
        Assert.Equal(1, Extent()); Assert.Equal(1, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Fact]
    public void Copying_and_moving_a_member_preserve_its_deleted_extent()
    {
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=8");
        var objects = new ObjectCatalogOperations(_system.Connections);
        objects.Relocate(new("QGPL", "NAV"), "*FILE", new("QGPL", "COPIED"), copy: true);
        Assert.Equal(8, Extent("COPIED", "NAV"));
        objects.Relocate(new("QGPL", "COPIED"), "*FILE", new("QGPL", "MOVED"));
        _files.Insert("QGPL", "MOVED", "NAV", "REC", new Dictionary<string, object?> { ["ID"] = 9L });
        Assert.Equal(9, Extent("MOVED", "NAV")); Assert.Equal(8, Extent());
    }
    [Fact]
    public void Copy_append_uses_the_durable_extent_and_replace_resets_it()
    {
        _files.CreatePhysicalFile("QGPL", "TARGET", _files.GetDefinition("QGPL", "NAV")!, "");
        _files.Insert("QGPL", "TARGET", "TARGET", "REC", new Dictionary<string, object?> { ["ID"] = 99L });
        Sql("UPDATE \"QGPL.TARGET.TARGET\" SET rowid=20; DELETE FROM \"QGPL.TARGET.TARGET\"");
        Assert.Equal(8, _files.CopyRecords(new("QGPL", "NAV"), "NAV", new("QGPL", "TARGET"), "TARGET"));
        Assert.Equal(28, Extent("TARGET", "TARGET"));
        Assert.Equal(8, _files.CopyRecords(new("QGPL", "NAV"), "NAV", new("QGPL", "TARGET"), "TARGET", replace: true));
        Assert.Equal(8, Extent("TARGET", "TARGET"));
    }
    [Fact]
    public void Exhausted_member_rejects_addition_even_after_its_last_record_is_deleted()
    {
        Sql("UPDATE \"QGPL.NAV.NAV\" SET rowid=4294967288 WHERE rowid=8; DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=4294967288");
        Assert.Equal("CPF5030", Assert.Throws<CpfException>(() => Add(9, 3, "C")).MessageId); Assert.Equal(7, _files.RowCount("QGPL", "NAV", "NAV"));
    }
}
