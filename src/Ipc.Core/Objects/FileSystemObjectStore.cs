using System.Text.Json;
using Ipc.Core.Messages;

namespace Ipc.Core.Objects;

/// <summary>Standalone descriptor store with atomic snapshots and a single process owner.</summary>
public sealed class FileSystemObjectStore : IObjectStore, IDisposable
{
    private readonly string _path;
    private readonly FileStream _ownership;
    private readonly object _gate = new();
    private List<StoredObject> _objects;
    private bool _disposed;

    public FileSystemObjectStore(string directory)
    {
        directory = Path.GetFullPath(directory);
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            if (!existed) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("Object store root must not be writable by other accounts.");
        }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Object store root must not be a symbolic link.");
        _path = Path.Combine(directory, "objects.json");
        RejectLink(_path);
        RejectLink(Path.Combine(directory, ".owner.lock"));
        _ownership = new FileStream(Path.Combine(directory, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(_path))
            {
                var document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("Empty object store.");
                if (document.Version != 1 || document.Objects is null)
                    throw new InvalidDataException("Unsupported object store format.");
                _objects = document.Objects;
                foreach (var value in _objects) value.Descriptor().ValidateIdentity();
                if (_objects.Select(o => (o.Library, o.Name, o.Type)).Distinct().Count() != _objects.Count)
                    throw new InvalidDataException("Duplicate object identities in store.");
            }
            else _objects = new();
        }
        catch { _ownership.Dispose(); throw; }
    }

    public long ObjectCount { get { lock (_gate) { CheckOpen(); return _objects.Count; } } }
    public bool Exists(string library, string name, string type) => Get(library, name, type) is not null;
    public ObjectDescriptor? Get(string library, string name, string type)
    {
        lock (_gate)
        {
            CheckOpen();
            return _objects.FirstOrDefault(o => o.Library == library && o.Name == name && o.Type == type)?.Descriptor();
        }
    }
    public ObjectDescriptor GetRequired(string library, string name, string type) => Get(library, name, type)
        ?? throw new CpfException("CPF9801", $"Object {type} {library}/{name} not found.");

    public void Create(ObjectDescriptor descriptor) => Mutate(items =>
    {
        descriptor.ValidateIdentity();
        if (items.Any(o => o.Matches(descriptor))) throw new InvalidOperationException($"Object {descriptor.Key} already exists.");
        items.Add(StoredObject.From(descriptor));
    });

    public void Update(ObjectDescriptor descriptor) => Mutate(items =>
    {
        descriptor.ValidateIdentity();
        var index = items.FindIndex(o => o.Matches(descriptor));
        if (index < 0) throw new CpfException("CPF9801", $"Object {descriptor.Key} not found.");
        descriptor.Touch();
        items[index] = StoredObject.From(descriptor);
    });

    public void Delete(string library, string name, string type) => Mutate(items =>
        items.RemoveAll(o => o.Library == library && o.Name == name && o.Type == type));

    public IReadOnlyList<ObjectDescriptor> Find(string library, string? namePattern, string? type, string? owner)
    {
        lock (_gate)
        {
            CheckOpen();
            return _objects.Where(o => o.Library == library && (type is null || o.Type == type) &&
                (owner is null || o.Owner == owner) && (namePattern is null || NamePattern.Matches(o.Name, namePattern)))
                .OrderBy(o => o.Name, StringComparer.Ordinal).Select(o => o.Descriptor()).ToArray();
        }
    }

    public IReadOnlyList<string> ListLibraries()
    {
        lock (_gate)
        {
            CheckOpen();
            return _objects.Where(o => o.Type == ObjectType.Library).Select(o => o.Name).Distinct().Order().ToArray();
        }
    }

    private void Mutate(Action<List<StoredObject>> mutate)
    {
        lock (_gate)
        {
            CheckOpen();
            var next = _objects.ToList();
            mutate(next);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    JsonSerializer.Serialize(stream, new StoreDocument(1, next));
                    stream.Flush(flushToDisk: true);
                }
                RejectLink(_path);
                File.Move(temporary, _path, overwrite: true);
                _objects = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Object store files must not be symbolic links.");
    }
    private void CheckOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _ownership.Dispose(); } }

    private sealed record StoreDocument(int Version, List<StoredObject> Objects);
    private sealed record StoredObject(string Library, string Name, string Type, string Owner, DateTimeOffset Created,
        DateTimeOffset Changed, string? Description, int Ccsid, string? Attribute, string? Format,
        AuthorityBit Authority, string? Source, Dictionary<string, string>? Attributes)
    {
        public bool Matches(ObjectDescriptor d) => Library == d.Library && Name == d.Name && Type == d.ObjectType;
        public static StoredObject From(ObjectDescriptor d) => new(d.Library, d.Name, d.ObjectType, d.Owner,
            d.Created, d.Changed, d.Description, d.Ccsid, d.Attribute, d.Format, d.PublicAuthority, d.Source,
            d.ExtendedAttributes is null ? null : new(d.ExtendedAttributes));
        public ObjectDescriptor Descriptor() => new()
        {
            Key = new QualifiedName(Library, Name), ObjectType = Type, Owner = Owner, Created = Created,
            Changed = Changed, Description = Description, Ccsid = Ccsid, Attribute = Attribute, Format = Format,
            PublicAuthority = Authority, Source = Source, ExtendedAttributes = Attributes is null ? null : new Dictionary<string, string>(Attributes),
        };
    }
}
