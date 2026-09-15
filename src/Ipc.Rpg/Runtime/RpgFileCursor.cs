using Ipc.Db.Definitions;

namespace Ipc.Rpg.Runtime;

public sealed class RpgFileCursor : IDisposable
{
    private readonly IRpgFileAccess _access;
    private readonly string _library;
    private readonly string _name;
    private readonly string _member;
    private readonly FileDefinition _definition;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _records = Array.Empty<IReadOnlyDictionary<string, object?>>();
    private int _position = -1;
    private bool _disposed;
    public void Dispose() { _disposed = true; _records = Array.Empty<IReadOnlyDictionary<string, object?>>(); }

    public RpgFileCursor(IRpgFileAccess access, string library, string name, string member)
    {
        _access = access;
        _library = library;
        _name = name;
        _member = member;
        _definition = access.GetDefinition(library, name)
            ?? throw new RpgRuntimeException($"File {name} in library {library} not found.");
        if (!access.MemberExists(library, name, member))
        {
            throw new RpgRuntimeException($"Member {member} not found in file {name}.");
        }

        Reload();
    }

    public string Library => _library;

    public string Name => _name;

    public string Member => _member;

    public string Format => _definition.PrimaryFormat.Name;

    public FileDefinition Definition => _definition;

    public bool AtEnd => _position >= _records.Count;

    public bool AtBeginning => _position < 0;

    public IReadOnlyDictionary<string, object?>? Current =>
        _position >= 0 && _position < _records.Count ? _records[_position] : null;

    public void Reload() { ObjectDisposedException.ThrowIf(_disposed, this); _records = _access.ReadKeyed(_library, _name, _member); }

    public bool ReadNext(out IReadOnlyDictionary<string, object?>? record, out bool eof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public bool ReadPrior(out IReadOnlyDictionary<string, object?>? record, out bool eof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public bool ReadNextKeyed(IReadOnlyDictionary<string, object?> prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public bool ReadPriorKeyed(IReadOnlyDictionary<string, object?> prefix, out IReadOnlyDictionary<string, object?>? record, out bool eof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public bool Chain(IReadOnlyDictionary<string, object?> search)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
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