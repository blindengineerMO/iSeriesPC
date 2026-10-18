using Ipc.Core.Messages;

namespace Ipc.Db.Store;

/// <summary>A live cursor retaining one record and a sort position. No reader or
/// SQLite transaction survives a read; commitment units may load member workspaces.</summary>
public sealed class DatabaseRecordCursor : IDisposable
{
    public const long MaximumRelativeRecordNumber = 4294967288L;
    internal enum Operation { Next, Prior, Equal, PriorEqual, Chain, Lower, Upper, Refresh, Validate }
    internal sealed record Request(Operation Action, object?[]? Position = null, IReadOnlyDictionary<string, object?>? Key = null,
        bool Inclusive = false, object?[]? EqualPosition = null, long? RecordNumber = null, bool LockRecord = false, Action<object?[]>? RecordBlocked = null)
    {
        internal bool Reverse => Action is Operation.Prior or Operation.PriorEqual;
        internal bool PositionOnly => Action is Operation.Lower or Operation.Upper;
    }
    internal sealed record Row(IReadOnlyDictionary<string, object?> Values, long Number, int Member, object?[] Position, bool Matches = true, string? Image = null, string? Schema = null);
    private enum Placement { Beginning, OnRecord, BeforeRecord, End, EqualEnd, EqualBeginning, Invalid, AfterRecord }
    private sealed record Position(object?[]? Values, Placement Place);
    private readonly Func<Request, Row?> _read;
    private readonly Action<Row, IReadOnlyDictionary<string, object?>?>? _mutate;
    private readonly Action? _requireDelete;
    private readonly IReadOnlyList<IDisposable> _allocations;
    private readonly Func<bool, IDisposable?>? _enter;
    private readonly Action<CancellationToken>? _rebind;
    private readonly Action? _unlock;
    private readonly Func<bool>? _locked;
    private object?[]? _position;
    private Placement _place;
    private Row? _current;
    private bool _disposed;
    internal DatabaseRecordCursor(Func<Request, Row?> read, IReadOnlyList<IDisposable> allocations, Func<bool, IDisposable?>? enter = null,
        Action<Row, IReadOnlyDictionary<string, object?>?>? mutate = null, Action? requireDelete = null, Action<CancellationToken>? rebind = null, Action? unlock = null, Func<bool>? locked = null)
    { _read = read; _allocations = allocations; _enter = enter; _mutate = mutate; _requireDelete = requireDelete; _rebind = rebind; _unlock = unlock; _locked = locked; }
    public void RebindCancellation(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        (_rebind ?? throw new InvalidOperationException("This cursor cannot rebind cancellation."))(cancellationToken);
    }
    private static object?[]? Clone(object?[]? position) => position?.Select(value => value is byte[] bytes ? bytes.ToArray() : value).ToArray();
    internal object CapturePosition() { ObjectDisposedException.ThrowIf(_disposed, this); return new Position(Clone(_position), _place); }
    internal void RestorePosition(object position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position is not Position saved) throw new InvalidOperationException("Invalid cursor checkpoint.");
        _position = Clone(saved.Values); _place = saved.Place; _current = null;
    }
    public bool AtEnd => _place is Placement.End or Placement.EqualEnd;
    public bool AtBeginning => _place is Placement.Beginning or Placement.EqualBeginning;
    public IReadOnlyDictionary<string, object?>? Current => _place == Placement.OnRecord ? _current?.Values : null;
    public long? CurrentRecordNumber => _place == Placement.OnRecord ? _current?.Number : null;
    public bool IsUpdateRead => _unlock is not null;
    public bool HasUpdateLock => !_disposed && (_locked?.Invoke() ?? false);
    public IReadOnlyDictionary<string, object?>? Read() => Read(true);
    public IReadOnlyDictionary<string, object?>? Read(bool lockRecord) => ReadNext(false, null, lockRecord: lockRecord);
    public IReadOnlyDictionary<string, object?>? ReadPrior() => ReadPrior(true);
    public IReadOnlyDictionary<string, object?>? ReadPrior(bool lockRecord) => ReadNext(true, null, lockRecord: lockRecord);
    public IReadOnlyDictionary<string, object?>? ReadEqual(IReadOnlyDictionary<string, object?>? key = null, bool prior = false) => ReadEqual(key, prior, true);
    public IReadOnlyDictionary<string, object?>? ReadEqual(IReadOnlyDictionary<string, object?>? key, bool prior, bool lockRecord) => ReadNext(prior, key, equal: true, lockRecord: lockRecord);
    private IReadOnlyDictionary<string, object?>? ReadNext(bool prior, IReadOnlyDictionary<string, object?>? key, bool equal = false, bool lockRecord = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        BeginInput();
        var atBeginning = _place is Placement.Beginning or Placement.EqualBeginning;
        var atEnd = _place is Placement.End or Placement.EqualEnd;
        var equalStopped = _place is Placement.EqualBeginning or Placement.EqualEnd;
        if (_place == Placement.Invalid || !equal && equalStopped) throw Reposition();
        if (equal && key is null && (atBeginning || atEnd)) throw Reposition();
        if (prior && atBeginning || !equal && !prior && _place == Placement.End) return null;
        var reverseFromEnd = prior && _place == Placement.End;
        var restartEqual = equal && key is not null && !prior && (atEnd || _place == Placement.EqualBeginning);
        var position = reverseFromEnd || !prior && _place == Placement.Beginning || restartEqual ? null : _position;
        var action = equal ? prior ? Operation.PriorEqual : Operation.Equal : prior ? Operation.Prior : Operation.Next;
        void Blocked(object?[] target) { _position = Clone(target); _place = prior ? Placement.AfterRecord : Placement.BeforeRecord; }
        var row = _read(new(action, position, key, !prior && _place == Placement.BeforeRecord || prior && _place == Placement.AfterRecord,
            equal && key is null ? _position : null, LockRecord: lockRecord && _unlock is not null, RecordBlocked: Blocked));
        if (row is null || !row.Matches)
        {
            _place = equal ? prior ? Placement.EqualBeginning : Placement.EqualEnd : prior ? Placement.Beginning : Placement.End;
            return null;
        }
        _position = row.Position; _place = Placement.OnRecord; _current = row; return row.Values;
    }
    public bool Chain(IReadOnlyDictionary<string, object?> key) => Chain(key, true);
    public bool Chain(IReadOnlyDictionary<string, object?> key, bool lockRecord)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ChainCore(new(Operation.Chain, Key: key, LockRecord: lockRecord && _unlock is not null));
    }
    public bool ChainRecord(long number) => ChainRecord(number, true);
    public bool ChainRecord(long number, bool lockRecord) => ChainCore(new(Operation.Chain, RecordNumber: RelativeNumber(number), LockRecord: lockRecord && _unlock is not null));
    private bool ChainCore(Request request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        BeginInput();
        Row? row;
        try { row = _read(request); }
        catch (CpfException error) when (_unlock is not null && error.MessageId is "CPF1002" or "CPF1003") { _place = Placement.Invalid; throw; }
        if (row is null) { _place = Placement.Invalid; return false; }
        _position = row.Position; _place = Placement.OnRecord; _current = row; return true;
    }
    public bool SetLowerBound(IReadOnlyDictionary<string, object?> key) => SetBound(key, upper: false);
    public void SetUpperBound(IReadOnlyDictionary<string, object?> key) => SetBound(key, upper: true);
    private bool SetBound(IReadOnlyDictionary<string, object?> key, bool upper)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SetBound(new(upper ? Operation.Upper : Operation.Lower, Key: key));
    }
    public bool SetRecordLowerBound(long number) => SetBound(new(Operation.Lower, RecordNumber: RelativeNumber(number)));
    public void SetRecordUpperBound(long number) => SetBound(new(Operation.Upper, RecordNumber: RelativeNumber(number)));
    private bool SetBound(Request request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        BeginInput();
        var row = _read(request);
        if (row is null) { _position = null; _place = Placement.End; return false; }
        _position = row.Position; _place = Placement.BeforeRecord; return row.Matches;
    }
    public void Rewind(bool end = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        _read(new(Operation.Validate)); BeginInput(); _position = null; _place = end ? Placement.End : Placement.Beginning;
    }
    public void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        _read(new(Operation.Validate));
    }
    public void InvalidateCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        _read(new(Operation.Validate)); _unlock?.Invoke(); _current = null;
    }
    public void RefreshCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_place != Placement.OnRecord) return;
        var lockRecord = HasUpdateLock; BeginInput();
        var row = _read(new(Operation.Refresh, _position, LockRecord: lockRecord)); _current = row;
    }
    public void Update(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values); MutateCurrent(values);
    }
    public void Delete() => MutateCurrent(null);
    public bool Delete(IReadOnlyDictionary<string, object?> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DeleteCore(new(Operation.Chain, Key: key));
    }
    public bool DeleteRecord(long number) => DeleteCore(new(Operation.Chain, RecordNumber: RelativeNumber(number)));
    private bool DeleteCore(Request request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutate is null || _requireDelete is null) throw new CpfException("IPC0138", "This cursor does not support keyed deletion.");
        _requireDelete();
        if (_unlock is not null) { BeginInput(); request = request with { LockRecord = true }; }
        var row = _read(request);
        if (row is null) return false;
        try { _mutate(row, null); } finally { _unlock?.Invoke(); }
        _position = row.Position; _place = Placement.OnRecord; _current = null; return true;
    }
    private static long RelativeNumber(long number) => number is >= 1 and <= MaximumRelativeRecordNumber ? number :
        throw new CpfException("IPC0138", "Relative record number must be from 1 through " + MaximumRelativeRecordNumber + ".");
    private void MutateCurrent(IReadOnlyDictionary<string, object?>? values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_place != Placement.OnRecord || _current is null) throw new CpfException("CPF5001", "Read a record successfully before updating or deleting it.");
        if (_mutate is null) throw new CpfException("IPC0138", "This cursor does not support record mutations.");
        if (_unlock is not null && !HasUpdateLock) throw new CpfException("CPF5001", "Read and lock a record before updating or deleting it.");
        _mutate(_current, values); _unlock?.Invoke(); _current = null;
    }
    private void BeginInput()
    {
        if (_unlock is null) return;
        _unlock(); _current = null; _read(new(Operation.Validate));
    }
    public void Unlock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); using var operation = _enter?.Invoke(false);
        if (_unlock is null) throw new CpfException("IPC0138", "This cursor was not opened for update reads.");
        _unlock();
    }
    private static CpfException Reposition() => new("IPC0138", "Reposition the cursor using CHAIN, SETLL or SETGT before reading.");
    public void Dispose()
    {
        if (_disposed) return; using var operation = _enter?.Invoke(true);
        if (_disposed) return; _disposed = true; _position = null; _current = null;
        List<Exception>? errors = null;
        foreach (var allocation in _allocations.Reverse())
            try { allocation.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        _place = Placement.End;
        if (errors is not null) throw new AggregateException("Could not release all cursor allocations.", errors);
    }
}
