using System.Collections.Concurrent;

namespace Ipc.Core.Objects;

public interface IObjectStore
{
    bool Exists(string library, string name, string type);

    ObjectDescriptor? Get(string library, string name, string type);

    ObjectDescriptor GetRequired(string library, string name, string type);

    void Create(ObjectDescriptor descriptor);

    void Update(ObjectDescriptor descriptor);

    void Delete(string library, string name, string type);

    IReadOnlyList<ObjectDescriptor> Find(string library, string? namePattern, string? type, string? owner);

    IReadOnlyList<string> ListLibraries();

    long ObjectCount { get; }
}

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<(QualifiedName Name, string Type), ObjectDescriptor> _objects =
        new();

    public long ObjectCount => _objects.Count;

    public void Create(ObjectDescriptor descriptor)
    {
        descriptor.ValidateIdentity();
        if (!_objects.TryAdd((descriptor.Key, descriptor.ObjectType), descriptor.Snapshot()))
            throw new InvalidOperationException($"Object {descriptor.Key} {descriptor.ObjectType} already exists.");
    }

    public void Delete(string library, string name, string type) =>
        _objects.TryRemove((new QualifiedName(library, name), type), out _);

    public bool Exists(string library, string name, string type) =>
        _objects.ContainsKey((new QualifiedName(library, name), type));

    public ObjectDescriptor? Get(string library, string name, string type)
    {
        if (_objects.TryGetValue((new QualifiedName(library, name), type), out var d))
        {
            return d.Snapshot();
        }

        return null;
    }

    public ObjectDescriptor GetRequired(string library, string name, string type) =>
        Get(library, name, type)
        ?? throw new Ipc.Core.Messages.CpfException("CPF9801",
            $"Object {type} {library}/{name} not found.");

    public void Update(ObjectDescriptor descriptor)
    {
        descriptor.ValidateIdentity();
        descriptor.Touch();
        var key = (descriptor.Key, descriptor.ObjectType);
        while (_objects.TryGetValue(key, out var current))
            if (_objects.TryUpdate(key, descriptor.Snapshot(), current)) return;
        throw new Ipc.Core.Messages.CpfException("CPF9801", $"Object {descriptor.Key} {descriptor.ObjectType} not found.");
    }

    public IReadOnlyList<ObjectDescriptor> Find(
        string library, string? namePattern, string? type, string? owner) =>
        _objects.Values
            .Where(d => d.Library == library)
            .Where(d => type is null || d.ObjectType == type)
            .Where(d => owner is null || d.Owner == owner)
            .Where(d => namePattern is null || NamePattern.Matches(d.Name, namePattern))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => d.Snapshot())
            .ToList();

    public IReadOnlyList<string> ListLibraries() =>
        _objects.Values
            .Where(d => d.ObjectType == ObjectType.Library)
            .Select(d => d.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}

public static class NamePattern
{
    public static bool Matches(string name, string pattern)
    {
        if (pattern.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1];
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
