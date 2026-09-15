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
        var row = Math.Min(StatusRow, buffer.Rows);
        var attributes = DisplayAttribute.ReverseVideo | (error ? DisplayAttribute.HighIntensity : DisplayAttribute.None);
        buffer.ClearRegion(row, 1, 1, buffer.Columns);
        for (var i = 0; i < Math.Min(text.Length, buffer.Columns - 2); i++) buffer.Set(row, i + 1, text[i], attributes);
    }
}

public delegate string SystemNameProvider();