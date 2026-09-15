using System.Runtime.InteropServices;
using System.Text;

namespace Ipc.Console.Session;

/// <summary>Owned Linux stdin duplicate with bounded polling and interruptible UTF-8 reads.</summary>
public sealed class TerminalInputReader : TextReader
{
    private int _descriptor;
    private int _stopped;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly byte[] _byte = new byte[1];
    private readonly char[] _characters = new char[2];
    private int _count;
    private int _index;
    private readonly object _readGate = new();
    public TerminalInputReader()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux terminal input is required.");
        _descriptor = fcntl(0, 1030, 3); // F_DUPFD_CLOEXEC: native child processes must not inherit this reader.
        if (_descriptor < 0) throw new IOException("Cannot duplicate terminal input.");
    }
    public void Stop() => Volatile.Write(ref _stopped, 1);
    public override int Read()
    {
        lock (_readGate)
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                if (_index < _count) return _characters[_index++];
                var descriptor = new PollDescriptor { Descriptor = _descriptor, Events = 1 };
                var ready = poll(ref descriptor, 1, 50);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                    throw new IOException("Terminal input polling failed.");
                }
                if (ready == 0) continue;
                var length = read(_descriptor, _byte, 1);
                if (length == 0) return -1;
                if (length < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is 4 or 11) continue;
                    if (error == 5 && (descriptor.ReturnedEvents & 16) != 0) return -1; // PTY hangup
                    throw new IOException("Terminal input read failed.");
                }
                _index = 0; _count = _decoder.GetChars(_byte, 0, 1, _characters, 0, flush: false);
            }
            return -1;
        }
    }
    protected override void Dispose(bool disposing)
    {
        Stop();
        lock (_readGate)
        {
            if (_descriptor >= 0) { close(_descriptor); _descriptor = -1; }
        }
        base.Dispose(disposing);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int descriptor, int command, int argument);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int descriptor, byte[] buffer, nuint count);
}
