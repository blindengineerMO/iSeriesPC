namespace Ipc.Core.Work;

/// <summary>A synchronous borrowed argument cell. Hosts must release it when the call ends.</summary>
public sealed class ProgramArgument
{
    private readonly Func<object?> _read;
    private readonly Action<object?> _write;
    private readonly Func<object?, object?> _normalize;
    private readonly Func<ProgramBuffer>? _buffer;
    public string? Type { get; }
    public int Length { get; }
    public int Decimals { get; }
    public ProgramConstant? Constant { get; }
    public ProgramArgument(Func<object?> read, Action<object?> write, Func<object?, object?> normalize,
        string? type = null, int length = 0, int decimals = 0, Func<ProgramBuffer>? buffer = null, ProgramConstant? constant = null)
    { _read = read; _write = write; _normalize = normalize; Type = type; Length = length; Decimals = decimals; _buffer = buffer; Constant = constant; }
    public object? Value { get => _read(); set => _write(Normalize(value)); }
    public object? Normalize(object? value) => _normalize(value);
    public ProgramBuffer? ToBuffer() => _buffer?.Invoke();
}
