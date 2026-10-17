namespace Ipc.Dsp;

public sealed record SubfileRecord(int Number, IReadOnlyDictionary<string, object?> Values, bool[] Indicators, DisplayMessage? Message = null);

/// <summary>Per-open subfile storage. Snapshots never expose mutable record or indicator state.</summary>
public sealed class SubfileState
{
    private sealed record Entry(Dictionary<string, object?> Values, bool[] Indicators, bool Changed, DisplayMessage? Message);
    private readonly SortedDictionary<int, Entry> _records = new();
    private readonly PanelRecord _definition;
    private readonly Func<string, string, DisplayMessage?>? _programMessages;
    public int Capacity { get; private set; }
    public int Count => _records.Count;
    public IReadOnlyList<int> RecordNumbers => _records.Keys.ToArray();
    public SubfileState(PanelRecord definition, int capacity, Func<string, string, DisplayMessage?>? programMessages = null)
    {
        if (capacity is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(capacity));
        _definition = definition; Capacity = capacity; _programMessages = programMessages;
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
        DisplayMessage? message = null;
        if (SubfileLayout.Keyword(_definition, "SFLMSGRCD") is not null)
        {
            var key = copy.GetValueOrDefault(_definition.Fields[0].BindingName) as string;
            var queue = copy.GetValueOrDefault(_definition.Fields[1].BindingName) as string;
            if (key is null || key.Length != 4 || key.Any(c => c > 255) || string.IsNullOrWhiteSpace(queue) || queue.Length > 10 || queue.Any(char.IsControl))
                throw new ArgumentException("Message subfiles require a four-byte key and program queue name of at most 10 characters.");
            message = _programMessages?.Invoke(queue.TrimEnd(), key) ?? throw new InvalidOperationException("Subfile message is unavailable from the program queue.");
            ValidateMessage(message);
        }
        _records[number] = new(copy, flags, changed || _definition.Keywords.Any(k => k.Name == "SFLNXTCHG" && k.Enabled(flags)), message);
    }
    public SubfileRecord Read(int number)
    {
        var entry = _records[number]; return new(number, new Dictionary<string, object?>(entry.Values), entry.Indicators.ToArray(), entry.Message);
    }
    public SubfileRecord? ReadNextChanged(int after = 0)
    {
        var record = PeekNextChanged(after);
        if (record is not null) AcknowledgeChanged(record.Number);
        return record;
    }
    public SubfileRecord? PeekNextChanged(int after = 0)
    {
        foreach (var (number, entry) in _records)
        {
            if (number <= after || !entry.Changed) continue;
            return Read(number);
        }
        return null;
    }
    public void AcknowledgeChanged(int number)
    {
        var entry = _records[number]; _records[number] = entry with { Changed = false };
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

    internal Action Checkpoint()
    {
        var records = _records.ToArray(); var capacity = Capacity;
        return () => { _records.Clear(); foreach (var pair in records) _records.Add(pair.Key, pair.Value); Capacity = capacity; };
    }

    internal static void ValidateMessage(DisplayMessage message)
    {
        if (message.Text is null || message.Text.Length > 32768 || message.Text.Any(char.IsControl) ||
            message.Details is { } details && (details.Title is null || details.Text is null || details.SecondLevel is null ||
                details.Title.Length > 132 || details.Text.Length > 32768 || details.SecondLevel.Length > 32768 ||
                details.Title.Any(char.IsControl) || details.Text.Any(char.IsControl) || details.SecondLevel.Any(char.IsControl)))
            throw new ArgumentException("Invalid display message text.");
    }

    internal void LoadMessages(IReadOnlyList<SubfileMessage> messages, string queue)
    {
        if (messages.Count > 9999) throw new ArgumentException("Message subfile exceeds 9999 records.");
        var next = new SortedDictionary<int, Entry>();
        for (var n = 0; n < messages.Count; n++)
        {
            var message = messages[n];
            if (message.Key.Length != 4 || message.Key.Any(c => c > 255)) throw new ArgumentException("Invalid message key.");
            ValidateMessage(message.Message);
            next[n + 1] = new(new(StringComparer.Ordinal) { [_definition.Fields[0].BindingName] = message.Key,
                [_definition.Fields[1].BindingName] = queue }, new bool[100], false, message.Message);
        }
        _records.Clear(); foreach (var pair in next) _records.Add(pair.Key, pair.Value);
        Capacity = Math.Max(Capacity, messages.Count);
    }
}
