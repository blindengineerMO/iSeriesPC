namespace Ipc.Core.Work;

/// <summary>An opaque storage identity and byte range; the owner must not expose backing memory.</summary>
public sealed record ProgramArgumentStorage(object Owner, int Offset, int Length);
public sealed record ProgramArgumentShape(string ElementType, int ElementLength, int Decimals, int Dimension, bool Structure = false, bool OpaqueBytes = false);

/// <summary>A synchronous borrowed argument cell. Hosts must release it when the call ends.</summary>
public sealed class ProgramArgument
{
    private readonly Func<object?> _read;
    private readonly Action<object?> _write;
    private readonly Func<object?, object?> _normalize;
    private readonly Func<ProgramBuffer?>? _buffer;
    public string? Type { get; }
    public int Length { get; }
    public int Decimals { get; }
    public ProgramConstant? Constant { get; }
    public bool IsReadOnly { get; init; }
    public bool IsOmitted { get; init; }
    public ProgramArgumentShape? Shape { get; init; }
    public ProgramArgumentMemory? Memory { get; init; }
    public bool HasBuffer => _buffer is not null;
    public ProgramArgumentStorage? Storage { get; }
    public ProgramArgument(Func<object?> read, Action<object?> write, Func<object?, object?> normalize,
        string? type = null, int length = 0, int decimals = 0, Func<ProgramBuffer?>? buffer = null, ProgramConstant? constant = null)
        : this(read, write, normalize, type, length, decimals, buffer, constant, null) { }
    public ProgramArgument(Func<object?> read, Action<object?> write, Func<object?, object?> normalize,
        string? type, int length, int decimals, Func<ProgramBuffer?>? buffer, ProgramConstant? constant, ProgramArgumentStorage? storage)
    {
        if (storage is not null && (storage.Owner is null || storage.Offset < 0 || storage.Length < 1 || (long)storage.Offset + storage.Length > int.MaxValue))
            throw new ArgumentException("Invalid borrowed storage range.", nameof(storage));
        _read = read; _write = write; _normalize = normalize; Type = type; Length = length; Decimals = decimals; _buffer = buffer; Constant = constant; Storage = storage;
    }
    public object? Value { get => _read(); set => _write(Normalize(value)); }
    public object? Normalize(object? value) => IsReadOnly
        ? throw new Ipc.Core.Messages.CpfException("CPF3C90", "A read-only parameter cannot be changed.") : _normalize(value);
    public object? NormalizeReturnedValue(object? value)
    {
        var normalized = _normalize(value);
        if (!IsReadOnly) return normalized;
        var unchanged = normalized is ProgramBuffer updated && ToBuffer() is { } current
            ? updated.Ccsid == current.Ccsid && updated.ToArray().AsSpan().SequenceEqual(current.ToArray())
            : Equals(normalized, Value);
        if (!unchanged) throw new Ipc.Core.Messages.CpfException("CPF3C90", "A returned value changed a read-only parameter.");
        return normalized;
    }
    public ProgramBuffer? ToBuffer() => _buffer?.Invoke();
    public static ProgramArgument Omitted() => new(() => null, _ => { }, value => value is null ? null :
        throw new Ipc.Core.Messages.CpfException("CPF3C90", "An omitted parameter has no returned storage."), "*OMIT")
        { IsOmitted = true, IsReadOnly = true };
    public static bool Overlaps(object? left, object? right)
    {
        if (left is not ProgramArgument lhs || right is not ProgramArgument rhs) return false;
        if (lhs.IsOmitted || rhs.IsOmitted) return false;
        if (ReferenceEquals(lhs, rhs)) return true;
        return lhs.Storage is { } a && rhs.Storage is { } b && ReferenceEquals(a.Owner, b.Owner) &&
            a.Offset < (long)b.Offset + b.Length && b.Offset < (long)a.Offset + a.Length;
    }
}
