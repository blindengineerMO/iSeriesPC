using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    private IReadOnlyDictionary<string, object?> Stored(long id) => _files.ReadAll("QGPL", "NAV", "NAV").Single(row => (long)row["ID"]! == id);
    private static Dictionary<string, object?> TextChange(string value) => new() { ["TEXT"] = value };
    [Fact]
    public void Cursor_update_changes_only_the_first_selected_duplicate_key_record()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(1, "A")));
        cursor.Update(TextChange("selected first")); Assert.Equal("selected first", Stored(1)["TEXT"]); Assert.Equal("row 2", Stored(2)["TEXT"]);
        Assert.Null(cursor.Current); Assert.Equal(2, Id(cursor.Read()));
    }
    [Fact]
    public void A_later_duplicate_can_be_updated_without_changing_earlier_records()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read())); Assert.Equal(2, Id(cursor.Read()));
        cursor.Update(TextChange("selected second")); Assert.Equal("row 1", Stored(1)["TEXT"]); Assert.Equal("selected second", Stored(2)["TEXT"]);
    }
    [Fact]
    public void Deleting_one_duplicate_preserves_the_other_and_does_not_skip_the_next_record()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read())); Assert.Equal(2, Id(cursor.Read()));
        cursor.Delete(); Assert.Equal(7, _files.RowCount("QGPL", "NAV", "NAV")); Assert.Equal("row 1", Stored(1)["TEXT"]);
        Assert.Equal(3, Id(cursor.Read()));
    }
    [Fact]
    public void Current_record_update_can_change_keys_without_using_them_as_the_target_predicate()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(1, "A")));
        cursor.Update(new Dictionary<string, object?> { ["AMOUNT"] = 3m, ["LABEL"] = "CHANGED", ["TEXT"] = "new key" });
        Assert.Equal(3m, Stored(1)["AMOUNT"]); Assert.Equal(1m, Stored(2)["AMOUNT"]);
        Assert.True(cursor.Chain(Key(3, "CHANGED"))); Assert.Equal(1, Id(cursor.Current));
    }
    [Fact]
    public void A_consumed_mutation_target_requires_another_successful_input_operation()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        cursor.Update(TextChange("first update")); Assert.Throws<CpfException>(() => cursor.Update(TextChange("invalid second update")));
        Assert.Throws<CpfException>(() => cursor.Delete()); Assert.True(cursor.Chain(Key(2))); cursor.Delete();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_changed_before_image_is_rejected_instead_of_overwriting_new_data(bool delete)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("UPDATE \"QGPL.NAV.NAV\" SET TEXT='other writer' WHERE ID=4");
        var error = delete ? Assert.Throws<CpfException>(() => cursor.Delete()) : Assert.Throws<CpfException>(() => cursor.Update(TextChange("stale")));
        Assert.Equal("CPF5026", error.MessageId); Assert.Equal("other writer", Stored(4)["TEXT"]);
    }
    [Fact]
    public void An_unprojected_physical_column_is_included_in_the_conflict_check()
    {
        Sql("ALTER TABLE \"QGPL.NAV.NAV\" ADD COLUMN EXTRA TEXT DEFAULT 'original'");
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("UPDATE \"QGPL.NAV.NAV\" SET EXTRA='hidden change' WHERE ID=4");
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Update(TextChange("stale"))).MessageId);
        Assert.Equal("row 4", Stored(4)["TEXT"]);
    }
    [Fact]
    public void A_physical_schema_change_after_read_invalidates_the_write_target()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("ALTER TABLE \"QGPL.NAV.NAV\" ADD COLUMN NEWFIELD TEXT"); Assert.Throws<CpfException>(() => cursor.Update(TextChange("invalid")));
        Assert.Equal("row 4", Stored(4)["TEXT"]);
    }
    [Fact]
    public void A_deleted_and_differently_reused_RRN_is_not_overwritten()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=4; INSERT INTO \"QGPL.NAV.NAV\"(rowid,ID,AMOUNT,LABEL,TEXT) VALUES(4,44,'2.00','A','replacement')");
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Update(TextChange("stale"))).MessageId); Assert.Equal("replacement", Stored(44)["TEXT"]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_searches_and_positioning_operations_do_not_supply_a_mutation_target(bool position)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read()));
        if (position) cursor.SetLowerBound(Key(2)); else Assert.False(cursor.Chain(Key(123)));
        Assert.Throws<CpfException>(() => cursor.Update(TextChange("invalid"))); Assert.Throws<CpfException>(() => cursor.Delete()); Assert.Equal("row 1", Stored(1)["TEXT"]);
    }
    [Fact]
    public void Invalid_fields_and_values_roll_back_without_consuming_a_valid_target()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Assert.Throws<CpfException>(() => cursor.Update(new Dictionary<string, object?> { ["UNKNOWN"] = 1 }));
        Assert.Throws<CpfException>(() => cursor.Update(new Dictionary<string, object?> { ["ID"] = null }));
        cursor.Update(TextChange("valid retry")); Assert.Equal("valid retry", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Trigger_effects_run_once_for_one_current_record()
    {
        Sql("CREATE TABLE mutation_audit(n INTEGER); INSERT INTO mutation_audit VALUES(0); CREATE TRIGGER count_mutation AFTER UPDATE ON \"QGPL.NAV.NAV\" BEGIN UPDATE mutation_audit SET n=n+1; END");
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(1, "A"))); cursor.Update(TextChange("one trigger"));
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT n FROM mutation_audit";
        Assert.Equal(1L, command.ExecuteScalar());
    }
    [Fact]
    public void A_trigger_failure_rolls_back_the_record_and_keeps_the_cursor_retryable()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("CREATE TRIGGER fail_point_write BEFORE UPDATE ON \"QGPL.NAV.NAV\" BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<SqliteException>(() => cursor.Update(TextChange("failed"))); Assert.Equal("row 4", Stored(4)["TEXT"]);
        Sql("DROP TRIGGER fail_point_write"); cursor.Update(TextChange("retry")); Assert.Equal("retry", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Logical_projection_writes_preserve_unprojected_columns_and_selection()
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)"), LogicalFileTests.Line(' ', "AMOUNT"), LogicalFileTests.Line(' ', "LABEL"), LogicalFileTests.Line(' ', "TEXT"), LogicalFileTests.Line('K', "AMOUNT"), LogicalFileTests.Line('K', "LABEL"), LogicalFileTests.Line('S', "ID", "COMP(GT 0)"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("POINTVIEW", source);
        _files.CreateLogicalFile("QGPL", "POINTVIEW", definition, source);
        using var cursor = _files.OpenSequentialCursor("QGPL", "POINTVIEW", "POINTVIEW"); Assert.True(cursor.Chain(Key(1, "A")));
        cursor.Update(TextChange("logical selected")); Assert.Equal("logical selected", Stored(1)["TEXT"]); Assert.Equal("row 2", Stored(2)["TEXT"]); Assert.Equal(1L, Stored(1)["ID"]);
    }
    [Fact]
    public void Logical_member_identity_disambiguates_equal_keys_and_RRNs()
    {
        _files.AddMember("QGPL", "NAV", "OTHER"); _files.Insert("QGPL", "NAV", "OTHER", "REC", new Dictionary<string, object?> { ["ID"] = 100L, ["AMOUNT"] = 1m, ["LABEL"] = "A", ["TEXT"] = "other member" });
        var source = string.Join('\n', LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)"), LogicalFileTests.Line('K', "AMOUNT"), LogicalFileTests.Line('K', "LABEL"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("MEMVIEW", source);
        _files.CreateLogicalFile("QGPL", "MEMVIEW", definition, source, ["NAV", "OTHER"]);
        using var cursor = _files.OpenSequentialCursor("QGPL", "MEMVIEW", "MEMVIEW"); Assert.Equal(1, Id(cursor.Read())); Assert.Equal(2, Id(cursor.Read())); Assert.Equal(100, Id(cursor.Read()));
        cursor.Update(TextChange("only other member")); Assert.Equal("row 1", Stored(1)["TEXT"]); Assert.Equal("row 2", Stored(2)["TEXT"]);
        Assert.Equal("only other member", Assert.Single(_files.ReadAll("QGPL", "NAV", "OTHER"))["TEXT"]);
    }
    [Fact]
    public void Unkeyed_files_can_update_and_delete_exactly_the_current_RRN()
    {
        var format = new RecordFormat { Name = "UREC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 }, new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "UNKEYED", new() { Name = "UNKEYED", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        foreach (var text in new[] { "first", "second", "third" }) _files.Insert("QGPL", "UNKEYED", "UNKEYED", "UREC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = text });
        using var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "UNKEYED", "UNKEYED"); Assert.True(cursor.ReadNext(out _, out _));
        cursor.UpdateCurrent(TextChange("only first")); Assert.True(cursor.ReadNext(out var second, out _)); Assert.Equal("second", second!["TEXT"]); cursor.DeleteCurrent();
        Assert.True(cursor.ReadNext(out var third, out _)); Assert.Equal("third", third!["TEXT"]); Assert.Equal(2, _files.RowCount("QGPL", "UNKEYED", "UNKEYED"));
    }
    [Fact]
    public void Update_authority_is_rechecked_after_read()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "POINTUSER" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "POINTUSER", Authorities.ChangeBits);
        DatabaseRecordCursor cursor; using (OperationIdentity.Enter("POINTUSER")) { cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2))); }
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "POINTUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("POINTUSER")) { Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.Update(TextChange("denied"))).MessageId); cursor.Dispose(); }
        Assert.Equal("row 4", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Foreign_callers_cannot_mutate_another_contexts_current_record()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "POINTUSER" });
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "POINTUSER", Authorities.ChangeBits);
        DatabaseRecordCursor cursor; using (OperationIdentity.Enter("POINTUSER")) { cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2))); }
        Assert.Throws<CpfException>(() => cursor.Update(TextChange("foreign"))); Assert.Throws<CpfException>(() => cursor.Delete()); cursor.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_rowid_alias_selection_accounts_for_unprojected_columns(bool generated)
    {
        Sql("ALTER TABLE \"QGPL.NAV.NAV\" ADD COLUMN _rowid_ TEXT " + (generated ? "GENERATED ALWAYS AS ('shadow') VIRTUAL" : "DEFAULT 'shadow'"));
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PointRecordFixture32");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        cursor.Update(TextChange("real RRN")); Assert.Equal("real RRN", Stored(4)["TEXT"]); Assert.Equal("row 1", Stored(1)["TEXT"]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_record_deleted_since_input_is_reported_as_a_conflict_with_or_without_a_job(bool authenticated)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PointRecordFixture32");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); using var identity = authenticated ? OperationIdentity.Enter("QSECOFR", job.Key) : null;
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(2)));
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE ID=4");
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Update(TextChange("missing"))).MessageId);
        Assert.Equal(7, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Fact]
    public void Logical_file_update_requires_source_update_authority()
    {
        var name = DescendingView(); _system.Security.Profiles.Create(new UserProfile { Name = "POINTUSER" });
        _system.Security.Authority.Grant("QGPL", name, ObjectType.File, "POINTUSER", Authorities.ChangeBits);
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "POINTUSER", Authorities.UseBits);
        using var identity = OperationIdentity.Enter("POINTUSER"); using var cursor = _files.OpenSequentialCursor("QGPL", name, name);
        Assert.True(cursor.Chain(Key(2))); Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.Update(TextChange("denied"))).MessageId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_current_record_write_honors_another_jobs_allocation_and_releases_its_own(bool delete)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PointRecordFixture32");
        var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections);
        using var identity = OperationIdentity.Enter("QSECOFR", first.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(1, "A")));
        IDisposable blocked;
        using (OperationIdentity.Enter("QSECOFR", second.Key))
            blocked = locks.Acquire(second.Key, new("QGPL", "NAV", ObjectType.File, "NAV", 1), JobLockMode.Exclusive, TimeSpan.Zero);
        using (blocked)
            Assert.Equal("CPF1002", (delete ? Assert.Throws<CpfException>(() => cursor.Delete()) : Assert.Throws<CpfException>(() => cursor.Update(TextChange("blocked")))).MessageId);
        Assert.NotNull(cursor.Current);
        if (delete) cursor.Delete(); else cursor.Update(TextChange("allowed"));
        if (!delete)
            using (OperationIdentity.Enter("QSECOFR", second.Key))
            using (locks.Acquire(second.Key, new("QGPL", "NAV", ObjectType.File, "NAV", 1), JobLockMode.Exclusive, TimeSpan.Zero)) { }
    }
    [Fact]
    public void Locking_a_duplicate_sibling_does_not_block_the_selected_record_write()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "PointRecordFixture32");
        var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        using var identity = OperationIdentity.Enter("QSECOFR", first.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.Chain(Key(1, "A")));
        IDisposable blocked; using (OperationIdentity.Enter("QSECOFR", second.Key))
            blocked = locks.Acquire(second.Key, new("QGPL", "NAV", ObjectType.File, "NAV", 2), JobLockMode.Exclusive, TimeSpan.Zero);
        using (blocked) cursor.Update(TextChange("selected only"));
        Assert.Equal("selected only", Stored(1)["TEXT"]); Assert.Equal("row 2", Stored(2)["TEXT"]);
    }
    [Fact]
    public void A_legacy_provider_without_record_identity_never_receives_an_empty_delete_key()
    {
        var access = new LegacyUnkeyedAccess();
        using var cursor = new RpgFileCursor(access, "QGPL", "LEGACY", "LEGACY"); Assert.True(cursor.ReadNext(out _, out _));
        Assert.Throws<RpgRuntimeException>(() => cursor.DeleteCurrent()); Assert.Equal(2, access.Rows.Count);
    }
    private sealed class LegacyUnkeyedAccess : IRpgFileAccess
    {
        public List<IReadOnlyDictionary<string, object?>> Rows { get; } = [new Dictionary<string, object?> { ["ID"] = 1L }, new Dictionary<string, object?> { ["ID"] = 1L }];
        public FileDefinition GetDefinition(string library, string name) => new() { Name = name, Attribute = FileAttribute.Physical,
            Formats = [new() { Name = "REC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 }] }] };
        public bool MemberExists(string library, string name, string member) => true;
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(string library, string name, string member) => Rows;
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyed(string library, string name, string member) => Rows;
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefix(string library, string name, string member, IReadOnlyDictionary<string, object?> prefix) => Rows;
        public void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues) { if (keyValues.Count == 0) Rows.Clear(); }
        public void Update(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values) => throw new NotSupportedException();
        public void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values) => throw new NotSupportedException();
        public long RowCount(string library, string name, string member) => Rows.Count;
    }
}
