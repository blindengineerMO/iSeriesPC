using Ipc.Core.Messages;

namespace Ipc.Db.Store;

/// <summary>An output-only member handle. It never reads records or retains a reader.</summary>
public sealed class DatabaseRecordWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _validate;
    private readonly Action<IReadOnlyDictionary<string, object?>> _write;
    private readonly Action<long, IReadOnlyDictionary<string, object?>>? _relativeWrite;
    private readonly Action<CancellationToken> _rebind;
    private readonly IReadOnlyList<IDisposable> _resources;
    private bool _disposed;
    internal DatabaseRecordWriter(Action validate, Action<IReadOnlyDictionary<string, object?>> write,
        Action<CancellationToken> rebind, IReadOnlyList<IDisposable> resources, Action<long, IReadOnlyDictionary<string, object?>>? relativeWrite = null)
    { _validate = validate; _write = write; _rebind = rebind; _resources = resources; _relativeWrite = relativeWrite; }
    public void Validate() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _validate(); } }
    public void Write(IReadOnlyDictionary<string, object?> values)
    { ArgumentNullException.ThrowIfNull(values); lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _validate(); _write(values); } }
    public void WriteRelative(long number, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _validate();
            if (number is < 1 or > DatabaseRecordCursor.MaximumRelativeRecordNumber) throw new CpfException("IPC0138", "Invalid relative output record number.");
            (_relativeWrite ?? throw new CpfException("IPC0138", "This provider does not support relative record output."))(number, values);
        }
    }
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
