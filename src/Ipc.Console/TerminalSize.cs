using System.Runtime.InteropServices;

namespace Ipc.Console.Session;

internal static class TerminalSize
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize { public ushort Rows; public ushort Columns; public ushort X; public ushort Y; }
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int GetWindowSize(int fd, nuint request, out WindowSize size);
    internal static (int Rows, int Columns)? Read()
    {
        if (!OperatingSystem.IsLinux() || global::System.Console.IsInputRedirected) return null;
        if (GetWindowSize(0, 0x5413, out var size) != 0) throw new IOException("Cannot read controlling terminal dimensions.");
        return (size.Rows == 0 ? 24 : Math.Clamp((int)size.Rows, 1, 500), size.Columns == 0 ? 80 : Math.Clamp((int)size.Columns, 1, 1000));
    }
}
