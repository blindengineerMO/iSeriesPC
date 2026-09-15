namespace Ipc.Db.Store;

/// <summary>A forward-only live cursor. Each read retains just a sort position;
/// no reader, SQLite transaction or materialized file survives a read.</summary>
public sealed class DatabaseRecordCursor : IDisposable
{
    internal sealed record Row(IReadOnlyDictionary<string, object?> Values, long Number, int Member, object?[] Position);
    private readonly Func<object?[]?, Row?> _read;
    private readonly IReadOnlyList<IDisposable> _allocations;
    private object?[]? _position;
    private bool _ended, _disposed;
    internal DatabaseRecordCursor(Func<object?[]?, Row?> read, IReadOnlyList<IDisposable> allocations) { _read = read; _allocations = allocations; }
    public IReadOnlyDictionary<string, object?>? Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ended) return null;
        var row = _read(_position);
        if (row is null) { _ended = true; return null; }
        _position = row.Position;
        return row.Values;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _position = null;
        List<Exception>? errors = null;
        foreach (var allocation in _allocations.Reverse())
            try { allocation.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        _ended = true;
        if (errors is not null) throw new AggregateException("Could not release all cursor allocations.", errors);
    }
}
