namespace Ipc.Terminal;

public enum AidKey : byte
{
    None = 0x00,
    Enter = 0x11,
    Clear = 0x06,
    RollUp = 0x4A,
    RollDown = 0x4D,
    Print = 0xF1,
    SysReq = 0x88,
    Pf1 = 0x31,
    Pf2 = 0x32,
    Pf3 = 0x33,
    Pf4 = 0x34,
    Pf5 = 0x35,
    Pf6 = 0x36,
    Pf7 = 0x37,
    Pf8 = 0x38,
    Pf9 = 0x39,
    Pf10 = 0x3A,
    Pf11 = 0x3B,
    Pf12 = 0x3C,
    Pf13 = 0x3D,
    Pf14 = 0x3E,
    Pf15 = 0x3F,
    Pf16 = 0x40,
    Pf17 = 0x41,
    Pf18 = 0x42,
    Pf19 = 0x43,
    Pf20 = 0x44,
    Pf21 = 0x45,
    Pf22 = 0x46,
    Pf23 = 0x47,
    Pf24 = 0x48,
    Pa1 = 0x6C,
    Pa2 = 0x6D,
    Pa3 = 0x6E,
}

public static class AidKeyExtensions
{
    public static bool IsFunctionKey(this AidKey key) => key is > AidKey.None and not AidKey.Enter;
}

public enum CursorEdit : byte
{
    None = 0,
    CursorLeft = 1,
    CursorRight = 2,
    CursorUp = 3,
    CursorDown = 4,
    FieldExit = 5,
    FieldBackspace = 6,
    FieldEraseToEnd = 7,
    Delete = 8,
    Insert = 9,
    Tab = 10,
    BackTab = 11,
    NextField = 12,
    PreviousField = 13,
}

public readonly record struct KeyPress(AidKey Aid, CursorEdit? Edit = null, char Character = '\0');

public static class KeyCodes
{
    public const string Escape = "\u001b";

    public static readonly string[] FunctionKeys =
    {
        $"{Escape}OP", // F1
        $"{Escape}OQ",
        $"{Escape}OR",
        $"{Escape}OS",
        $"{Escape}[15~",
        $"{Escape}[17~",
        $"{Escape}[18~",
        $"{Escape}[19~",
        $"{Escape}[20~",
        $"{Escape}[21~",
        $"{Escape}[23~",
        $"{Escape}[24~",
    };

    public static readonly string Space = " ";
}