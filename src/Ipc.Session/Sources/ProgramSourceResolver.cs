using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Security;

namespace Ipc.Session.Sources;

/// <summary>Shared compilation source resolver; dialects decide which directives invoke it.</summary>
public sealed class ProgramSourceResolver(IpcSystem system, Job? job = null, CancellationToken cancellationToken = default)
{
    private readonly SqliteFileStore _files = new(system.Connections, system.Objects);
    public SourceDocument Member(string file, string member)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = QualifiedName.Parse(file.ToUpperInvariant(), "*LIBL"); member = member.ToUpperInvariant();
        if (!ObjectName.IsValid(member)) throw new CpfException("IPC0006", "Invalid source member name.");
        var library = system.SearchLibraries(job, key.Library).FirstOrDefault(l => _files.FileExists(l, key.Name.Value))
            ?? throw new CpfException("CPF9801", "Source file " + file + " not found.");
        var source = _files.ReadSourceMember(library, key.Name.Value, member);
        if (source.Revision is null) throw new CpfException("CPF2817", "Source member " + member + " not found.");
        return new($"{library}/{key.Name}({member})", source.Source);
    }
    public SourceDocument Stream(string path)
    {
        new ServiceAuthorization(system.Connections).RequireSpecial(SpecialAuthority.Service, allowAdopted: false);
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (path.Length > 1024 || path.Any(char.IsControl)) throw new ArgumentException("Invalid source path.");
        var file = new FileInfo(path); path = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
        using var stream = OpenSource(path);
        if (!stream.CanSeek || stream.Length > 1048576) throw new InvalidDataException("Source stream must be a regular UTF-8 file no larger than 1 MiB.");
        var bytes = new byte[(int)stream.Length]; var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(4096, bytes.Length - offset));
            if (read == 0) throw new IOException("Source stream changed while reading."); offset += read;
        }
        if (stream.ReadByte() != -1) throw new IOException("Source stream changed while reading.");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return new(path, new UTF8Encoding(false, true).GetString(bytes, bom, bytes.Length - bom));
    }
    public SourceDocument Resolve(SourceDocument origin, string reference)
    {
        if (Path.IsPathRooted(origin.Identity))
            return Stream(Path.IsPathRooted(reference) ? reference : Path.Combine(Path.GetDirectoryName(origin.Identity)!, reference));
        var open = reference.IndexOf('(');
        if (open > 0 && reference.EndsWith(')')) return Member(reference[..open], reference[(open + 1)..^1]);
        var comma = reference.IndexOf(',');
        if (comma > 0 && reference.LastIndexOf(',') == comma) return Member(reference[..comma], reference[(comma + 1)..]);
        if (ObjectName.IsValid(reference.ToUpperInvariant()) && origin.Identity.IndexOf('(') is var parent && parent > 0)
            return Member(origin.Identity[..parent], reference);
        throw new ArgumentException("Member includes use MEMBER, LIB/FILE(MEMBER), or LIB/FILE,MEMBER. Stream sources resolve filesystem paths.");
    }

    private static FileStream OpenSource(string path)
    {
        if (!OperatingSystem.IsLinux()) return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        // A FIFO must not block open before the seekability check can reject it.
        var descriptor = Open(path, 0x800 | 0x80000 | 0x100); // O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOCTTY
        if (descriptor < 0) throw new IOException("Cannot open source stream.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read, 4096); }
        catch { handle.Dispose(); throw; }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
