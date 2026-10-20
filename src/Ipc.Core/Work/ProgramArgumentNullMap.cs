using Ipc.Core.Messages;

namespace Ipc.Core.Work;

/// <summary>A bounded borrowed null-byte map. The owner enforces its synchronous call lifetime.</summary>
public sealed class ProgramArgumentNullMap
{
    private readonly Func<int, bool> _nullable;
    private readonly Func<int, bool> _read;
    private readonly Action<int, bool> _validate;
    private readonly Action<int, bool> _write;
    private readonly Func<int, ProgramArgumentStorage?>? _storage;
    public ProgramArgumentStorage? Storage(int index) { Check(index); return _storage?.Invoke(index); }
    public int Count { get; }
    public bool IsReadOnly { get; }
    public ProgramArgumentNullMap(int count, Func<int, bool> nullable, Func<int, bool> read,
        Action<int, bool> validate, Action<int, bool> write, bool readOnly = false, Func<int, ProgramArgumentStorage?>? storage = null)
    {
        if (count is < 0 or > 1048576) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count; _storage = storage; _nullable = nullable; _read = read; _validate = validate; _write = write; IsReadOnly = readOnly;
    }
    private void Check(int index) { if (index < 0 || index >= Count) throw new CpfException("CPF3C90", "Null-map access exceeds its argument bounds."); }
    public bool IsNullable(int index) { Check(index); return _nullable(index); }
    public bool Read(int index)
    {
        Check(index); var value = _read(index);
        if (value && !IsNullable(index)) throw new CpfException("CPF3C90", "A nonnullable null-map entry cannot be on.");
        return value;
    }
    public ProgramArgumentNullMap Snapshot()
    {
        var values = new bool[Count]; var nullable = new bool[Count];
        for (var index = 0; index < Count; index++) { nullable[index] = IsNullable(index); values[index] = Read(index); }
        return new(Count, index => nullable[index], index => values[index], (_, _) => { }, (index, value) => values[index] = value, IsReadOnly);
    }
    public void ValidateReturnedValues(IReadOnlyList<bool> values)
    {
        if (values.Count != Count) throw new CpfException("CPF3C90", "Returned null-map length differs from its argument.");
        for (var index = 0; index < Count; index++)
            if (values[index] != Read(index)) Validate(index, values[index]);
    }
    public void Validate(int index, bool value)
    {
        Check(index);
        if (IsReadOnly) throw new CpfException("CPF3C90", "A read-only null map cannot be changed.");
        if (value && !IsNullable(index)) throw new CpfException("CPF3C90", "A nonnullable null-map entry cannot be set on.");
        _validate(index, value);
    }
    public void Write(int index, bool value) { Validate(index, value); _write(index, value); }
    public ProgramArgumentNullMap Scoped(Action check, bool readOnly = false) => new(Count,
        index => { check(); return IsNullable(index); }, index => { check(); return Read(index); },
        (index, value) => { check(); Validate(index, value); }, (index, value) => { check(); _write(index, value); }, readOnly || IsReadOnly, index => { check(); return Storage(index); });
}
