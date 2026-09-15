using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class NullableKeyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public NullableKeyTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        var format = new RecordFormat { Name = "REC", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 9, Decimals = 2, NullCapable = true, Sequence = 1 },
            new() { Name = "LABEL", Type = FieldType.Alpha, Length = 8, NullCapable = true, Sequence = 2 }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "DATA", new() { Name = "DATA", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
    }
    private void Add(int id, decimal? amount, string? label) => _files.Insert("QGPL", "DATA", "DATA", "REC", new Dictionary<string, object?> { ["ID"] = id, ["AMOUNT"] = amount, ["LABEL"] = label });
    private void Logical(string name, string? uniqueness = null, bool descending = false, bool selected = false)
    {
        var lines = new List<string>();
        if (uniqueness is not null) lines.Add(LogicalFileTests.Line(' ', keywords: uniqueness));
        lines.Add(LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/DATA)"));
        lines.Add(LogicalFileTests.Line('K', "AMOUNT", descending ? "DESCEND" : ""));
        lines.Add(LogicalFileTests.Line('K', "LABEL", descending ? "DESCEND" : ""));
        if (selected) lines.Add(LogicalFileTests.Line('S', "ID", "COMP(GT 0)"));
        var source = string.Join('\n', lines);
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile(name, source);
        _files.CreateLogicalFile("QGPL", name, definition, source);
    }
    [Fact]
    public void Compound_PF_LF_and_copy_order_null_above_values_in_each_key_component()
    {
        Add(1, null, null); Add(2, 0m, null); Add(3, 0m, "A"); Add(4, -1m, "A"); Add(5, null, "A");
        var ascending = new long[] { 4, 3, 2, 5, 1 };
        Assert.Equal(ascending, _files.ReadKeyed("QGPL", "DATA", "DATA").Select(r => (long)r["ID"]!));
        Logical("ASCEND"); Logical("DESCEND", descending: true);
        Assert.Equal(ascending, _files.ReadKeyed("QGPL", "ASCEND", "ASCEND").Select(r => (long)r["ID"]!));
        Assert.Equal(ascending.Reverse(), _files.ReadKeyed("QGPL", "DESCEND", "DESCEND").Select(r => (long)r["ID"]!));
        _files.AddMember("QGPL", "DATA", "COPY");
        Assert.Equal(5, _files.CopyRecords(new("QGPL", "DESCEND"), "DESCEND", new("QGPL", "DATA"), "COPY"));
        Assert.Equal(ascending.Reverse(), _files.ReadAll("QGPL", "DATA", "COPY").Select(r => (long)r["ID"]!));
        Assert.Equal(new long[] { 5, 1 }, _files.ReadKeyPrefix("QGPL", "ASCEND", "ASCEND", new Dictionary<string, object?> { ["AMOUNT"] = null }).Select(r => (long)r["ID"]!));
    }
    [Theory]
    [InlineData("UNIQUE")]
    [InlineData("UNIQUE(*INCNULL)")]
    public void Unique_includes_nulls_without_colliding_with_real_zero_or_blank(string keyword)
    {
        Logical("UNIQ", keyword, selected: true);
        Add(1, null, null); Add(2, 0m, null); Add(3, null, ""); Add(4, 0m, "");
        Add(-1, null, null); Add(-2, null, null); // Omitted rows are outside the unique path.
        Assert.Throws<SqliteException>(() => Add(5, null, null));
        Assert.Throws<SqliteException>(() => Add(6, null, ""));
        Assert.Equal(6, _files.RowCount("QGPL", "DATA", "DATA"));
        _system.ObjectOperations.Relocate(new("QGPL", "UNIQ"), ObjectType.File, new("QGPL", "ALIAS"), copy: true);
        _files.DeleteFile("QGPL", "UNIQ"); Assert.Throws<SqliteException>(() => Add(7, null, null));
        _files.DeleteFile("QGPL", "ALIAS"); Add(7, null, null);
    }
    [Fact]
    public void Exclude_null_keys_allows_nullable_duplicates_but_rejects_complete_duplicates()
    {
        Logical("UNIQ", "UNIQUE(*EXCNULL)");
        Add(1, null, null); Add(2, null, null); Add(3, 0m, null); Add(4, 0m, null);
        Add(5, 0m, "A"); Assert.Throws<SqliteException>(() => Add(6, 0m, "A"));
        Assert.True(_files.GetDefinition("QGPL", "UNIQ")!.Logical!.ExcludeNullKeys);
    }
    [Fact]
    public void Unique_creation_with_existing_null_duplicates_rolls_back_object_and_index()
    {
        Add(1, null, null); Add(2, null, null);
        Assert.Throws<SqliteException>(() => Logical("UNIQ", "UNIQUE"));
        Assert.False(_files.FileExists("QGPL", "UNIQ")); Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Schema_17_upgrade_preserves_path_identity_or_rolls_back_duplicate_nulls(bool duplicates)
    {
        Logical("UNIQ", "UNIQUE", selected: true);
        var name = _files.GetDefinition("QGPL", "UNIQ")!.Logical!.UniquePaths["DATA"];
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        // Reconstruct the exact prior version's index shape, including a filtered constraint.
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.DATA.DATA'";
        var indexes = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) indexes.Add(reader.GetString(0));
        foreach (var index in indexes) { command.CommandText = "DROP INDEX \"" + index + "\""; command.ExecuteNonQuery(); }
        command.CommandText = "CREATE UNIQUE INDEX \"" + name + "\" ON \"QGPL.DATA.DATA\" (ipc_decimal_key_v1(\"AMOUNT\"),ipc_text_key_v1(\"LABEL\",37,8)) WHERE ID>0"; command.ExecuteNonQuery();
        Add(1, null, null); if (duplicates) Add(2, null, null);
        command.CommandText = "DELETE FROM sys_migrations WHERE version>=18; UPDATE sys_meta SET value='17' WHERE key='schema_version'"; command.ExecuteNonQuery();
        // This fixture isolates the frozen 17→18 index migration, including its rollback.
        var migrator = new Migrator(_system.Connections, Migrator.Migrations.Take(18).ToArray());
        if (duplicates)
        {
            Assert.Contains("duplicate keys", Assert.Throws<InvalidDataException>(() => migrator.MigrateToLatest()).Message);
            Assert.Equal(17, migrator.CurrentVersion());
            Add(3, null, null); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        }
        else
        {
            migrator.MigrateToLatest(); migrator.MigrateToLatest(); Assert.Equal(18, migrator.CurrentVersion());
            Assert.Equal(name, _files.GetDefinition("QGPL", "UNIQ")!.Logical!.UniquePaths["DATA"]);
            Assert.Throws<SqliteException>(() => Add(2, null, null)); Add(-1, null, null); Add(-2, null, null);
            Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        }
        command.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", command.ExecuteScalar());
    }
    public void Dispose() => _system.Dispose();
}
