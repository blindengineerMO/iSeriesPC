using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;

namespace Ipc.Core.Work;

/// <summary>An immutable program argument containing bytes, not a Unicode string.</summary>
public sealed class ProgramBuffer
{
    private readonly byte[] _bytes;
    public int Ccsid { get; }
    public int Length => _bytes.Length;
    public ProgramBuffer(ReadOnlySpan<byte> bytes, int ccsid)
    {
        if (bytes.Length > 1048576 || !CodePage.IsSupported(ccsid)) throw new CpfException("IPC0136", "Invalid program buffer size or CCSID.");
        _bytes = bytes.ToArray(); Ccsid = ccsid;
    }
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public string ToBase64() => Convert.ToBase64String(_bytes);
    // Compatibility bridge for the existing string-based interpreted runtimes and native ABI 1.
    // Conversion must never replace undecodable bytes or silently alter a binary argument.
    public string ToText()
    {
        try
        {
            var encoding = (Encoding)CodePage.FromCcsid(Ccsid).Clone();
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            var text = encoding.GetString(_bytes);
            if (!encoding.GetBytes(text).AsSpan().SequenceEqual(_bytes)) throw new DecoderFallbackException();
            return text;
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        { throw new CpfException("IPC0136", "Program requires a byte-buffer interface for this argument and CCSID."); }
    }
}
