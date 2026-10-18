namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed class CursorUpdateRead(CancellationToken caller, CancellationToken shutdown, Func<string, bool> held) : IDisposable
    {
        internal string Owner { get; } = Guid.NewGuid().ToString("N");
        private IDisposable? _lease;
        private CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
        internal CancellationToken Token => _cancellation.Token;
        internal bool Held => _lease is not null && held(Owner);
        internal CancellationToken Rebind(CancellationToken caller)
        {
            var next = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
            _cancellation.Dispose(); _cancellation = next; return next.Token;
        }
        internal void Hold(IDisposable lease)
        {
            if (_lease is not null) throw new InvalidOperationException("Release the prior update-read lease before retrieving another record.");
            _lease = lease;
        }
        internal void Release() { _lease?.Dispose(); _lease = null; }
        public void Dispose() { try { Release(); } finally { _cancellation.Dispose(); } }
    }
    private sealed class CursorUpdateGate(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }
}
