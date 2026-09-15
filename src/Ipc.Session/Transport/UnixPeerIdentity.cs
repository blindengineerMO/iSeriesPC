using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ipc.Session.Transport;

public static class UnixPeerIdentity
{
    internal static string? Account(Socket socket)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var held = false;
        try
        {
            socket.SafeHandle.DangerousAddRef(ref held);
            uint length = 12;
            if (getsockopt(socket.SafeHandle.DangerousGetHandle().ToInt32(), 1, 17, out var credentials, ref length) != 0 || length != 12)
                throw new IOException("Cannot determine the session's Unix peer identity.");
            return AccountForUid(credentials.Uid);
        }
        finally { if (held) socket.SafeHandle.DangerousRelease(); }
    }

    public static string? CurrentAccount() => OperatingSystem.IsLinux() ? AccountForUid(geteuid()) : null;
    private static string? AccountForUid(uint uid)
    {
        var entry = Marshal.AllocHGlobal(128);
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            return getpwuid_r(uid, entry, buffer, 65536, out var result) == 0 && result != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result)) : null;
        }
        finally { Marshal.FreeHGlobal(entry); Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Credentials { public int Pid; public uint Uid; public uint Gid; }
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int socket, int level, int option, out Credentials value, ref uint length);
    [DllImport("libc")] private static extern int getpwuid_r(uint uid, IntPtr entry, IntPtr buffer, nuint size, out IntPtr result);
    [DllImport("libc")] private static extern uint geteuid();
}
