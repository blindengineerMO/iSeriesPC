using Ipc.Terminal;

namespace Ipc.Console.Session;

public static class Screens
{
    public static readonly int BufferRows = 24;
    public static readonly int BufferColumns = 80;
    public static readonly int StatusRow = 24;

    public static DisplayBuffer SignOn(SystemNameProvider systemName)
    {
        var buffer = new DisplayBuffer(BufferRows, BufferColumns);
        buffer.MoveCursor(1, 1);

        buffer.WriteLine("iSeriesPC", DisplayAttribute.HighIntensity);
        buffer.WriteLine(new string('=', BufferColumns - 2));
        buffer.WriteLine();
        buffer.Write("Sign On to System . . . :  ");
        buffer.Write(systemName(), DisplayAttribute.HighIntensity);
        buffer.WriteLine();
        buffer.WriteLine();
        Status(buffer, "Enter your user profile and password.");
        return buffer;
    }

    public static void Status(DisplayBuffer buffer, string text, bool error = false)
    {
        buffer.ClearRegion(StatusRow, 1, 1, buffer.Columns);
        var attributes = DisplayAttribute.ReverseVideo | (error ? DisplayAttribute.HighIntensity : DisplayAttribute.None);
        buffer.MoveCursor(StatusRow, 1);
        buffer.Write(text.PadRight(buffer.Columns - 2), attributes);
    }
}

public delegate string SystemNameProvider();