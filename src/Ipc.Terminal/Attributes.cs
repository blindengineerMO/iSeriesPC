namespace Ipc.Terminal;

[Flags]
public enum DisplayAttribute : byte
{
    None = 0,
    HighIntensity = 1 << 0,
    ReverseVideo = 1 << 1,
    Underline = 1 << 2,
    Blink = 1 << 3,
    ColumnSeparator = 1 << 4,
    NonDisplay = 1 << 5,
    InputField = 1 << 6,
}

public static class Ansicolor
{
    public const int Default = 39;
    public const int Black = 30;
    public const int Red = 31;
    public const int Green = 32;
    public const int Yellow = 33;
    public const int Blue = 34;
    public const int Magenta = 35;
    public const int Cyan = 36;
    public const int White = 37;
}

public readonly record struct Cell(char Value, DisplayAttribute Attributes, int Foreground = Ansicolor.Green);

public readonly record struct CellPosition(int Row, int Column)
{
    public int Index(int columns) => Row * columns + Column;

    public static CellPosition operator +(CellPosition left, CellPosition right) =>
        new(left.Row + right.Row, left.Column + right.Column);
}