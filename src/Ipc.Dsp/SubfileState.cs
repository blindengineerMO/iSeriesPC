namespace Ipc.Dsp;

public sealed record SubfileRecord(int Number, IReadOnlyDictionary<string, object?> Values, bool[] Indicators);

/// <summary>Per-open subfile storage. Snapshots never expose mutable record or indicator state.</summary>
public sealed class SubfileState
{
    private sealed record Entry(Dictionary<string, object?> Values, bool[] Indicators, bool Changed);
    private readonly SortedDictionary<int, Entry> _records = new();
    private readonly PanelRecord _definition;
    public int Capacity { get; }
    public int Count => _records.Count;
    public IReadOnlyList<int> RecordNumbers => _records.Keys.ToArray();
    public SubfileState(PanelRecord definition, int capacity)
    {
        if (capacity is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(capacity));
        _definition = definition; Capacity = capacity;
    }
    public void Write(int number, IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators = null)
    {
        if (_records.ContainsKey(number)) throw new InvalidOperationException("Subfile record already exists; use Update.");
        Store(number, values, indicators, false);
    }
    public void Update(int number, IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators = null)
    {
        if (!_records.ContainsKey(number)) throw new InvalidOperationException("Subfile record does not exist.");
        Store(number, values, indicators ?? _records[number].Indicators, false);
    }
    private void Store(int number, IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators, bool changed)
    {
        if (number < 1 || number > Capacity) throw new ArgumentOutOfRangeException(nameof(number));
        if (indicators is not null && indicators.Count != 100) throw new ArgumentException("Expected 100 indicators.");
        var flags = indicators?.ToArray() ?? new bool[100];
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in _definition.Fields.Where(f => !f.Constant))
        {
            var value = values.GetValueOrDefault(field.BindingName);
            if (value is not (null or string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan))
                throw new ArgumentException("Subfile bindings require scalar values.");
            copy[field.BindingName] = value;
        }
        _records[number] = new(copy, flags, changed || _definition.Keywords.Any(k => k.Name == "SFLNXTCHG" && k.Enabled(flags)));
    }
    public SubfileRecord Read(int number)
    {
        var entry = _records[number]; return new(number, new Dictionary<string, object?>(entry.Values), entry.Indicators.ToArray());
    }
    public SubfileRecord? ReadNextChanged(int after = 0)
    {
        foreach (var (number, entry) in _records)
        {
            if (number <= after || !entry.Changed) continue;
            _records[number] = entry with { Changed = false }; return Read(number);
        }
        return null;
    }
    internal void Accept(int number, IReadOnlyDictionary<string, object?> values)
    {
        var entry = _records[number];
        // Only fields transferred by this page participate; hidden and truncated fields retain their values.
        var next = new Dictionary<string, object?>(entry.Values); var changed = entry.Changed;
        foreach (var (key, value) in values)
        {
            changed = true;
            next[key] = value;
        }
        _records[number] = entry with { Values = next, Changed = changed };
    }
    public void Clear() => _records.Clear();
}
