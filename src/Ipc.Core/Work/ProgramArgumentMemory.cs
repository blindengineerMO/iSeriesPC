using Ipc.Core.Messages;

namespace Ipc.Core.Work;

/// <summary>A bounded synchronous byte address. Delegates must enforce their call lifetime.</summary>
public sealed class ProgramArgumentMemory
{
    private readonly Func<int, int, ProgramBuffer> _read;
    private readonly Action<int, ProgramBuffer> _validate;
    private readonly Action<int, ProgramBuffer> _write;
    private readonly Action? _check;
    public ProgramArgumentStorage Storage { get; }
    public ProgramArgumentShape? Shape { get; }
    public int Capacity => Storage.Length;
    public int Ccsid { get; }
    public bool IsReadOnly { get; }
    public ProgramArgumentMemory(ProgramArgumentStorage storage, int ccsid, ProgramArgumentShape? shape,
        Func<int, int, ProgramBuffer> read, Action<int, ProgramBuffer> validate, Action<int, ProgramBuffer> write, bool readOnly = false, Action? check = null)
    {
        if (storage.Owner is null || storage.Offset < 0 || storage.Length is < 0 or > 1048576 || (long)storage.Offset + storage.Length > int.MaxValue)
            throw new ArgumentException("Invalid native argument memory range.", nameof(storage));
        Storage = storage; Ccsid = ccsid; Shape = shape; _read = read; _validate = validate; _write = write; IsReadOnly = readOnly; _check = check;
    }
    private void Range(int offset, int length)
    {
        _check?.Invoke();
        if (offset < 0 || length < 0 || (long)offset + length > Capacity) throw new CpfException("CPF3C3A", "Native parameter access exceeds the actual argument storage.");
    }
    public ProgramBuffer Read(int offset, int length)
    {
        Range(offset, length); var value = _read(offset, length);
        if (value.Length != length || value.Ccsid != Ccsid) throw new CpfException("CPF3C3A", "Native memory provider returned incompatible bytes.");
        return value;
    }
    public void Validate(int offset, ProgramBuffer value)
    {
        Range(offset, value.Length);
        if (IsReadOnly) throw new CpfException("CPF3C90", "A read-only parameter cannot be changed.");
        if (value.Ccsid != Ccsid) throw new CpfException("CPF3C3A", "Native memory CCSIDs differ.");
        _validate(offset, value);
    }
    public void Write(int offset, ProgramBuffer value) { Validate(offset, value); _write(offset, value); }
    public ProgramArgumentMemory Slice(int offset, int? limit = null, ProgramArgumentShape? shape = null)
    {
        if (offset < 0 || limit < 0 || (long)Storage.Offset + offset > int.MaxValue) throw new CpfException("CPF3C3A", "Invalid native parameter address.");
        var length = Math.Min(Math.Max(0, Capacity - offset), limit ?? int.MaxValue);
        return new(new(Storage.Owner, Storage.Offset + offset, length), Ccsid, shape ?? Shape,
            (start, count) => Read(checked(offset + start), count), (start, value) => Validate(checked(offset + start), value),
            (start, value) => Write(checked(offset + start), value), IsReadOnly, _check);
    }
    public ProgramArgumentMemory Scoped(Action check, bool readOnly = false) => new(Storage, Ccsid, Shape,
        (start, count) => { check(); return Read(start, count); }, (start, value) => { check(); Validate(start, value); },
        (start, value) => { check(); Write(start, value); }, readOnly || IsReadOnly, check);
}
