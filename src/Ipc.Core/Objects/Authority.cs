namespace Ipc.Core.Objects;

[Flags]
public enum AuthorityBit
{
    None = 0,
    ObjectOperate = 1 << 0,
    Read = 1 << 1,
    Add = 1 << 2,
    Update = 1 << 3,
    Delete = 1 << 4,
    ObjectManagement = 1 << 5,
    ObjectExist = 1 << 6,
    ObjectAlter = 1 << 7,
    ObjectReference = 1 << 8,
}

public static class Authorities
{
    public const string Exclude = "*EXCLUDE";
    public const string Use = "*USE";
    public const string Change = "*CHANGE";
    public const string All = "*ALL";

    public static readonly AuthorityBit UseBits =
        AuthorityBit.ObjectOperate | AuthorityBit.Read;

    public static readonly AuthorityBit ChangeBits =
        UseBits | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete;

    public static readonly AuthorityBit AllBits =
        ChangeBits | AuthorityBit.ObjectManagement | AuthorityBit.ObjectExist |
        AuthorityBit.ObjectAlter | AuthorityBit.ObjectReference;

    public static AuthorityBit FromLevel(string level) => level switch
    {
        Exclude => AuthorityBit.None,
        Use => UseBits,
        Change => ChangeBits,
        All => AllBits,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown authority '{level}'."),
    };

    public static string? ToLevel(AuthorityBit bits)
    {
        if ((bits & AllBits) == AllBits) return All;
        if ((bits & ChangeBits) == ChangeBits) return Change;
        if ((bits & UseBits) == UseBits) return Use;
        return null;
    }

    public static bool CanRead(AuthorityBit bits) => (bits & AuthorityBit.Read) != 0;
    public static bool CanChange(AuthorityBit bits) => (bits & ChangeBits) == ChangeBits;
    public static bool CanDelete(AuthorityBit bits) => (bits & AuthorityBit.Delete) != 0;
}