using Ipc.Db.Definitions;
using Ipc.Db.Store;

namespace Ipc.Rpg.Runtime;

public sealed class RpgFileCursor : IDisposable
{
    private readonly IRpgFileAccess _access;
    private readonly string _library;
    private readonly string _name;
    private readonly string _member;
    private readonly FileDefinition _definition;
    private readonly DatabaseRecordCursor? _live;
    private readonly DatabaseRecordWriter? _writer;
    private readonly bool _keyed;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _records = Array.Empty<IReadOnlyDictionary<string, object?>>();
    private int _position = -1;
    private bool _disposed;
    public void Dispose() { if (_disposed) return; try { _live?.Dispose(); } finally { try { _writer?.Dispose(); } finally { _disposed = true; _records = Array.Empty<IReadOnlyDictionary<string, object?>>(); } } }

    public RpgFileCursor(IRpgFileAccess access, string library, string name, string member, CancellationToken cancellationToken = default, bool? keyed = null, bool updateRead = false, bool deleteAccess = false, bool output = false, bool input = true, TimeSpan? recordWait = null)
    {
        if (deleteAccess && !updateRead) throw new RpgRuntimeException("Delete access requires an update cursor.");
        if (!input && (updateRead || !output)) throw new RpgRuntimeException("Invalid file input/output access.");
        IsUpdateRead = updateRead; DeleteAccess = deleteAccess;
        OutputAccess = output; InputAccess = input;
        RecordWait = recordWait;
        _access = access;
        _library = library;
        _name = name;
        _member = member;
        _definition = access.GetDefinition(library, name)
            ?? throw new RpgRuntimeException($"File {name} in library {library} not found.");
        var hasKeys = _definition.PrimaryFormat.Fields.Any(field => field.Sequence > 0);
        _keyed = keyed ?? hasKeys;
        if (_keyed && !hasKeys) throw new RpgRuntimeException("Keyed access requires declared file keys.");
        if (!access.MemberExists(library, name, member))
        {
            throw new RpgRuntimeException($"Member {member} not found in file {name}.");
        }

        try
        {
            if (access is IRpgCursorFileAccess cursors)
            {
                if (output) _writer = cursors.OpenWriter(library, name, member, cancellationToken);
                if (input) _live = updateRead ? recordWait is { } wait ? cursors.OpenUpdateCursor(library, name, member, _keyed, deleteAccess, wait, cancellationToken) :
                    cursors.OpenUpdateCursor(library, name, member, _keyed, deleteAccess, cancellationToken) :
                    keyed.HasValue ? cursors.OpenCursor(library, name, member, _keyed, cancellationToken) : cursors.OpenCursor(library, name, member, cancellationToken);
            }
            else if (keyed.HasValue || updateRead || output) throw new RpgRuntimeException("An explicit access mode requires a cursor file provider.");
            else Reload();
            if (updateRead && _live?.IsUpdateRead != true)
                throw new RpgRuntimeException("Update input requires a cursor with retained read-lock ownership.");
        }
        catch { try { _live?.Dispose(); } finally { _writer?.Dispose(); } throw; }
    }

    public string Library => _library;

    public string Name => _name;

    public string Member => _member;

    public string Format => _definition.PrimaryFormat.Name;

