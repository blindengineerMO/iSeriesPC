using System.Collections;

namespace Ipc.Cl.Interpreter;

// A call frame maps names to shared cells, so aliased parameters stay aliased even
// before RETURN and mutations remain visible when a callee propagates an error.
internal sealed class ClVariableCell(string value, ClVariableDefinition? definition, int ccsid)
{
    private string _value = value;
    private Ipc.Core.Work.ProgramBuffer? _raw;
    internal Ipc.Core.Work.ProgramBuffer? RawBuffer => _raw;
    internal ClVariableDefinition? Definition { get; } = definition;
    internal string Value { get => _raw?.ToText() ?? _value; set { var assigned = Definition?.Assign(value, ccsid) ?? value; _value = assigned; _raw = null; } }
    internal void AssignBuffer(Ipc.Core.Work.ProgramBuffer buffer)
    {
        if (Definition?.Type != "*CHAR" || buffer.Length != Definition.Length) throw new ClRuntimeException("CL character buffer length does not match its declaration.");
        _raw = new(buffer.ToArray(), ccsid);
    }
    internal Ipc.Core.Work.ProgramBuffer? ToBuffer() => _raw ?? Definition?.Encode(Value, ccsid);
    internal object ResultValue()
    {
        if (_raw is not null)
        {
            try { return _raw.ToText(); }
            catch (Ipc.Core.Messages.CpfException) { return _raw; }
        }
        return Definition?.Read(Value) ?? Value;
    }
    private object? Normalize(object? updated)
    {
        if (Definition?.Type == "*CHAR" && updated is Ipc.Core.Work.ProgramBuffer bytes)
        {
            if (bytes.Length != Definition.Length) throw new ClRuntimeException("CL character buffer length does not match its declaration.");
            return new Ipc.Core.Work.ProgramBuffer(bytes.ToArray(), ccsid);
        }
        return Definition is null ? updated is Ipc.Core.Work.ProgramBuffer raw ? raw.ToText() : ClExpression.Text(updated ?? "")
            : Definition.Read(updated is Ipc.Core.Work.ProgramBuffer buffer ? Definition.Decode(buffer) : Definition.Assign(updated ?? "", ccsid));
    }
    internal Ipc.Core.Work.ProgramArgument Borrow() => new(
        ResultValue,
        updated => { if (updated is Ipc.Core.Work.ProgramBuffer bytes) AssignBuffer(bytes); else Value = ClExpression.Text(updated ?? ""); },
        Normalize,
        Definition?.Type, Definition?.Length ?? 0, Definition?.Decimals ?? 0,
        Definition is null ? null : () => ToBuffer()!);
}

internal sealed class ClVariables : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, ClVariableCell> _cells = new(StringComparer.OrdinalIgnoreCase);
    internal ClVariableCell Cell(string name) => _cells.TryGetValue(name, out var cell) ? cell : throw new ClRuntimeException($"Variable '{name}' is not initialized.");
    internal void Bind(string name, ClVariableCell cell) => _cells.Add(name, cell);
    internal void Add(string name, string value, ClVariableDefinition? definition, int ccsid) => _cells.Add(name, new(value, definition, ccsid));
    public string this[string name] { get => Cell(name).Value; set { if (_cells.TryGetValue(name, out var cell)) cell.Value = value; else _cells.Add(name, new(value, null, 37)); } }
    public IEnumerable<string> Keys => _cells.Keys;
    public IEnumerable<string> Values => _cells.Values.Select(c => c.Value);
    public int Count => _cells.Count;
    public bool ContainsKey(string key) => _cells.ContainsKey(key);
    public bool TryGetValue(string key, out string value) { if (_cells.TryGetValue(key, out var cell)) { value = cell.Value; return true; } value = ""; return false; }
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _cells.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Value)).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record ClInvocationResult(Ipc.Cl.Commands.CommandResult Result, IReadOnlyList<object?> Parameters);

public sealed record ClCommandContext(Func<string, Ipc.Core.Work.ProgramArgument> Variable, Func<string, string> Resolve);

public sealed record ClProgramMessage(string Text, string? MessageId, Ipc.Core.Work.ProgramBuffer? ReplacementData,
    Ipc.Core.Work.ProgramMessageReference? Reference = null);
