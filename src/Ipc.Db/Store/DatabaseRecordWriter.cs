namespace Ipc.Db.Store;

/// <summary>An output-only member handle. It never reads records or retains a reader.</summary>
public sealed class DatabaseRecordWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _validate;
    private readonly Action<IReadOnlyDictionary<string, object?>> _write;
    private readonly Action<CancellationToken> _rebind;
    private readonly IReadOnlyList<IDisposable> _resources;
    private bool _disposed;
    internal DatabaseRecordWriter(Action validate, Action<IReadOnlyDictionary<string, object?>> write,
        Action<CancellationToken> rebind, IReadOnlyList<IDisposable> resources)
    { _validate = validate; _write = write; _rebind = rebind; _resources = resources; }
    public void Validate() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _validate(); } }
    public void Write(IReadOnlyDictionary<string, object?> values)
    { ArgumentNullException.ThrowIfNull(values); lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _validate(); _write(values); } }
    public void RebindCancellation(CancellationToken token)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _rebind(token); } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception>? errors = null;
            foreach (var resource in _resources.Reverse())
                try { resource.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            if (errors is not null) throw new AggregateException("Could not close record output.", errors);
        }
    }
}
