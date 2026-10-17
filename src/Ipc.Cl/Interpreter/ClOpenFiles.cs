namespace Ipc.Cl.Interpreter;

internal sealed class ClOpenFiles(IReadOnlyList<ClFileBinding> bindings, Func<ClFileBinding, ClDatabaseCursor>? open,
    Func<ClFileBinding, IClDisplayFile>? openDisplay = null) : IDisposable
{
    private sealed class State(ClDatabaseCursor cursor) { internal ClDatabaseCursor Cursor { get; } = cursor; internal bool Ended { get; set; } }
    private readonly Dictionary<string, State> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IClDisplayFile> _displays = new(StringComparer.OrdinalIgnoreCase);
    internal ClFileReadResult? Display(string id, ClDisplayOperation operation, string record, IReadOnlyDictionary<string, object?> values)
    {
        if (!_displays.TryGetValue(id, out var display))
        {
            display = openDisplay?.Invoke(bindings.Single(f => f.Request.OpenId == id)) ?? throw new ClRuntimeException("CL display execution requires a terminal host.");
            _displays.Add(id, display);
        }
        return display.Transfer(operation, record, values);
    }
    internal ClFileReadResult Receive(string id)
    {
        if (!_files.TryGetValue(id, out var state))
        {
            var binding = bindings.Single(f => f.Request.OpenId == id);
            var cursor = open?.Invoke(binding) ?? throw new ClRuntimeException("CL database file execution requires a file host.");
            state = new(cursor); _files.Add(id, state);
        }
        if (!state.Ended && state.Cursor.Read() is { } row) return row;
        state.Ended = true;
        throw new ClRuntimeException("End of file for open identifier " + id + ".", "CPF0864");
    }
    internal void Close(string id)
    {
        if (_files.Remove(id, out var state)) state.Cursor.Close();
        if (_displays.Remove(id, out var display)) display.Dispose();
    }
    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var file in _files.Values)
            try { file.Cursor.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        _files.Clear();
        foreach (var display in _displays.Values)
            try { display.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        _displays.Clear();
        if (errors is not null) throw new AggregateException("Could not close all CL database files.", errors);
    }
}
