namespace Ipc.Core.Objects;

public readonly record struct QualifiedName
{
    public ObjectName Name { get; }

    public string Library { get; }

    public QualifiedName(string library, string name)
    {
        if (ObjectName.IsSpecial(library))
        {
            Library = library;
        }
        else
        {
            if (!ObjectName.IsValid(library))
            {
                throw new ArgumentException($"Invalid library name '{library}'.", nameof(library));
            }

            Library = library;
        }

        Name = new ObjectName(name);
    }

    public static QualifiedName Parse(string qualified)
    {
        var parts = qualified.Split('/', 2);
        return parts.Length == 2
            ? new QualifiedName(parts[0].Trim(), parts[1].Trim())
            : throw new FormatException($"Not a qualified name '{qualified}'.");
    }

    public static QualifiedName Parse(string name, string defaultLibrary) =>
        name.Contains('/') ? Parse(name) : new QualifiedName(defaultLibrary, name.Trim());

    public bool UsesLibraryList =>
        Library == ObjectName.LibraryListMagic || Library == ObjectName.CurrentLibraryMagic;

    public override string ToString() => $"{Library}/{Name.Value}";

    public static IReadOnlyList<string> LibrariesIn(
        QualifiedName q,
        string currentLibrary,
        IReadOnlyList<string> systemLibraryList,
        IReadOnlyList<string> userPartOfLibraryList) =>
        q.Library switch
        {
            ObjectName.CurrentLibraryMagic => new[] { currentLibrary },
            ObjectName.LibraryListMagic => systemLibraryList.Concat(new[] { currentLibrary })
                .Concat(userPartOfLibraryList).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => new[] { q.Library },
        };
}
