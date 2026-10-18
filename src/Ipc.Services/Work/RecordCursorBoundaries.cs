using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;

namespace Ipc.Services.Work;

public sealed partial class JobRecordTransactions
{
    internal CursorRegistration? RegisterCursor(JobKey job, IReadOnlyList<QualifiedName> files,
        Func<object> capture, Action<object> restore, CancellationToken cancellationToken)
    {
        var unit = Find(job);
        if (unit is null || !files.Any(unit.Enlisted.Contains)) return null;
        RequireOwner(job); EnterGate(unit, cancellationToken);
        try
        {
            unit.RequireCursorAccess();
            if (unit.Cursors.Count >= 256) throw Invalid("A record definition supports at most 256 open commitment cursors.");
            var entry = new CursorBoundary(capture, restore, capture());
            unit.Cursors.Add(entry);
            return new(this, unit, job, entry, cancellationToken);
        }
        finally { Monitor.Exit(unit.Gate); }
    }
    internal sealed class CursorBoundary(Func<object> capture, Action<object> restore, object initial)
    {
        internal Func<object> Capture { get; } = capture;
        internal Action<object> Restore { get; } = restore;
        internal object Saved { get; set; } = initial;
    }
    internal sealed class CursorRegistration : IDisposable
    {
        private readonly JobRecordTransactions _owner;
        private readonly RecordUnit _unit;
        private readonly JobKey _job;
        private readonly CursorBoundary _entry;
        private readonly CancellationToken _cancellationToken;
        private bool _disposed;
        internal CursorRegistration(JobRecordTransactions owner, RecordUnit unit, JobKey job, CursorBoundary entry, CancellationToken cancellationToken)
        { _owner = owner; _unit = unit; _job = job; _entry = entry; _cancellationToken = cancellationToken; }
        internal IDisposable Enter(bool closing = false, CancellationToken? cancellationToken = null)
        {
            if (!closing) _owner.RequireOwner(_job);
            else if (OperationIdentity.Current is { } identity && identity.Job != _job)
                throw new CpfException("CPF9802", "Cursor belongs to its executing job.");
            EnterGate(_unit, closing ? CancellationToken.None : cancellationToken ?? _cancellationToken);
            try
            {
                if (!closing)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _unit.RequireCursorAccess();
                }
                return new CursorLease(_unit.Gate);
            }
            catch { Monitor.Exit(_unit.Gate); throw; }
        }
        public void Dispose()
        {
            using var lease = Enter(closing: true);
            if (_disposed) return; _disposed = true; _unit.Cursors.Remove(_entry);
        }
    }
    private sealed class CursorLease(object gate) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (!_disposed) { _disposed = true; Monitor.Exit(gate); } }
    }
    internal sealed partial class RecordUnit
    {
        internal readonly List<CursorBoundary> Cursors = [];
        internal void RequireCursorAccess() { ObjectDisposedException.ThrowIf(_disposed, this); RequireIdle(); }
        private IReadOnlyList<(CursorBoundary Cursor, object Position)> CaptureCursorPositions() =>
            Cursors.Select(cursor => (cursor, cursor.Capture())).ToArray();
        private static void AcceptCursorPositions(IReadOnlyList<(CursorBoundary Cursor, object Position)> positions)
        { foreach (var item in positions) item.Cursor.Saved = item.Position; }
        internal void RollbackWithCursors()
        {
            RequireIdle(); Reset();
            List<Exception>? errors = null;
            foreach (var cursor in Cursors)
                try { cursor.Restore(cursor.Saved); }
                catch (Exception error) { (errors ??= []).Add(error); }
            if (errors is not null) throw new AggregateException("Could not restore all commitment cursor positions.", errors);
        }
        internal void RequireClosedCursors()
        { if (Cursors.Count != 0) throw Invalid("Close cursors opened under this record definition before ending it."); }
    }
}
