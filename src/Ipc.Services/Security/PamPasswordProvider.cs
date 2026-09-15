using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ipc.Services.Security;

/// <summary>Linux-PAM password and account checks with bounded admission and caller wait.</summary>
public sealed class PamPasswordProvider : IPasswordAuthenticationProvider
{
    private static readonly SemaphoreSlim Capacity = new(8, 8);
    private readonly string _service;
    private readonly TimeSpan _timeout;
    private readonly string? _configurationDirectory;

    public PamPasswordProvider(string service = "iseriespc", TimeSpan? timeout = null)
        : this(service, timeout ?? TimeSpan.FromSeconds(15), null) { }

    internal PamPasswordProvider(string service, TimeSpan timeout, string? configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(service) || service.Length > 64 || service.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("PAM service must be a simple service name.", nameof(service));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _service = service;
        _timeout = timeout;
        _configurationDirectory = configurationDirectory;
    }

    public CredentialVerification Verify(string account, string password)
    {
        if (!OperatingSystem.IsLinux()) return new(CredentialStatus.ProviderUnavailable);
        if (string.IsNullOrEmpty(account) || account.Length > 256 || account.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 511 || password.Contains('\0'))
            return new(CredentialStatus.Invalid);
        if (!Capacity.Wait(0)) return new(CredentialStatus.ProviderUnavailable);
        // A timed-out native module retains its slot until it actually returns. It cannot
        // authenticate later or mutate a profile. Shutdown never waits on these workers.
        var work = Task.Run(() =>
        {
            try { return VerifyNative(account, password); }
            catch (Exception) { return new CredentialVerification(CredentialStatus.ProviderUnavailable); }
            finally { Capacity.Release(); }
        });
        try { return work.WaitAsync(_timeout).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return new(CredentialStatus.ProviderUnavailable); }
    }

    private CredentialVerification VerifyNative(string account, string password)
    {
        if (!OperatingSystem.IsLinux()) return new(CredentialStatus.ProviderUnavailable);
        var directory = _configurationDirectory ?? "/etc/pam.d";
        var serviceFile = Path.Combine(directory, _service);
        var writable = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if (!File.Exists(serviceFile) || (File.GetUnixFileMode(directory) & writable) != 0 ||
            (File.GetUnixFileMode(serviceFile) & writable) != 0)
            return new(CredentialStatus.ProviderUnavailable);
        Conversation callback = (int count, IntPtr messages, out IntPtr responses, IntPtr data) =>
            Respond(count, messages, out responses, account, password);
        var conversation = new PamConversation { Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var status = _configurationDirectory is null
            ? pam_start(_service, account, ref conversation, out var handle)
            : pam_start_confdir(_service, account, ref conversation, _configurationDirectory, out handle);
        if (status != 0) return new(CredentialStatus.ProviderUnavailable);
        try
        {
            status = pam_authenticate(handle, 1); // Reject null authentication tokens.
            if (status != 0) return Map(status);
            status = pam_acct_mgmt(handle, 0);
            if (status != 0) return Map(status);
            status = pam_get_item(handle, 2, out var canonicalUser);
            if (status != 0 || Marshal.PtrToStringUTF8(canonicalUser) != account)
                return new(CredentialStatus.AccountUnavailable);
            return new(CredentialStatus.Valid);
        }
        finally { pam_end(handle, status); GC.KeepAlive(callback); }
    }

    private static CredentialVerification Map(int status) => new(status switch
    {
        0 => CredentialStatus.Valid,
        12 or 27 => CredentialStatus.PasswordExpired,
        13 => CredentialStatus.AccountUnavailable,
        6 or 7 or 8 or 10 or 11 => CredentialStatus.Invalid,
        _ => CredentialStatus.ProviderUnavailable,
    });

    private static int Respond(int count, IntPtr messages, out IntPtr responses, string account, string password)
    {
        responses = IntPtr.Zero;
        if (count is < 1 or > 32) return 19;
        var size = Marshal.SizeOf<PamResponse>();
        var allocated = calloc((nuint)count, (nuint)size);
        if (allocated == IntPtr.Zero) return 5;
        var strings = new List<(IntPtr Address, int Length)>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var message = Marshal.ReadIntPtr(messages, index * IntPtr.Size);
                var style = Marshal.ReadInt32(message);
                string? answer = style switch { 1 => password, 2 => account, 3 or 4 => null, _ => throw new InvalidDataException() };
                if (answer is null) continue;
                var bytes = Encoding.UTF8.GetBytes(answer + "\0");
                try
                {
                    var pointer = calloc((nuint)bytes.Length, 1);
                    if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                    strings.Add((pointer, bytes.Length));
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    Marshal.StructureToPtr(new PamResponse { Text = pointer }, allocated + index * size, false);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            responses = allocated; // Linux-PAM owns and frees successful responses.
            return 0;
        }
        catch
        {
            foreach (var (address, length) in strings)
            {
                for (var i = 0; i < length; i++) Marshal.WriteByte(address, i, 0);
                free(address);
            }
            free(allocated);
            return 19;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct PamConversation { public IntPtr Callback; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct PamResponse { public IntPtr Text; public int ReturnCode; }
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_start([MarshalAs(UnmanagedType.LPUTF8Str)] string service, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, ref PamConversation conversation, out IntPtr handle);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_start_confdir([MarshalAs(UnmanagedType.LPUTF8Str)] string service, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, ref PamConversation conversation, [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, out IntPtr handle);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_authenticate(IntPtr handle, int flags);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_get_item(IntPtr handle, int item, out IntPtr value);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_end(IntPtr handle, int status);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr calloc(nuint count, nuint size);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern void free(IntPtr pointer);
}
