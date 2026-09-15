namespace Ipc.Cl.Interpreter;

internal sealed class ClOpenFiles(IReadOnlyList<ClFileBinding> bindings, Func<ClFileBinding, ClDatabaseCursor>? open) : IDisposable
{
    private sealed class State(ClDatabaseCursor cursor) { internal ClDatabaseCursor Cursor { get; } = cursor; internal bool Ended { get; set; } }
    private readonly Dictionary<string, State> _files = new(StringComparer.OrdinalIgnoreCase);
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
    }
    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var file in _files.Values)
            try { file.Cursor.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        _files.Clear();
        if (errors is not null) throw new AggregateException("Could not close all CL database files.", errors);
    }
}
