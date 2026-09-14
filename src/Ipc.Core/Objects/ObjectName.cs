namespace Ipc.Core.Objects;

public readonly partial record struct ObjectName
{
    public const int MaxLength = 10;
    public const int LibraryMaxLength = 10;

    private const string ValidChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$@_";

    private static readonly IReadOnlySet<string> SystemLibraries = new HashSet<string>(
        new[]
        {
            "QSYS", "QSYS2", "QUSRSYS", "QGPL", "QTEMP", "QHLPSYS", "QSPL", "QSPLDTA",
            "QHST", "QC2", "QILE", "QRPG", "QCL", "QSSP", "QSHELL", "QTCP", "QUSRTOOL",
            "QAS4000", "QDISETUP", "QSECURITY", "QCFG", "QSYSLIB", "QJOBLOG",
        },
        StringComparer.Ordinal);

    public const string CurrentLibraryMagic = "*CURLIB";
    public const string LibraryListMagic = "*LIBL";
    public const string NoneMagic = "*N";
    public const string AllMagic = "*ALL";
    public const string GenericSuffix = "*";

    private readonly string _value;

    public ObjectName(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"Invalid object name '{value}'.", nameof(value));
        }

        _value = value;
    }

    public string Value => _value ?? throw new InvalidOperationException("Object name not set.");

    public bool IsReserved => Value.Length > 0 && (Value[0] == 'Q' || Value[0] == '#');

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!ValidChars.Contains(ch))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSystemLibrary(string name) => SystemLibraries.Contains(name);

    public static bool IsSpecial(string? name) =>
        name is CurrentLibraryMagic or LibraryListMagic or NoneMagic or AllMagic;

    public static ObjectName Parse(string value) => new(value);

    public override string ToString() => Value;
}