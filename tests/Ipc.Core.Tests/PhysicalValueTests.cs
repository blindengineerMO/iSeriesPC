using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class PhysicalValueTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public PhysicalValueTests() { _system.Start(); _files = new(_system.Connections, _system.Objects); }
    private void Create(string name, int decimals = 2, bool keyed = true, bool nullable = false)
    {
        var format = new RecordFormat { Name = "RECORD", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4 },
            new() { Name = "VALUE", Type = FieldType.Packed, Length = 29, Decimals = decimals, Sequence = keyed ? 1 : 0, NullCapable = nullable }
        } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", name, new() { Name = name, Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
    }
    private void Add(string name, int id, object? value) => _files.Insert("QGPL", name, name, "RECORD", new Dictionary<string, object?> { ["ID"] = id, ["VALUE"] = value });

    [Fact]
    public void Physical_key_order_and_prefixes_distinguish_adjacent_large_scaled_decimals()
    {
        Create("SCALED"); var smaller = 10000000000000000000000000.01m; var larger = 10000000000000000000000000.02m;
        Add("SCALED", 1, larger); Add("SCALED", 2, smaller); Add("SCALED", 3, -smaller);
        Assert.Equal(new long[] { 3, 2, 1 }, _files.ReadKeyed("QGPL", "SCALED", "SCALED").Select(r => (long)r["ID"]!));
        Assert.Equal(2L, Assert.Single(_files.ReadKeyPrefix("QGPL", "SCALED", "SCALED", new Dictionary<string, object?> { ["VALUE"] = smaller }))["ID"]);
        _files.Update("QGPL", "SCALED", "SCALED", "RECORD", new Dictionary<string, object?> { ["VALUE"] = smaller, ["ID"] = 8 });
        _files.Delete("QGPL", "SCALED", "SCALED", "RECORD", new Dictionary<string, object?> { ["VALUE"] = larger });
        Assert.Equal(new long[] { 3, 8 }, _files.ReadKeyed("QGPL", "SCALED", "SCALED").Select(r => (long)r["ID"]!));
    }

    [Fact]
    public void Large_integral_decimals_are_stored_as_text_and_preserve_all_29_digits()
    {
        Create("WHOLE", 0); Add("WHOLE", 1, decimal.MaxValue); Add("WHOLE", 2, decimal.MaxValue - 1);
        Assert.Equal(decimal.MaxValue, _files.ReadKeyed("QGPL", "WHOLE", "WHOLE")[1]["VALUE"]);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(VALUE) FROM \"QGPL.WHOLE.WHOLE\" WHERE ID=1"; Assert.Equal("text", command.ExecuteScalar());
        Assert.Equal(2L, Assert.Single(_files.ReadKeyPrefix("QGPL", "WHOLE", "WHOLE", new Dictionary<string, object?> { ["VALUE"] = decimal.MaxValue - 1 }))["ID"]);
    }

    [Fact]
    public void Legacy_large_integer_affinity_is_rejected_before_a_write_can_round_data()
    {
        Create("LEGACY", 0, keyed: false);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE \"QGPL.LEGACY.LEGACY\"; CREATE TABLE \"QGPL.LEGACY.LEGACY\" (ID INTEGER,VALUE INTEGER)"; command.ExecuteNonQuery();
        Assert.Throws<CpfException>(() => Add("LEGACY", 1, decimal.MaxValue));
        Assert.Equal(0, _files.RowCount("QGPL", "LEGACY", "LEGACY"));
    }

    [Fact]
    public void Nulls_and_invalid_values_do_not_turn_into_zero_or_blank()
    {
        Create("VALUES", nullable: true); Add("VALUES", 1, null);
        Assert.Null(Assert.Single(_files.ReadAll("QGPL", "VALUES", "VALUES"))["VALUE"]);
        Assert.Single(_files.ReadKeyPrefix("QGPL", "VALUES", "VALUES", new Dictionary<string, object?> { ["VALUE"] = null }));
        Assert.Throws<CpfException>(() => Add("VALUES", 2, "bad")); Assert.Throws<CpfException>(() => Add("VALUES", 2, 1.001m));
        Create("STRICT", keyed: false); Assert.Throws<CpfException>(() => Add("STRICT", 1, null));
        Add("STRICT", 1, 1m);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE \"QGPL.STRICT.STRICT\" SET VALUE='bad'"; command.ExecuteNonQuery();
        Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "STRICT", "STRICT"));
    }
    [Fact]
    public void Single_precision_storage_matches_the_independent_IEEE_record_fixture()
    {
        var format = new RecordFormat { Name = "FLOATREC", Fields = new() { new() { Name = "VALUE", Type = FieldType.Float, Length = 4 } } }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "FLOATS", new() { Name = "FLOATS", Attribute = FileAttribute.Physical, Formats = new() { format } }, "");
        _files.Insert("QGPL", "FLOATS", "FLOATS", "FLOATREC", new Dictionary<string, object?> { ["VALUE"] = 0.1d });
        var value = Assert.IsType<float>(Assert.Single(_files.ReadAll("QGPL", "FLOATS", "FLOATS"))["VALUE"]);
        Assert.Equal(0x3DCCCCCD, BitConverter.SingleToInt32Bits(value));
        Assert.Throws<CpfException>(() => _files.Insert("QGPL", "FLOATS", "FLOATS", "FLOATREC", new Dictionary<string, object?> { ["VALUE"] = double.MaxValue }));
        Assert.Equal(1, _files.RowCount("QGPL", "FLOATS", "FLOATS"));
    }
    public void Dispose() => _system.Dispose();
}
