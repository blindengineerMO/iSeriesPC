using Ipc.Core.Messages;

namespace Ipc.Core.Objects;

public static class LibraryNames
{
    public const string QSys = "QSYS";
    public const string QSys2 = "QSYS2";
    public const string QUsrSys = "QUSRSYS";
    public const string QGpl = "QGPL";
    public const string QTemp = "QTEMP";
    public const string QHlpSys = "QHLPSYS";
    public const string QSpl = "QSPL";
    public const string QGpl2 = "QGPL2";
    public const string QSsl = "QSSL";
    public const string QIle = "QILE";
    public const string QRpgl = "QRPGL";
    public const string QCl = "QCL";
    public const string QTcp = "QTCP";
    public const string QUsrTool = "QUSRTOOL";
    public const string QShell = "QSHELL";

    public static readonly IReadOnlyList<SystemLibraryDefinition> SystemDefaults = new[]
    {
        L(QSys, "*SYS", "System library"),
        L(QSys2, "*SYS", "System SQL library"),
        L(QUsrSys, "*SYS", "System user library"),
        L(QHlpSys, "*SYS", "Help library"),
        L(QSpl, "*SYS", "Spool library"),
        L(QSsl, "*SYS", "SSL APAR library"),
        L(QGpl, "*PROD", "General purpose library"),
        L(QTcp, "*PROD", "TCP/IP utilities"),
        L(QIle, "*PROD", "ILE compilers"), 
        L(QRpgl, "*PROD", "RPG ILE"),
        L(QCl, "*PROD", "CL command source"),
        L(QUsrTool, "*PROD", "User tools"),
        L(QShell, "*PROD", "PASE shell"),
    };

    private static SystemLibraryDefinition L(string name, string type, string description) =>
        new(name, type, description);

    public sealed record SystemLibraryDefinition(string Name, string Type, string Description);
}

public sealed class LibraryManager
{
    private readonly IObjectStore _store;
    private readonly string _librarySuffix;

    public LibraryManager(IObjectStore store, string librarySuffix = "")
    {
        _store = store;
        _librarySuffix = librarySuffix;
    }

    public void SeedSystemLibraries()
    {
        foreach (var lib in LibraryNames.SystemDefaults)
        {
            var name = lib.Name + _librarySuffix;
            if (_store.Exists(LibraryNames.QSys, name, ObjectType.Library))
            {
                continue;
            }

            _store.Create(new ObjectDescriptor
            {
                Key = new QualifiedName(LibraryNames.QSys, name),
                ObjectType = ObjectType.Library,
                Attribute = lib.Type,
                Description = lib.Description,
                Owner = "QSECOFR",
            });
        }
    }

    public void CreateLibrary(string name, string type = "*PROD", string? description = null)
    {
        if (!ObjectName.IsValid(name))
        {
            throw new ArgumentException($"Invalid library name '{name}'.", nameof(name));
        }

        if (name == LibraryNames.QTemp)
        {
            throw new InvalidOperationException("QTEMP cannot be created explicitly.");
        }

        var key = new QualifiedName(LibraryNames.QSys, name);
        if (_store.Exists(LibraryNames.QSys, name, ObjectType.Library))
        {
            throw new CpfException("CPF2110", $"Library {name} already exists.");
        }

        _store.Create(new ObjectDescriptor
        {
            Key = key,
            ObjectType = ObjectType.Library,
            Attribute = type,
            Description = description,
            Owner = "QSECOFR",
        });
    }

    public bool LibraryExists(string library) =>
        library == LibraryNames.QTemp || _store.Exists(LibraryNames.QSys, library, ObjectType.Library);

    public string? LibraryType(string library) =>
        library == LibraryNames.QTemp
            ? "*PROD"
            : _store.Get(LibraryNames.QSys, library, ObjectType.Library)?.Attribute;

    public IReadOnlyList<string> AllLibraries() => _store.ListLibraries();
}