    public FileDefinition Definition => _definition;
    public bool IsKeyed => _keyed;
    public bool IsUpdateRead { get; }
    public bool DeleteAccess { get; }
    public bool OutputAccess { get; }
    public bool InputAccess { get; }
    public TimeSpan? RecordWait { get; }
    public void Write(IReadOnlyDictionary<string, object?> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        (_writer ?? throw new RpgRuntimeException("WRITE requires declared output access.")).Write(values);
    }
    public bool HasUpdateLock => _live?.HasUpdateLock ?? false;
    public void Unlock()
    {
        if (!IsUpdateRead) throw new RpgRuntimeException("UNLOCK requires an update cursor.");
        RecordCursor().Unlock();
    }
    public long? CurrentRecordNumber => _live?.CurrentRecordNumber;
    public void RebindCancellation(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_live is null && _writer is null) throw new RpgRuntimeException("Retained DISK files require a live file provider.");
        _writer?.RebindCancellation(cancellationToken); _live?.RebindCancellation(cancellationToken);
    }
    private DatabaseRecordCursor RecordCursor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireInput();
        return _live ?? throw new RpgRuntimeException("Relative record access requires a cursor provider with physical record identity.");
    }
    public bool ChainRecord(long number) => ChainRecord(number, true);
    public bool ChainRecord(long number, bool lockRecord) => RecordCursor().ChainRecord(number, lockRecord);
    public bool SetRecordLowerBound(long number) => RecordCursor().SetRecordLowerBound(number);
    public void SetRecordUpperBound(long number) => RecordCursor().SetRecordUpperBound(number);
    public bool DeleteRecord(long number) => RecordCursor().DeleteRecord(number);

    public bool AtEnd => _live?.AtEnd ?? _position >= _records.Count;

    public bool AtBeginning => _live?.AtBeginning ?? _position < 0;

    public IReadOnlyDictionary<string, object?>? Current =>
        _live is not null ? _live.Current : _position >= 0 && _position < _records.Count ? _records[_position] : null;

    public void Reload() { ObjectDisposedException.ThrowIf(_disposed, this); _writer?.Validate(); if (_live is not null) _live.Validate(); else if (InputAccess) _records = _access.ReadKeyed(_library, _name, _member); }
    private void RequireInput() { if (!InputAccess) throw new RpgRuntimeException("Input requires an input-capable file declaration."); }

    public bool ReadNext(out IReadOnlyDictionary<string, object?>? record, out bool eof) => ReadNext(out record, out eof, true);
    public bool ReadNext(out IReadOnlyDictionary<string, object?>? record, out bool eof, bool lockRecord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { record = _live.Read(lockRecord); eof = record is null; return !eof; }
        if (_position < _records.Count - 1)
        {
            _position++;
            record = _records[_position];
            eof = false;
            return true;
        }

        _position = _records.Count;
        record = null;
        eof = true;
        return false;
    }

    public bool ReadPrior(out IReadOnlyDictionary<string, object?>? record, out bool eof) => ReadPrior(out record, out eof, true);
    public bool ReadPrior(out IReadOnlyDictionary<string, object?>? record, out bool eof, bool lockRecord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { record = _live.ReadPrior(lockRecord); eof = record is null; return !eof; }
        if (_position > 0)
        {
            _position--;
            record = _records[_position];
            eof = false;
            return true;
        }

        _position = -1;
        record = null;
        eof = true;
        return false;
    }

    public bool ReadNextKeyed(IReadOnlyDictionary<string, object?>? prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof) => ReadNextKeyed(prefix, out record, out eof, true);
    public bool ReadNextKeyed(IReadOnlyDictionary<string, object?>? prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof, bool lockRecord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { record = _live.ReadEqual(prefix, prior: false, lockRecord); eof = record is null; return !eof; }
        prefix ??= CurrentKeys();
        for (var index = _position + 1; index < _records.Count; index++)
        {
            if (Matches(_records[index], prefix))
            {
                _position = index;
                record = _records[index];
                eof = false;
                return true;
            }
        }

        record = null;
        eof = true;
        return false;
    }

    public bool ReadPriorKeyed(IReadOnlyDictionary<string, object?>? prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof) => ReadPriorKeyed(prefix, out record, out eof, true);
    public bool ReadPriorKeyed(IReadOnlyDictionary<string, object?>? prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof, bool lockRecord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { record = _live.ReadEqual(prefix, prior: true, lockRecord); eof = record is null; return !eof; }
        prefix ??= CurrentKeys();
        for (var index = _position - 1; index >= 0; index--)
        {
            if (Matches(_records[index], prefix))
            {
                _position = index;
                record = _records[index];
                eof = false;
                return true;
            }
        }

        record = null;
        eof = true;
        return false;
    }

    public bool Chain(IReadOnlyDictionary<string, object?> search) => Chain(search, true);
    public bool Chain(IReadOnlyDictionary<string, object?> search, bool lockRecord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { return _live.Chain(search, lockRecord); }
        for (var index = 0; index < _records.Count; index++)
        {
            if (Matches(_records[index], search))
            {
                _position = index;
                return true;
            }
        }

        _position = _records.Count;
        return false;
    }

    public bool SetLowerBound(IReadOnlyDictionary<string, object?> search)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { return _live.SetLowerBound(search); }
        for (var index = 0; index < _records.Count; index++)
        {
            var comparison = CompareKeys(_records[index], search);
            if (comparison >= 0)
            {
                _position = index - 1;
                return comparison == 0;
            }
        }

        _position = _records.Count - 1;
        return false;
    }

    public void SetUpperBound(IReadOnlyDictionary<string, object?> search)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireInput();
        if (_live is not null) { _live.SetUpperBound(search); return; }
        for (var index = 0; index < _records.Count; index++)
        {
            if (CompareKeys(_records[index], search) > 0)
            {
                _position = index - 1;
                return;
            }
        }

        _position = _records.Count - 1;
    }

    public void UpdateCurrent(IReadOnlyDictionary<string, object?> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is null) throw new RpgRuntimeException("UPDATE requires a record to have been read.") { StatusCode = 1221, FileError = true };
        if (IsUpdateRead && !HasUpdateLock) throw new RpgRuntimeException("Read and lock the record before updating it.") { StatusCode = 1221, FileError = true };
        if (_live is not null) { _live.Update(values); return; }
        var fallback = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in CurrentKeys()) fallback[key.Key] = key.Value;
        _access.Update(_library, _name, _member, Format, fallback); Reload();
    }
    public void DeleteCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is null) throw new RpgRuntimeException("DELETE requires a record to have been read.") { StatusCode = 1221, FileError = true };
        if (IsUpdateRead && !HasUpdateLock) throw new RpgRuntimeException("Read and lock the record before deleting it.") { StatusCode = 1221, FileError = true };
        if (_live is not null) { _live.Delete(); return; }
        var keys = CurrentKeys();
        if (keys.Count == 0) throw new RpgRuntimeException("DELETE requires declared key fields for this file provider.");
        _access.Delete(_library, _name, _member, Format, keys); Reload();
    }
    public bool DeleteKey(IReadOnlyDictionary<string, object?> key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_live is null) throw new RpgRuntimeException("Keyed DELETE requires a cursor provider with physical record identity.");
        return _live.Delete(key);
    }
    public void InvalidateCurrent() { ObjectDisposedException.ThrowIf(_disposed, this); _live?.InvalidateCurrent(); }
    private IReadOnlyDictionary<string, object?> CurrentKeys() => Current?.Where(pair => _definition.PrimaryFormat.Find(pair.Key)?.Sequence > 0)
        .ToDictionary(pair => pair.Key, pair => pair.Value) ?? throw new RpgRuntimeException("Reposition the file before a keyed read without a search argument.");
    private bool Matches(IReadOnlyDictionary<string, object?> record, IReadOnlyDictionary<string, object?> search)
    {
        var keys = _definition.PrimaryFormat.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        foreach (var key in keys)
        {
            if (!search.TryGetValue(key.Name, out var wanted))
            {
                break;
            }

            record.TryGetValue(key.Name, out var actual);
            if (RpgValues.Compare(actual, wanted) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private int CompareKeys(IReadOnlyDictionary<string, object?> record, IReadOnlyDictionary<string, object?> search)
    {
        var keys = _definition.PrimaryFormat.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        foreach (var key in keys)
        {
            if (!search.TryGetValue(key.Name, out var wanted))
            {
                break;
            }

            record.TryGetValue(key.Name, out var actual);
            var comparison = RpgValues.Compare(actual, wanted);
            if (comparison != 0)
            {
                return key.Descending ? -comparison : comparison;
            }
        }

        return 0;
    }
}
